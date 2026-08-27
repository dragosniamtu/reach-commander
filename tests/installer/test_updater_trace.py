from __future__ import annotations

import dataclasses
import datetime as dt
import json
import os
import stat
import tempfile
import threading
import time
import unittest
import uuid
from pathlib import Path

from deploy.updater_trace import (
    MAX_PUBLIC_EVENTS,
    MAX_TRACE_DIRECTORY_BYTES,
    MAX_TRACE_FILES,
    ProtectedUpdateTraceStore,
    TraceError,
)


NOW = dt.datetime(2026, 8, 27, 10, 0, tzinfo=dt.timezone.utc)
SECOND = dt.timedelta(seconds=1)


class ProtectedUpdateTraceStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name) / "update-traces"
        self.store = ProtectedUpdateTraceStore(self.root, clock=lambda: NOW)
        self.operation_id = str(uuid.uuid4())

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_nonblocking_public_projection_skips_an_in_flight_trace_write(self) -> None:
        lock_held = threading.Event()
        release_lock = threading.Event()

        def hold_store_lock() -> None:
            with self.store._lock:
                lock_held.set()
                release_lock.wait(timeout=2)

        worker = threading.Thread(target=hold_store_lock, daemon=True)
        worker.start()
        self.assertTrue(lock_held.wait(timeout=1))
        started = time.monotonic()
        try:
            snapshot = self.store.public_snapshot(
                self.operation_id,
                NOW,
                blocking=False,
            )
        finally:
            release_lock.set()
            worker.join(timeout=2)

        self.assertIsNone(snapshot)
        self.assertLess(time.monotonic() - started, 0.5)

    def test_trace_is_private_ordered_and_public_projection_is_sanitized(self) -> None:
        self.store.start(self.operation_id, NOW)
        self.store.append(
            self.operation_id,
            "downloadStarted",
            "started",
            NOW + SECOND,
            stage="downloading",
            timeout_seconds=300,
        )
        self.store.append(
            self.operation_id,
            "hostActivity",
            "activity",
            NOW + 2 * SECOND,
            stage="downloading",
            exit_code=0,
        )

        snapshot = self.store.public_snapshot(self.operation_id, NOW + 3 * SECOND)

        self.assertIsNotNone(snapshot)
        assert snapshot is not None
        self.assertEqual([1, 2, 3], [event.sequence for event in snapshot.events])
        self.assertEqual("2026-08-27T10:00:02Z", snapshot.last_activity_at)
        self.assertEqual(3, snapshot.elapsed_seconds)
        encoded = json.dumps(dataclasses.asdict(snapshot))
        self.assertNotIn("exit_code", encoded)
        self.assertNotIn("timeout_seconds", encoded)
        self.assertNotIn("sha256:", encoded)
        trace_path = self.root / f"{self.operation_id}.jsonl"
        if os.name != "nt":
            self.assertEqual(0o700, stat.S_IMODE(self.root.stat().st_mode))
            self.assertEqual(0o600, stat.S_IMODE(trace_path.stat().st_mode))

    def test_public_projection_keeps_only_latest_bounded_events(self) -> None:
        self.store.start(self.operation_id, NOW)
        for offset in range(MAX_PUBLIC_EVENTS + 5):
            self.store.append(
                self.operation_id,
                "hostActivity",
                "activity",
                NOW + (offset + 1) * SECOND,
                stage="downloading",
            )

        snapshot = self.store.public_snapshot(
            self.operation_id,
            NOW + (MAX_PUBLIC_EVENTS + 6) * SECOND,
        )

        assert snapshot is not None
        self.assertEqual(MAX_PUBLIC_EVENTS, len(snapshot.events))
        self.assertGreater(snapshot.events[0].sequence, 1)
        self.assertEqual(
            sorted(event.sequence for event in snapshot.events),
            [event.sequence for event in snapshot.events],
        )

    def test_rejects_invalid_identifiers_events_outcomes_stages_and_time(self) -> None:
        with self.assertRaisesRegex(TraceError, "identifier"):
            self.store.start("not-a-uuid", NOW)

        self.store.start(self.operation_id, NOW)
        invalid = (
            {"code": "unknown", "outcome": "started"},
            {"code": "downloadStarted", "outcome": "unknown"},
            {"code": "downloadStarted", "outcome": "succeeded"},
            {"code": "operationFailed", "outcome": "succeeded"},
            {"code": "downloadStarted", "outcome": "started", "stage": "unknown"},
        )
        for values in invalid:
            with self.subTest(values=values), self.assertRaises(TraceError):
                self.store.append(self.operation_id, now=NOW + SECOND, **values)
        with self.assertRaisesRegex(TraceError, "timestamp"):
            self.store.append(
                self.operation_id,
                "downloadStarted",
                "started",
                NOW - SECOND,
                stage="downloading",
            )

    def test_rejects_append_after_terminal_event(self) -> None:
        self.store.start(self.operation_id, NOW)
        self.store.append(
            self.operation_id,
            "operationCompleted",
            "succeeded",
            NOW + SECOND,
        )

        with self.assertRaisesRegex(TraceError, "terminal"):
            self.store.append(
                self.operation_id,
                "hostActivity",
                "activity",
                NOW + 2 * SECOND,
                stage="healthChecking",
            )

    def test_retention_keeps_active_plus_nine_newest_traces(self) -> None:
        operation_ids: list[str] = []
        for index in range(MAX_TRACE_FILES + 2):
            operation_id = str(uuid.uuid4())
            operation_ids.append(operation_id)
            started = NOW + index * SECOND
            self.store.start(operation_id, started)
            if index < MAX_TRACE_FILES + 1:
                self.store.append(
                    operation_id,
                    "operationCompleted",
                    "succeeded",
                    started + SECOND,
                )

        active_id = operation_ids[-1]
        self.store.prune(active_id)

        traces = list(self.root.iterdir())
        self.assertLessEqual(len(traces), MAX_TRACE_FILES)
        self.assertTrue((self.root / f"{active_id}.jsonl").exists())
        self.assertFalse((self.root / f"{operation_ids[0]}.jsonl").exists())
        self.assertLessEqual(
            sum(path.stat().st_size for path in traces),
            MAX_TRACE_DIRECTORY_BYTES,
        )

    def test_latest_path_uses_last_event_timestamp(self) -> None:
        first = str(uuid.uuid4())
        second = str(uuid.uuid4())
        self.store.start(first, NOW)
        self.store.start(second, NOW + SECOND)

        latest = self.store.latest_path()

        self.assertEqual(self.root / f"{second}.jsonl", latest)

    def test_validation_rejects_malformed_and_unexpected_entries_without_contents(self) -> None:
        self.store.start(self.operation_id, NOW)
        unexpected = self.root / "secret.txt"
        unexpected.write_text("super-secret-value", encoding="utf-8")

        valid, detail = self.store.validate_tree()

        self.assertFalse(valid)
        self.assertNotIn("super-secret-value", detail)
        self.assertIn("unsafe", detail.lower())

    def test_rejects_symlinked_trace_without_following_it(self) -> None:
        self.root.mkdir(mode=0o700)
        outside = Path(self.temporary.name) / "outside.jsonl"
        outside.write_text("secret", encoding="utf-8")
        link = self.root / f"{self.operation_id}.jsonl"
        try:
            link.symlink_to(outside)
        except OSError as error:
            self.skipTest(f"symlinks unavailable: {error}")

        valid, detail = self.store.validate_tree()

        self.assertFalse(valid)
        self.assertNotIn("secret", detail)
        self.assertEqual("secret", outside.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
