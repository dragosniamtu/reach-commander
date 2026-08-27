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
    MAX_MESSAGE_BYTES,
    TRUSTED_IMAGE_REPOSITORY,
    ResolvedImage,
    UpdateSnapshot,
    UpdaterRequest,
)
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


NOW = dt.datetime(2026, 8, 25, 12, 0, tzinfo=dt.timezone.utc)
CURRENT_DIGEST = "sha256:" + "1" * 64
TARGET_DIGEST = "sha256:" + "2" * 64
TARGET_REFERENCE = f"{TRUSTED_IMAGE_REPOSITORY}:v1.4.0"
LEGACY_PROTOCOL_VERSION = 1
DETAILED_PROTOCOL_VERSION = 2


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
    ) -> None:
        self.exit_code = exit_code
        self.output = output
        self.progress_stages = progress_stages
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
    ) -> CommandResult:
        self.argv.append(list(argv))
        self.environments.append(dict(env))
        self.timeouts.append(timeout)
        self.shell_values.append(shell)
        if progress_callback is not None:
            for stage in self.progress_stages:
                progress_callback(stage)
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


class SequenceRunner:
    def __init__(self, results: list[CommandResult]) -> None:
        self.results = list(results)
        self.argv: list[list[str]] = []

    def run(self, argv, **_kwargs) -> CommandResult:  # type: ignore[no-untyped-def]
        self.argv.append(list(argv))
        return self.results.pop(0)


class UpdaterRuntimeTests(unittest.TestCase):
    def runtime(
        self,
        root: Path,
        *,
        runner: object | None = None,
        discovery: StaticDiscovery | None = None,
    ) -> UpdaterRuntime:
        return UpdaterRuntime(
            discovery or StaticDiscovery(snapshot()),
            AtomicUpdateJournal(root / "system-update.json"),
            runner=runner or RecordingRunner(),
            clock=lambda: NOW,
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

    def test_v1_response_has_exact_legacy_shape_and_v2_adds_progress(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            runtime = self.runtime(Path(directory))

            legacy = runtime.handle(request("check", LEGACY_PROTOCOL_VERSION))
            detailed = runtime.handle(request("check", DETAILED_PROTOCOL_VERSION))

        self.assertEqual(LEGACY_PROTOCOL_VERSION, legacy["protocolVersion"])
        self.assertNotIn("progressStage", legacy)
        self.assertEqual(DETAILED_PROTOCOL_VERSION, detailed["protocolVersion"])
        self.assertIn("progressStage", detailed)
        self.assertIsNone(detailed["progressStage"])

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

    def test_progress_persistence_failure_does_not_change_update_result(self) -> None:
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
        self.server = UpdaterSocketServer(
            self.runtime,
            self.root / "run" / "updater.sock",
            runtime_gid=os.getgid() if hasattr(os, "getgid") else None,
            request_timeout=0.1,
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


class ServiceProcessContractTests(unittest.TestCase):
    @unittest.skipIf(os.name == "nt", "Linux process groups are required")
    def test_timeout_kills_descendant_and_never_waits_forever_for_output_pipe(self) -> None:
        import sys

        child = (
            "import subprocess,sys,time;"
            "subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']);"
            "print('ready', flush=True);time.sleep(60)"
        )
        started = time.monotonic()

        with self.assertRaises(CommandTimedOut) as raised:
            SubprocessCommandRunner().run(
                (sys.executable, "-c", child),
                env=os.environ,
                timeout=1,
                shell=False,
            )

        self.assertLess(time.monotonic() - started, 8)
        self.assertIn("ready", raised.exception.output)

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
        self.assertNotIn("/bin/sh", unit)
        self.assertNotIn("$", unit)


if __name__ == "__main__":
    unittest.main()
