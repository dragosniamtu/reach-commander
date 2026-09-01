#!/usr/bin/env python3
"""Exercise authenticated media preview and transactional subtitle saving."""

from __future__ import annotations

import argparse
import hashlib
import json
from http.cookiejar import CookieJar, DefaultCookiePolicy
from pathlib import Path
from typing import Any
from urllib.error import HTTPError
from urllib.request import HTTPCookieProcessor, Request, build_opener


USERNAME = "container-smoke"
PASSWORD = "container-smoke-password-2026"


class SmokeClient:
    def __init__(self, base_url: str) -> None:
        self.base_url = base_url.rstrip("/")
        policy = DefaultCookiePolicy(secure_protocols=("https", "http", "wss"))
        self.opener = build_opener(HTTPCookieProcessor(CookieJar(policy=policy)))
        self.csrf_token: str | None = None
        self.response_bodies: list[str] = []

    def refresh_antiforgery(self) -> None:
        response = self.json_request("GET", "/api/auth/antiforgery")
        self.csrf_token = required_string(response, "requestToken")

    def json_request(
        self,
        method: str,
        path: str,
        payload: Any | None = None,
        *,
        expected: tuple[int, ...] = (200,),
        antiforgery: bool = False,
    ) -> Any:
        data = None
        headers = {"Accept": "application/json", "X-Forwarded-Proto": "https"}
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if antiforgery:
            if not self.csrf_token:
                raise RuntimeError("An antiforgery token has not been obtained.")
            headers["X-ReachCommander-CSRF"] = self.csrf_token

        request = Request(f"{self.base_url}{path}", data=data, headers=headers, method=method)
        try:
            with self.opener.open(request, timeout=30) as response:
                body = response.read().decode("utf-8")
                status = response.status
        except HTTPError as error:
            body = error.read().decode("utf-8", errors="replace")
            self.response_bodies.append(body)
            raise RuntimeError(
                f"{method} {path} returned HTTP {error.code}: {body[:500]}"
            ) from error

        self.response_bodies.append(body)
        if status not in expected:
            raise RuntimeError(f"{method} {path} returned HTTP {status}, expected {expected}.")
        return json.loads(body) if body else None

    def ranged_content(self, path: str) -> bytes:
        request = Request(
            f"{self.base_url}{path}",
            headers={
                "Accept": "video/mp4",
                "Range": "bytes=0-31",
                "X-Forwarded-Proto": "https",
            },
            method="GET",
        )
        with self.opener.open(request, timeout=30) as response:
            body = response.read()
            if response.status != 206:
                raise RuntimeError(f"Range request returned HTTP {response.status}, expected 206.")
            content_range = response.headers.get("Content-Range", "")
            if not content_range.startswith("bytes 0-31/"):
                raise RuntimeError(f"Unexpected Content-Range: {content_range!r}")
            if len(body) != 32:
                raise RuntimeError(f"Range response returned {len(body)} bytes, expected 32.")
            return body


def required_string(value: Any, key: str) -> str:
    if not isinstance(value, dict) or not isinstance(value.get(key), str) or not value[key]:
        raise RuntimeError(f"Response is missing required string property {key!r}.")
    return value[key]


def assert_no_host_paths(response_bodies: list[str], root: Path) -> None:
    combined = "\n".join(response_bodies).casefold().replace("\\", "/")
    physical = str(root.resolve()).casefold().replace("\\", "/")
    if physical in combined:
        raise RuntimeError("A media-preview API response disclosed a physical host path.")


def run_lifecycle(base_url: str, source_root: Path) -> None:
    video = source_root / "incoming" / "media-smoke.mp4"
    subtitle = source_root / "incoming" / "media-smoke.srt"
    backup = source_root / "incoming" / "media-smoke_original.srt"
    if not video.is_file() or video.stat().st_size < 32:
        raise RuntimeError("The generated MP4 smoke fixture is missing or too small.")
    if not subtitle.is_file():
        raise RuntimeError("The SRT smoke fixture is missing.")
    original_video_hash = hashlib.sha256(video.read_bytes()).digest()
    original_subtitle = subtitle.read_bytes()

    client = SmokeClient(base_url)
    client.refresh_antiforgery()
    client.json_request(
        "POST",
        "/api/auth/login",
        {"username": USERNAME, "password": PASSWORD},
        antiforgery=True,
    )
    client.refresh_antiforgery()

    preview = client.json_request(
        "POST",
        "/api/media-previews",
        {"sourceId": "source-a", "videoPath": "/incoming/media-smoke.mp4"},
        antiforgery=True,
    )
    session_id = required_string(preview, "sessionId")
    if preview.get("phase") != "ready" or preview.get("playbackMode") != "direct":
        raise RuntimeError(f"Generated MP4 was not classified for direct playback: {preview!r}")
    if preview.get("subtitlePath") != "/incoming/media-smoke.srt":
        raise RuntimeError("The same-name SRT was not selected automatically.")
    if len(preview.get("cues", [])) != 2:
        raise RuntimeError("The SRT smoke fixture did not return exactly two cues.")

    client.ranged_content(f"/api/media-previews/{session_id}/content")
    plan = client.json_request(
        "POST",
        f"/api/media-previews/{session_id}/subtitle-save-plans",
        {"offsetMilliseconds": 1_000},
        antiforgery=True,
    )
    if plan.get("backupPath") != "/incoming/media-smoke_original.srt":
        raise RuntimeError(f"Unexpected subtitle backup path: {plan.get('backupPath')!r}")
    if plan.get("canExecute") is not True:
        raise RuntimeError("The writable subtitle save plan was not executable.")
    result = client.json_request(
        "POST",
        f"/api/media-previews/subtitle-save-plans/{required_string(plan, 'planId')}/execute",
        antiforgery=True,
    )
    if result.get("recoveryRequired") is not False:
        raise RuntimeError("Subtitle save unexpectedly requires recovery.")

    if backup.read_bytes() != original_subtitle:
        raise RuntimeError("The _original SRT backup was not preserved byte-for-byte.")
    expected = (
        "1\r\n00:00:02,000 --> 00:00:02,500\r\nFirst cue\r\n\r\n"
        "2\r\n00:00:02,600 --> 00:00:02,900\r\nSecond cue\r\n"
    ).encode("utf-8")
    if subtitle.read_bytes() != expected:
        raise RuntimeError("The corrected SRT does not contain the expected +1000 ms timing.")
    if hashlib.sha256(video.read_bytes()).digest() != original_video_hash:
        raise RuntimeError("The media preview lifecycle modified the video file.")

    client.json_request(
        "DELETE",
        f"/api/media-previews/{session_id}",
        expected=(204,),
        antiforgery=True,
    )
    assert_no_host_paths(client.response_bodies, source_root)
    print("Hardened container media preview and subtitle save lifecycle passed.")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--source-root", type=Path, required=True)
    args = parser.parse_args()
    run_lifecycle(args.base_url, args.source_root)


if __name__ == "__main__":
    main()
