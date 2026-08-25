#!/usr/bin/env python3
"""Exercise authenticated file operations against the hardened smoke container."""

from __future__ import annotations

import argparse
import json
import os
import time
from http.cookiejar import CookieJar, DefaultCookiePolicy
from pathlib import Path
from typing import Any
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import HTTPCookieProcessor, Request, build_opener


TERMINAL_PHASES = {
    "completed",
    "completedWithErrors",
    "cancelled",
    "failed",
    "interrupted",
}


class SmokeClient:
    def __init__(self, base_url: str) -> None:
        self.base_url = base_url.rstrip("/")
        proxy_tls_policy = DefaultCookiePolicy(
            secure_protocols=("https", "http", "wss")
        )
        self.opener = build_opener(
            HTTPCookieProcessor(CookieJar(policy=proxy_tls_policy))
        )
        self.csrf_token: str | None = None
        self.response_bodies: list[str] = []

    def refresh_antiforgery(self) -> None:
        response = self.request("GET", "/api/auth/antiforgery", expected=(200,))
        self.csrf_token = required_string(response, "requestToken")

    def request(
        self,
        method: str,
        path: str,
        payload: Any | None = None,
        *,
        expected: tuple[int, ...] = (200,),
        antiforgery: bool = False,
    ) -> Any:
        data = None
        headers = {
            "Accept": "application/json",
            "X-Forwarded-Proto": "https",
        }
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if antiforgery:
            if not self.csrf_token:
                raise RuntimeError("An antiforgery token has not been obtained.")
            headers["X-ReachCommander-CSRF"] = self.csrf_token

        request = Request(f"{self.base_url}{path}", data=data, headers=headers, method=method)
        try:
            with self.opener.open(request, timeout=15) as response:
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

    def wait_for_terminal(self, operation_id: str) -> dict[str, Any]:
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            status = self.request("GET", f"/api/file-operations/{operation_id}")
            phase = required_string(status, "phase")
            if phase in TERMINAL_PHASES:
                if phase != "completed":
                    raise RuntimeError(
                        f"Operation {operation_id} ended in unexpected phase {phase}."
                    )
                return status
            time.sleep(0.1)
        raise RuntimeError(f"Operation {operation_id} did not finish within 20 seconds.")


def required_string(value: Any, key: str) -> str:
    if not isinstance(value, dict) or not isinstance(value.get(key), str) or not value[key]:
        raise RuntimeError(f"Response is missing required string property {key!r}.")
    return value[key]


def assert_file(path: Path, expected: str) -> None:
    if not path.is_file():
        raise RuntimeError(f"Expected lifecycle file is missing: {path.name}")
    actual = path.read_text(encoding="utf-8")
    if actual != expected:
        raise RuntimeError(f"Lifecycle file changed unexpectedly: {path.name}")


def assert_no_host_paths(response_bodies: list[str], roots: list[Path]) -> None:
    combined = "\n".join(response_bodies).casefold().replace("\\", "/")
    for root in roots:
        physical = str(root.resolve()).casefold().replace("\\", "/")
        if physical in combined:
            raise RuntimeError("An API response disclosed a physical host path.")


def run_lifecycle(base_url: str, source_a_root: Path, source_b_root: Path) -> None:
    setup_code = os.environ.get("REACHCOMMANDER_SMOKE_SETUP_CODE", "")
    if not setup_code:
        raise RuntimeError("REACHCOMMANDER_SMOKE_SETUP_CODE is required.")

    copy_source = source_a_root / "incoming" / "copy.txt"
    copy_destination = source_b_root / "target" / "copy.txt"
    copy_canary = source_a_root / "incoming" / "copy-canary.txt"
    trash_canary = source_b_root / "target" / "trash-canary.txt"
    assert_file(copy_source, "copy payload\n")
    assert_file(copy_canary, "copy canary\n")
    assert_file(trash_canary, "trash canary\n")

    client = SmokeClient(base_url)
    client.refresh_antiforgery()
    client.request(
        "POST",
        "/api/auth/setup",
        {
            "setupCode": setup_code,
            "username": "container-smoke",
            "password": "container-smoke-password-2026",
        },
        antiforgery=True,
    )
    client.refresh_antiforgery()

    copy_preview = client.request(
        "POST",
        "/api/file-operations/preview",
        {
            "kind": "copy",
            "sourceId": "source-a",
            "logicalPaths": ["/incoming/copy.txt"],
            "destinationSourceId": "source-b",
            "destinationLogicalDirectory": "/target",
        },
        antiforgery=True,
    )
    copy_status = client.request(
        "POST",
        "/api/file-operations",
        {"planId": required_string(copy_preview, "planId"), "resolutions": []},
        expected=(202,),
        antiforgery=True,
    )
    client.wait_for_terminal(required_string(copy_status, "operationId"))
    assert_file(copy_source, "copy payload\n")
    assert_file(copy_destination, "copy payload\n")
    assert_file(copy_canary, "copy canary\n")

    delete_preview = client.request(
        "POST",
        "/api/trash/preview-delete",
        {
            "sourceId": "source-b",
            "logicalPaths": ["/target/copy.txt"],
            "mode": "trash",
        },
        antiforgery=True,
    )
    delete_status = client.request(
        "POST",
        "/api/trash/delete",
        {
            "planId": required_string(delete_preview, "planId"),
            "permanentDeleteConfirmed": False,
        },
        expected=(202,),
        antiforgery=True,
    )
    client.wait_for_terminal(required_string(delete_status, "operationId"))
    if copy_destination.exists():
        raise RuntimeError("Trash did not remove the copied file from its original path.")
    assert_file(trash_canary, "trash canary\n")

    query = urlencode({"sourceId": "source-b"})
    trash_entries = client.request("GET", f"/api/trash?{query}")
    matching = [
        entry
        for entry in trash_entries
        if entry.get("name") == "copy.txt"
        and entry.get("originalLogicalPath") == "/target/copy.txt"
    ]
    if len(matching) != 1:
        raise RuntimeError("The copied file was not represented exactly once in managed Trash.")
    trash_id = required_string(matching[0], "trashId")

    restore_preview = client.request(
        "POST",
        "/api/trash/preview-restore",
        {"trashIds": [trash_id]},
        antiforgery=True,
    )
    restore_status = client.request(
        "POST",
        "/api/trash/restore",
        {"planId": required_string(restore_preview, "planId"), "resolutions": []},
        expected=(202,),
        antiforgery=True,
    )
    client.wait_for_terminal(required_string(restore_status, "operationId"))
    assert_file(copy_destination, "copy payload\n")
    assert_file(trash_canary, "trash canary\n")
    remaining = client.request("GET", "/api/trash?sourceId=source-b")
    if any(entry.get("trashId") == trash_id for entry in remaining):
        raise RuntimeError("The restored entry remained in managed Trash.")

    assert_no_host_paths(client.response_bodies, [source_a_root, source_b_root])
    print("Hardened container Copy, Trash, Restore, and redaction lifecycle passed.")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--source-a-root", type=Path, required=True)
    parser.add_argument("--source-b-root", type=Path, required=True)
    args = parser.parse_args()
    run_lifecycle(args.base_url, args.source_a_root, args.source_b_root)


if __name__ == "__main__":
    main()
