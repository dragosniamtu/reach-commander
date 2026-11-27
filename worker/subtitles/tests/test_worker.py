"""Offline contract tests: no model downloads, media is confined to temporary fixtures."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch

WORKER = Path(__file__).resolve().parents[1] / "worker.py"
sys.path.insert(0, str(WORKER.parent))
spec = importlib.util.spec_from_file_location("subtitle_worker", WORKER)
worker = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = worker
if WORKER.exists():
    spec.loader.exec_module(worker)


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.media = self.root / "media"
        self.media.mkdir()
        self.video = self.media / "Film ș ! & ' $(safe).mkv"
        self.video.write_bytes(b"fixture video")
        self.request = {"version": 1, "id": "a" * 32,
                        "videoPath": str(self.video), "romanian": False}

    def test_request_validates_identity_version_and_exact_types(self):
        self.assertEqual(worker.validate_request(self.request, "a" * 32), self.request)
        for changes in ({"version": 2}, {"version": True}, {"romanian": "yes"},
                        {"id": "b" * 32}, {"videoPath": "../movie.mkv"}, {"extra": 1}):
            with self.subTest(changes=changes), self.assertRaises(worker.WorkerError):
                worker.validate_request(self.request | changes, "a" * 32)

    def test_media_path_requires_allowlisted_regular_video(self):
        self.assertEqual(worker.validate_video(str(self.video), [self.media]), self.video)
        outsider = self.root / "outside.mkv"
        outsider.write_bytes(b"private")
        for value in (str(outsider), str(self.media / ".." / "outside.mkv"),
                      str(self.media), str(self.media / "missing.mkv")):
            with self.subTest(value=value), self.assertRaises(worker.WorkerError):
                worker.validate_video(value, [self.media])

    def test_rejects_file_and_directory_symlinks(self):
        linked = self.media / "linked.mkv"
        try:
            linked.symlink_to(self.video)
        except OSError:
            self.skipTest("Symlinks require Windows Developer Mode; exercised by Linux CI")
        with self.assertRaises(worker.WorkerError):
            worker.validate_video(str(linked), [self.media])
        directory = self.media / "linked-dir"
        directory.symlink_to(self.media, target_is_directory=True)
        with self.assertRaises(worker.WorkerError):
            worker.validate_video(str(directory / self.video.name), [self.media])

    def test_default_audio_track_wins_and_audio_less_is_rejected(self):
        streams = [{"index": 0, "codec_type": "video"},
                   {"index": 2, "codec_type": "audio", "disposition": {"default": 0}},
                   {"index": 4, "codec_type": "audio", "disposition": {"default": 1}}]
        self.assertEqual(worker.choose_audio({"streams": streams})["index"], 4)
        self.assertEqual(worker.choose_audio({"streams": streams[:2]})["index"], 2)
        with self.assertRaisesRegex(worker.WorkerError, "no_audio"):
            worker.choose_audio({"streams": streams[:1]})

    def test_srt_is_utf8_ordered_two_lines_and_bilingual_timing_matches(self):
        long_text = "A reasonably long sentence containing several words to make readable subtitle lines. " * 4
        cues = [worker.Cue(0.125, 14.5, long_text), worker.Cue(15, 17, "Hello!")]
        english, romanian = worker.render_subtitles(cues, ["O propoziție cu diacritice ș ț ă. " * 9, "Bună!"])
        self.assertIn("00:00:00,125", english)
        self.assertIn("Bună!", romanian)
        english_blocks, romanian_blocks = english.strip().split("\n\n"), romanian.strip().split("\n\n")
        self.assertEqual(len(english_blocks), len(romanian_blocks))
        for en, ro in zip(english_blocks, romanian_blocks):
            self.assertEqual(en.splitlines()[1], ro.splitlines()[1])
            for block in (en, ro):
                self.assertLessEqual(len(block.splitlines()[2:]), 2)
                self.assertTrue(all(0 < len(line) <= 42 for line in block.splitlines()[2:]))

    def test_empty_nonfinite_and_unordered_cues_fail(self):
        for cues in ([], [worker.Cue(0, 1, " ")], [worker.Cue(2, 1, "bad")],
                     [worker.Cue(float("nan"), 2, "bad")],
                     [worker.Cue(2, 4, "first"), worker.Cue(1, 2, "second")]):
            with self.subTest(cues=cues), self.assertRaises(worker.WorkerError):
                worker.render_subtitles(cues)

    def test_english_transcribes_and_nonenglish_translates_into_english(self):
        for language, task in (("en", "transcribe"), ("fr", "translate")):
            calls = []
            class FakeModel:
                def transcribe(self, audio, **kwargs):
                    calls.append(kwargs)
                    return iter([SimpleNamespace(start=0, end=1, text="Hello")]), SimpleNamespace(language=language)
            cues = worker.transcribe_english(FakeModel(), "audio.wav", 1, lambda *_: None)
            self.assertEqual(cues[0].text, "Hello")
            self.assertEqual(calls[-1]["task"], task)
            self.assertIsNone(calls[0].get("language"))
            self.assertTrue(all(call.get("word_timestamps") is True for call in calls),
                            "VAD-mapped segment ends must follow words, not the next speech chunk")

    def test_process_failure_does_not_touch_media_and_cleans_audio(self):
        original = self.media / "Film.srt"
        original.write_text("existing", encoding="utf-8")
        config = worker.Config(self.root / "mail", (self.media,), self.root / "models", self.root / "tmp")
        config.temp.mkdir()
        with patch.object(worker, "probe_video", return_value=({"index": 1}, 4)), \
             patch.object(worker, "extract_audio", side_effect=lambda *args: Path(args[1]).write_bytes(b"audio")), \
             patch.object(worker, "load_whisper", side_effect=RuntimeError("private model failure")):
            result = worker.execute(self.request, config, config.temp, lambda *_: None)
        self.assertEqual(result["state"], "failed")
        self.assertEqual(result["errorCode"], "transcription_failed")
        self.assertEqual(original.read_text(), "existing")
        self.assertEqual(self.video.read_bytes(), b"fixture video")
        self.assertFalse((config.temp / "audio.wav").exists())

    def test_audio_less_request_never_loads_model(self):
        config = worker.Config(self.root / "mail", (self.media,), self.root / "models", self.root / "tmp")
        config.temp.mkdir()
        with patch.object(worker, "probe_video", side_effect=worker.WorkerError("no_audio")), \
             patch.object(worker, "load_whisper") as model:
            result = worker.execute(self.request, config, config.temp, lambda *_: None)
        self.assertEqual(result["errorCode"], "no_audio")
        model.assert_not_called()

    def test_precancel_and_terminal_results_do_not_spawn(self):
        supervisor = self.supervisor()
        path = supervisor.requests / (self.request["id"] + ".json")
        worker.atomic_json(path, self.request)
        (supervisor.requests / (self.request["id"] + ".cancel")).touch()
        with patch.object(subprocess, "Popen") as process:
            supervisor.run_once()
            supervisor.run_once()
        process.assert_not_called()
        self.assertEqual(worker.read_json(supervisor.results / path.name)["state"], "cancelled")

    def test_invalid_request_produces_bounded_safe_error(self):
        supervisor = self.supervisor()
        path = supervisor.requests / (self.request["id"] + ".json")
        path.write_text(json.dumps(self.request | {"videoPath": "relative"}), encoding="utf-8")
        supervisor.run_once()
        result = worker.read_json(supervisor.results / path.name)
        self.assertEqual(result["errorCode"], "invalid_request")
        self.assertIsNone(result["englishSrt"])
        self.assertNotIn("videoPath", result)

    def test_duplicate_json_keys_and_large_requests_fail(self):
        path = self.root / "bad.json"
        path.write_text('{"id": 1, "id": 2}', encoding="utf-8")
        with self.assertRaises(worker.WorkerError):
            worker.read_json(path)
        path.write_bytes(b" " * (worker.MAX_REQUEST + 1))
        with self.assertRaises(worker.WorkerError):
            worker.read_json(path)

    def test_cancellation_terminates_child_and_cleans_temporary_files(self):
        supervisor = self.supervisor()
        request_path = supervisor.requests / (self.request["id"] + ".json")
        worker.atomic_json(request_path, self.request)
        script = "import pathlib,time,sys; pathlib.Path(sys.argv[1]).write_text('temporary'); time.sleep(60)"
        def command(request_path, temp):
            return [sys.executable, "-c", script, str(temp / "audio.wav")]
        with patch.object(supervisor, "child_command", side_effect=command):
            thread = threading.Thread(target=supervisor.run_once)
            thread.start()
            deadline = time.monotonic() + 5
            while not list(supervisor.config.temp.glob("*/audio.wav")) and time.monotonic() < deadline:
                time.sleep(.02)
            self.assertTrue(list(supervisor.config.temp.glob("*/audio.wav")))
            (supervisor.requests / (self.request["id"] + ".cancel")).touch()
            thread.join(10)
            self.assertFalse(thread.is_alive())
        self.assertEqual(worker.read_json(supervisor.results / request_path.name)["state"], "cancelled")
        self.assertEqual(list(supervisor.config.temp.iterdir()), [])

    def test_subprocess_output_and_runtime_are_bounded(self):
        with self.assertRaises(worker.WorkerError):
            worker.run_bounded([sys.executable, "-c", "print('x'*10000)"], timeout=2, max_output=100)
        with self.assertRaisesRegex(worker.WorkerError, "timeout"):
            worker.run_bounded([sys.executable, "-c", "import time; time.sleep(30)"], timeout=.1)

    def test_success_publishes_terminal_only_after_child_has_exited(self):
        supervisor = self.supervisor()
        request_path = supervisor.requests / (self.request["id"] + ".json")
        worker.atomic_json(request_path, self.request)
        final = worker.result_document(self.request["id"], "completed", percent=100,
                                       english="1\n00:00:00,000 --> 00:00:01,000\nHello\n\n")
        script = "import pathlib,json,time,sys; pathlib.Path(sys.argv[1]).write_text(sys.argv[2]); time.sleep(.3)"
        def command(request_path, temp):
            return [sys.executable, "-c", script, str(temp / "completed.json"), json.dumps(final)]
        with patch.object(supervisor, "child_command", side_effect=command):
            thread = threading.Thread(target=supervisor.run_once)
            thread.start()
            deadline = time.monotonic() + 5
            while not list(supervisor.config.temp.glob("*/completed.json")) and time.monotonic() < deadline:
                time.sleep(.01)
            self.assertEqual(worker.read_json(supervisor.results / request_path.name)["state"], "running")
            thread.join(5)
            self.assertFalse(thread.is_alive())
        self.assertEqual(worker.read_json(supervisor.results / request_path.name)["state"], "completed")
        self.assertEqual(list(supervisor.config.temp.iterdir()), [])

    def test_mocked_pipeline_returns_requested_languages_without_media_writes(self):
        config = worker.Config(self.root / "mail", (self.media,), self.root / "models", self.root / "tmp")
        config.temp.mkdir()
        for romanian in (False, True):
            with patch.object(worker, "probe_video", return_value=({"index": 1}, 4)), \
                 patch.object(worker, "extract_audio", side_effect=lambda *args: Path(args[1]).write_bytes(b"audio")), \
                 patch.object(worker, "load_whisper"), \
                 patch.object(worker, "transcribe_english", return_value=[worker.Cue(0, 1, "Hello!")]), \
                 patch.object(worker, "translate_romanian", return_value=["Bună!"]):
                result = worker.execute(self.request | {"romanian": romanian}, config, config.temp, lambda *_: None)
            self.assertEqual(result["state"], "completed")
            self.assertIn("Hello!", result["englishSrt"])
            self.assertEqual(result["romanianSrt"] is not None, romanian)
            self.assertEqual(list(self.media.iterdir()), [self.video])
            self.assertEqual(list(config.temp.iterdir()), [])

    def test_child_timeout_is_terminal_and_cleans_temp(self):
        config = worker.Config(self.root / "mail", (self.media,), self.root / "models", self.root / "tmp",
                               job_timeout=.1, poll_seconds=.01)
        supervisor = worker.Supervisor(config)
        request_path = supervisor.requests / (self.request["id"] + ".json")
        worker.atomic_json(request_path, self.request)
        with patch.object(supervisor, "child_command", return_value=[sys.executable, "-c", "import time; time.sleep(60)"]):
            supervisor.run_once()
        final = worker.read_json(supervisor.results / request_path.name)
        self.assertEqual((final["state"], final["errorCode"]), ("failed", "timeout"))
        self.assertEqual(list(config.temp.iterdir()), [])

    def test_cancellation_kills_descendant_decoder(self):
        supervisor = self.supervisor()
        request_path = supervisor.requests / (self.request["id"] + ".json")
        worker.atomic_json(request_path, self.request)
        survived = self.root / "descendant-survived"
        ready = self.root / "descendant-started"
        descendant = "import pathlib,time,sys; pathlib.Path(sys.argv[1]).touch(); time.sleep(1); pathlib.Path(sys.argv[2]).touch()"
        script = "import subprocess,sys,time; subprocess.Popen([sys.executable,'-c',sys.argv[1],sys.argv[2],sys.argv[3]]); time.sleep(60)"
        command = [sys.executable, "-c", script, descendant, str(ready), str(survived)]
        with patch.object(supervisor, "child_command", return_value=command):
            thread = threading.Thread(target=supervisor.run_once)
            thread.start()
            deadline = time.monotonic() + 5
            while not ready.exists() and time.monotonic() < deadline:
                time.sleep(.02)
            self.assertTrue(ready.exists())
            (supervisor.requests / (self.request["id"] + ".cancel")).touch()
            thread.join(10)
            self.assertFalse(thread.is_alive())
        time.sleep(1.1)
        self.assertFalse(survived.exists(), "cancelled decoder subprocess was left running")
        self.assertEqual(list(supervisor.config.temp.iterdir()), [])

    def test_translation_failure_does_not_publish_partial_english(self):
        config = worker.Config(self.root / "mail", (self.media,), self.root / "models", self.root / "tmp")
        config.temp.mkdir()
        existing = self.media / "Film.ro.srt"
        existing.write_text("Original!", encoding="utf-8")
        with patch.object(worker, "probe_video", return_value=({"index": 1}, 4)), \
             patch.object(worker, "extract_audio", side_effect=lambda *args: Path(args[1]).write_bytes(b"audio")), \
             patch.object(worker, "load_whisper"), \
             patch.object(worker, "transcribe_english", return_value=[worker.Cue(0, 1, "Hello!")]), \
             patch.object(worker, "translate_romanian", side_effect=RuntimeError("failure with sensitive details")):
            result = worker.execute(self.request | {"romanian": True}, config, config.temp, lambda *_: None)
        self.assertEqual(result["errorCode"], "translation_failed")
        self.assertIsNone(result["englishSrt"])
        self.assertIsNone(result["romanianSrt"])
        self.assertEqual(existing.read_text(), "Original!")
        self.assertEqual(list(config.temp.iterdir()), [])

    def test_failed_child_does_not_stop_later_request_and_never_runs_concurrently(self):
        supervisor = self.supervisor()
        for identifier in ("a" * 32, "b" * 32):
            worker.atomic_json(supervisor.requests / (identifier + ".json"), self.request | {"id": identifier})
        order = []
        def command(request_path, temp):
            # The prior result must be terminal before the next child can be created.
            if order:
                self.assertEqual(worker.read_json(supervisor.results / (order[0] + ".json"))["state"], "failed")
            order.append(request_path.stem)
            return [sys.executable, "-c", "raise SystemExit(3)"]
        with patch.object(supervisor, "child_command", side_effect=command):
            supervisor.run_once()
        self.assertEqual(order, ["a" * 32, "b" * 32])
        self.assertEqual(list(supervisor.config.temp.iterdir()), [])

    def test_restart_cleans_only_private_job_directories(self):
        supervisor = self.supervisor()
        stale = supervisor.config.temp / ("job-" + "a" * 32 + "-stale_1")
        stale.mkdir()
        (stale / "audio.wav").write_bytes(b"old audio")
        unrelated = supervisor.config.temp / "unrelated"
        unrelated.mkdir()
        supervisor.stop.set()
        supervisor.serve()
        self.assertFalse(stale.exists())
        self.assertTrue(unrelated.exists())

    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "FFmpeg not installed")
    def test_real_ffmpeg_audio_probe_and_extraction_with_special_filename(self):
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=size=16x16:rate=1",
                        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000", "-t", "1",
                        "-c:v", "ffv1", "-c:a", "pcm_s16le", str(self.video)], check=True,
                       capture_output=True, timeout=15)
        before = self.video.read_bytes()
        config = worker.Config(self.root / "mail", (self.media,), self.root / "models", self.root / "tmp")
        config.temp.mkdir()
        output = config.temp / "audio.wav"
        with worker.opened_video(self.video) as (source, descriptors):
            stream, duration = worker.probe_video(source, config, descriptors)
            worker.extract_audio(source, output, stream, duration, config, descriptors)
        self.assertGreater(output.stat().st_size, 30000)
        self.assertEqual(self.video.read_bytes(), before)

    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "FFmpeg not installed")
    def test_real_ffprobe_audio_less_fixture(self):
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=size=16x16:rate=1",
                        "-t", "1", "-c:v", "ffv1", str(self.video)], check=True, capture_output=True, timeout=15)
        config = worker.Config(self.root / "mail", (self.media,), self.root / "models", self.root / "tmp")
        with worker.opened_video(self.video) as (source, descriptors):
            with self.assertRaisesRegex(worker.WorkerError, "no_audio"):
                worker.probe_video(source, config, descriptors)

    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "FFmpeg not installed")
    def test_real_mailbox_child_reports_missing_models_and_cleans_audio(self):
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=size=16x16:rate=1",
                        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000", "-t", "1",
                        "-c:v", "ffv1", "-c:a", "pcm_s16le", str(self.video)], check=True,
                       capture_output=True, timeout=15)
        supervisor = self.supervisor()
        request_path = supervisor.requests / (self.request["id"] + ".json")
        worker.atomic_json(request_path, self.request)
        with patch.dict(os.environ, {"SUBTITLE_SHARED_PATH": str(supervisor.config.shared),
                                    "SUBTITLE_MEDIA_ROOTS": json.dumps([str(self.media)]),
                                    "SUBTITLE_MODEL_PATH": str(supervisor.config.models),
                                    "SUBTITLE_TEMP_PATH": str(supervisor.config.temp)}):
            supervisor.run_once()
        final = worker.read_json(supervisor.results / request_path.name)
        self.assertEqual((final["state"], final["errorCode"]), ("failed", "models_missing"))
        self.assertEqual(list(supervisor.config.temp.iterdir()), [])
        self.assertEqual(list(self.media.iterdir()), [self.video])

    def supervisor(self):
        config = worker.Config(self.root / "mail", (self.media,), self.root / "models", self.root / "tmp", poll_seconds=.02)
        return worker.Supervisor(config)


if __name__ == "__main__":
    unittest.main()
