#!/usr/bin/env python3
"""Restricted host-side update service for installer-managed Ubuntu systems."""

from __future__ import annotations

import dataclasses
import datetime as dt
import json
import os
import re
import signal
import socket
import stat
import subprocess
import sys
import threading
import time
import urllib.request
import uuid
from pathlib import Path
from typing import Callable, Mapping

try:
    from .updater_protocol import (
        DIGEST,
        DETAILED_PROTOCOL_VERSION,
        LEGACY_PROTOCOL_VERSION,
        MAX_MESSAGE_BYTES,
        PROTOCOL_VERSION,
        STABLE_TAG,
        TRUSTED_IMAGE_REPOSITORY,
        GitHubRelease,
        InstalledState,
        ProtocolError,
        ResolvedImage,
        UpdateDiscovery,
        UpdateSnapshot,
        UpdaterRequest,
    )
except ImportError:  # Installed helper: bin/updater_service.py + lib/updater_protocol.py
    library_directory = Path(__file__).resolve().parents[1] / "lib"
    sys.path.insert(0, str(library_directory))
    from updater_protocol import (  # type: ignore[no-redef]
        DIGEST,
        DETAILED_PROTOCOL_VERSION,
        LEGACY_PROTOCOL_VERSION,
        MAX_MESSAGE_BYTES,
        PROTOCOL_VERSION,
        STABLE_TAG,
        TRUSTED_IMAGE_REPOSITORY,
        GitHubRelease,
        InstalledState,
        ProtocolError,
        ResolvedImage,
        UpdateDiscovery,
        UpdateSnapshot,
        UpdaterRequest,
    )

try:
    from .updater_trace import EVENT_OUTCOMES, ProtectedUpdateTraceStore
except ImportError:  # Installed helper: bin/updater_service.py + lib/updater_trace.py
    library_directory = Path(__file__).resolve().parents[1] / "lib"
    sys.path.insert(0, str(library_directory))
    from updater_trace import (  # type: ignore[no-redef]
        EVENT_OUTCOMES,
        ProtectedUpdateTraceStore,
    )


FIXED_COMMAND = ("/usr/local/bin/reachcommander", "update")
DOCKER_COMMAND = "/usr/bin/docker"
FIXED_GITHUB_RELEASE_URL = (
    "https://api.github.com/repos/dragosniamtu/reach-commander/releases/latest"
)
LEGACY_JOURNAL_SCHEMA = 1
JOURNAL_SCHEMA = 2
COMMAND_TIMEOUT_SECONDS = 300
DISCOVERY_TIMEOUT_SECONDS = 120
NETWORK_TIMEOUT_SECONDS = 10
MAX_COMMAND_OUTPUT_CHARS = 16_384
MAX_JOURNAL_BYTES = 32_768
RESULT_RETENTION_SECONDS = 600
PROGRESS_MARKER_PREFIX = "REACHCOMMANDER_UPDATE_STAGE="
TRACE_MARKER_PREFIX = "REACHCOMMANDER_UPDATE_EVENT="
TRACE_ACTIVITY_INTERVAL_SECONDS = 15
TERMINATION_GRACE_SECONDS = 5
READER_JOIN_TIMEOUT_SECONDS = 1

SANITIZED_ENVIRONMENT = {
    "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
    "LANG": "C.UTF-8",
    "LC_ALL": "C.UTF-8",
}

_TERMINAL_PHASES = frozenset({"completed", "rolledBack", "failed"})
_JOURNAL_PHASES = frozenset(
    {"unavailable", "current", "available", "applying", *_TERMINAL_PHASES}
)
_PUBLIC_DETAILS = {
    "update_available": "A trusted ReachCommander update is available.",
    "up_to_date": "ReachCommander is up to date.",
    "version_pinned": "Updates are disabled while this deployment is version-pinned.",
    "invalid_state": "The trusted installer state is unavailable or invalid.",
    "release_unavailable": "The stable release could not be checked.",
    "release_invalid": "The stable release metadata is invalid.",
    "manifest_unavailable": "The trusted container manifest could not be checked.",
    "manifest_invalid": "The trusted container manifest metadata is invalid.",
    "update_applying": "ReachCommander is applying the trusted update.",
    "update_completed": "ReachCommander was updated successfully.",
    "candidate_rolled_back": "The candidate was unhealthy and the previous version was restored.",
    "update_failed": "The update requires administrator attention.",
    "update_command_timeout": "The host update command timed out and was stopped.",
    "update_interrupted": "The host update service restarted during an update.",
    "updater_journal_invalid": "The host update journal is invalid.",
}
PROGRESS_STAGES = frozenset(
    {
        "downloading",
        "installing",
        "restarting",
        "healthChecking",
        "restoring",
        "restartingPrevious",
        "verifyingRecovery",
    }
)
PROGRESS_TRANSITIONS = {
    None: frozenset({"downloading"}),
    "downloading": frozenset({"installing"}),
    "installing": frozenset({"restarting"}),
    "restarting": frozenset({"healthChecking", "restoring"}),
    "healthChecking": frozenset({"restoring"}),
    "restoring": frozenset({"restartingPrevious"}),
    "restartingPrevious": frozenset({"verifyingRecovery"}),
    "verifyingRecovery": frozenset(),
}
V1_RESPONSE_FIELDS = (
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
)
V2_RESPONSE_FIELDS = (*V1_RESPONSE_FIELDS, "progressStage")
V3_RESPONSE_FIELDS = (*V2_RESPONSE_FIELDS, "trace")
_REVISION = re.compile(r"^[0-9a-f]{40}$")
_EDGE_REFERENCE = f"{TRUSTED_IMAGE_REPOSITORY}:edge"


class JournalError(ValueError):
    pass


class CommandTimedOut(TimeoutError):
    def __init__(self, timeout_seconds: int, output: str) -> None:
        super().__init__(f"the fixed updater command exceeded {timeout_seconds} seconds")
        self.timeout_seconds = timeout_seconds
        self.output = output


@dataclasses.dataclass(frozen=True, slots=True)
class CommandResult:
    returncode: int
    output: str


TraceCallback = Callable[[str, str, str | None], None]


def _emit_trace(
    callback: TraceCallback | None,
    code: str,
    outcome: str,
    progress_stage: str | None,
) -> None:
    if callback is None:
        return
    try:
        callback(code, outcome, progress_stage)
    except Exception:
        # Trace persistence is observational and must not change the update result.
        print(
            "ReachCommander updater could not persist an update trace event.",
            file=sys.stderr,
        )


def _terminate_process_tree(
    process: subprocess.Popen[str],
    *,
    trace_callback: TraceCallback | None = None,
    progress_stage: str | None = None,
) -> None:
    _emit_trace(
        trace_callback,
        "terminationRequested",
        "started",
        progress_stage,
    )
    if os.name != "nt":
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except OSError:
            try:
                process.terminate()
            except OSError:
                return
        else:
            if _wait_for_process_group_exit(
                process,
                TERMINATION_GRACE_SECONDS,
            ):
                return

            _emit_trace(
                trace_callback,
                "terminationForced",
                "started",
                progress_stage,
            )
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except OSError:
                try:
                    process.kill()
                except OSError:
                    pass
            _wait_for_process_group_exit(process, TERMINATION_GRACE_SECONDS)
            return

    try:
        process.terminate()
    except OSError:
        pass

    try:
        process.wait(timeout=TERMINATION_GRACE_SECONDS)
        return
    except subprocess.TimeoutExpired:
        pass

    _emit_trace(
        trace_callback,
        "terminationForced",
        "started",
        progress_stage,
    )
    try:
        if os.name != "nt":
            os.killpg(process.pid, signal.SIGKILL)
        else:
            process.kill()
    except (OSError, ProcessLookupError):
        try:
            process.kill()
        except OSError:
            pass

    try:
        process.wait(timeout=TERMINATION_GRACE_SECONDS)
    except subprocess.TimeoutExpired:
        # The service must return even if the operating system cannot reap the wrapper.
        pass


def _process_group_exists(process_group_id: int) -> bool:
    try:
        os.killpg(process_group_id, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return True
    return True


def _wait_for_process_group_exit(
    process: subprocess.Popen[str],
    timeout: float,
) -> bool:
    deadline = time.monotonic() + timeout
    while True:
        process.poll()
        if not _process_group_exists(process.pid):
            return True
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return False
        time.sleep(min(0.05, remaining))


class SubprocessCommandRunner:
    def __init__(self, *, monotonic: Callable[[], float] = time.monotonic) -> None:
        self._monotonic = monotonic

    def run(
        self,
        argv: tuple[str, ...] | list[str],
        *,
        env: Mapping[str, str],
        timeout: int,
        shell: bool,
        progress_callback: Callable[[str], None] | None = None,
        trace_callback: TraceCallback | None = None,
    ) -> CommandResult:
        if shell:
            raise ValueError("shell execution is not supported")
        process = subprocess.Popen(
            list(argv),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            env=dict(env),
            shell=False,
            start_new_session=os.name != "nt",
        )
        output_parts: list[str] = []
        output_length = 0
        progress_stage: str | None = None
        last_activity_at: float | None = None
        output_lock = threading.Lock()
        callback_lock = threading.Lock()
        callbacks_active = True

        def publish_progress(stage: str) -> None:
            with callback_lock:
                if callbacks_active and progress_callback is not None:
                    progress_callback(stage)

        def publish_trace(code: str, outcome: str, stage: str | None) -> None:
            with callback_lock:
                if callbacks_active:
                    _emit_trace(trace_callback, code, outcome, stage)

        def disable_callbacks() -> None:
            nonlocal callbacks_active
            with callback_lock:
                callbacks_active = False

        def append_output(line: str) -> None:
            nonlocal output_length
            with output_lock:
                remaining = MAX_COMMAND_OUTPUT_CHARS - output_length
                if remaining > 0:
                    bounded = line[:remaining]
                    output_parts.append(bounded)
                    output_length += len(bounded)

        def captured_output() -> str:
            with output_lock:
                return "".join(output_parts)

        def read_output() -> None:
            nonlocal last_activity_at, progress_stage
            if process.stdout is None:
                return
            for line in process.stdout:
                marker = line.rstrip("\r\n")
                if marker.startswith(PROGRESS_MARKER_PREFIX):
                    stage = marker[len(PROGRESS_MARKER_PREFIX) :]
                    if stage in PROGRESS_STAGES:
                        progress_stage = stage
                        publish_progress(stage)
                    continue
                if marker.startswith(TRACE_MARKER_PREFIX):
                    payload = marker[len(TRACE_MARKER_PREFIX) :]
                    code, separator, outcome = payload.partition(":")
                    if (
                        separator
                        and outcome in EVENT_OUTCOMES.get(code, frozenset())
                    ):
                        publish_trace(
                            code,
                            outcome,
                            progress_stage,
                        )
                        continue
                append_output(line)
                now = self._monotonic()
                if (
                    last_activity_at is None
                    or now - last_activity_at >= TRACE_ACTIVITY_INTERVAL_SECONDS
                ):
                    last_activity_at = now
                    publish_trace(
                        "hostActivity",
                        "activity",
                        progress_stage,
                    )

        reader = threading.Thread(
            target=read_output,
            name="reachcommander-update-output",
            daemon=True,
        )
        reader.start()
        try:
            try:
                returncode = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                publish_trace(
                    "commandTimedOut",
                    "timedOut",
                    progress_stage,
                )
                _terminate_process_tree(
                    process,
                    trace_callback=publish_trace,
                    progress_stage=progress_stage,
                )
                reader.join(timeout=READER_JOIN_TIMEOUT_SECONDS)
                if process.stdout is not None and not reader.is_alive():
                    process.stdout.close()
                raise CommandTimedOut(timeout, captured_output())
            reader.join(timeout=READER_JOIN_TIMEOUT_SECONDS)
            if process.stdout is not None and not reader.is_alive():
                process.stdout.close()
            return CommandResult(returncode, captured_output())
        finally:
            disable_callbacks()


class GitHubLatestReleaseProvider:
    def __init__(
        self,
        *,
        opener: Callable[..., object] = urllib.request.urlopen,
    ) -> None:
        self._opener = opener

    def __call__(self) -> GitHubRelease:
        request = urllib.request.Request(
            FIXED_GITHUB_RELEASE_URL,
            headers={
                "Accept": "application/vnd.github+json",
                "User-Agent": "ReachCommander-Updater/1",
                "X-GitHub-Api-Version": "2022-11-28",
            },
            method="GET",
        )
        with self._opener(request, timeout=NETWORK_TIMEOUT_SECONDS) as response:
            raw = response.read(MAX_MESSAGE_BYTES + 1)
        if len(raw) > MAX_MESSAGE_BYTES:
            raise ValueError("release response is too large")
        value = json.loads(raw)
        if not isinstance(value, dict):
            raise ValueError("release response is invalid")
        tag_name = value.get("tag_name")
        draft = value.get("draft")
        prerelease = value.get("prerelease")
        if (
            not isinstance(tag_name, str)
            or not isinstance(draft, bool)
            or not isinstance(prerelease, bool)
        ):
            raise ValueError("release response is invalid")
        return GitHubRelease(tag_name, draft, prerelease)


class DockerImageResolver:
    def __init__(self, *, runner: object | None = None) -> None:
        self._runner = runner or SubprocessCommandRunner()

    def __call__(self, reference: str) -> ResolvedImage:
        if not _trusted_floating_reference(reference):
            raise ValueError("only a trusted ReachCommander image can be resolved")
        self._run((DOCKER_COMMAND, "pull", reference))
        digest_output = self._run(
            (
                DOCKER_COMMAND,
                "image",
                "inspect",
                "--format",
                "{{range .RepoDigests}}{{println .}}{{end}}",
                reference,
            )
        )
        prefix = f"{TRUSTED_IMAGE_REPOSITORY}@"
        digests = {
            line[len(prefix) :]
            for line in digest_output.splitlines()
            if line.startswith(prefix) and DIGEST.fullmatch(line[len(prefix) :])
        }
        if len(digests) != 1:
            raise ValueError("trusted image digest metadata is invalid")
        version = self._run(
            (
                DOCKER_COMMAND,
                "image",
                "inspect",
                "--format",
                '{{ index .Config.Labels "org.opencontainers.image.version" }}',
                reference,
            )
        ).strip()
        revision = self._run(
            (
                DOCKER_COMMAND,
                "image",
                "inspect",
                "--format",
                '{{ index .Config.Labels "org.opencontainers.image.revision" }}',
                reference,
            )
        ).strip()
        digest = next(iter(digests))
        if not version or len(version) > 128 or any(value in version for value in "\r\n\x00"):
            raise ValueError("trusted image version metadata is invalid")
        if not _REVISION.fullmatch(revision):
            raise ValueError("trusted image revision metadata is invalid")
        return ResolvedImage(reference, digest, version, revision)

    def _run(self, argv: tuple[str, ...]) -> str:
        result = self._runner.run(
            argv,
            env=SANITIZED_ENVIRONMENT,
            timeout=DISCOVERY_TIMEOUT_SECONDS,
            shell=False,
        )
        if result.returncode != 0:
            raise RuntimeError("trusted image inspection failed")
        return result.output


class _FilesystemStateProvider:
    def __init__(self, state_root: Path) -> None:
        self._state_root = state_root

    def load(self) -> InstalledState:
        return InstalledState.load(self._state_root)


class AtomicUpdateJournal:
    def __init__(self, path: Path | str) -> None:
        self.path = Path(path)
        self._lock = threading.Lock()

    def read_optional(self) -> dict[str, object] | None:
        with self._lock:
            return self._read_optional_unlocked()

    def write_snapshot(self, snapshot: UpdateSnapshot) -> dict[str, object]:
        value = {"schemaVersion": JOURNAL_SCHEMA, **snapshot.to_journal()}
        value["detail"] = _detail_for(str(value.get("reasonCode") or ""))
        with self._lock:
            self._write_unlocked(value)
        return value

    def begin(self, snapshot: UpdateSnapshot, now: dt.datetime) -> dict[str, object]:
        if snapshot.phase != "available":
            raise JournalError("an update operation requires an available snapshot")
        timestamp = _iso_utc(now)
        value = {
            "schemaVersion": JOURNAL_SCHEMA,
            **snapshot.to_journal(),
            "phase": "applying",
            "reasonCode": "update_applying",
            "detail": _detail_for("update_applying"),
            "operationId": str(uuid.uuid4()),
            "progressStage": None,
            "startedAt": timestamp,
            "updatedAt": timestamp,
        }
        with self._lock:
            self._write_unlocked(value)
        return value

    def advance(
        self,
        operation: Mapping[str, object],
        stage: str,
        now: dt.datetime,
    ) -> dict[str, object]:
        operation_id = operation.get("operationId")
        if not isinstance(operation_id, str) or stage not in PROGRESS_STAGES:
            return dict(operation)
        with self._lock:
            persisted = self._read_optional_unlocked()
            if (
                persisted is None
                or persisted.get("operationId") != operation_id
                or persisted.get("phase") != "applying"
                or stage
                not in PROGRESS_TRANSITIONS.get(
                    persisted.get("progressStage"),
                    frozenset(),
                )
            ):
                return dict(operation)
            value = {
                **persisted,
                "schemaVersion": JOURNAL_SCHEMA,
                "progressStage": stage,
                "updatedAt": _iso_utc(now),
            }
            self._write_unlocked(value)
        return value

    def finish(
        self,
        operation: Mapping[str, object],
        phase: str,
        now: dt.datetime,
        *,
        reason_code: str | None = None,
    ) -> dict[str, object]:
        if phase not in _TERMINAL_PHASES:
            raise JournalError("the journal terminal phase is invalid")
        default_reason = {
            "completed": "update_completed",
            "rolledBack": "candidate_rolled_back",
            "failed": "update_failed",
        }[phase]
        reason = reason_code or default_reason
        value = {
            **dict(operation),
            "schemaVersion": JOURNAL_SCHEMA,
            "phase": phase,
            "reasonCode": reason,
            "detail": _detail_for(reason),
            "updatedAt": _iso_utc(now),
        }
        with self._lock:
            self._write_unlocked(value)
        return value

    def _read_optional_unlocked(self) -> dict[str, object] | None:
        try:
            status = self.path.lstat()
        except FileNotFoundError:
            return None
        except OSError as error:
            raise JournalError("the update journal is unavailable") from error
        if self.path.is_symlink() or not stat.S_ISREG(status.st_mode):
            raise JournalError("the update journal must be a regular protected file")
        try:
            raw = self.path.read_bytes()
        except OSError as error:
            raise JournalError("the update journal cannot be read") from error
        if len(raw) > MAX_JOURNAL_BYTES:
            raise JournalError("the update journal is too large")
        try:
            value = json.loads(raw, object_pairs_hook=_reject_duplicate_journal_keys)
        except (json.JSONDecodeError, UnicodeDecodeError, TypeError, JournalError) as error:
            raise JournalError("the update journal is invalid") from error
        return _validate_journal(value)

    def _write_unlocked(self, value: Mapping[str, object]) -> None:
        sanitized = _validate_journal(dict(value))
        payload = (json.dumps(sanitized, separators=(",", ":"), sort_keys=True) + "\n").encode()
        if len(payload) > MAX_JOURNAL_BYTES:
            raise JournalError("the update journal is too large")
        self.path.parent.mkdir(parents=True, exist_ok=True)
        if self.path.parent.is_symlink() or not self.path.parent.is_dir():
            raise JournalError("the update journal directory is not protected")
        temporary = self.path.parent / f".{self.path.name}.{uuid.uuid4().hex}.tmp"
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
        flags |= getattr(os, "O_CLOEXEC", 0)
        flags |= getattr(os, "O_NOFOLLOW", 0)
        descriptor: int | None = None
        try:
            descriptor = os.open(temporary, flags, 0o600)
            with os.fdopen(descriptor, "wb", closefd=True) as output:
                descriptor = None
                output.write(payload)
                output.flush()
                os.fsync(output.fileno())
            os.replace(temporary, self.path)
            if os.name != "nt":
                os.chmod(self.path, 0o600, follow_symlinks=False)
                directory_descriptor = os.open(
                    self.path.parent,
                    os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
                )
                try:
                    os.fsync(directory_descriptor)
                finally:
                    os.close(directory_descriptor)
        except (OSError, ValueError) as error:
            raise JournalError("the update journal cannot be written atomically") from error
        finally:
            if descriptor is not None:
                os.close(descriptor)
            try:
                temporary.unlink()
            except FileNotFoundError:
                pass


class UpdaterRuntime:
    def __init__(
        self,
        discovery: object,
        journal: AtomicUpdateJournal,
        *,
        runner: object | None = None,
        clock: Callable[[], dt.datetime] | None = None,
        trace_store: object | None = None,
    ) -> None:
        self._discovery = discovery
        self._journal = journal
        self._runner = runner or SubprocessCommandRunner()
        self._clock = clock or (lambda: dt.datetime.now(dt.timezone.utc))
        self._trace_store = trace_store
        self._lock = threading.Lock()
        self._worker: threading.Thread | None = None

    def handle(self, request: UpdaterRequest) -> dict[str, object]:
        with self._lock:
            try:
                current = self._journal.read_optional()
            except JournalError:
                return self._response(request, _journal_failure(self._clock()))

            if current and current.get("phase") == "applying":
                if self._worker is not None and self._worker.is_alive():
                    return self._response(request, current)
                interrupted = self._journal.finish(
                    current,
                    "failed",
                    self._clock(),
                    reason_code="update_interrupted",
                )
                self._finish_trace(interrupted, "failed")
                return self._response(request, interrupted)

            if request.action == "check":
                if current and current.get("phase") in _TERMINAL_PHASES and _is_recent(
                    current.get("updatedAt"), self._clock()
                ):
                    return self._response(request, current)
                return self._response(request, self._check_and_store())

            checked = self._discovery.check()
            if checked.phase != "available":
                return self._response(
                    request,
                    self._journal.write_snapshot(checked),
                )
            operation = self._journal.begin(checked, self._clock())
            trace_active = self._start_trace(operation)
            self._worker = threading.Thread(
                target=self._apply_worker,
                args=(operation, trace_active),
                name="reachcommander-update",
                daemon=True,
            )
            try:
                self._worker.start()
            except RuntimeError:
                failed = self._journal.finish(operation, "failed", self._clock())
                self._finish_trace(failed, "failed")
                return self._response(request, failed)
            return self._response(request, operation)

    def wait_for_worker(self, timeout: float = 5) -> None:
        worker = self._worker
        if worker is not None:
            worker.join(timeout=timeout)
            if worker.is_alive():
                raise TimeoutError("the update worker did not finish")

    def _check_and_store(self) -> dict[str, object]:
        checked = self._discovery.check()
        return self._journal.write_snapshot(checked)

    def _response(
        self,
        request: UpdaterRequest,
        value: Mapping[str, object],
    ) -> dict[str, object]:
        return protocol_response(
            request,
            value,
            trace_store=self._trace_store,
            now=self._clock(),
        )

    def _start_trace(self, operation: Mapping[str, object]) -> bool:
        operation_id = operation.get("operationId")
        if self._trace_store is None or not isinstance(operation_id, str):
            return False
        try:
            self._trace_store.start(operation_id, self._clock())  # type: ignore[attr-defined]
            return True
        except Exception:
            self._trace_warning()
            return False

    def _append_trace(
        self,
        operation: Mapping[str, object],
        code: str,
        outcome: str,
        *,
        stage: str | None = None,
        exit_code: int | None = None,
        timeout_seconds: int | None = None,
    ) -> bool:
        operation_id = operation.get("operationId")
        if self._trace_store is None or not isinstance(operation_id, str):
            return False
        try:
            self._trace_store.append(  # type: ignore[attr-defined]
                operation_id,
                code,
                outcome,
                self._clock(),
                stage=stage,
                exit_code=exit_code,
                timeout_seconds=timeout_seconds,
            )
            return True
        except Exception:
            self._trace_warning()
            return False

    def _finish_trace(
        self,
        operation: Mapping[str, object],
        phase: str,
        *,
        exit_code: int | None = None,
    ) -> None:
        terminal = {
            "completed": ("operationCompleted", "succeeded"),
            "rolledBack": ("operationRolledBack", "succeeded"),
            "failed": ("operationFailed", "failed"),
        }[phase]
        if not self._append_trace(
            operation,
            terminal[0],
            terminal[1],
            stage=operation.get("progressStage")
            if isinstance(operation.get("progressStage"), str)
            else None,
            exit_code=exit_code,
        ):
            return
        try:
            self._trace_store.prune(None)  # type: ignore[union-attr]
        except Exception:
            self._trace_warning()

    @staticmethod
    def _trace_warning() -> None:
        print(
            "ReachCommander updater could not persist update trace data.",
            file=sys.stderr,
        )

    def _apply_worker(
        self,
        operation: Mapping[str, object],
        trace_active: bool,
    ) -> None:
        operation_state = dict(operation)
        trace_codes: set[str] = set()
        callback_lock = threading.Lock()
        callbacks_active = True

        def record_progress(stage: str) -> None:
            nonlocal operation_state, callbacks_active
            with callback_lock:
                if not callbacks_active:
                    return
                try:
                    operation_state = self._journal.advance(
                        operation_state,
                        stage,
                        self._clock(),
                    )
                except JournalError:
                    # Progress is observational and must not change the update result.
                    print(
                        "ReachCommander updater could not persist update progress.",
                        file=sys.stderr,
                    )

        def record_trace(code: str, outcome: str, stage: str | None) -> None:
            nonlocal trace_active, callbacks_active
            with callback_lock:
                if not callbacks_active or not trace_active:
                    return
                if self._append_trace(
                    operation_state,
                    code,
                    outcome,
                    stage=stage,
                    timeout_seconds=COMMAND_TIMEOUT_SECONDS
                    if code == "commandTimedOut"
                    else None,
                ):
                    trace_codes.add(code)
                else:
                    trace_active = False

        exit_code: int | None = None
        reason_code: str | None = None
        try:
            completed = self._runner.run(
                FIXED_COMMAND,
                env=SANITIZED_ENVIRONMENT,
                timeout=COMMAND_TIMEOUT_SECONDS,
                shell=False,
                progress_callback=record_progress,
                trace_callback=record_trace,
            )
            exit_code = completed.returncode
            phase = {0: "completed", 2: "rolledBack"}.get(
                completed.returncode, "failed"
            )
        except CommandTimedOut:
            phase = "failed"
            reason_code = "update_command_timeout"
            if trace_active and "commandTimedOut" not in trace_codes:
                record_trace(
                    "commandTimedOut",
                    "timedOut",
                    operation_state.get("progressStage")
                    if isinstance(operation_state.get("progressStage"), str)
                    else None,
                )
        except Exception:
            phase = "failed"
        finally:
            with callback_lock:
                callbacks_active = False
        try:
            finished = self._journal.finish(
                operation_state,
                phase,
                self._clock(),
                reason_code=reason_code,
            )
            if trace_active:
                self._finish_trace(finished, phase, exit_code=exit_code)
        except JournalError:
            # The service log remains fixed; command output and physical paths are omitted.
            print("ReachCommander updater could not persist the update result.", file=sys.stderr)


def protocol_response(
    request: UpdaterRequest,
    value: Mapping[str, object],
    *,
    trace_store: object | None = None,
    now: dt.datetime | None = None,
) -> dict[str, object]:
    if request.protocol_version == LEGACY_PROTOCOL_VERSION:
        fields = V1_RESPONSE_FIELDS
    elif request.protocol_version == DETAILED_PROTOCOL_VERSION:
        fields = V2_RESPONSE_FIELDS
    else:
        fields = V3_RESPONSE_FIELDS
    response: dict[str, object] = {
        "protocolVersion": request.protocol_version,
        "requestId": request.request_id,
    }
    response.update(
        {field: value.get(field) for field in fields if field != "trace"}
    )
    if request.protocol_version == PROTOCOL_VERSION:
        response["trace"] = None
        operation_id = value.get("operationId")
        if trace_store is not None and isinstance(operation_id, str):
            try:
                snapshot = trace_store.public_snapshot(  # type: ignore[attr-defined]
                    operation_id,
                    now,
                )
                if snapshot is not None:
                    response["trace"] = snapshot.to_protocol()
            except Exception:
                UpdaterRuntime._trace_warning()
    reason = response.get("reasonCode")
    response["detail"] = _detail_for(str(reason or ""))
    return response


class UpdaterSocketServer:
    def __init__(
        self,
        runtime: UpdaterRuntime,
        socket_path: Path | str,
        *,
        runtime_gid: int | None,
        request_timeout: float = 5,
    ) -> None:
        self.runtime = runtime
        self.socket_path = Path(socket_path)
        self.runtime_gid = runtime_gid
        self.request_timeout = request_timeout
        self._listener: socket.socket | None = None
        self._closed = threading.Event()

    def start(self) -> None:
        if self._listener is not None:
            raise RuntimeError("the updater socket is already started")
        runtime_directory = self.socket_path.parent
        runtime_directory.mkdir(parents=True, exist_ok=True)
        if runtime_directory.is_symlink() or not runtime_directory.is_dir():
            raise RuntimeError("the updater runtime directory is not protected")
        if os.name != "nt":
            os.chmod(runtime_directory, 0o750)
            if self.runtime_gid is not None and hasattr(os, "geteuid") and os.geteuid() == 0:
                os.chown(runtime_directory, 0, self.runtime_gid)
        try:
            status = self.socket_path.lstat()
        except FileNotFoundError:
            status = None
        if status is not None:
            if not stat.S_ISSOCK(status.st_mode):
                raise RuntimeError("the updater socket path is not a socket")
            self.socket_path.unlink()
        listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        try:
            listener.bind(str(self.socket_path))
            if os.name != "nt":
                os.chmod(self.socket_path, 0o660)
                if self.runtime_gid is not None and hasattr(os, "geteuid") and os.geteuid() == 0:
                    os.chown(self.socket_path, 0, self.runtime_gid)
            listener.listen(16)
            listener.settimeout(0.5)
        except Exception:
            listener.close()
            try:
                self.socket_path.unlink()
            except FileNotFoundError:
                pass
            raise
        self._listener = listener
        self._closed.clear()

    def serve_once(self) -> None:
        listener = self._listener
        if listener is None:
            raise RuntimeError("the updater socket has not been started")
        try:
            connection, _ = listener.accept()
        except socket.timeout:
            return
        with connection:
            connection.settimeout(self.request_timeout)
            response = self._handle_connection(connection)
            encoded = (json.dumps(response, separators=(",", ":")) + "\n").encode()
            if len(encoded) > MAX_MESSAGE_BYTES:
                encoded = (
                    json.dumps(_error_response("response_too_large"), separators=(",", ":"))
                    + "\n"
                ).encode()
            try:
                connection.sendall(encoded)
            except (BrokenPipeError, ConnectionResetError, socket.timeout):
                pass

    def serve_forever(self, stop_event: threading.Event) -> None:
        while not stop_event.is_set() and not self._closed.is_set():
            try:
                self.serve_once()
            except OSError:
                if not stop_event.is_set() and not self._closed.is_set():
                    raise

    def close(self) -> None:
        self._closed.set()
        listener = self._listener
        self._listener = None
        if listener is not None:
            listener.close()
        try:
            status = self.socket_path.lstat()
        except FileNotFoundError:
            return
        if stat.S_ISSOCK(status.st_mode):
            self.socket_path.unlink()

    def _handle_connection(self, connection: socket.socket) -> dict[str, object]:
        try:
            raw = _read_bounded_message(connection)
            parsed = UpdaterRequest.parse(raw)
            return self.runtime.handle(parsed)
        except socket.timeout:
            return _error_response("request_timeout")
        except ProtocolError as error:
            return _error_response(error.code)
        except (OSError, ValueError):
            return _error_response("invalid_request")


def _read_bounded_message(connection: socket.socket) -> bytes:
    buffer = bytearray()
    while True:
        chunk = connection.recv(4096)
        if not chunk:
            raise ProtocolError(
                "invalid_request", "The updater request must end with a newline."
            )
        buffer.extend(chunk)
        if len(buffer) > MAX_MESSAGE_BYTES + 1:
            raise ProtocolError("request_too_large", "The updater request is too large.")
        newline = buffer.find(b"\n")
        if newline >= 0:
            if newline > MAX_MESSAGE_BYTES:
                raise ProtocolError(
                    "request_too_large", "The updater request is too large."
                )
            if buffer[newline + 1 :]:
                raise ProtocolError(
                    "invalid_request", "Only one updater request is allowed."
                )
            return bytes(buffer[:newline])


def read_runtime_gid(env_path: Path | str) -> int:
    path = Path(env_path)
    try:
        status = path.lstat()
    except OSError as error:
        raise ValueError("the runtime GID configuration is unavailable") from error
    if path.is_symlink() or not stat.S_ISREG(status.st_mode):
        raise ValueError("the runtime GID configuration must be a regular file")
    if status.st_size > 16_384:
        raise ValueError("the runtime GID configuration is too large")
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeDecodeError) as error:
        raise ValueError("the runtime GID configuration cannot be read") from error
    values = [line.removeprefix("REACHCOMMANDER_GID=") for line in lines if line.startswith("REACHCOMMANDER_GID=")]
    if len(values) != 1 or not values[0].isdigit():
        raise ValueError("the runtime GID is invalid")
    gid = int(values[0], 10)
    if gid < 1 or gid > 2_147_483_647:
        raise ValueError("the runtime GID is invalid")
    return gid


def install_signal_handlers(
    server: UpdaterSocketServer,
    stop_event: threading.Event,
) -> None:
    def stop(_number: int, _frame: object) -> None:
        stop_event.set()
        server.close()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)


def _trusted_floating_reference(reference: object) -> bool:
    if reference == _EDGE_REFERENCE:
        return True
    if not isinstance(reference, str):
        return False
    prefix = f"{TRUSTED_IMAGE_REPOSITORY}:"
    return reference.startswith(prefix) and STABLE_TAG.fullmatch(reference[len(prefix) :]) is not None


def _reject_duplicate_journal_keys(pairs: list[tuple[str, object]]) -> dict[str, object]:
    value: dict[str, object] = {}
    for key, item in pairs:
        if key in value:
            raise JournalError("the update journal contains duplicate fields")
        value[key] = item
    return value


def _validate_journal(value: object) -> dict[str, object]:
    if not isinstance(value, dict) or value.get("schemaVersion") not in {
        LEGACY_JOURNAL_SCHEMA,
        JOURNAL_SCHEMA,
    }:
        raise JournalError("the update journal schema is invalid")
    schema_version = value["schemaVersion"]
    phase = value.get("phase")
    reason = value.get("reasonCode")
    if phase not in _JOURNAL_PHASES or not isinstance(reason, str) or reason not in _PUBLIC_DETAILS:
        raise JournalError("the update journal state is invalid")
    operation_id = value.get("operationId")
    if operation_id is not None:
        if not isinstance(operation_id, str):
            raise JournalError("the update journal operation is invalid")
        try:
            operation_id = str(uuid.UUID(operation_id))
        except ValueError as error:
            raise JournalError("the update journal operation is invalid") from error
    if phase in {"applying", *_TERMINAL_PHASES} and operation_id is None:
        raise JournalError("the update journal operation is missing")
    progress_stage = value.get("progressStage")
    if schema_version == LEGACY_JOURNAL_SCHEMA and "progressStage" in value:
        raise JournalError("the legacy update journal contains unexpected fields")
    if progress_stage is not None and (
        not isinstance(progress_stage, str)
        or progress_stage not in PROGRESS_STAGES
        or phase not in {"applying", *_TERMINAL_PHASES}
    ):
        raise JournalError("the update journal progress is invalid")
    for digest_field in ("currentDigest", "targetDigest"):
        digest = value.get(digest_field)
        if digest is not None and (
            not isinstance(digest, str) or DIGEST.fullmatch(digest) is None
        ):
            raise JournalError("the update journal digest is invalid")
    sanitized = dict(value)
    sanitized["operationId"] = operation_id
    sanitized["progressStage"] = progress_stage
    sanitized["detail"] = _detail_for(reason)
    for key in ("channel", "currentVersion", "targetVersion"):
        item = sanitized.get(key)
        if item is not None and (
            not isinstance(item, str)
            or not item
            or len(item) > 128
            or any(character in item for character in "\r\n\x00/\\")
        ):
            raise JournalError("the update journal public identity is invalid")
    for key in ("lastCheckedAt", "updatedAt", "startedAt"):
        item = sanitized.get(key)
        if item is not None and (not isinstance(item, str) or _parse_timestamp(item) is None):
            raise JournalError("the update journal timestamp is invalid")
    allowed = {
        "schemaVersion",
        *V2_RESPONSE_FIELDS,
        "startedAt",
    }
    raw_allowed = allowed if schema_version == JOURNAL_SCHEMA else allowed - {"progressStage"}
    if set(value) - raw_allowed or set(sanitized) - allowed:
        raise JournalError("the update journal contains unexpected fields")
    return sanitized


def _detail_for(reason_code: str) -> str:
    return _PUBLIC_DETAILS.get(reason_code, "The updater request could not be completed.")


def _journal_failure(now: dt.datetime) -> dict[str, object]:
    timestamp = _iso_utc(now)
    return {
        "schemaVersion": JOURNAL_SCHEMA,
        "supported": True,
        "channel": None,
        "currentVersion": None,
        "targetVersion": None,
        "currentDigest": None,
        "targetDigest": None,
        "phase": "failed",
        "reasonCode": "updater_journal_invalid",
        "detail": _detail_for("updater_journal_invalid"),
        "operationId": str(uuid.uuid4()),
        "lastCheckedAt": None,
        "updatedAt": timestamp,
        "startedAt": timestamp,
    }


def _error_response(reason_code: str) -> dict[str, object]:
    public_reason = reason_code if reason_code in {
        "request_timeout",
        "request_too_large",
        "invalid_request",
        "invalid_action",
        "protocol_incompatible",
        "response_too_large",
    } else "invalid_request"
    return {
        "protocolVersion": PROTOCOL_VERSION,
        "requestId": None,
        "supported": True,
        "channel": None,
        "currentVersion": None,
        "targetVersion": None,
        "currentDigest": None,
        "targetDigest": None,
        "phase": "unavailable",
        "reasonCode": public_reason,
        "detail": {
            "request_timeout": "The updater request timed out.",
            "request_too_large": "The updater request is too large.",
            "protocol_incompatible": "The host updater protocol is incompatible.",
        }.get(public_reason, "The updater request is invalid."),
        "operationId": None,
        "lastCheckedAt": None,
        "updatedAt": None,
        "progressStage": None,
        "trace": None,
    }


def _iso_utc(value: dt.datetime) -> str:
    if value.tzinfo is None:
        value = value.replace(tzinfo=dt.timezone.utc)
    return value.astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def _parse_timestamp(value: str) -> dt.datetime | None:
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return parsed if parsed.tzinfo is not None else None


def _is_recent(value: object, now: dt.datetime) -> bool:
    if not isinstance(value, str):
        return False
    parsed = _parse_timestamp(value)
    if parsed is None:
        return False
    if now.tzinfo is None:
        now = now.replace(tzinfo=dt.timezone.utc)
    age = (now.astimezone(dt.timezone.utc) - parsed.astimezone(dt.timezone.utc)).total_seconds()
    return 0 <= age <= RESULT_RETENTION_SECONDS


def main() -> int:
    install_root = Path("/opt/reachcommander")
    runtime_gid = read_runtime_gid(install_root / ".env")
    discovery = UpdateDiscovery(
        state=_FilesystemStateProvider(install_root / "state"),
        latest_release=GitHubLatestReleaseProvider(),
        resolve_image=DockerImageResolver(),
    )
    runtime = UpdaterRuntime(
        discovery,
        AtomicUpdateJournal(install_root / "state" / "system-update.json"),
        trace_store=ProtectedUpdateTraceStore(
            install_root / "state" / "update-traces"
        ),
    )
    server = UpdaterSocketServer(
        runtime,
        "/run/reachcommander-updater/updater.sock",
        runtime_gid=runtime_gid,
    )
    stop_event = threading.Event()
    server.start()
    install_signal_handlers(server, stop_event)
    try:
        server.serve_forever(stop_event)
    finally:
        server.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
