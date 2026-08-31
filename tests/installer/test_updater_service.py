from __future__ import annotations

import datetime as dt
import json
import os
import signal
import socket
import stat
import subprocess
import tempfile
import threading
import time
import unittest
import uuid
from pathlib import Path
from typing import Callable
from unittest import mock

import deploy.updater_service as updater_service
from deploy.updater_protocol import (
    DIAGNOSTIC_PROTOCOL_VERSION,
    MAX_MESSAGE_BYTES,
    SOURCE_MANAGEMENT_PROTOCOL_VERSION,
    TRUSTED_IMAGE_REPOSITORY,
    SourceManagementCapability,
    SourceManagementRequest,
    ResolvedImage,
    UpdateSnapshot,
    UpdaterRequest,
)
from deploy.support_bundle import DiagnosticCheck, DiagnosticSnapshot
from deploy.updater_service import (
    CommandTimedOut,
    FIXED_COMMAND,
    JOURNAL_SCHEMA,
    SANITIZED_ENVIRONMENT,
    AtomicUpdateJournal,
    CommandResult,
    DockerImageResolver,
    GitHubLatestReleaseProvider,
    JournalError,
    SubprocessCommandRunner,
    UpdaterRuntime,
    UpdaterSocketServer,
    install_signal_handlers,
    read_runtime_gid,
)
from deploy.updater_trace import ProtectedUpdateTraceStore, TraceError


NOW = dt.datetime(2026, 8, 25, 12, 0, tzinfo=dt.timezone.utc)
CURRENT_DIGEST = "sha256:" + "1" * 64
TARGET_DIGEST = "sha256:" + "2" * 64
TARGET_REFERENCE = f"{TRUSTED_IMAGE_REPOSITORY}:v1.4.0"
LEGACY_PROTOCOL_VERSION = 1
DETAILED_PROTOCOL_VERSION = 2
TRACE_PROTOCOL_VERSION = 3


class StaticDiagnosticCollector:
    def __init__(self) -> None:
        self.calls = 0

    def collect(self) -> DiagnosticSnapshot:
        self.calls += 1
        return DiagnosticSnapshot(
            "2026-08-25T12:00:00Z",
            True,
            DIAGNOSTIC_PROTOCOL_VERSION,
            "stable",
            "v1.3.0",
            None,
            None,
            (
                DiagnosticCheck(
                    "dockerEngine",
                    "healthy",
                    "docker_engine_healthy",
                ),
            ),
        )


def snapshot(phase: str = "available") -> UpdateSnapshot:
    target = phase in {"available", "current", "applying", "completed", "rolledBack", "failed"}
    return UpdateSnapshot(
        supported=True,
        channel="stable",
        current_version="v1.3.0",
        target_version="v1.4.0" if target else None,
        current_digest=CURRENT_DIGEST,
        target_digest=TARGET_DIGEST if target else None,
        phase=phase,
        reason_code="update_available" if phase == "available" else "up_to_date",
        detail="A trusted ReachCommander update is available.",
        target_reference=TARGET_REFERENCE if target else None,
        last_checked_at="2026-08-25T12:00:00Z",
        updated_at="2026-08-25T12:00:00Z",
    )


def request(
    action: str,
    protocol_version: int = LEGACY_PROTOCOL_VERSION,
) -> UpdaterRequest:
    return UpdaterRequest.parse(
        json.dumps(
            {
                "protocolVersion": protocol_version,
                "requestId": str(uuid.uuid4()),
                "action": action,
            }
        ).encode()
    )


class StaticDiscovery:
    def __init__(self, value: UpdateSnapshot) -> None:
        self.value = value
        self.calls = 0

    def check(self) -> UpdateSnapshot:
        self.calls += 1
        return self.value


class RecordingRunner:
    def __init__(
        self,
        exit_code: int = 0,
        output: str = "sensitive output",
        progress_stages: tuple[str, ...] = (),
        trace_events: tuple[tuple[str, str, str | None], ...] = (),
    ) -> None:
        self.exit_code = exit_code
        self.output = output
        self.progress_stages = progress_stages
        self.trace_events = trace_events
        self.argv: list[list[str]] = []
        self.environments: list[dict[str, str]] = []
        self.timeouts: list[int] = []
        self.shell_values: list[bool] = []

    def run(
        self,
        argv: tuple[str, ...],
        *,
        env: dict[str, str],
        timeout: int,
        shell: bool,
        progress_callback: Callable[[str], None] | None = None,
        trace_callback: Callable[[str, str, str | None], None] | None = None,
    ) -> CommandResult:
        self.argv.append(list(argv))
        self.environments.append(dict(env))
        self.timeouts.append(timeout)
        self.shell_values.append(shell)
        if progress_callback is not None:
            for stage in self.progress_stages:
                progress_callback(stage)
        if trace_callback is not None:
            for code, outcome, stage in self.trace_events:
                trace_callback(code, outcome, stage)
        return CommandResult(self.exit_code, self.output)


class BlockingRunner(RecordingRunner):
    def __init__(self) -> None:
        super().__init__()
        self.started = threading.Event()
        self.release = threading.Event()

    def run(self, *args, **kwargs) -> CommandResult:  # type: ignore[no-untyped-def]
        self.started.set()
        self.release.wait(timeout=2)
        return super().run(*args, **kwargs)


class RaisingRunner(RecordingRunner):
    def run(self, *args, **kwargs) -> CommandResult:  # type: ignore[no-untyped-def]
        raise OSError("/opt/reachcommander must not be public")


class TimeoutRunner(RecordingRunner):
    def run(self, *args, **kwargs) -> CommandResult:  # type: ignore[no-untyped-def]
        raise CommandTimedOut(
            300,
            "/private updater output",
            (
                ("commandTimedOut", "timedOut", "downloading"),
                ("terminationRequested", "started", "downloading"),
            ),
        )


class LateProgressRunner(RecordingRunner):
    def __init__(self) -> None:
        super().__init__()
        self.progress_callback: Callable[[str], None] | None = None

    def run(self, *args, **kwargs) -> CommandResult:  # type: ignore[no-untyped-def]
        self.progress_callback = kwargs.get("progress_callback")
        return super().run(*args, **kwargs)

    def publish_late_progress(self, stage: str) -> None:
        assert self.progress_callback is not None
        self.progress_callback(stage)


class SequenceRunner:
    def __init__(self, results: list[CommandResult]) -> None:
        self.results = list(results)
        self.argv: list[list[str]] = []

    def run(self, argv, **_kwargs) -> CommandResult:  # type: ignore[no-untyped-def]
        self.argv.append(list(argv))
        return self.results.pop(0)


def source_request(
    action: str,
    *,
    request_id: str | None = None,
    operation_id: str | None = None,
) -> SourceManagementRequest:
    value: dict[str, object] = {
        "protocolVersion": SOURCE_MANAGEMENT_PROTOCOL_VERSION,
        "requestId": request_id or str(uuid.uuid4()),
        "action": action,
    }
    if action == "addSource":
        value.update(
            {
                "displayName": "Archive",
                "hostPath": "/srv/reachcommander/archive",
                "access": "readOnly",
            }
        )
    if action == "getOperation":
        value["operationId"] = operation_id or str(uuid.uuid4())
    return SourceManagementRequest.parse(json.dumps(value).encode())


class StaticSourceDiscovery:
    def __init__(self, capability: SourceManagementCapability) -> None:
        self.capability = capability

    def check(self) -> SourceManagementCapability:
        return self.capability


class RecordingSourceRunner:
    def __init__(
        self,
        *,
        returncode: int = 0,
        output: bytes = b'{"sourceId":"archive","displayName":"Archive"}\n',
    ) -> None:
        self.result = (returncode, output)
        self.started = threading.Event()
        self.release = threading.Event()
        self.block = False
        self.calls: list[dict[str, object]] = []

    def run(self, argv, *, stdin, env, timeout, shell):  # type: ignore[no-untyped-def]
        self.calls.append(
            {
                "argv": tuple(argv),
                "stdin": bytes(stdin),
                "env": dict(env),
                "timeout": timeout,
                "shell": shell,
            }
        )
        self.started.set()
        if self.block:
            self.release.wait(timeout=2)
        return updater_service.SourceCommandResult(*self.result)


class TimeoutSourceRunner(RecordingSourceRunner):
    def run(self, *args, **kwargs):  # type: ignore[no-untyped-def]
        super().run(*args, **kwargs)
        raise updater_service.SourceCommandTimedOut(
            "/srv/private must never appear in a public response"
        )


class SourceManagementRuntimeTests(unittest.TestCase):
    def supported_discovery(self) -> StaticSourceDiscovery:
        return StaticSourceDiscovery(
            SourceManagementCapability(
                True,
                "supported",
                "Source management is available.",
            )
        )

    def runtime(
        self,
        root: Path,
        *,
        runner: object | None = None,
        gate: object | None = None,
    ):
        return updater_service.SourceManagementRuntime(
            self.supported_discovery(),
            updater_service.AtomicSourceOperationStore(
                root / "state" / "source-runtime-operation.json"
            ),
            runner=runner or RecordingSourceRunner(),
            gate=gate,
            clock=lambda: NOW,
        )

    def test_support_discovery_distinguishes_platform_deployment_and_old_helper(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            command = root / "usr" / "local" / "bin" / "reachcommander"

            unsupported_platform = updater_service.SourceManagementDiscovery(
                root,
                command_path=command,
                platform="win32",
            ).check()
            unsupported_deployment = updater_service.SourceManagementDiscovery(
                root,
                command_path=command,
                platform="linux",
            ).check()

            for relative in (
                ".env",
                "compose.yaml",
                "config/sources.json",
                "state/source-mounts.json",
            ):
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("{}\n", encoding="utf-8")
            command.parent.mkdir(parents=True, exist_ok=True)
            command.write_text("#!/bin/sh\n", encoding="utf-8")
            command.chmod(0o755)

            old_helper = updater_service.SourceManagementDiscovery(
                root,
                command_path=command,
                platform="linux",
            ).check()

            for relative in ("bin/source_management.py", "lib/updater_protocol.py"):
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(
                    "SOURCE_MANAGEMENT_PROTOCOL_VERSION = 5\n",
                    encoding="utf-8",
                )
                path.chmod(0o755 if relative.startswith("bin/") else 0o644)
            supported = updater_service.SourceManagementDiscovery(
                root,
                command_path=command,
                platform="linux",
            ).check()

        self.assertEqual("unsupported_platform", unsupported_platform.reason_code)
        self.assertEqual("unsupported_deployment", unsupported_deployment.reason_code)
        self.assertEqual("installer_upgrade_required", old_helper.reason_code)
        self.assertEqual("supported", supported.reason_code)

    def test_add_is_accepted_quickly_uses_only_fixed_command_and_persists_for_restart(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            helper_journal = root / "state" / "source-operation.json"
            helper_journal.parent.mkdir(parents=True)
            helper_contents = b'{"helper":"recovery state"}\n'
            helper_journal.write_bytes(helper_contents)
            runner = RecordingSourceRunner()
            runner.block = True
            runtime = self.runtime(root, runner=runner)
            request_value = source_request("addSource")

            accepted = runtime.handle(request_value)
            self.assertEqual("accepted", accepted["payload"]["phase"])
            self.assertTrue(runner.started.wait(timeout=1))
            call = runner.calls[0]
            self.assertEqual(updater_service.FIXED_SOURCE_COMMAND, call["argv"])
            self.assertEqual(updater_service.SANITIZED_ENVIRONMENT, call["env"])
            self.assertEqual(updater_service.SOURCE_COMMAND_TIMEOUT_SECONDS, call["timeout"])
            self.assertFalse(call["shell"])
            submitted = json.loads(call["stdin"])
            self.assertEqual(
                {
                    "protocolVersion",
                    "requestId",
                    "action",
                    "displayName",
                    "hostPath",
                    "access",
                },
                set(submitted),
            )
            self.assertNotIn("command", submitted)
            self.assertNotIn("image", submitted)
            self.assertNotIn("environment", submitted)

            operation_id = str(accepted["payload"]["operationId"])
            runner.release.set()
            runtime.wait_for_worker()
            recovered = self.runtime(root).handle(
                source_request("getOperation", operation_id=operation_id)
            )

            self.assertEqual("completed", recovered["payload"]["phase"])
            self.assertEqual("archive", recovered["payload"]["sourceId"])
            self.assertEqual(helper_contents, helper_journal.read_bytes())
            self.assertTrue((root / "state" / "source-runtime-operation.json").is_file())

    def test_one_source_operation_is_serialized_and_update_source_gate_works_both_ways(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            gate = updater_service.DeploymentMutationGate()
            source_runner = RecordingSourceRunner()
            source_runner.block = True
            source_runtime = self.runtime(root, runner=source_runner, gate=gate)
            updater_runner = RecordingRunner()
            update_runtime = UpdaterRuntime(
                StaticDiscovery(snapshot()),
                AtomicUpdateJournal(root / "state" / "system-update.json"),
                runner=updater_runner,
                clock=lambda: NOW,
                mutation_gate=gate,
            )

            source_runtime.handle(source_request("addSource"))
            self.assertTrue(source_runner.started.wait(timeout=1))
            duplicate = source_runtime.handle(source_request("addSource"))
            blocked_update = update_runtime.handle(request("applyConfiguredChannel"))

            self.assertEqual("busy", duplicate["payload"]["code"])
            self.assertEqual("available", blocked_update["phase"])
            self.assertEqual([], updater_runner.argv)
            source_runner.release.set()
            source_runtime.wait_for_worker()

            blocking_update = BlockingRunner()
            update_runtime = UpdaterRuntime(
                StaticDiscovery(snapshot()),
                AtomicUpdateJournal(root / "state" / "system-update.json"),
                runner=blocking_update,
                clock=lambda: NOW,
                mutation_gate=gate,
            )
            update_runtime.handle(request("applyConfiguredChannel"))
            self.assertTrue(blocking_update.started.wait(timeout=1))
            blocked_source = source_runtime.handle(source_request("addSource"))
            self.assertEqual("busy", blocked_source["payload"]["code"])
            blocking_update.release.set()
            update_runtime.wait_for_worker()

    def test_timeout_exit_codes_invalid_output_and_private_diagnostics_are_bounded(self) -> None:
        cases = (
            (TimeoutSourceRunner(), "failed", "source_management_failed"),
            (RecordingSourceRunner(returncode=3), "failed", "validation_failed"),
            (RecordingSourceRunner(returncode=5), "rolledBack", "rolled_back"),
            (
                RecordingSourceRunner(output=b'{"sourceId":"bad","displayName":"Archive","hostPath":"/srv/private"}'),
                "failed",
                "source_management_failed",
            ),
        )
        for runner, phase, reason in cases:
            with self.subTest(phase=phase, reason=reason), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                runtime = self.runtime(root, runner=runner)
                accepted = runtime.handle(source_request("addSource"))
                runtime.wait_for_worker()
                response = runtime.handle(
                    source_request(
                        "getOperation",
                        operation_id=str(accepted["payload"]["operationId"]),
                    )
                )
                encoded = json.dumps(response)

                self.assertEqual(phase, response["payload"]["phase"])
                self.assertEqual(reason, response["payload"]["reasonCode"])
                self.assertNotIn("/srv/private", encoded)
                self.assertLessEqual(len(encoded.encode()), 4_096)

    def test_success_output_must_correlate_to_the_requested_display_name(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime = self.runtime(
                root,
                runner=RecordingSourceRunner(
                    output=b'{"sourceId":"archive","displayName":"Different"}\n'
                ),
            )
            accepted = runtime.handle(source_request("addSource"))
            runtime.wait_for_worker()

            result = runtime.handle(
                source_request(
                    "getOperation",
                    operation_id=str(accepted["payload"]["operationId"]),
                )
            )

        self.assertEqual("failed", result["payload"]["phase"])
        self.assertEqual("source_management_failed", result["payload"]["reasonCode"])

    def test_service_restart_converts_a_nonterminal_runtime_record_to_failure(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            store = updater_service.AtomicSourceOperationStore(
                root / "state" / "source-runtime-operation.json"
            )
            operation_id = str(uuid.uuid4())
            store.write(
                updater_service.SourceManagementOperation(
                    operation_id=operation_id,
                    source_id=None,
                    display_name=None,
                    phase="accepted",
                    reason_code="accepted",
                    detail="ignored",
                    created_at="2026-08-25T11:59:59Z",
                    updated_at="2026-08-25T11:59:59Z",
                )
            )
            restarted = self.runtime(root)

            response = restarted.handle(
                source_request("getOperation", operation_id=operation_id)
            )
            persisted = store.read_optional()

        self.assertEqual("failed", response["payload"]["phase"])
        self.assertEqual("source_management_failed", response["payload"]["reasonCode"])
        self.assertIsNotNone(persisted)
        self.assertEqual("failed", persisted.phase)

    def test_service_restart_reconciles_nonterminal_state_before_any_new_add(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            store = updater_service.AtomicSourceOperationStore(
                root / "state" / "source-runtime-operation.json"
            )
            operation_id = str(uuid.uuid4())
            store.write(
                updater_service.SourceManagementOperation(
                    operation_id=operation_id,
                    source_id=None,
                    display_name=None,
                    phase="accepted",
                    reason_code="accepted",
                    detail="ignored",
                    created_at="2026-08-25T11:59:59Z",
                    updated_at="2026-08-25T11:59:59Z",
                )
            )

            self.runtime(root)
            reconciled = store.read_optional()

        self.assertIsNotNone(reconciled)
        self.assertEqual(operation_id, reconciled.operation_id)
        self.assertEqual("failed", reconciled.phase)
        self.assertEqual("source_management_failed", reconciled.reason_code)

    def test_worker_start_failure_is_terminal_and_releases_the_mutation_gate(self) -> None:
        class StartFailingThread:
            def __init__(self, **_kwargs):
                pass

            def start(self) -> None:
                raise RuntimeError("private thread failure")

            def is_alive(self) -> bool:
                return False

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            gate = updater_service.DeploymentMutationGate()
            runtime = self.runtime(root, gate=gate)
            with mock.patch(
                "deploy.updater_service.threading.Thread",
                StartFailingThread,
            ):
                response = runtime.handle(source_request("addSource"))
            persisted = updater_service.AtomicSourceOperationStore(
                root / "state" / "source-runtime-operation.json"
            ).read_optional()

            self.assertEqual("source_management_failed", response["payload"]["code"])
            self.assertIsNotNone(persisted)
            self.assertEqual("failed", persisted.phase)
            self.assertTrue(gate.try_acquire("probe"))
            gate.release("probe")

    def test_reads_helper_progress_without_rewriting_its_recovery_journal(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            helper_journal = root / "state" / "source-operation.json"
            helper_journal.parent.mkdir(parents=True)
            helper_value = {
                "schemaVersion": 1,
                "transactionId": str(uuid.uuid4()),
                "sourceId": "archive",
                "displayName": "Archive",
                "phase": "staging",
                "reasonCode": "in_progress",
                "updatedAt": "2026-08-25T12:00:00Z",
            }
            helper_contents = (json.dumps(helper_value) + "\n").encode()
            helper_journal.write_bytes(helper_contents)
            if os.name != "nt":
                helper_journal.chmod(0o600)
            runner = RecordingSourceRunner()
            runner.block = True
            runtime = updater_service.SourceManagementRuntime(
                self.supported_discovery(),
                updater_service.AtomicSourceOperationStore(
                    root / "state" / "source-runtime-operation.json"
                ),
                runner=runner,
                helper_status_reader=updater_service.SourceTransactionStatusReader(
                    helper_journal
                ),
                clock=lambda: NOW,
            )

            accepted = runtime.handle(source_request("addSource"))
            self.assertTrue(runner.started.wait(timeout=1))
            progress = runtime.handle(
                source_request(
                    "getOperation",
                    operation_id=str(accepted["payload"]["operationId"]),
                )
            )
            runner.release.set()
            runtime.wait_for_worker()

            self.assertEqual("applying", progress["payload"]["phase"])
            self.assertEqual("archive", progress["payload"]["sourceId"])
            self.assertEqual(helper_contents, helper_journal.read_bytes())

    def test_helper_progress_rejects_boolean_schema_version(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "source-operation.json"
            path.write_text(
                json.dumps(
                    {
                        "schemaVersion": True,
                        "transactionId": str(uuid.uuid4()),
                        "sourceId": "archive",
                        "displayName": "Archive",
                        "phase": "staging",
                        "reasonCode": "in_progress",
                        "updatedAt": "2026-08-25T12:00:00Z",
                    }
                ),
                encoding="utf-8",
            )
            if os.name != "nt":
                path.chmod(0o600)

            with self.assertRaises(updater_service.JournalError):
                updater_service.SourceTransactionStatusReader(path).read_optional()


class AtomicSourceOperationStoreTests(unittest.TestCase):
    @staticmethod
    def operation() -> object:
        return updater_service.SourceManagementOperation(
            operation_id=str(uuid.uuid4()),
            source_id="archive",
            display_name="Archive",
            phase="completed",
            reason_code="completed",
            detail="ignored",
            created_at="2026-08-25T12:00:00Z",
            updated_at="2026-08-25T12:00:01Z",
        )

    def test_store_is_exact_bounded_atomic_private_and_leaves_no_temporary_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "state" / "source-runtime-operation.json"
            store = updater_service.AtomicSourceOperationStore(path)

            store.write(self.operation())
            recovered = store.read_optional()

            self.assertIsNotNone(recovered)
            self.assertEqual([path.name], [item.name for item in path.parent.iterdir()])
            self.assertLessEqual(len(path.read_bytes()), 4_096)
            if os.name != "nt":
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
                self.assertEqual(0o700, stat.S_IMODE(path.parent.stat().st_mode))

    def test_store_rejects_duplicates_unknown_fields_nonregular_and_oversize(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "state" / "source-runtime-operation.json"
            path.parent.mkdir(mode=0o700)
            store = updater_service.AtomicSourceOperationStore(path)
            operation = self.operation()
            store.write(operation)
            valid = path.read_text(encoding="utf-8")
            invalid_values = (
                valid.replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1'),
                valid.replace('"schemaVersion":1', '"schemaVersion":1,"hostPath":"/srv/private"'),
                valid.replace('"schemaVersion":1', '"schemaVersion":true'),
                "x" * 4_097,
            )
            for invalid in invalid_values:
                with self.subTest(invalid=invalid[:32]):
                    path.write_text(invalid, encoding="utf-8")
                    with self.assertRaises(updater_service.JournalError):
                        store.read_optional()
            path.unlink()
            path.mkdir()
            with self.assertRaises(updater_service.JournalError):
                store.read_optional()

    def test_store_rejects_symlink_and_unsafe_mode(self) -> None:
        if os.name == "nt":
            self.skipTest("POSIX protected-file modes and symlinks are required")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "state" / "source-runtime-operation.json"
            path.parent.mkdir(mode=0o700)
            outside = root / "outside"
            outside.write_text("{}", encoding="utf-8")
            path.symlink_to(outside)
            store = updater_service.AtomicSourceOperationStore(path)
            with self.assertRaises(updater_service.JournalError):
                store.read_optional()
            path.unlink()
            store.write(self.operation())
            path.chmod(0o644)
            with self.assertRaises(updater_service.JournalError):
                store.read_optional()


class UpdaterRuntimeTests(unittest.TestCase):
    def runtime(
        self,
        root: Path,
        *,
        runner: object | None = None,
        discovery: StaticDiscovery | None = None,
        diagnostics: object | None = None,
    ) -> UpdaterRuntime:
        return UpdaterRuntime(
            discovery or StaticDiscovery(snapshot()),
            AtomicUpdateJournal(root / "system-update.json"),
            runner=runner or RecordingRunner(),
            clock=lambda: NOW,
            trace_store=ProtectedUpdateTraceStore(
                root / "update-traces",
                clock=lambda: NOW,
            ),
            diagnostics_collector=diagnostics,
        )

    def test_apply_uses_only_fixed_command_and_sanitized_environment(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            runner = RecordingRunner(exit_code=0)
            runtime = self.runtime(Path(directory), runner=runner)

            response = runtime.handle(request("applyConfiguredChannel"))
            self.assertEqual("applying", response["phase"])
            runtime.wait_for_worker()

            self.assertEqual([["/usr/local/bin/reachcommander", "update"]], runner.argv)
            self.assertEqual(list(FIXED_COMMAND), runner.argv[0])
            self.assertEqual([SANITIZED_ENVIRONMENT], runner.environments)
            self.assertEqual([300], runner.timeouts)
            self.assertEqual([False], runner.shell_values)
            self.assertNotIn("stable", runner.argv[0])

    def test_update_releases_shared_gate_when_journal_begin_fails(self) -> None:
        class BeginFailingJournal(AtomicUpdateJournal):
            def begin(self, snapshot, now):  # type: ignore[no-untyped-def]
                raise JournalError("private journal failure")

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            gate = updater_service.DeploymentMutationGate()
            runtime = UpdaterRuntime(
                StaticDiscovery(snapshot()),
                BeginFailingJournal(root / "system-update.json"),
                runner=RecordingRunner(),
                clock=lambda: NOW,
                mutation_gate=gate,
            )

            with self.assertRaises(JournalError):
                runtime.handle(request("applyConfiguredChannel"))

            self.assertTrue(gate.try_acquire("probe"))
            gate.release("probe")

    def test_protocol_responses_preserve_v1_v2_shapes_and_v3_adds_trace(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            runtime = self.runtime(Path(directory))

            legacy = runtime.handle(request("check", LEGACY_PROTOCOL_VERSION))
            detailed = runtime.handle(request("check", DETAILED_PROTOCOL_VERSION))
            traced = runtime.handle(request("check", TRACE_PROTOCOL_VERSION))

        self.assertEqual(LEGACY_PROTOCOL_VERSION, legacy["protocolVersion"])
        legacy_fields = {
            "protocolVersion",
            "requestId",
            "supported",
            "channel",
            "currentVersion",
            "targetVersion",
            "currentDigest",
            "targetDigest",
            "phase",
            "reasonCode",
            "detail",
            "operationId",
            "lastCheckedAt",
            "updatedAt",
        }
        self.assertEqual(legacy_fields, set(legacy))
        self.assertNotIn("progressStage", legacy)
        self.assertEqual(DETAILED_PROTOCOL_VERSION, detailed["protocolVersion"])
        self.assertEqual(legacy_fields | {"progressStage"}, set(detailed))
        self.assertIn("progressStage", detailed)
        self.assertIsNone(detailed["progressStage"])
        self.assertNotIn("trace", detailed)
        self.assertEqual(TRACE_PROTOCOL_VERSION, traced["protocolVersion"])
        self.assertEqual(
            legacy_fields | {"progressStage", "trace"},
            set(traced),
        )
        self.assertIn("progressStage", traced)
        self.assertIn("trace", traced)
        self.assertIsNone(traced["trace"])

    def test_protocol_v4_collects_diagnostics_without_changing_status_shapes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            diagnostics = StaticDiagnosticCollector()
            runtime = self.runtime(Path(directory), diagnostics=diagnostics)

            response = runtime.handle(
                request("collectDiagnostics", DIAGNOSTIC_PROTOCOL_VERSION)
            )
            legacy = runtime.handle(request("check", LEGACY_PROTOCOL_VERSION))

        self.assertEqual(
            {"protocolVersion", "requestId", "diagnostics"},
            set(response),
        )
        self.assertEqual(DIAGNOSTIC_PROTOCOL_VERSION, response["protocolVersion"])
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
            set(response["diagnostics"]),
        )
        self.assertEqual(1, diagnostics.calls)
        self.assertNotIn("progressStage", legacy)
        self.assertNotIn("trace", legacy)

    def test_v3_response_contains_only_the_bounded_public_trace(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime = self.runtime(
                root,
                runner=RecordingRunner(
                    trace_events=(
                        ("downloadStarted", "started", "downloading"),
                        ("hostActivity", "activity", "downloading"),
                        ("downloadCompleted", "succeeded", "downloading"),
                    )
                ),
            )

            runtime.handle(request("applyConfiguredChannel", TRACE_PROTOCOL_VERSION))
            runtime.wait_for_worker()
            response = runtime.handle(request("check", TRACE_PROTOCOL_VERSION))

        trace = response["trace"]
        self.assertIsInstance(trace, dict)
        self.assertEqual(
            {"startedAt", "elapsedSeconds", "lastActivityAt", "events"},
            set(trace),
        )
        self.assertLessEqual(len(trace["events"]), 32)
        self.assertEqual("operationAccepted", trace["events"][0]["code"])
        self.assertEqual("operationCompleted", trace["events"][-1]["code"])
        encoded = json.dumps(trace)
        self.assertNotIn("exitCode", encoded)
        self.assertNotIn("timeoutSeconds", encoded)
        self.assertNotIn("sha256:", encoded)
        self.assertNotIn("/private", encoded)
        self.assertLessEqual(len(json.dumps(response).encode()), 65_536)

    def test_v2_and_v3_responses_derive_live_progress_from_the_sanitized_trace(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            journal = AtomicUpdateJournal(root / "system-update.json")
            operation = journal.begin(snapshot(), NOW)
            traces = ProtectedUpdateTraceStore(root / "update-traces", clock=lambda: NOW)
            traces.start(str(operation["operationId"]), NOW)
            traces.append(
                str(operation["operationId"]),
                "installStarted",
                "started",
                NOW,
                stage="installing",
            )

            detailed = updater_service.protocol_response(
                request("check", DETAILED_PROTOCOL_VERSION),
                operation,
                trace_store=traces,
                now=NOW,
            )
            traced = updater_service.protocol_response(
                request("check", TRACE_PROTOCOL_VERSION),
                operation,
                trace_store=traces,
                now=NOW,
            )

        self.assertEqual("installing", detailed["progressStage"])
        self.assertNotIn("trace", detailed)
        self.assertEqual("installing", traced["progressStage"])
        self.assertIn("trace", traced)

    def test_timeout_is_terminal_traceable_and_uses_a_fixed_public_reason(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime = self.runtime(root, runner=TimeoutRunner())

            runtime.handle(request("applyConfiguredChannel", TRACE_PROTOCOL_VERSION))
            runtime.wait_for_worker()
            response = runtime.handle(request("check", TRACE_PROTOCOL_VERSION))

        self.assertEqual("failed", response["phase"])
        self.assertEqual("update_command_timeout", response["reasonCode"])
        self.assertEqual(
            [
                "operationAccepted",
                "commandTimedOut",
                "terminationRequested",
                "operationFailed",
            ],
            [event["code"] for event in response["trace"]["events"]],
        )
        self.assertNotIn("private updater output", json.dumps(response))

    def test_v3_trace_is_scoped_to_the_journal_operation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runner = RecordingRunner(
                trace_events=(("downloadStarted", "started", "downloading"),)
            )
            runtime = self.runtime(root, runner=runner)

            first = runtime.handle(
                request("applyConfiguredChannel", TRACE_PROTOCOL_VERSION)
            )
            runtime.wait_for_worker()
            runner.trace_events = (("backupStarted", "started", "installing"),)
            second = runtime.handle(
                request("applyConfiguredChannel", TRACE_PROTOCOL_VERSION)
            )
            runtime.wait_for_worker()
            current = runtime.handle(request("check", TRACE_PROTOCOL_VERSION))

        self.assertNotEqual(first["operationId"], second["operationId"])
        codes = [event["code"] for event in current["trace"]["events"]]
        self.assertIn("backupStarted", codes)
        self.assertNotIn("downloadStarted", codes)

    def test_interrupted_service_closes_only_the_matching_operation_trace(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            journal = AtomicUpdateJournal(root / "system-update.json")
            operation = journal.begin(snapshot(), NOW)
            traces = ProtectedUpdateTraceStore(root / "update-traces", clock=lambda: NOW)
            traces.start(str(operation["operationId"]), NOW)
            runtime = UpdaterRuntime(
                StaticDiscovery(snapshot()),
                journal,
                runner=RecordingRunner(),
                clock=lambda: NOW,
                trace_store=traces,
            )

            response = runtime.handle(request("check", TRACE_PROTOCOL_VERSION))

        self.assertEqual("update_interrupted", response["reasonCode"])
        self.assertEqual("operationFailed", response["trace"]["events"][-1]["code"])

    def test_trace_persistence_failure_does_not_change_the_update_result(self) -> None:
        class FailingTraceStore:
            def start(self, *_args, **_kwargs):  # type: ignore[no-untyped-def]
                raise TraceError("trace unavailable")

            def public_snapshot(self, *_args, **_kwargs):  # type: ignore[no-untyped-def]
                raise TraceError("trace unavailable")

            def prune(self, *_args, **_kwargs):  # type: ignore[no-untyped-def]
                raise TraceError("trace unavailable")

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            journal = AtomicUpdateJournal(root / "system-update.json")
            runtime = UpdaterRuntime(
                StaticDiscovery(snapshot()),
                journal,
                runner=RecordingRunner(),
                clock=lambda: NOW,
                trace_store=FailingTraceStore(),
            )

            runtime.handle(request("applyConfiguredChannel", TRACE_PROTOCOL_VERSION))
            runtime.wait_for_worker()

            self.assertEqual("completed", journal.read_optional()["phase"])

    def test_apply_is_idempotent_while_operation_is_active(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            runner = BlockingRunner()
            runtime = self.runtime(Path(directory), runner=runner)

            first = runtime.handle(request("applyConfiguredChannel"))
            self.assertTrue(runner.started.wait(timeout=1))
            second = runtime.handle(request("applyConfiguredChannel"))

            self.assertEqual(first["operationId"], second["operationId"])
            self.assertEqual(0, len(runner.argv))
            runner.release.set()
            runtime.wait_for_worker()
            self.assertEqual(1, len(runner.argv))

    def test_exit_codes_map_to_completed_rolled_back_and_failed(self) -> None:
        expectations = ((0, "completed"), (2, "rolledBack"), (3, "failed"), (99, "failed"))
        for exit_code, expected in expectations:
            with self.subTest(exit_code=exit_code), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                runtime = self.runtime(root, runner=RecordingRunner(exit_code))
                runtime.handle(request("applyConfiguredChannel"))
                runtime.wait_for_worker()
                journal = AtomicUpdateJournal(root / "system-update.json").read_optional()
                self.assertIsNotNone(journal)
                self.assertEqual(expected, journal["phase"])
                self.assertNotIn("sensitive output", json.dumps(journal))

    def test_apply_persists_live_progress_and_retains_it_in_terminal_state(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runner = RecordingRunner(
                progress_stages=(
                    "downloading",
                    "installing",
                    "restarting",
                    "healthChecking",
                )
            )
            runtime = self.runtime(root, runner=runner)

            runtime.handle(request("applyConfiguredChannel"))
            runtime.wait_for_worker()

            journal = AtomicUpdateJournal(root / "system-update.json").read_optional()
            self.assertEqual("completed", journal["phase"])
            self.assertEqual("healthChecking", journal["progressStage"])

    def test_reader_progress_never_writes_the_terminal_journal(self) -> None:
        class ProgressFailingJournal(AtomicUpdateJournal):
            def advance(self, operation, stage, now):  # type: ignore[no-untyped-def]
                raise JournalError("progress unavailable")

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            journal = ProgressFailingJournal(root / "system-update.json")
            runtime = UpdaterRuntime(
                StaticDiscovery(snapshot()),
                journal,
                runner=RecordingRunner(progress_stages=("downloading",)),
                clock=lambda: NOW,
            )

            runtime.handle(request("applyConfiguredChannel"))
            runtime.wait_for_worker()

            self.assertEqual("completed", journal.read_optional()["phase"])

    def test_late_progress_callback_cannot_restore_a_terminal_operation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runner = LateProgressRunner()
            runtime = self.runtime(root, runner=runner)

            runtime.handle(request("applyConfiguredChannel"))
            runtime.wait_for_worker()
            runner.publish_late_progress("downloading")

            journal = AtomicUpdateJournal(root / "system-update.json").read_optional()
            self.assertEqual("completed", journal["phase"])

    def test_blocking_progress_persistence_cannot_delay_terminal_journal(self) -> None:
        class BlockingAdvanceJournal(AtomicUpdateJournal):
            def __init__(self, path: Path) -> None:
                super().__init__(path)
                self.advance_entered = threading.Event()
                self.release_advance = threading.Event()

            def advance(self, operation, stage, now):  # type: ignore[no-untyped-def]
                with self._lock:
                    self.advance_entered.set()
                    self.release_advance.wait(timeout=5)
                    return dict(operation)

        class ConcurrentProgressRunner(RecordingRunner):
            def __init__(self, advance_entered: threading.Event) -> None:
                super().__init__()
                self.advance_entered = advance_entered
                self.callback_thread: threading.Thread | None = None

            def run(self, *args, **kwargs) -> CommandResult:  # type: ignore[no-untyped-def]
                callback = kwargs["progress_callback"]
                self.callback_thread = threading.Thread(
                    target=lambda: callback("downloading"),
                    daemon=True,
                )
                self.callback_thread.start()
                self.advance_entered.wait(timeout=0.25)
                return CommandResult(0, "")

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            journal = BlockingAdvanceJournal(root / "system-update.json")
            runner = ConcurrentProgressRunner(journal.advance_entered)
            runtime = UpdaterRuntime(
                StaticDiscovery(snapshot()),
                journal,
                runner=runner,
                clock=lambda: NOW,
            )
            worker_finished = threading.Event()

            runtime.handle(request("applyConfiguredChannel"))
            waiter = threading.Thread(
                target=lambda: (runtime.wait_for_worker(), worker_finished.set()),
                daemon=True,
            )
            waiter.start()
            try:
                self.assertTrue(worker_finished.wait(timeout=1))
                self.assertEqual("completed", journal.read_optional()["phase"])
            finally:
                journal.release_advance.set()
                waiter.join(timeout=2)
                if runner.callback_thread is not None:
                    runner.callback_thread.join(timeout=2)

    def test_spawn_failure_is_sanitized_and_recorded(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime = self.runtime(root, runner=RaisingRunner())
            runtime.handle(request("applyConfiguredChannel"))
            runtime.wait_for_worker()

            journal = AtomicUpdateJournal(root / "system-update.json").read_optional()
            self.assertEqual("failed", journal["phase"])
            self.assertEqual("update_failed", journal["reasonCode"])
            self.assertNotIn(str(root), json.dumps(journal))
            self.assertNotIn("/opt/reachcommander", json.dumps(journal))

    def test_check_persists_discovery_and_apply_rechecks_the_target(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            discovery = StaticDiscovery(snapshot())
            runtime = self.runtime(Path(directory), discovery=discovery)

            checked = runtime.handle(request("check"))
            applied = runtime.handle(request("applyConfiguredChannel"))
            runtime.wait_for_worker()

            self.assertEqual("available", checked["phase"])
            self.assertEqual("applying", applied["phase"])
            self.assertEqual(2, discovery.calls)

    def test_terminal_journal_is_recovered_after_application_restart(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime = self.runtime(root, runner=RecordingRunner(exit_code=2))
            runtime.handle(request("applyConfiguredChannel"))
            runtime.wait_for_worker()

            recovered = self.runtime(root).handle(request("check"))

            self.assertEqual("rolledBack", recovered["phase"])
            self.assertNotIn(str(root), json.dumps(recovered))

    def test_interrupted_applying_journal_recovers_as_failed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            journal = AtomicUpdateJournal(root / "system-update.json")
            journal.begin(snapshot(), NOW)

            recovered = self.runtime(root).handle(request("check"))

            self.assertEqual("failed", recovered["phase"])
            self.assertEqual("update_interrupted", recovered["reasonCode"])

    def test_invalid_journal_schema_fails_closed_without_exposing_contents(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "system-update.json").write_text(
                json.dumps({"schemaVersion": 999, "detail": str(root)}), encoding="utf-8"
            )

            response = self.runtime(root).handle(request("check"))

            self.assertEqual("failed", response["phase"])
            self.assertEqual("updater_journal_invalid", response["reasonCode"])
            self.assertNotIn(str(root), json.dumps(response))


class AtomicUpdateJournalTests(unittest.TestCase):
    def test_journal_is_atomic_bounded_sanitized_and_has_no_temporary_leaks(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            journal = AtomicUpdateJournal(root / "system-update.json")

            operation = journal.begin(snapshot(), NOW)
            finished = journal.finish(operation, "completed", NOW)

            self.assertEqual(JOURNAL_SCHEMA, finished["schemaVersion"])
            self.assertEqual("completed", finished["phase"])
            self.assertRegex(finished["operationId"], r"^[0-9a-f-]{36}$")
            self.assertEqual(["system-update.json"], [item.name for item in root.iterdir()])
            encoded = (root / "system-update.json").read_text(encoding="utf-8")
            self.assertNotIn(str(root), encoded)
            self.assertLessEqual(len(finished["detail"]), 240)
            if os.name != "nt":
                self.assertEqual(0o600, stat.S_IMODE((root / "system-update.json").stat().st_mode))

    def test_rejects_symlinked_journal_and_unknown_terminal_phase(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            outside = root / "outside.json"
            outside.write_text("{}", encoding="utf-8")
            path = root / "system-update.json"
            try:
                path.symlink_to(outside)
            except OSError as error:
                self.skipTest(f"symlinks unavailable: {error}")

            journal = AtomicUpdateJournal(path)
            with self.assertRaisesRegex(JournalError, "regular protected file"):
                journal.read_optional()
            path.unlink()
            operation = journal.begin(snapshot(), NOW)
            with self.assertRaisesRegex(JournalError, "terminal phase"):
                journal.finish(operation, "applying", NOW)

    def test_accepts_forward_and_recovery_progress_transitions(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            journal = AtomicUpdateJournal(Path(directory) / "system-update.json")
            operation = journal.begin(snapshot(), NOW)

            for stage in (
                "downloading",
                "installing",
                "restarting",
                "healthChecking",
                "restoring",
                "restartingPrevious",
                "verifyingRecovery",
            ):
                operation = journal.advance(operation, stage, NOW)
                self.assertEqual(stage, operation["progressStage"])

    def test_ignores_duplicate_unknown_and_backward_progress(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            journal = AtomicUpdateJournal(Path(directory) / "system-update.json")
            operation = journal.begin(snapshot(), NOW)
            operation = journal.advance(operation, "downloading", NOW)
            operation = journal.advance(operation, "installing", NOW)

            self.assertEqual(
                operation,
                journal.advance(operation, "installing", NOW),
            )
            self.assertEqual(
                operation,
                journal.advance(operation, "downloading", NOW),
            )
            self.assertEqual(
                operation,
                journal.advance(operation, "notAStage", NOW),
            )

    def test_stale_progress_cannot_replace_a_terminal_or_newer_operation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            journal = AtomicUpdateJournal(Path(directory) / "system-update.json")
            first = journal.begin(snapshot(), NOW)
            journal.finish(first, "completed", NOW)

            journal.advance(first, "downloading", NOW)
            self.assertEqual("completed", journal.read_optional()["phase"])

            second = journal.begin(snapshot(), NOW)
            journal.advance(first, "downloading", NOW)
            persisted = journal.read_optional()
            self.assertEqual(second["operationId"], persisted["operationId"])
            self.assertIsNone(persisted["progressStage"])

    def test_reads_legacy_journal_without_progress_and_upgrades_on_write(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            journal = AtomicUpdateJournal(root / "system-update.json")
            operation = journal.begin(snapshot(), NOW)
            legacy = dict(operation)
            legacy["schemaVersion"] = 1
            legacy.pop("progressStage", None)
            journal.path.write_text(json.dumps(legacy), encoding="utf-8")

            recovered = journal.read_optional()
            self.assertIsNone(recovered["progressStage"])
            advanced = journal.advance(recovered, "downloading", NOW)

            self.assertEqual(JOURNAL_SCHEMA, advanced["schemaVersion"])
            self.assertEqual("downloading", advanced["progressStage"])


class TrustedNetworkBoundaryTests(unittest.TestCase):
    def test_github_provider_uses_only_the_fixed_latest_release_endpoint(self) -> None:
        response = mock.MagicMock()
        response.__enter__.return_value.read.return_value = json.dumps(
            {"tag_name": "v1.4.0", "draft": False, "prerelease": False}
        ).encode()
        opener = mock.Mock(return_value=response)

        release = GitHubLatestReleaseProvider(opener=opener)()

        request_value = opener.call_args.args[0]
        self.assertEqual(
            "https://api.github.com/repos/dragosniamtu/reach-commander/releases/latest",
            request_value.full_url,
        )
        self.assertEqual("v1.4.0", release.tag_name)
        self.assertEqual(10, opener.call_args.kwargs["timeout"])

    def test_docker_resolver_rejects_untrusted_input_before_process_execution(self) -> None:
        runner = SequenceRunner([])
        resolver = DockerImageResolver(runner=runner)

        with self.assertRaisesRegex(ValueError, "trusted"):
            resolver("attacker.example/root:latest")

        self.assertEqual([], runner.argv)

    def test_docker_resolver_uses_fixed_argv_and_validates_digest_and_labels(self) -> None:
        runner = SequenceRunner(
            [
                CommandResult(0, "pulled"),
                CommandResult(0, f"{TRUSTED_IMAGE_REPOSITORY}@{TARGET_DIGEST}\n"),
                CommandResult(0, "v1.4.0\n"),
                CommandResult(0, "a" * 40 + "\n"),
            ]
        )
        resolver = DockerImageResolver(runner=runner)

        image = resolver(TARGET_REFERENCE)

        self.assertEqual(ResolvedImage(TARGET_REFERENCE, TARGET_DIGEST, "v1.4.0", "a" * 40), image)
        self.assertEqual(["/usr/bin/docker", "pull", TARGET_REFERENCE], runner.argv[0])
        self.assertTrue(all(TARGET_REFERENCE in argv for argv in runner.argv))
        self.assertTrue(all("sh" not in argv and "bash" not in argv for argv in runner.argv))


class UpdaterSocketServerTests(unittest.TestCase):
    def setUp(self) -> None:
        if not hasattr(socket, "AF_UNIX"):
            self.skipTest("Unix-domain sockets are unavailable")
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.runtime = UpdaterRuntime(
            StaticDiscovery(snapshot("current")),
            AtomicUpdateJournal(self.root / "state" / "system-update.json"),
            runner=RecordingRunner(),
            clock=lambda: NOW,
        )
        self.source_runner = RecordingSourceRunner()
        self.source_runtime = updater_service.SourceManagementRuntime(
            StaticSourceDiscovery(
                SourceManagementCapability(
                    True,
                    "supported",
                    "Source management is available.",
                )
            ),
            updater_service.AtomicSourceOperationStore(
                self.root / "state" / "source-runtime-operation.json"
            ),
            runner=self.source_runner,
            clock=lambda: NOW,
        )
        self.server = UpdaterSocketServer(
            self.runtime,
            self.root / "run" / "updater.sock",
            runtime_gid=os.getgid() if hasattr(os, "getgid") else None,
            request_timeout=0.1,
            source_runtime=self.source_runtime,
        )
        self.server.start()

    def tearDown(self) -> None:
        if hasattr(self, "server"):
            self.server.close()
        if hasattr(self, "temporary"):
            self.temporary.cleanup()

    def exchange(self, raw: bytes) -> dict[str, object]:
        worker = threading.Thread(target=self.server.serve_once, daemon=True)
        worker.start()
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(2)
            client.connect(str(self.server.socket_path))
            client.sendall(raw)
            response = b""
            while not response.endswith(b"\n"):
                chunk = client.recv(4096)
                if not chunk:
                    break
                response += chunk
        worker.join(timeout=2)
        self.assertFalse(worker.is_alive())
        return json.loads(response)

    def test_accepts_exactly_one_newline_delimited_request(self) -> None:
        raw = json.dumps(
            {"protocolVersion": 1, "requestId": str(uuid.uuid4()), "action": "check"}
        ).encode()

        response = self.exchange(raw + b"\n")

        self.assertEqual(1, response["protocolVersion"])
        self.assertEqual("current", response["phase"])

    def test_routes_only_strict_bounded_v5_messages_to_source_management(self) -> None:
        request_id = str(uuid.uuid4())
        status = self.exchange(
            json.dumps(
                {
                    "protocolVersion": 5,
                    "requestId": request_id,
                    "action": "status",
                }
            ).encode()
            + b"\n"
        )
        duplicate = self.exchange(
            (
                '{"protocolVersion":5,"requestId":"'
                + request_id
                + '","action":"status","action":"status"}\n'
            ).encode()
        )
        oversized = self.exchange(
            json.dumps(
                {
                    "protocolVersion": 5,
                    "requestId": str(uuid.uuid4()),
                    "action": "addSource",
                    "displayName": "Archive",
                    "hostPath": "/srv/" + "x" * 4_100,
                    "access": "readOnly",
                }
            ).encode()
            + b"\n"
        )
        browser_command = self.exchange(
            json.dumps(
                {
                    "protocolVersion": 5,
                    "requestId": str(uuid.uuid4()),
                    "action": "addSource",
                    "displayName": "Archive",
                    "hostPath": "/srv/archive",
                    "access": "readOnly",
                    "command": "id",
                }
            ).encode()
            + b"\n"
        )

        self.assertEqual(5, status["protocolVersion"])
        self.assertEqual("supported", status["payload"]["reasonCode"])
        self.assertEqual("invalid_request", duplicate["payload"]["code"])
        self.assertEqual("request_too_large", oversized["payload"]["code"])
        self.assertEqual("invalid_request", browser_command["payload"]["code"])
        self.assertEqual([], self.source_runner.calls)

    def test_rejects_multiple_oversized_and_timed_out_messages(self) -> None:
        raw = json.dumps(
            {"protocolVersion": 1, "requestId": str(uuid.uuid4()), "action": "check"}
        ).encode()
        multiple = self.exchange(raw + b"\n" + raw + b"\n")
        self.assertEqual("invalid_request", multiple["reasonCode"])

        oversized = self.exchange(b"x" * (MAX_MESSAGE_BYTES + 1) + b"\n")
        self.assertEqual("request_too_large", oversized["reasonCode"])

        worker = threading.Thread(target=self.server.serve_once, daemon=True)
        worker.start()
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(2)
            client.connect(str(self.server.socket_path))
            timed_out = json.loads(client.recv(4096))
        worker.join(timeout=2)
        self.assertEqual("request_timeout", timed_out["reasonCode"])

    def test_runtime_directory_and_socket_modes_are_restricted_and_close_cleans_up(self) -> None:
        if os.name != "nt":
            self.assertEqual(0o750, stat.S_IMODE(self.server.socket_path.parent.stat().st_mode))
            self.assertEqual(0o660, stat.S_IMODE(self.server.socket_path.stat().st_mode))

        self.server.close()

        self.assertFalse(self.server.socket_path.exists())


def _process_is_running(process_id: int) -> bool:
    try:
        os.kill(process_id, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True

    stat_path = Path(f"/proc/{process_id}/stat")
    if stat_path.exists():
        fields = stat_path.read_text(encoding="utf-8").split()
        if len(fields) > 2 and fields[2] == "Z":
            return False
    return True


class ServiceProcessContractTests(unittest.TestCase):
    def test_source_protocol_routing_fails_closed_on_duplicate_version_fields(self) -> None:
        request_id = str(uuid.uuid4())
        duplicate = (
            '{"protocolVersion":5,"protocolVersion":4,"requestId":"'
            + request_id
            + '","action":"status"}'
        ).encode()

        self.assertTrue(updater_service._source_protocol_requested(duplicate))
        with self.assertRaisesRegex(updater_service.ProtocolError, "duplicate"):
            SourceManagementRequest.parse(duplicate)

    def test_source_runner_accepts_only_fixed_bounded_stdin_without_a_shell(self) -> None:
        import sys

        script = (
            "import sys;"
            "data=sys.stdin.buffer.read();"
            "sys.stdout.buffer.write(b'{\"sourceId\":\"archive\",\"displayName\":\"Archive\"}\\n')"
        )
        command = (sys.executable, "-c", script)
        runner = updater_service.SourceManagementCommandRunner()
        with mock.patch("deploy.updater_service.FIXED_SOURCE_COMMAND", command):
            result = runner.run(
                command,
                stdin=b'{"bounded":true}\n',
                env=SANITIZED_ENVIRONMENT,
                timeout=5,
                shell=False,
            )
            with self.assertRaisesRegex(ValueError, "fixed"):
                runner.run(
                    ("/bin/sh", "-c", "id"),
                    stdin=b"{}\n",
                    env=SANITIZED_ENVIRONMENT,
                    timeout=5,
                    shell=False,
                )
            with self.assertRaisesRegex(ValueError, "input"):
                runner.run(
                    command,
                    stdin=b"x" * 4_097,
                    env=SANITIZED_ENVIRONMENT,
                    timeout=5,
                    shell=False,
                )
            with self.assertRaisesRegex(ValueError, "fixed"):
                runner.run(
                    command,
                    stdin=b"{}\n",
                    env=SANITIZED_ENVIRONMENT,
                    timeout=5,
                    shell=True,
                )

        self.assertEqual(0, result.returncode)
        self.assertEqual(
            b'{"sourceId":"archive","displayName":"Archive"}\n',
            result.output,
        )

    @unittest.skipIf(os.name == "nt", "Linux process groups are required")
    def test_timeout_kills_descendant_and_never_waits_forever_for_output_pipe(self) -> None:
        import sys

        descendant = (
            "import signal,time;"
            "signal.signal(signal.SIGTERM, signal.SIG_IGN);"
            "print('descendant-ready', flush=True);time.sleep(60)"
        )
        child = (
            "import subprocess,sys,time;"
            f"process=subprocess.Popen([sys.executable,'-c',{descendant!r}]);"
            "print(f'descendant={process.pid}', flush=True);time.sleep(60)"
        )
        started = time.monotonic()

        descendant_pid: int | None = None
        try:
            with self.assertRaises(CommandTimedOut) as raised:
                SubprocessCommandRunner().run(
                    (sys.executable, "-c", child),
                    env=os.environ,
                    timeout=1,
                    shell=False,
                )
            descendant_pid = int(
                next(
                    line.split("=", 1)[1]
                    for line in raised.exception.output.splitlines()
                    if line.startswith("descendant=")
                )
            )

            deadline = time.monotonic() + 2
            while time.monotonic() < deadline and _process_is_running(descendant_pid):
                time.sleep(0.05)

            self.assertFalse(_process_is_running(descendant_pid))
        finally:
            if descendant_pid is not None and _process_is_running(descendant_pid):
                os.kill(descendant_pid, signal.SIGKILL)

        self.assertLess(time.monotonic() - started, 8)
        self.assertIn("descendant-ready", raised.exception.output)

    def test_subprocess_runner_emits_only_valid_trace_markers_and_coalesces_activity(self) -> None:
        import sys

        script = "\n".join(
            (
                "print('REACHCOMMANDER_UPDATE_STAGE=downloading')",
                "print('REACHCOMMANDER_UPDATE_EVENT=downloadStarted:started')",
                "print('download output one')",
                "print('download output two')",
                "print('REACHCOMMANDER_UPDATE_EVENT=notAllowed:succeeded')",
            )
        )
        times = iter((0.0, 10.0, 16.0))
        events: list[tuple[str, str, str | None]] = []

        result = SubprocessCommandRunner(monotonic=lambda: next(times)).run(
            (sys.executable, "-c", script),
            env=os.environ,
            timeout=5,
            shell=False,
            trace_callback=lambda code, outcome, stage: events.append(
                (code, outcome, stage)
            ),
        )

        self.assertEqual(
            [
                ("downloadStarted", "started", "downloading"),
                ("hostActivity", "activity", "downloading"),
                ("hostActivity", "activity", "downloading"),
            ],
            events,
        )
        self.assertNotIn("downloadStarted", result.output)
        self.assertIn("REACHCOMMANDER_UPDATE_EVENT=notAllowed:succeeded", result.output)

    def test_posix_termination_requests_term_before_forcing_kill(self) -> None:
        process = mock.Mock(pid=4321)
        process.wait.side_effect = [
            subprocess.TimeoutExpired(["fixed"], 5),
            0,
        ]
        events: list[tuple[str, str, str | None]] = []

        with (
            mock.patch("deploy.updater_service.os.name", "posix"),
            mock.patch("deploy.updater_service.os.killpg", create=True) as killpg,
            mock.patch(
                "deploy.updater_service._wait_for_process_group_exit",
                side_effect=(False, True),
            ),
            mock.patch.object(signal, "SIGKILL", 9, create=True),
        ):
            updater_service._terminate_process_tree(
                process,
                trace_callback=lambda code, outcome, stage: events.append(
                    (code, outcome, stage)
                ),
                progress_stage="installing",
            )

        self.assertEqual(
            [
                mock.call(4321, signal.SIGTERM),
                mock.call(4321, 9),
            ],
            killpg.call_args_list,
        )
        self.assertEqual(
            [
                ("terminationRequested", "started", "installing"),
                ("terminationForced", "started", "installing"),
            ],
            events,
        )

    def test_windows_termination_falls_back_to_the_fixed_wrapper_process(self) -> None:
        process = mock.Mock(pid=4321)
        process.wait.side_effect = [
            subprocess.TimeoutExpired(["fixed"], 5),
            0,
        ]

        with mock.patch("deploy.updater_service.os.name", "nt"):
            updater_service._terminate_process_tree(process)

        process.terminate.assert_called_once_with()
        process.kill.assert_called_once_with()
        self.assertEqual(2, process.wait.call_count)

    def test_subprocess_runner_bounds_reader_completion_after_process_exit(self) -> None:
        process = mock.Mock()
        process.stdout = []
        process.wait.return_value = 0
        reader = mock.Mock()

        with (
            mock.patch("deploy.updater_service.subprocess.Popen", return_value=process),
            mock.patch("deploy.updater_service.threading.Thread", return_value=reader),
        ):
            result = SubprocessCommandRunner().run(
                ("/fixed", "update"),
                env=SANITIZED_ENVIRONMENT,
                timeout=5,
                shell=False,
            )

        self.assertEqual(0, result.returncode)
        reader.join.assert_called_once_with(
            timeout=updater_service.READER_JOIN_TIMEOUT_SECONDS
        )

    def test_subprocess_runner_disables_callbacks_before_returning(self) -> None:
        released = threading.Event()
        stages: list[str] = []
        real_thread = threading.Thread
        process = mock.Mock()
        process.stdout = iter(("REACHCOMMANDER_UPDATE_STAGE=installing\n",))
        process.wait.return_value = 0

        class DelayedReader:
            def __init__(self, *, target, **_kwargs):  # type: ignore[no-untyped-def]
                self.thread = real_thread(
                    target=lambda: (released.wait(timeout=2), target()),
                    daemon=True,
                )

            def start(self) -> None:
                self.thread.start()

            def join(self, timeout: float) -> None:
                self.thread.join(timeout)

            def is_alive(self) -> bool:
                return self.thread.is_alive()

        with (
            mock.patch("deploy.updater_service.subprocess.Popen", return_value=process),
            mock.patch("deploy.updater_service.threading.Thread", DelayedReader),
            mock.patch("deploy.updater_service.READER_JOIN_TIMEOUT_SECONDS", 0),
        ):
            result = SubprocessCommandRunner().run(
                ("/fixed", "update"),
                env=SANITIZED_ENVIRONMENT,
                timeout=5,
                shell=False,
                progress_callback=stages.append,
            )

        released.set()
        time.sleep(0.05)

        self.assertEqual(0, result.returncode)
        self.assertEqual([], stages)

    def test_subprocess_runner_does_not_wait_for_an_in_flight_callback(self) -> None:
        callback_started = threading.Event()
        release_callback = threading.Event()
        runner_returned = threading.Event()
        process = mock.Mock()
        process.stdout = iter(("REACHCOMMANDER_UPDATE_STAGE=installing\n",))

        def wait_for_reader(*, timeout):  # type: ignore[no-untyped-def]
            self.assertEqual(5, timeout)
            self.assertTrue(callback_started.wait(timeout=1))
            return 0

        process.wait.side_effect = wait_for_reader

        def blocking_progress(_stage: str) -> None:
            callback_started.set()
            release_callback.wait(timeout=5)

        def run() -> None:
            try:
                SubprocessCommandRunner().run(
                    ("/fixed", "update"),
                    env=SANITIZED_ENVIRONMENT,
                    timeout=5,
                    shell=False,
                    progress_callback=blocking_progress,
                )
            finally:
                runner_returned.set()

        with (
            mock.patch("deploy.updater_service.subprocess.Popen", return_value=process),
            mock.patch("deploy.updater_service.READER_JOIN_TIMEOUT_SECONDS", 0.05),
        ):
            worker = threading.Thread(target=run, daemon=True)
            worker.start()
            try:
                self.assertTrue(callback_started.wait(timeout=1))
                self.assertTrue(runner_returned.wait(timeout=1))
            finally:
                release_callback.set()
                worker.join(timeout=2)

    def test_timeout_carries_ordered_supervision_events_when_callback_is_busy(self) -> None:
        callback_started = threading.Event()
        release_callback = threading.Event()
        process = mock.Mock(pid=4321)
        process.stdout = iter(("REACHCOMMANDER_UPDATE_STAGE=installing\n",))

        def wait_for_reader(*, timeout):  # type: ignore[no-untyped-def]
            self.assertEqual(5, timeout)
            self.assertTrue(callback_started.wait(timeout=1))
            raise subprocess.TimeoutExpired(["fixed"], timeout)

        process.wait.side_effect = wait_for_reader

        def blocking_progress(_stage: str) -> None:
            callback_started.set()
            release_callback.wait(timeout=5)

        def terminate(_process, *, trace_callback, progress_stage):  # type: ignore[no-untyped-def]
            trace_callback("terminationRequested", "started", progress_stage)
            trace_callback("terminationForced", "started", progress_stage)

        try:
            with (
                mock.patch("deploy.updater_service.subprocess.Popen", return_value=process),
                mock.patch(
                    "deploy.updater_service._terminate_process_tree",
                    side_effect=terminate,
                ),
                mock.patch("deploy.updater_service.READER_JOIN_TIMEOUT_SECONDS", 0.05),
            ):
                with self.assertRaises(CommandTimedOut) as raised:
                    SubprocessCommandRunner().run(
                        ("/fixed", "update"),
                        env=SANITIZED_ENVIRONMENT,
                        timeout=5,
                        shell=False,
                        progress_callback=blocking_progress,
                    )
        finally:
            release_callback.set()

        self.assertEqual(
            (
                ("commandTimedOut", "timedOut", "installing"),
                ("terminationRequested", "started", "installing"),
                ("terminationForced", "started", "installing"),
            ),
            raised.exception.trace_events,
        )

    def test_subprocess_runner_streams_only_valid_markers_and_bounds_output(self) -> None:
        import sys

        script = "\n".join(
            (
                "print('ordinary output')",
                "print('REACHCOMMANDER_UPDATE_STAGE=downloading')",
                "print('REACHCOMMANDER_UPDATE_STAGE=notAStage')",
                "print('REACHCOMMANDER_UPDATE_STAGE=installing')",
                "print('x' * 100000)",
            )
        )
        stages: list[str] = []

        result = SubprocessCommandRunner().run(
            (sys.executable, "-c", script),
            env=os.environ,
            timeout=5,
            shell=False,
            progress_callback=stages.append,
        )

        self.assertEqual(0, result.returncode)
        self.assertEqual(["downloading", "installing"], stages)
        self.assertIn("ordinary output", result.output)
        self.assertNotIn("REACHCOMMANDER_UPDATE_STAGE", result.output)
        self.assertLessEqual(len(result.output), 16_384)
        with self.assertRaisesRegex(ValueError, "shell"):
            SubprocessCommandRunner().run(
                (sys.executable, "-c", "pass"),
                env=os.environ,
                timeout=5,
                shell=True,
            )

    def test_runtime_gid_reader_requires_one_valid_non_root_value(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            env_path = Path(directory) / ".env"
            env_path.write_text("REACHCOMMANDER_UID=1000\nREACHCOMMANDER_GID=1001\n", encoding="utf-8")
            self.assertEqual(1001, read_runtime_gid(env_path))

            env_path.write_text("REACHCOMMANDER_GID=0\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "runtime GID"):
                read_runtime_gid(env_path)

    def test_signal_handler_requests_shutdown_and_removes_socket(self) -> None:
        server = mock.Mock()
        stop_event = threading.Event()
        registrations: dict[int, object] = {}
        with mock.patch(
            "deploy.updater_service.signal.signal",
            side_effect=lambda number, handler: registrations.__setitem__(number, handler),
        ):
            install_signal_handlers(server, stop_event)

        import signal

        registrations[signal.SIGTERM](signal.SIGTERM, None)  # type: ignore[operator]
        self.assertTrue(stop_event.is_set())
        server.close.assert_called_once_with()

    def test_systemd_unit_is_fixed_and_hardened(self) -> None:
        unit = (
            Path(__file__).resolve().parents[2]
            / "deploy"
            / "systemd"
            / "reachcommander-updater.service"
        ).read_text(encoding="utf-8")

        self.assertIn(
            "ExecStart=/usr/bin/python3 /opt/reachcommander/bin/updater_service.py",
            unit,
        )
        self.assertIn("RuntimeDirectory=reachcommander-updater", unit)
        self.assertIn("RuntimeDirectoryMode=0750", unit)
        self.assertIn("ProtectSystem=strict", unit)
        self.assertIn("NoNewPrivileges=yes", unit)
        self.assertIn("Restart=on-failure", unit)
        self.assertIn(
            "ReadWritePaths=/opt/reachcommander /opt/.reachcommander.lock /run/reachcommander-updater",
            unit,
        )
        self.assertIn(
            "ReadOnlyPaths=/opt/reachcommander/bin /opt/reachcommander/lib /opt/reachcommander/data",
            unit,
        )
        self.assertNotIn("/var/run/docker.sock", unit)
        self.assertNotIn("/bin/sh", unit)
        self.assertNotIn("$", unit)


if __name__ == "__main__":
    unittest.main()
