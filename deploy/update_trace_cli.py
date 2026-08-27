#!/usr/bin/env python3
"""Root-only, fixed-path diagnostics for protected ReachCommander update traces."""

from __future__ import annotations

import os
import sys
import time
from pathlib import Path
from typing import Sequence

sys.dont_write_bytecode = True

try:
    from .updater_trace import TERMINAL_CODES, ProtectedUpdateTraceStore, TraceError
except ImportError:  # Installed helper: bin/update_trace_cli.py + lib/updater_trace.py
    library_directory = Path(__file__).resolve().parents[1] / "lib"
    sys.path.insert(0, str(library_directory))
    from updater_trace import (  # type: ignore[no-redef]
        TERMINAL_CODES,
        ProtectedUpdateTraceStore,
        TraceError,
    )


USAGE = "Usage: reachcommander update-log [--follow]"
NO_TRACE_MESSAGE = "No ReachCommander update trace is available."
UNSAFE_TRACE_MESSAGE = (
    "ReachCommander update trace storage is unsafe; "
    "run 'sudo reachcommander doctor'."
)


def _trace_root() -> Path:
    if (
        os.environ.get("REACHCOMMANDER_TESTING") == "1"
        and (not hasattr(os, "geteuid") or os.geteuid() != 0)
    ):
        test_root = os.environ.get("REACHCOMMANDER_TEST_TRACE_ROOT", "")
        candidate = Path(test_root)
        if not test_root or not candidate.is_absolute():
            raise ValueError("the fixed update trace test root is unavailable")
        return candidate
    return Path("/opt/reachcommander/state/update-traces")


def _read_latest(store: ProtectedUpdateTraceStore):  # type: ignore[no-untyped-def]
    try:
        store.root.lstat()
    except FileNotFoundError:
        return None, []
    path = store.latest_path()
    if path is None:
        return None, []
    events = store._read_trace(path, path.stem)  # noqa: SLF001 - same protected package
    return path, events


def _event_line(event) -> str:  # type: ignore[no-untyped-def]
    stage = event.stage or "-"
    details: list[str] = []
    if event.exit_code is not None:
        details.append(f"exit={event.exit_code}")
    if event.timeout_seconds is not None:
        details.append(f"timeout={event.timeout_seconds}s")
    suffix = "" if not details else f" ({', '.join(details)})"
    return (
        f"{event.sequence:04d} {event.timestamp} "
        f"+{event.elapsed_milliseconds / 1000:.3f}s "
        f"{event.code} stage={stage} outcome={event.outcome}{suffix}"
    )


def print_latest(store: ProtectedUpdateTraceStore, *, follow: bool) -> int:
    path, events = _read_latest(store)
    if path is None:
        print(NO_TRACE_MESSAGE)
        return 0

    snapshot = store.public_snapshot(path.stem)
    if snapshot is None or not events:
        print(NO_TRACE_MESSAGE)
        return 0
    print("ReachCommander update trace")
    print(f"Operation: {path.stem}")
    print(f"Started: {snapshot.started_at}")
    print(f"Elapsed: {snapshot.elapsed_seconds}s")
    print("Events:")

    last_sequence = 0
    while True:
        for event in events:
            if event.sequence > last_sequence:
                print(_event_line(event), flush=True)
                last_sequence = event.sequence
        if not follow or events[-1].code in TERMINAL_CODES:
            return 0
        time.sleep(1)
        events = store._read_trace(path, path.stem)  # noqa: SLF001


def main(argv: Sequence[str] | None = None) -> int:
    arguments = tuple(sys.argv[1:] if argv is None else argv)
    if arguments not in {(), ("--follow",), ("--doctor",)}:
        print(USAGE, file=sys.stderr)
        return 64
    try:
        store = ProtectedUpdateTraceStore(_trace_root())
        if arguments == ("--doctor",):
            valid, detail = store.validate_tree()
            print(detail)
            return 0 if valid else 1
        return print_latest(store, follow=arguments == ("--follow",))
    except (OSError, TraceError, ValueError):
        print(UNSAFE_TRACE_MESSAGE, file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
