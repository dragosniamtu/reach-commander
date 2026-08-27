from __future__ import annotations

import datetime as dt
import io
import json
import sys
import tempfile
import unittest
import uuid
import zipfile
from pathlib import Path

from deploy.support_bundle import (
    BUNDLE_ENTRIES,
    DIAGNOSTIC_CHECK_NAMES,
    CommandResult,
    FixedCommandRunner,
    HostDiagnosticCollector,
    build_support_bundle,
)


CURRENT_DIGEST = "sha256:" + "1" * 64
FIXED_NOW = dt.datetime(2026, 8, 27, 12, 0, tzinfo=dt.timezone.utc)


class FakeRunner:
    def __init__(self, result: CommandResult) -> None:
        self.result = result
        self.calls: list[tuple[tuple[str, ...], float]] = []

    def run(self, command: tuple[str, ...], timeout: float) -> CommandResult:
        self.calls.append((command, timeout))
        return self.result


class FakeTrace:
    def to_protocol(self) -> dict[str, object]:
        return {
            "startedAt": "2026-08-27T11:59:00Z",
            "elapsedSeconds": 60,
            "lastActivityAt": "2026-08-27T11:59:30Z",
            "events": [],
        }


class FakeTraceStore:
    def __init__(self) -> None:
        self.blocking: bool | None = None

    def public_snapshot(
        self,
        operation_id: str,
        now: dt.datetime,
        *,
        blocking: bool,
    ) -> FakeTrace:
        uuid.UUID(operation_id)
        self.blocking = blocking
        return FakeTrace()


class SupportBundleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "state").mkdir()
        (self.root / "state" / "channel").write_text("stable\n", encoding="utf-8")
        (self.root / "state" / "current-image").write_text(
            f"ghcr.io/dragosniamtu/reach-commander@{CURRENT_DIGEST}\n",
            encoding="utf-8",
        )
        (self.root / "state" / "current-version").write_text(
            "v1.4.0\n", encoding="utf-8"
        )
        self.operation_id = str(uuid.uuid4())
        (self.root / "state" / "system-update.json").write_text(
            json.dumps({"operationId": self.operation_id}), encoding="utf-8"
        )

    @staticmethod
    def healthy_doctor_output() -> str:
        return "\n".join(
            [
                "[PASS] Docker Engine is available",
                "[PASS] Docker Compose v2 is available",
                "[PASS] Required deployment files are present",
                "[PASS] Management command is installed at the fixed path",
                "[PASS] No incomplete update transaction is present",
                "[PASS] No incomplete reconfiguration transaction is present",
                "[PASS] Application source configuration is valid JSON",
                "[PASS] Source path is accessible",
                "[PASS] Application data tree is structurally safe",
                "[PASS] Application data directory ownership and mode are correct",
                "[PASS] Application data directory is accessible to the runtime identity",
                "[PASS] Application data file ownership and mode are correct",
                "[PASS] Update channel is valid",
                "[PASS] Environment image matches current-image state",
                "[PASS] Current display version state is valid",
                "[PASS] Previous display version state is valid",
                "[PASS] Container is healthy",
                "[PASS] Updater service is active",
                "[PASS] Updater socket is ready",
            ]
        )

    def test_collects_exact_allowlisted_schema_and_nonblocking_trace(self) -> None:
        trace_store = FakeTraceStore()
        runner = FakeRunner(CommandResult(0, self.healthy_doctor_output()))

        snapshot = HostDiagnosticCollector(
            self.root,
            command_path="/fixed/reachcommander",
            command_runner=runner,
            trace_store=trace_store,
            clock=lambda: FIXED_NOW,
            disk_usage=lambda _path: (100, 20, 80),
        ).collect()
        payload = snapshot.to_protocol()

        self.assertEqual(
            {
                "schemaVersion",
                "generatedAt",
                "complete",
                "updaterProtocolVersion",
                "channel",
                "currentVersion",
                "operationId",
                "trace",
                "checks",
            },
            set(payload),
        )
        self.assertEqual(4, payload["updaterProtocolVersion"])
        self.assertEqual("stable", payload["channel"])
        self.assertEqual("v1.4.0", payload["currentVersion"])
        self.assertEqual(self.operation_id, payload["operationId"])
        self.assertTrue(payload["complete"])
        self.assertFalse(trace_store.blocking)
        checks = payload["checks"]
        self.assertEqual(DIAGNOSTIC_CHECK_NAMES, tuple(item["name"] for item in checks))
        self.assertTrue(all(set(item) == {"name", "status", "reasonCode"} for item in checks))
        self.assertEqual([(("/fixed/reachcommander", "doctor"), 2.0)], runner.calls)

    def test_hostile_output_is_never_copied_and_timeout_is_partial(self) -> None:
        hostile = (
            "/srv/media/private.mov token=secret 192.168.1.44 "
            "sha256:" + "f" * 64
        )
        runner = FakeRunner(CommandResult(None, hostile, timed_out=True))

        payload = HostDiagnosticCollector(
            self.root,
            command_runner=runner,
            clock=lambda: FIXED_NOW,
            disk_usage=lambda _path: (100, 99, 1),
        ).collect().to_protocol()
        encoded = json.dumps(payload)

        self.assertFalse(payload["complete"])
        self.assertNotIn(hostile, encoded)
        self.assertNotIn("/srv/media", encoded)
        self.assertNotIn("192.168", encoded)
        self.assertNotIn("sha256", encoded)
        self.assertTrue(
            any(item["status"] == "timedOut" for item in payload["checks"])
        )

    def test_timeout_preserves_completed_checks_and_marks_the_rest_partial(self) -> None:
        runner = FakeRunner(
            CommandResult(
                None,
                "[PASS] Docker Engine is available\n",
                timed_out=True,
            )
        )

        payload = HostDiagnosticCollector(
            self.root,
            command_runner=runner,
            clock=lambda: FIXED_NOW,
            disk_usage=lambda _path: (100, 20, 80),
        ).collect().to_protocol()
        statuses = {item["name"]: item["status"] for item in payload["checks"]}

        self.assertEqual("healthy", statuses["dockerEngine"])
        self.assertEqual("timedOut", statuses["dockerCompose"])
        self.assertFalse(payload["complete"])

    def test_unknown_doctor_failure_is_reported_fail_closed(self) -> None:
        output = self.healthy_doctor_output() + "\n[FAIL] New unsafe host condition"
        payload = HostDiagnosticCollector(
            self.root,
            command_runner=FakeRunner(CommandResult(1, output)),
            clock=lambda: FIXED_NOW,
            disk_usage=lambda _path: (100, 20, 80),
        ).collect().to_protocol()
        statuses = {item["name"]: item["status"] for item in payload["checks"]}

        self.assertEqual("failed", statuses["deploymentFiles"])

    def test_unknown_doctor_warning_makes_the_snapshot_partial(self) -> None:
        output = self.healthy_doctor_output() + "\n[WARN] New host condition"
        payload = HostDiagnosticCollector(
            self.root,
            command_runner=FakeRunner(CommandResult(0, output)),
            clock=lambda: FIXED_NOW,
            disk_usage=lambda _path: (100, 20, 80),
        ).collect().to_protocol()
        statuses = {item["name"]: item["status"] for item in payload["checks"]}

        self.assertEqual("unavailable", statuses["deploymentFiles"])
        self.assertFalse(payload["complete"])

    def test_updater_unit_failure_is_mapped_to_the_updater_service_check(self) -> None:
        output = (
            self.healthy_doctor_output()
            + "\n[FAIL] Updater systemd unit is missing or symlinked"
        )
        payload = HostDiagnosticCollector(
            self.root,
            command_runner=FakeRunner(CommandResult(1, output)),
            clock=lambda: FIXED_NOW,
            disk_usage=lambda _path: (100, 20, 80),
        ).collect().to_protocol()
        statuses = {item["name"]: item["status"] for item in payload["checks"]}

        self.assertEqual("failed", statuses["updaterService"])

    def test_fixed_runner_never_retains_more_than_the_command_output_limit(self) -> None:
        result = FixedCommandRunner().run(
            (
                sys.executable,
                "-c",
                "import sys; sys.stdout.buffer.write(b'x' * 70000)",
            ),
            2.0,
        )

        self.assertEqual(65_536, len(result.output.encode("utf-8")))
        self.assertTrue(result.unavailable)

    def test_total_collection_budget_marks_unstarted_checks_timed_out(self) -> None:
        ticks = iter((0.0, 0.0, 11.0, 11.0))
        payload = HostDiagnosticCollector(
            self.root,
            command_runner=FakeRunner(CommandResult(0, self.healthy_doctor_output())),
            clock=lambda: FIXED_NOW,
            monotonic=lambda: next(ticks),
            disk_usage=lambda _path: self.fail("disk usage started after the deadline"),
        ).collect().to_protocol()

        statuses = {item["name"]: item["status"] for item in payload["checks"]}
        self.assertEqual("timedOut", statuses["installDiskSpace"])
        self.assertEqual("timedOut", statuses["dockerDiskSpace"])
        self.assertFalse(payload["complete"])

    def test_builds_only_the_five_bounded_sanitized_entries(self) -> None:
        snapshot = HostDiagnosticCollector(
            self.root,
            command_runner=FakeRunner(CommandResult(0, self.healthy_doctor_output())),
            clock=lambda: FIXED_NOW,
            disk_usage=lambda _path: (100, 20, 80),
        ).collect()

        archive_bytes = build_support_bundle(snapshot)
        self.assertLessEqual(len(archive_bytes), 1_048_576)
        with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
            self.assertEqual(BUNDLE_ENTRIES, tuple(sorted(archive.namelist())))
            self.assertTrue(all(".." not in name and not name.startswith("/") for name in archive.namelist()))
            manifest = json.loads(archive.read("manifest.json"))
            self.assertEqual(1, manifest["bundleSchemaVersion"])
            self.assertEqual("v1.4.0", manifest["currentVersion"])
            self.assertEqual("stable", manifest["channel"])
            self.assertIn("sudo reachcommander doctor", archive.read("summary.txt").decode())
            self.assertLessEqual(
                sum(info.file_size for info in archive.infolist()),
                1_048_576,
            )


if __name__ == "__main__":
    unittest.main()
