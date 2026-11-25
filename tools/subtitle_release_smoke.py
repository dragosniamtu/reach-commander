#!/usr/bin/env python3
"""Real-model release verification. No model downloads or mocked inference occur here."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import uuid


TIMING = re.compile(r"(\d{2}):(\d{2}):(\d{2}),(\d{3}) --> (\d{2}):(\d{2}):(\d{2}),(\d{3})")


def file_hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def parse_cues(text: str) -> list[tuple[int, int]]:
    assert isinstance(text, str) and text.strip(), "Empty subtitle output"
    cues = []
    previous = 0
    for index, block in enumerate(re.split(r"\n\s*\n", text.strip()), 1):
        lines = block.splitlines()
        assert 3 <= len(lines) <= 4 and lines[0] == str(index), "Invalid SRT cue structure"
        match = TIMING.fullmatch(lines[1])
        assert match, "Invalid timestamp"
        values = [int(value) for value in match.groups()]
        assert all(values[i] < 60 for i in (1, 2, 5, 6)), "Invalid timestamp component"
        start = ((values[0] * 60 + values[1]) * 60 + values[2]) * 1000 + values[3]
        end = ((values[4] * 60 + values[5]) * 60 + values[6]) * 1000 + values[7]
        assert previous <= start < end, "Overlapping or empty cue"
        assert all(line.strip() and len(line) <= 42 for line in lines[2:]), "Unreadable cue"
        previous = end
        cues.append((start, end))
    return cues


def validate_pair(english: str, romanian: str) -> None:
    assert parse_cues(english) == parse_cues(romanian), "Bilingual cue timings differ"
    assert english != romanian, "Translation did not change the English speech fixture"


def assert_preserved(video: Path, digest: str, original: bytes) -> None:
    assert file_hash(video) == digest, "Video content changed"
    backups = list(video.parent.glob(f"{video.stem}.existing-*.srt.bak"))
    assert len(backups) == 1 and backups[0].read_bytes() == original, "Original subtitle was not preserved exactly"


def wait_for(predicate, timeout: float = 180, interval: float = 0.5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(interval)
    raise TimeoutError("Subtitle release smoke exceeded its bounded wait")


def local_inference(models: Path, video: Path, output: Path) -> None:
    """Run the actual mailbox supervisor and child, useful when Docker is unavailable."""
    worker = Path(__file__).resolve().parents[1] / "worker" / "subtitles" / "worker.py"
    output.mkdir(parents=True, exist_ok=True)
    original_hash = file_hash(video)
    with tempfile.TemporaryDirectory(prefix="subtitle-release-", dir=output) as temporary:
        root = Path(temporary)
        shared = root / "mailbox"
        (shared / "requests").mkdir(parents=True)
        environment = os.environ | {
            "SUBTITLE_SHARED_PATH": str(shared), "SUBTITLE_MODEL_PATH": str(models),
            "SUBTITLE_TEMP_PATH": str(root / "temp"), "SUBTITLE_THREADS": "2",
            "SUBTITLE_MEDIA_ROOTS": json.dumps([str(video.parent)]),
            "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1",
        }
        with (output / "worker.log").open("w", encoding="utf-8") as log:
            process = subprocess.Popen([sys.executable, str(worker), "serve"], env=environment,
                                       stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                       creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            try:
                request_id = uuid.uuid4().hex
                request = {"version": 1, "id": request_id, "videoPath": str(video), "romanian": True}
                staged = shared / "requests" / f"{request_id}.tmp"
                staged.write_text(json.dumps(request), encoding="utf-8")
                staged.replace(staged.with_suffix(".json"))
                result_path = shared / "results" / f"{request_id}.json"

                def completed():
                    assert process.poll() is None, "Supervisor stopped before inference completed"
                    if not result_path.is_file():
                        return None
                    result = json.loads(result_path.read_text(encoding="utf-8"))
                    return result if result.get("state") in ("completed", "failed", "cancelled") else None

                started = time.monotonic()
                result = wait_for(completed, timeout=900)
                assert result["state"] == "completed", f"Inference failed: {result.get('errorCode')}"
                validate_pair(result["englishSrt"], result["romanianSrt"])
                assert file_hash(video) == original_hash, "Worker modified the video"
                (output / "speech.srt").write_text(result["englishSrt"], encoding="utf-8")
                (output / "speech.ro.srt").write_text(result["romanianSrt"], encoding="utf-8")
                (output / "result.json").write_text(json.dumps({
                    "state": result["state"], "seconds": round(time.monotonic() - started, 2),
                    "cues": len(parse_cues(result["englishSrt"])), "videoSha256": original_hash,
                    "model": "small", "device": "cpu", "threads": 2,
                }, indent=2) + "\n", encoding="utf-8")
                print("Real offline CPU transcription and Romanian translation passed.")
            finally:
                process.terminate()
                try:
                    process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)


def api_lifecycle(base_url: str, source: Path, container_source: str | None = None) -> None:
    sys.path.insert(0, str(Path(__file__).parent))
    from container_media_preview_smoke import SmokeClient, USERNAME, PASSWORD, assert_no_host_paths

    video = source / "speech.mp4"
    original = b"1\n00:00:00,000 --> 00:00:01,000\nOriginal subtitle canary.\n\n"
    subtitle = source / "speech.srt"
    subtitle.write_bytes(original)
    # Linux smoke is launched by root to inspect private container-owned fixtures.
    # Match the video's owner so the non-root API can read and preserve this canary.
    if os.name == "posix" and os.geteuid() == 0:
        owner = video.stat()
        os.chown(subtitle, owner.st_uid, owner.st_gid)
    original_hash = file_hash(video)
    client = SmokeClient(base_url)
    client.refresh_antiforgery()
    client.json_request("POST", "/api/auth/setup", {
        "setupCode": os.environ["REACHCOMMANDER_SMOKE_SETUP_CODE"], "username": USERNAME, "password": PASSWORD,
    }, antiforgery=True)
    client.refresh_antiforgery()
    wait_for(lambda: client.json_request("GET", "/api/subtitle-jobs/capabilities").get("romanianAvailable"))
    request = {"sourceId": "speech", "path": "/speech.mp4", "english": True, "romanian": True, "skipExisting": True}

    def generate():
        created = client.json_request("POST", "/api/subtitle-jobs", request, expected=(202,), antiforgery=True)
        def terminal():
            status = client.json_request("GET", f"/api/subtitle-jobs/{created['jobId']}")
            return status if status["phase"] in ("completed", "failed", "cancelled", "interrupted") else None
        status = wait_for(terminal, timeout=900)
        assert status["phase"] == "completed", f"Job failed: {status.get('error')}"
        return status

    result = generate()
    assert len(result["files"]) == 1 and result["files"][0]["phase"] == "completed", "File was not generated"
    validate_pair(subtitle.read_text(encoding="utf-8"), (source / "speech.ro.srt").read_text(encoding="utf-8"))
    assert_preserved(video, original_hash, original)
    output_hashes = (file_hash(subtitle), file_hash(source / "speech.ro.srt"))
    result = generate()
    assert result["files"][0]["phase"] == "skipped", "Receipt did not skip unchanged video"
    assert (file_hash(subtitle), file_hash(source / "speech.ro.srt")) == output_hashes
    assert_preserved(video, original_hash, original)
    assert_no_host_paths(client.response_bodies, source)
    if container_source:
        assert container_source.casefold() not in "\n".join(client.response_bodies).casefold(), "API disclosed a container source path"
    print("Real API subtitle generation, bilingual timing, preservation and rerun skip passed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", type=Path)
    parser.add_argument("--video", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--api-url")
    parser.add_argument("--source-root", type=Path)
    parser.add_argument("--container-source")
    args = parser.parse_args()
    if args.api_url and args.source_root:
        api_lifecycle(args.api_url, args.source_root.resolve(), args.container_source)
    elif args.models and args.video and args.output:
        local_inference(args.models.resolve(), args.video.resolve(), args.output.resolve())
    else:
        parser.error("provide --api-url/--source-root or --models/--video/--output")


if __name__ == "__main__":
    main()
