"""Single-consumer, offline subtitle mailbox worker. Only the API writes media sidecars."""
from __future__ import annotations

import argparse
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import textwrap
import threading
import time
import uuid

from model_readiness import ModelReadiness
import subtitle_sync

MAX_REQUEST = 32 * 1024
# JSON can escape each UTF-8 byte as six ASCII bytes. Also allow the envelope.
MAX_REPAIR_REQUEST = 6 * subtitle_sync.MAX_SRT_BYTES + MAX_REQUEST
MAX_RESULT = 16 * 1024 * 1024
MAX_CUES = 50000
HEARTBEAT_INTERVAL = 5
ID = re.compile(r"[a-f0-9]{32}\Z")
VIDEO_EXTENSIONS = {".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm"}
TERMINAL = {"completed", "failed", "cancelled"}


class WorkerError(Exception):
    """Only enumerated codes cross the mailbox; exception text never does."""


@dataclass(frozen=True)
class Config:
    shared: Path
    roots: tuple[Path, ...]
    models: Path
    temp: Path
    threads: int = 2
    job_timeout: float = 21600
    poll_seconds: float = .5
    max_duration: float = 14400
    probe_timeout: float = 30
    extract_timeout: float = 1800

    @staticmethod
    def from_env():
        roots = json.loads(os.environ.get("SUBTITLE_MEDIA_ROOTS", "[]"))
        if not isinstance(roots, list) or not roots or any(not isinstance(p, str) or not Path(p).is_absolute() for p in roots):
            raise ValueError("SUBTITLE_MEDIA_ROOTS must be a nonempty JSON array of absolute directories")
        return Config(Path(os.environ.get("SUBTITLE_SHARED_PATH", "/subtitle-work")),
                      tuple(Path(p) for p in roots), Path(os.environ.get("SUBTITLE_MODEL_PATH", "/models")),
                      Path(os.environ.get("SUBTITLE_TEMP_PATH", "/tmp/subtitles")),
                      threads=bounded_env("SUBTITLE_THREADS", 2, 1, 32),
                      job_timeout=bounded_env("SUBTITLE_JOB_TIMEOUT", 21600, 60, 86400),
                      max_duration=bounded_env("SUBTITLE_MAX_DURATION", 14400, 60, 86400))


def bounded_env(name, default, minimum, maximum):
    value = int(os.environ.get(name, default))
    if not minimum <= value <= maximum:
        raise ValueError(f"{name} must be between {minimum} and {maximum}")
    return value


def no_links(path: Path):
    """Reject all symlinks/reparse points, including ancestors and configured roots."""
    for item in reversed((path, *path.parents)):
        metadata = item.lstat()
        if stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & 0x400:
            raise WorkerError("path_denied")


def validate_request(value, expected_id):
    if (not isinstance(value, dict) or set(value) != {"version", "id", "videoPath", "romanian"}
            or type(value["version"]) is not int or value["version"] != 1
            or not isinstance(value["id"], str) or not ID.fullmatch(value["id"])
            or value["id"] != expected_id or type(value["romanian"]) is not bool
            or not isinstance(value["videoPath"], str) or "\0" in value["videoPath"]
            or not Path(value["videoPath"]).is_absolute()):
        raise WorkerError("invalid_request")
    return value


def validate_repair_request(value, expected_id):
    if (not isinstance(value, dict) or set(value) != {"version", "id", "kind", "videoPath", "subtitleSrt"}
            or type(value["version"]) is not int or value["version"] != 2
            or not isinstance(value["id"], str) or not ID.fullmatch(value["id"])
            or value["id"] != expected_id or value["kind"] != "synchronize"
            or not isinstance(value["videoPath"], str) or "\0" in value["videoPath"]
            or not Path(value["videoPath"]).is_absolute()):
        raise WorkerError("invalid_request")
    try:
        subtitle_sync.cue_intervals(value["subtitleSrt"])
    except ValueError:
        raise WorkerError("invalid_subtitles") from None
    return value


def validate_video(value, roots):
    try:
        path = Path(value)
        if not path.is_absolute() or ".." in path.parts or path.suffix.lower() not in VIDEO_EXTENSIONS:
            raise WorkerError("path_denied")
        no_links(path)
        resolved = path.resolve(strict=True)
        allowed = False
        for root in roots:
            no_links(root)
            if root.is_dir() and resolved.is_relative_to(root.resolve(strict=True)):
                allowed = True
        if not allowed or not stat.S_ISREG(path.stat().st_mode):
            raise WorkerError("path_denied")
        return resolved
    except (OSError, ValueError):
        raise WorkerError("path_denied") from None


@contextmanager
def opened_video(path):
    # Linux openat + O_NOFOLLOW pins every ancestor and the final file against rename races.
    # The descriptor is inherited by ffprobe/FFmpeg; they never reopen the submitted path.
    descriptors = []
    try:
        if os.name == "posix":
            descriptor = os.open(path.anchor, os.O_RDONLY | os.O_DIRECTORY)
            descriptors.append(descriptor)
            for index, part in enumerate(path.parts[1:]):
                flags = os.O_RDONLY | os.O_NOFOLLOW
                if index < len(path.parts) - 2:
                    flags |= os.O_DIRECTORY
                descriptor = os.open(part, flags, dir_fd=descriptor)
                descriptors.append(descriptor)
            if not stat.S_ISREG(os.fstat(descriptor).st_mode):
                raise WorkerError("path_denied")
            yield f"/proc/self/fd/{descriptor}", (descriptor,)
        else:
            no_links(path)
            yield str(path), ()
    except OSError:
        raise WorkerError("path_denied") from None
    finally:
        for descriptor in reversed(descriptors):
            os.close(descriptor)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise WorkerError("invalid_request")
        result[key] = value
    return result


def read_json(path, limit=MAX_REQUEST):
    try:
        no_links(path)
        if not stat.S_ISREG(path.stat().st_mode):
            raise WorkerError("invalid_request")
        with path.open("rb") as file:
            data = file.read(limit + 1)
        if len(data) > limit:
            raise WorkerError("invalid_request")
        return json.loads(data.decode("utf-8"), object_pairs_hook=unique_object)
    except (OSError, ValueError, RecursionError):
        raise WorkerError("invalid_request") from None


def atomic_json(path, document):
    data = json.dumps(document, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(data) > MAX_RESULT:
        raise WorkerError("invalid_subtitles")
    no_links(path.parent)
    temporary = path.parent / ("." + path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temporary.open("xb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def result_document(identifier, state, stage=None, percent=0, english=None, romanian=None, error=None):
    return {"version": 1, "id": identifier, "state": state, "stage": stage or state,
            "percent": max(0, min(100, int(percent))), "englishSrt": english,
            "romanianSrt": romanian, "errorCode": error}


def run_bounded(arguments, *, timeout, max_output=1024 * 1024, pass_fds=()):
    """No shell, bounded captured stdout, no unbounded/private decoder stderr."""
    kwargs = {"pass_fds": pass_fds} if os.name == "posix" else {}
    with tempfile.TemporaryFile() as output:
        process = subprocess.Popen(guarded_command(arguments), stdin=subprocess.DEVNULL, stdout=output,
                                   stderr=subprocess.DEVNULL, **kwargs)
        deadline = time.monotonic() + timeout
        try:
            while process.poll() is None:
                if time.monotonic() > deadline:
                    raise WorkerError("timeout")
                if os.fstat(output.fileno()).st_size > max_output:
                    raise WorkerError("probe_failed")
                time.sleep(.025)
            if process.returncode != 0:
                raise WorkerError("process_failed")
            output.seek(0)
            data = output.read(max_output + 1)
            if len(data) > max_output:
                raise WorkerError("probe_failed")
            return data
        finally:
            if process.poll() is None:
                process.kill()
            process.wait()


def choose_audio(probe):
    audio = [stream for stream in probe.get("streams", []) if stream.get("codec_type") == "audio"
             and type(stream.get("index")) is int and stream["index"] >= 0]
    if not audio:
        raise WorkerError("no_audio")
    return min(audio, key=lambda stream: (not stream.get("disposition", {}).get("default", 0), stream["index"]))


def probe_video(source, config, pass_fds=()):
    try:
        output = run_bounded(["ffprobe", "-v", "error", "-protocol_whitelist", "file,pipe",
                              "-format_whitelist", "matroska,webm,mov,avi", "-show_entries",
                              "format=duration:stream=index,codec_type:stream_disposition=default",
                              "-of", "json", source], timeout=config.probe_timeout, pass_fds=pass_fds)
        probe = json.loads(output)
        stream = choose_audio(probe)
        duration = float(probe.get("format", {}).get("duration", 0))
        if not math.isfinite(duration) or duration <= 0:
            raise WorkerError("probe_failed")
        if duration > config.max_duration:
            raise WorkerError("media_too_long")
        return stream, duration
    except WorkerError as error:
        if str(error) in {"no_audio", "media_too_long", "timeout"}:
            raise
        raise WorkerError("probe_failed") from None
    except (OSError, ValueError, TypeError):
        raise WorkerError("probe_failed") from None


def extract_audio(source, destination, stream, duration, config, pass_fds=()):
    expected_size = math.ceil(duration * 32000) + 1024 * 1024
    if shutil.disk_usage(destination.parent).free < expected_size + 64 * 1024 * 1024:
        raise WorkerError("insufficient_space")
    try:
        run_bounded(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
                     "-protocol_whitelist", "file,pipe", "-format_whitelist", "matroska,webm,mov,avi",
                     "-threads", str(config.threads), "-i", source, "-map", f"0:{stream['index']}",
                     "-vn", "-sn", "-dn", "-t", str(duration + 1), "-ac", "1", "-ar", "16000",
                     "-c:a", "pcm_s16le", "-threads", str(config.threads), "-fs", str(expected_size),
                     str(destination)], timeout=config.extract_timeout, pass_fds=pass_fds)
        if destination.stat().st_size < 44 or destination.stat().st_size >= expected_size:
            raise WorkerError("extraction_failed")
    except WorkerError as error:
        if str(error) == "timeout":
            raise
        raise WorkerError("extraction_failed") from None
    except OSError:
        raise WorkerError("extraction_failed") from None


@dataclass(frozen=True)
class Cue:
    start: float
    end: float
    text: str


def clean_text(text):
    return " ".join("".join(char for char in text if char.isprintable() or char.isspace()).split())


def timestamp(milliseconds):
    seconds, ms = divmod(milliseconds, 1000)
    minutes, seconds = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)
    return f"{hours:02}:{minutes:02}:{seconds:02},{ms:03}"


def split_text(text):
    middle = len(text) // 2
    spaces = [index for index, char in enumerate(text) if char == " " and 0 < index < len(text) - 1]
    split = min(spaces, key=lambda value: abs(value - middle)) if spaces else middle
    return text[:split].strip(), text[split:].strip()


def render_subtitles(cues, translations=None):
    if not cues or len(cues) > MAX_CUES or (translations is not None and len(translations) != len(cues)):
        raise WorkerError("invalid_subtitles")
    output = []

    def append(start, end, english, romanian):
        en_lines = textwrap.wrap(english, width=42, break_long_words=True, break_on_hyphens=False)
        ro_lines = textwrap.wrap(romanian, width=42, break_long_words=True, break_on_hyphens=False) if romanian else []
        if max(len(en_lines), len(ro_lines)) > 2:
            if end - start < 2 or len(english) < 2 or (romanian is not None and len(romanian) < 2):
                raise WorkerError("invalid_subtitles")
            en_left, en_right = split_text(english)
            ro_left, ro_right = split_text(romanian) if romanian else (None, None)
            midpoint = (start + end) // 2
            append(start, midpoint, en_left, ro_left)
            append(midpoint, end, en_right, ro_right)
        else:
            if not en_lines or (romanian is not None and not ro_lines) or len(output) >= MAX_CUES:
                raise WorkerError("invalid_subtitles")
            output.append((start, end, "\n".join(en_lines), "\n".join(ro_lines)))

    previous_end = 0
    for index, cue in enumerate(cues):
        if not math.isfinite(cue.start) or not math.isfinite(cue.end):
            raise WorkerError("invalid_subtitles")
        start, end = round(cue.start * 1000), round(cue.end * 1000)
        if start < previous_end or end <= start or start < 0 or end >= 100 * 3600 * 1000:
            raise WorkerError("invalid_subtitles")
        english = clean_text(cue.text)
        romanian = clean_text(translations[index]) if translations is not None else None
        append(start, end, english, romanian)
        previous_end = end
    english_srt, romanian_srt = [], []
    for index, (start, end, en_text, ro_text) in enumerate(output, 1):
        # Add reading time after splitting captions, without moving their starts
        # or letting either language overlap the next displayed caption.
        next_start = output[index][0] if index < len(output) else 100 * 3600 * 1000 - 1
        end = min(end + 1000, next_start)
        header = f"{index}\n{timestamp(start)} --> {timestamp(end)}\n"
        english_srt.append(header + en_text + "\n\n")
        romanian_srt.append(header + ro_text + "\n\n")
    english, romanian = "".join(english_srt), "".join(romanian_srt) if translations is not None else None
    if len(english.encode("utf-8")) > MAX_RESULT // 2 or (romanian and len(romanian.encode("utf-8")) > MAX_RESULT // 2):
        raise WorkerError("invalid_subtitles")
    return english, romanian


def offline_environment():
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"


def load_whisper(config):
    path = config.models / "whisper-small"
    if not ModelReadiness(config.models).valid("whisper-small"):
        raise WorkerError("models_missing")
    from faster_whisper import WhisperModel
    return WhisperModel(str(path), device="cpu", compute_type="int8", cpu_threads=config.threads,
                        num_workers=1, local_files_only=True)


def transcribe_english(model, audio, duration, progress):
    # Word timing restores each cue end to its last spoken word after VAD removes
    # silence. Segment-only timing can map that end to the next speech chunk.
    segments, info = model.transcribe(str(audio), task="transcribe", beam_size=5,
                                      vad_filter=True, word_timestamps=True,
                                      condition_on_previous_text=False)
    if info.language != "en":
        segments, _ = model.transcribe(str(audio), task="translate", language=info.language,
                                       beam_size=5, vad_filter=True, word_timestamps=True,
                                       condition_on_previous_text=False)
    cues = []
    for segment in segments:
        if len(cues) >= MAX_CUES:
            raise WorkerError("invalid_subtitles")
        if clean_text(segment.text):
            cues.append(Cue(segment.start, segment.end, segment.text))
        progress("transcribing", 15 + min(60, int(60 * segment.end / duration)))
    return cues


def translate_romanian(cues, config, progress):
    path = config.models / "marian-en-ro"
    if not ModelReadiness(config.models).valid("marian-en-ro"):
        raise WorkerError("models_missing")
    import torch
    from transformers import MarianMTModel, MarianTokenizer
    torch.set_num_threads(config.threads)
    tokenizer = MarianTokenizer.from_pretrained(str(path), local_files_only=True)
    model = MarianMTModel.from_pretrained(str(path), local_files_only=True, weights_only=True).eval()
    translated = []
    with torch.inference_mode():
        for index, cue in enumerate(cues):
            # Split before translation to avoid silently truncating long source segments.
            parts = textwrap.wrap(clean_text(cue.text), width=200, break_long_words=True)
            pieces = []
            for part in parts:
                tokens = tokenizer(part, return_tensors="pt", truncation=False)
                if tokens["input_ids"].shape[-1] > 256:
                    raise WorkerError("translation_failed")
                generated = model.generate(**tokens, max_new_tokens=256, num_beams=4)
                if generated.shape[-1] >= 257:
                    raise WorkerError("translation_failed")
                pieces.append(tokenizer.decode(generated[0], skip_special_tokens=True))
            translated.append(" ".join(pieces))
            progress("translating", 76 + int(20 * (index + 1) / len(cues)))
    return translated


def execute(request, config, temp, progress):
    audio = temp / "audio.wav"
    stage = "probing"
    try:
        offline_environment()
        path = validate_video(request["videoPath"], config.roots)
        progress(stage, 1)
        with opened_video(path) as (source, descriptors):
            stream, duration = probe_video(source, config, descriptors)
            stage = "extracting"
            progress(stage, 5)
            extract_audio(source, audio, stream, duration, config, descriptors)
        stage = "transcribing"
        progress(stage, 15)
        cues = transcribe_english(load_whisper(config), audio, duration, progress)
        # Validate English even if Romanian alone will ultimately be installed.
        render_subtitles(cues)
        translations = None
        if request["romanian"]:
            stage = "translating"
            progress(stage, 76)
            translations = translate_romanian(cues, config, progress)
        stage = "validating"
        progress(stage, 98)
        english, romanian = render_subtitles(cues, translations)
        result = result_document(request["id"], "completed", percent=100, english=english, romanian=romanian)
        if len(json.dumps(result, ensure_ascii=False).encode("utf-8")) > MAX_RESULT:
            raise WorkerError("invalid_subtitles")
        return result
    except WorkerError as error:
        return result_document(request["id"], "failed", error=str(error))
    except Exception:
        error = {"transcribing": "transcription_failed", "translating": "translation_failed"}.get(stage, "worker_failed")
        return result_document(request["id"], "failed", error=error)
    finally:
        audio.unlink(missing_ok=True)


def execute_repair(request, config, temp, progress):
    audio = temp / "audio.wav"
    stage = "probing"
    try:
        offline_environment()
        if not subtitle_sync.available():
            raise WorkerError("alignment_unavailable")
        intervals = subtitle_sync.cue_intervals(request["subtitleSrt"])
        path = validate_video(request["videoPath"], config.roots)
        progress(stage, 1)
        with opened_video(path) as (source, descriptors):
            stream, duration = probe_video(source, config, descriptors)
            stage = "extracting"
            progress(stage, 5)
            extract_audio(source, audio, stream, duration, config, descriptors)
        stage = "synchronizing"
        progress(stage, 15)
        offset, scale = subtitle_sync.estimate(audio, intervals)
        if (type(offset) is not int or abs(offset) > subtitle_sync.MAX_OFFSET_MS or not math.isfinite(scale)
                or not subtitle_sync.MIN_SCALE <= scale <= subtitle_sync.MAX_SCALE):
            raise WorkerError("synchronization_failed")
        return (result_document(request["id"], "completed", percent=100)
                | {"offsetMilliseconds": offset, "timeScale": scale})
    except WorkerError as error:
        return result_document(request["id"], "failed", error=str(error))
    except subtitle_sync.DependencyUnavailable:
        return result_document(request["id"], "failed", error="alignment_unavailable")
    except Exception:
        error = "synchronization_failed" if stage == "synchronizing" else "worker_failed"
        return result_document(request["id"], "failed", error=error)
    finally:
        audio.unlink(missing_ok=True)


def guarded_command(arguments):
    if sys.platform != "linux":
        return arguments
    # Protect the model child and each decoder without running Python preexec_fn
    # in the supervisor, which has a background model-integrity scanner thread.
    return [sys.executable, str(Path(__file__).resolve().with_name("process_launcher.py")),
            str(os.getpid()), *arguments]


def attach_windows_job(process):
    """Attach a suspended child before resuming it; descendants cannot escape the job."""
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    class BasicLimits(ctypes.Structure):
        _fields_ = [("process_time", ctypes.c_longlong), ("job_time", ctypes.c_longlong),
                    ("flags", wintypes.DWORD), ("minimum", ctypes.c_size_t), ("maximum", ctypes.c_size_t),
                    ("processes", wintypes.DWORD), ("affinity", ctypes.c_size_t),
                    ("priority", wintypes.DWORD), ("scheduling", wintypes.DWORD)]
    class ExtendedLimits(ctypes.Structure):
        _fields_ = [("basic", BasicLimits), ("io", ctypes.c_ulonglong * 6),
                    ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
                    ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]
    class ThreadEntry(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD), ("thread_id", wintypes.DWORD),
                    ("process_id", wintypes.DWORD), ("base_priority", wintypes.LONG),
                    ("delta_priority", wintypes.LONG), ("flags", wintypes.DWORD)]
    signatures = {
        "CreateJobObjectW": ([ctypes.c_void_p, wintypes.LPCWSTR], wintypes.HANDLE),
        "SetInformationJobObject": ([wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD], wintypes.BOOL),
        "AssignProcessToJobObject": ([wintypes.HANDLE, wintypes.HANDLE], wintypes.BOOL),
        "TerminateJobObject": ([wintypes.HANDLE, wintypes.UINT], wintypes.BOOL),
        "CloseHandle": ([wintypes.HANDLE], wintypes.BOOL),
        "CreateToolhelp32Snapshot": ([wintypes.DWORD, wintypes.DWORD], wintypes.HANDLE),
        "Thread32First": ([wintypes.HANDLE, ctypes.POINTER(ThreadEntry)], wintypes.BOOL),
        "Thread32Next": ([wintypes.HANDLE, ctypes.POINTER(ThreadEntry)], wintypes.BOOL),
        "OpenThread": ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
        "ResumeThread": ([wintypes.HANDLE], wintypes.DWORD),
    }
    for name, (arguments, returns) in signatures.items():
        function = getattr(kernel, name)
        function.argtypes, function.restype = arguments, returns
    job = kernel.CreateJobObjectW(None, None)
    try:
        if not job:
            raise ctypes.WinError(ctypes.get_last_error())
        limits = ExtendedLimits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not kernel.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            raise ctypes.WinError(ctypes.get_last_error())
        if not kernel.AssignProcessToJobObject(job, int(process._handle)):
            raise ctypes.WinError(ctypes.get_last_error())
        snapshot = kernel.CreateToolhelp32Snapshot(0x4, 0)  # TH32CS_SNAPTHREAD
        if snapshot == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            entry = ThreadEntry()
            entry.size = ctypes.sizeof(entry)
            found = False
            more = kernel.Thread32First(snapshot, ctypes.byref(entry))
            while more:
                if entry.process_id == process.pid:
                    thread = kernel.OpenThread(0x2, False, entry.thread_id)  # THREAD_SUSPEND_RESUME
                    if not thread:
                        raise ctypes.WinError(ctypes.get_last_error())
                    try:
                        if kernel.ResumeThread(thread) == 0xFFFFFFFF:
                            raise ctypes.WinError(ctypes.get_last_error())
                        found = True
                    finally:
                        kernel.CloseHandle(thread)
                more = kernel.Thread32Next(snapshot, ctypes.byref(entry))
            if not found:
                raise OSError("Worker primary thread unavailable")
        finally:
            kernel.CloseHandle(snapshot)
        process._subtitle_job = (kernel, job)
    except BaseException:
        if job:
            kernel.CloseHandle(job)
        process.kill()
        process.wait(timeout=10)
        raise


def kill_tree(process):
    if os.name == "posix":
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    else:
        kernel, job = process._subtitle_job
        try:
            if not kernel.TerminateJobObject(job, 1):
                import ctypes
                raise ctypes.WinError(ctypes.get_last_error())
        finally:
            kernel.CloseHandle(job)
    if process.poll() is None:
        process.kill()
    process.wait(timeout=10)


class Supervisor:
    def __init__(self, config):
        self.config = config
        self.requests = config.shared / "requests"
        self.results = config.shared / "results"
        self.repair_requests = config.shared / "repair-requests"
        self.repair_results = config.shared / "repair-results"
        self.stop = threading.Event()
        self.model_availability = (False, False)
        self.model_readiness = ModelReadiness(config.models, self.invalidate_models)
        self.next_heartbeat = 0
        for directory in (self.requests, self.results, self.repair_requests, self.repair_results, config.temp):
            directory.mkdir(parents=True, exist_ok=True)
            no_links(directory)

    def invalidate_models(self):
        self.model_availability = (False, False)

    def scan_models(self):
        while not self.stop.is_set():
            try:
                self.model_availability = self.model_readiness.snapshot()
            except Exception:
                # Keep the supervisor usable if model setup is incomplete or invalid.
                self.model_availability = (False, False)
            self.stop.wait(HEARTBEAT_INTERVAL)

    def publish_status(self):
        if time.monotonic() < self.next_heartbeat:
            return
        english, romanian = self.model_availability
        updated = datetime.now(timezone.utc).isoformat()
        atomic_json(self.config.shared / "status.json", {
            "version": 1, "updatedAt": updated,
            "englishAvailable": english, "romanianAvailable": romanian})
        atomic_json(self.config.shared / "repair-status.json", {
            "version": 1, "updatedAt": updated, "synchronizationAvailable": subtitle_sync.available()})
        self.next_heartbeat = time.monotonic() + HEARTBEAT_INTERVAL

    def child_command(self, request_path, temp):
        return [sys.executable, str(Path(__file__).resolve()), "child", str(request_path), str(temp)]

    def read_result(self, path, identifier):
        if not path.exists():
            return None
        value = read_json(path, MAX_RESULT)
        if not isinstance(value, dict) or value.get("version") != 1 or value.get("id") != identifier:
            raise WorkerError("worker_failed")
        return value

    def run_once(self):
        self.publish_status()
        self.run_queue(self.requests, self.results, validate_request, MAX_REQUEST)
        if not self.stop.is_set():
            self.run_queue(self.repair_requests, self.repair_results, validate_repair_request, MAX_REPAIR_REQUEST)

    def run_queue(self, requests, results, validate, limit):
        for request_path in sorted(requests.glob("*.json")):
            identifier = request_path.stem
            if not ID.fullmatch(identifier):
                continue
            result_path = results / request_path.name
            cancel = requests / (identifier + ".cancel")
            try:
                existing = self.read_result(result_path, identifier)
                if existing and existing.get("state") in TERMINAL:
                    continue
                if self.stop.is_set() or cancel.exists():
                    atomic_json(result_path, result_document(identifier, "cancelled"))
                    continue
                request = validate(read_json(request_path, limit), identifier)
                validate_video(request["videoPath"], self.config.roots)
            except WorkerError as error:
                # API cleanup removes the request before its terminal result. A
                # previously enumerated, now-consumed request needs no new result.
                if not request_path.exists():
                    continue
                atomic_json(result_path, result_document(identifier, "failed", error=str(error)))
                continue
            atomic_json(result_path, result_document(identifier, "running", "starting"))
            with tempfile.TemporaryDirectory(prefix="job-" + identifier + "-", dir=self.config.temp) as temporary:
                kwargs = {"start_new_session": True} if os.name == "posix" else {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP | 0x4}
                try:
                    process = subprocess.Popen(guarded_command(self.child_command(request_path, Path(temporary))), stdin=subprocess.DEVNULL,
                                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **kwargs)
                    if os.name == "nt":
                        attach_windows_job(process)
                except OSError:
                    process = None
                deadline = time.monotonic() + self.config.job_timeout
                cancelled = timed_out = False
                try:
                    while process is not None and process.poll() is None:
                        self.publish_status()
                        cancelled = self.stop.is_set() or cancel.exists() or not request_path.exists()
                        timed_out = time.monotonic() > deadline
                        if cancelled or timed_out:
                            break
                        self.stop.wait(self.config.poll_seconds)
                finally:
                    if process is not None:
                        kill_tree(process)
                cancelled = cancelled or self.stop.is_set() or cancel.exists()
                if cancelled:
                    final = result_document(identifier, "cancelled")
                elif timed_out:
                    final = result_document(identifier, "failed", error="timeout")
                elif process is not None:
                    try:
                        final = self.read_result(Path(temporary) / "completed.json", identifier)
                    except WorkerError:
                        final = None
                    if not final or final.get("state") not in TERMINAL:
                        final = result_document(identifier, "failed", error="worker_failed")
                else:
                    final = result_document(identifier, "failed", error="worker_failed")
            # Publish terminal state after process-tree termination and temp cleanup.
            atomic_json(result_path, final)
            if self.stop.is_set():
                return

    def serve(self):
        # An OS-held lock releases on crash. A second supervisor cannot run the same mailbox.
        lock_path = self.config.shared / "worker.lock"
        if lock_path.exists():
            no_links(lock_path)
        with lock_path.open("a+b") as lock:
            if os.name == "posix":
                import fcntl
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            else:
                import msvcrt
                lock.write(b"0")
                lock.flush()
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            # Only our private, strictly named job directories can be removed after restart.
            for directory in self.config.temp.iterdir():
                if re.fullmatch(r"job-[a-f0-9]{32}-[a-z0-9_]+", directory.name):
                    no_links(directory)
                    if directory.is_dir():
                        shutil.rmtree(directory)
            try:
                self.publish_status()
                scanner = threading.Thread(target=self.scan_models, name="subtitle-model-readiness", daemon=True)
                scanner.start()
                while not self.stop.is_set():
                    self.run_once()
                    self.stop.wait(self.config.poll_seconds)
            finally:
                self.stop.set()
                (self.config.shared / "status.json").unlink(missing_ok=True)
                (self.config.shared / "repair-status.json").unlink(missing_ok=True)


def healthcheck(shared):
    """Check the serving loop heartbeat without loading or requiring models."""
    try:
        value = read_json(shared / "status.json", 4096)
        if (not isinstance(value, dict) or set(value) != {"version", "updatedAt", "englishAvailable", "romanianAvailable"}
                or type(value["version"]) is not int or value["version"] != 1
                or type(value["englishAvailable"]) is not bool or type(value["romanianAvailable"]) is not bool
                or (value["romanianAvailable"] and not value["englishAvailable"])):
            return False
        updated = datetime.fromisoformat(value["updatedAt"])
        if updated.utcoffset() is None or updated.utcoffset().total_seconds() != 0:
            return False
        age = (datetime.now(timezone.utc) - updated).total_seconds()
        return -5 <= age <= 30
    except (WorkerError, ValueError, TypeError, KeyError):
        return False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("serve", "child", "healthcheck"), nargs="?", default="serve")
    parser.add_argument("request", nargs="?")
    parser.add_argument("temp", nargs="?")
    args = parser.parse_args()
    if args.command == "healthcheck":
        raise SystemExit(0 if healthcheck(Path(os.environ.get("SUBTITLE_SHARED_PATH", "/subtitle-work"))) else 1)
    offline_environment()
    config = Config.from_env()
    if args.command == "child":
        request_path = Path(args.request)
        if request_path.parent == config.shared / "repair-requests":
            request = validate_repair_request(read_json(request_path, MAX_REPAIR_REQUEST), request_path.stem)
            output = config.shared / "repair-results" / request_path.name
            execute_job = execute_repair
        else:
            request = validate_request(read_json(request_path), request_path.stem)
            output = config.shared / "results" / request_path.name
            execute_job = execute
        last_progress = None
        def progress(stage, percent):
            nonlocal last_progress
            if (stage, percent) != last_progress:
                atomic_json(output, result_document(request["id"], "running", stage, percent))
                last_progress = (stage, percent)
        atomic_json(Path(args.temp) / "completed.json", execute_job(request, config, Path(args.temp), progress))
    else:
        supervisor = Supervisor(config)
        signal.signal(signal.SIGTERM, lambda *_: supervisor.stop.set())
        signal.signal(signal.SIGINT, lambda *_: supervisor.stop.set())
        supervisor.serve()


if __name__ == "__main__":
    main()
