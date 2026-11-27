"""Run an offline FFmpeg flite fixture through the real repair mailbox."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import wave

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker" / "subtitles"))
import worker


def render_speech(directory, phrase):
    wav = directory / "phrase.wav"
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                    f"flite=text={phrase}", "-ar", "16000", "-ac", "1", str(wav)], check=True)
    with wave.open(str(wav), "rb") as stream:
        return stream.readframes(stream.getnframes())


def exercise(scale, offset):
    phrases = ["The motor is ready", "Open that window", "Please keep your ticket for the next train",
               "Are we almost there yet", "The ocean is bright and quiet this morning",
               "One more", "Bright green birds move across the river", "There is enough for everyone to share",
               "Please wait", "I will bring the book with me tomorrow", "Take a breath before you answer",
               "The lamp is warm", "A little further down the road we saw the snow begin to fall"]
    starts = [5, 10.3, 14.9, 21.4, 25, 31, 39.5, 45, 49, 53.1, 58, 61, 67]
    with tempfile.TemporaryDirectory(prefix="subtitle-sync-smoke-") as name:
        root = Path(name).resolve()
        mix = bytearray(75 * 16000 * 2)
        cues = []
        for index, (start, phrase) in enumerate(zip(starts, phrases, strict=True), 1):
            speech = render_speech(root, phrase)
            position = round(start * 16000) * 2
            mix[position:position + len(speech)] = speech
            target_start = round(start * 1000)
            target_end = target_start + round(len(speech) / 32)
            input_start = round((target_start - offset) / scale)
            input_end = round((target_end - offset) / scale)
            cues.append(f"{index}\n{worker.timestamp(input_start)} --> {worker.timestamp(input_end)}\n字幕 Ș\n\n")
        wav = root / "fixture.wav"
        with wave.open(str(wav), "wb") as stream:
            stream.setparams((1, 2, 16000, 0, "NONE", ""))
            stream.writeframes(mix)
        video = root / "fixture.mkv"
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(wav),
                        "-c:a", "pcm_s16le", str(video)], check=True)
        os.environ.update(SUBTITLE_SHARED_PATH=str(root / "mail"), SUBTITLE_MODEL_PATH=str(root / "models"),
                          SUBTITLE_TEMP_PATH=str(root / "temp"), SUBTITLE_MEDIA_ROOTS=json.dumps([str(root)]))
        config = worker.Config.from_env()
        supervisor = worker.Supervisor(config)
        identifier = "a" * 32
        request_path = supervisor.repair_requests / (identifier + ".json")
        worker.atomic_json(request_path, {"version": 2, "id": identifier, "kind": "synchronize",
                                         "videoPath": str(video), "subtitleSrt": "".join(cues)})
        supervisor.run_once()
        result = worker.read_json(supervisor.repair_results / request_path.name)
        print(f"Expected offset={offset} ms, scale={scale:.4f}. Result={result}")
        assert result["state"] == "completed", result
        assert abs(result["offsetMilliseconds"] - offset) <= 225, result
        assert abs(result["timeScale"] - scale) <= .004, result
        assert result["englishSrt"] is None and result["romanianSrt"] is None, result
        assert list(config.temp.iterdir()) == []
        assert not config.models.exists()

        # Use the same video path with silence to verify the real detector fails
        # the job rather than returning a plausible but random alignment.
        if offset < 0:
            with wave.open(str(wav), "wb") as stream:
                stream.setparams((1, 2, 16000, 0, "NONE", ""))
                stream.writeframes(bytes(len(mix)))
            subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(wav),
                            "-c:a", "pcm_s16le", str(video)], check=True)
            result_path = supervisor.repair_results / request_path.name
            result_path.unlink()
            supervisor.run_once()
            result = worker.read_json(result_path)
            print(f"Silent fixture result={result}")
            assert (result["state"], result["errorCode"]) == ("failed", "synchronization_failed"), result
            assert list(config.temp.iterdir()) == []


if __name__ == "__main__":
    exercise(1.04, -1800)
    exercise(.96, 1600)
