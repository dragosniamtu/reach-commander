#!/usr/bin/env python3
"""Strict, sanitized diagnostics for installer-managed ReachCommander deployments."""

from __future__ import annotations

import dataclasses
import datetime as dt
import io
import json
import os
import shutil
import signal
import stat
import subprocess
import threading
import time
import uuid
import zipfile
from pathlib import Path
from typing import Callable, Mapping, Protocol, Sequence

try:
    from .updater_protocol import InstalledState, StateError
    from .updater_trace import ProtectedUpdateTraceStore, TraceError
except ImportError:  # Installed helper: bin client + lib modules.
    from updater_protocol import InstalledState, StateError  # type: ignore[no-redef]
    from updater_trace import ProtectedUpdateTraceStore, TraceError  # type: ignore[no-redef]


DIAGNOSTIC_SCHEMA_VERSION = 1
DIAGNOSTIC_PROTOCOL_VERSION = 4
COMMAND_TIMEOUT_SECONDS = 2.0
COLLECTION_TIMEOUT_SECONDS = 10.0
MAX_COMMAND_OUTPUT_BYTES = 65_536
MAX_BUNDLE_CONTENT_BYTES = 1_048_576
MAX_JOURNAL_BYTES = 65_536

DIAGNOSTIC_STATUSES = frozenset(
    {"healthy", "warning", "failed", "timedOut", "unavailable", "notApplicable"}
)
DIAGNOSTIC_CHECK_NAMES = (
    "dockerEngine",
    "dockerCompose",
    "deploymentFiles",
    "managementCommand",
    "updateTransactions",
    "sourceConfiguration",
    "sourceAccessibility",
    "applicationData",
    "updateChannel",
    "versionState",
    "imageConsistency",
    "containerHealth",
    "updaterService",
    "updaterSocket",
    "installDiskSpace",
    "dockerDiskSpace",
)
BUNDLE_ENTRIES = (
    "README.txt",
    "deployment-health.json",
    "manifest.json",
    "summary.txt",
    "update-trace.json",
)

_DOCTOR_CHECKS: tuple[tuple[str, str], ...] = (
    ("Docker Engine ", "dockerEngine"),
    ("Docker Compose v2 ", "dockerCompose"),
    ("Required deployment file", "deploymentFiles"),
    ("Required deployment files ", "deploymentFiles"),
    ("Compose configuration ", "deploymentFiles"),
    ("Secure HTTPS upstream ", "deploymentFiles"),
    ("Trusted LAN HTTP ", "deploymentFiles"),
    ("Network access policy ", "deploymentFiles"),
    ("Host port ", "deploymentFiles"),
    ("Management command ", "managementCommand"),
    ("Updater systemd unit ", "updaterService"),
    ("No incomplete update transaction ", "updateTransactions"),
    ("Incomplete update transaction ", "updateTransactions"),
    ("No incomplete reconfiguration transaction ", "updateTransactions"),
    ("Incomplete reconfiguration transaction ", "updateTransactions"),
    ("Application source configuration ", "sourceConfiguration"),
    ("Installer source metadata ", "sourceConfiguration"),
    ("Source path ", "sourceAccessibility"),
    ("Application data ", "applicationData"),
    ("Runtime UID and GID ", "applicationData"),
    ("Runtime UID or GID ", "applicationData"),
    ("Administrator account ", "applicationData"),
    ("First-run setup state ", "applicationData"),
    ("Update trace storage ", "applicationData"),
    ("Update channel ", "updateChannel"),
    ("Current display version ", "versionState"),
    ("Previous display version ", "versionState"),
    ("Environment image ", "imageConsistency"),
    ("Immutable image state ", "imageConsistency"),
    ("Container ", "containerHealth"),
    ("Updater service ", "updaterService"),
    ("Updater socket ", "updaterSocket"),
)


@dataclasses.dataclass(frozen=True, slots=True)
class CommandResult:
    exit_code: int | None
    output: str
    timed_out: bool = False
    unavailable: bool = False


class CommandRunner(Protocol):
    def run(self, command: tuple[str, ...], timeout: float) -> CommandResult: ...


class FixedCommandRunner:
    """Run only caller-supplied fixed tuples with bounded output and process lifetime."""

    def run(self, command: tuple[str, ...], timeout: float) -> CommandResult:
        try:
            process = subprocess.Popen(
                command,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                start_new_session=True,
            )
        except (OSError, ValueError):
            return CommandResult(None, "", unavailable=True)

        captured = bytearray()
        output_overflowed = [False]
        read_failed = [False]

        def drain_output() -> None:
            assert process.stdout is not None
            try:
                while chunk := process.stdout.read(4_096):
                    remaining = MAX_COMMAND_OUTPUT_BYTES - len(captured)
                    if remaining > 0:
                        captured.extend(chunk[:remaining])
                    if len(chunk) > remaining:
                        output_overflowed[0] = True
            except OSError:
                read_failed[0] = True

        reader = threading.Thread(target=drain_output, daemon=True)
        reader.start()
        timed_out = False
        try:
            process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            _terminate_process_group(process)
            try:
                process.wait(timeout=0.5)
            except subprocess.TimeoutExpired:
                pass
        reader.join(timeout=0.5)
        if reader.is_alive() and process.stdout is not None:
            process.stdout.close()
            reader.join(timeout=0.1)
            read_failed[0] = True
        elif process.stdout is not None:
            process.stdout.close()
        return CommandResult(
            None if timed_out else process.returncode,
            _bounded_decode(bytes(captured)),
            timed_out=timed_out,
            unavailable=output_overflowed[0] or read_failed[0],
        )


@dataclasses.dataclass(frozen=True, slots=True)
class DiagnosticCheck:
    name: str
    status: str
    reason_code: str

    def to_protocol(self) -> dict[str, str]:
        if self.name not in DIAGNOSTIC_CHECK_NAMES or self.status not in DIAGNOSTIC_STATUSES:
            raise ValueError("the diagnostic check is invalid")
        return {
            "name": self.name,
            "status": self.status,
            "reasonCode": self.reason_code,
        }


@dataclasses.dataclass(frozen=True, slots=True)
class DiagnosticSnapshot:
    generated_at: str
    complete: bool
    updater_protocol_version: int
    channel: str | None
    current_version: str | None
    operation_id: str | None
    trace: Mapping[str, object] | None
    checks: tuple[DiagnosticCheck, ...]

    def to_protocol(self) -> dict[str, object]:
        return {
            "schemaVersion": DIAGNOSTIC_SCHEMA_VERSION,
            "generatedAt": self.generated_at,
            "complete": self.complete,
            "updaterProtocolVersion": self.updater_protocol_version,
            "channel": self.channel,
            "currentVersion": self.current_version,
            "operationId": self.operation_id,
            "trace": None if self.trace is None else dict(self.trace),
            "checks": [check.to_protocol() for check in self.checks],
        }


class HostDiagnosticCollector:
    def __init__(
        self,
        install_root: Path | str,
        *,
        command_path: str = "/usr/local/bin/reachcommander",
        command_runner: CommandRunner | None = None,
        trace_store: object | None = None,
        clock: Callable[[], dt.datetime] | None = None,
        disk_usage: Callable[[Path | str], Sequence[int]] | None = None,
        monotonic: Callable[[], float] | None = None,
        docker_root: Path | str = "/var/lib/docker",
    ) -> None:
        self._root = Path(install_root)
        self._command_path = command_path
        self._runner = command_runner or FixedCommandRunner()
        self._trace_store = trace_store
        self._clock = clock or (lambda: dt.datetime.now(dt.timezone.utc))
        self._disk_usage = disk_usage or shutil.disk_usage
        self._monotonic = monotonic or time.monotonic
        self._docker_root = Path(docker_root)

    def collect(self) -> DiagnosticSnapshot:
        deadline = self._monotonic() + COLLECTION_TIMEOUT_SECONDS
        now = _utc(self._clock())
        state = self._load_state()
        operation_id = self._operation_id()
        trace = self._trace(operation_id, now)
        remaining = deadline - self._monotonic()
        doctor = (
            CommandResult(None, "", timed_out=True)
            if remaining <= 0
            else self._runner.run(
                (self._command_path, "doctor"),
                min(COMMAND_TIMEOUT_SECONDS, remaining),
            )
        )
        observations = self._parse_doctor(doctor)
        observations["installDiskSpace"] = [
            self._budgeted_disk_check(deadline, self._root, "installDiskSpace")
        ]
        observations["dockerDiskSpace"] = [
            self._budgeted_disk_check(
                deadline,
                self._docker_root,
                "dockerDiskSpace",
                missing_ok=True,
            )
        ]
        checks = tuple(
            self._collapse_check(name, observations.get(name, []), doctor)
            for name in DIAGNOSTIC_CHECK_NAMES
        )
        complete = all(check.status not in {"timedOut", "unavailable"} for check in checks)
        return DiagnosticSnapshot(
            _iso_utc(now),
            complete,
            DIAGNOSTIC_PROTOCOL_VERSION,
            None if state is None else state.channel,
            None if state is None else (state.current_version or "unknown"),
            operation_id,
            trace,
            checks,
        )

    def _budgeted_disk_check(
        self,
        deadline: float,
        path: Path,
        name: str,
        *,
        missing_ok: bool = False,
    ) -> str:
        if self._monotonic() >= deadline:
            return "timedOut"
        return self._disk_check(path, name, missing_ok=missing_ok)

    def _load_state(self) -> InstalledState | None:
        try:
            return InstalledState.load(self._root / "state")
        except (OSError, StateError, ValueError, TypeError):
            return None

    def _operation_id(self) -> str | None:
        path = self._root / "state" / "system-update.json"
        try:
            raw = _read_regular(path, MAX_JOURNAL_BYTES)
            value = json.loads(raw, object_pairs_hook=_reject_duplicate_keys)
            if not isinstance(value, dict):
                return None
            operation_id = value.get("operationId")
            if not isinstance(operation_id, str) or str(uuid.UUID(operation_id)) != operation_id:
                return None
            return operation_id
        except (FileNotFoundError, OSError, UnicodeError, ValueError, TypeError, json.JSONDecodeError):
            return None

    def _trace(self, operation_id: str | None, now: dt.datetime) -> Mapping[str, object] | None:
        if operation_id is None:
            return None
        store = self._trace_store
        if store is None:
            store = ProtectedUpdateTraceStore(self._root / "state" / "update-traces")
        try:
            snapshot = store.public_snapshot(operation_id, now, blocking=False)  # type: ignore[attr-defined]
            return None if snapshot is None else snapshot.to_protocol()
        except (OSError, TraceError, ValueError, TypeError, AttributeError):
            return None

    @staticmethod
    def _parse_doctor(result: CommandResult) -> dict[str, list[str]]:
        observations: dict[str, list[str]] = {}
        unmapped_failure = False
        unmapped_observation = False
        for line in result.output.splitlines():
            status = None
            detail = ""
            if line.startswith("[PASS] "):
                status, detail = "healthy", line[7:]
            elif line.startswith("[WARN] "):
                status, detail = "warning", line[7:]
            elif line.startswith("[FAIL] "):
                status, detail = "failed", line[7:]
            if status is None:
                continue
            mapped = False
            for prefix, name in _DOCTOR_CHECKS:
                if detail.startswith(prefix):
                    observations.setdefault(name, []).append(status)
                    mapped = True
                    break
            if status == "failed" and not mapped:
                unmapped_failure = True
            elif not mapped:
                unmapped_observation = True
        if result.unavailable:
            observations.setdefault("deploymentFiles", []).append("unavailable")
        if unmapped_observation:
            observations.setdefault("deploymentFiles", []).append("unavailable")
        if unmapped_failure or (
            result.exit_code not in (None, 0)
            and not any("failed" in values for values in observations.values())
        ):
            observations.setdefault("deploymentFiles", []).append("failed")
        return observations

    def _disk_check(
        self,
        path: Path,
        name: str,
        *,
        missing_ok: bool = False,
    ) -> str:
        try:
            usage = self._disk_usage(path)
            total, _used, free = int(usage[0]), int(usage[1]), int(usage[2])
            if total <= 0 or free < 0:
                return "unavailable"
            ratio = free / total
            if free < 268_435_456 or ratio < 0.05:
                return "failed"
            if free < 1_073_741_824 or ratio < 0.10:
                return "warning"
            return "healthy"
        except (FileNotFoundError, NotADirectoryError):
            return "notApplicable" if missing_ok else "unavailable"
        except (OSError, TypeError, ValueError, IndexError):
            return "unavailable"

    @staticmethod
    def _collapse_check(
        name: str,
        observations: Sequence[str],
        doctor: CommandResult,
    ) -> DiagnosticCheck:
        if "failed" in observations:
            status = "failed"
        elif "timedOut" in observations:
            status = "timedOut"
        elif "unavailable" in observations:
            status = "unavailable"
        elif "warning" in observations:
            status = "warning"
        elif observations and all(item == "healthy" for item in observations):
            status = "healthy"
        elif "notApplicable" in observations:
            status = "notApplicable"
        elif doctor.timed_out:
            status = "timedOut"
        else:
            status = "unavailable"
        return DiagnosticCheck(name, status, _reason_code(name, status))


def build_support_bundle(snapshot: DiagnosticSnapshot) -> bytes:
    payload = snapshot.to_protocol()
    entries: dict[str, bytes] = {
        "manifest.json": _json_bytes(
            {
                "bundleSchemaVersion": 1,
                "generatedAt": snapshot.generated_at,
                "hostSnapshotComplete": snapshot.complete,
                "updaterProtocolVersion": snapshot.updater_protocol_version,
                "channel": snapshot.channel,
                "currentVersion": snapshot.current_version,
                "operationId": snapshot.operation_id,
            }
        ),
        "update-trace.json": _json_bytes(
            {"available": snapshot.trace is not None, "trace": snapshot.trace}
        ),
        "deployment-health.json": _json_bytes(
            {"schemaVersion": DIAGNOSTIC_SCHEMA_VERSION, "checks": payload["checks"]}
        ),
        "summary.txt": _summary(snapshot).encode("utf-8"),
        "README.txt": _README.encode("utf-8"),
    }
    if tuple(sorted(entries)) != BUNDLE_ENTRIES:
        raise ValueError("the support bundle entry contract is invalid")
    if sum(len(value) for value in entries.values()) > MAX_BUNDLE_CONTENT_BYTES:
        raise ValueError("the support bundle is too large")
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name in BUNDLE_ENTRIES:
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 0
            info.external_attr = 0
            archive.writestr(info, entries[name])
    return output.getvalue()


def _summary(snapshot: DiagnosticSnapshot) -> str:
    failed = sum(check.status == "failed" for check in snapshot.checks)
    delayed = sum(check.status in {"timedOut", "unavailable"} for check in snapshot.checks)
    state = "complete" if snapshot.complete else "partial"
    return (
        "ReachCommander sanitized update diagnostics\n"
        f"Snapshot: {state}\n"
        f"Failed checks: {failed}\n"
        f"Timed out or unavailable checks: {delayed}\n\n"
        "Safe next commands:\n"
        "  sudo reachcommander update-log\n"
        "  sudo reachcommander doctor\n"
        "No data was uploaded automatically.\n"
    )


_README = """ReachCommander support bundle

This archive contains allowlisted update stages and deployment-health status codes.
It intentionally excludes raw logs, credentials, tokens, paths, filenames, addresses,
hostnames, environment values, image digests, container identifiers, and file contents.
No data was uploaded automatically. Review the files before sharing the archive.
"""


def _reason_code(name: str, status: str) -> str:
    if name not in DIAGNOSTIC_CHECK_NAMES or status not in DIAGNOSTIC_STATUSES:
        raise ValueError("the diagnostic reason is invalid")
    return f"{_lower_snake(name)}_{_lower_snake(status)}"


def _lower_snake(value: str) -> str:
    output: list[str] = []
    for character in value:
        if character.isupper():
            output.extend(("_", character.lower()))
        else:
            output.append(character)
    return "".join(output).lstrip("_")


def _read_regular(path: Path, maximum: int) -> str:
    status = path.lstat()
    if path.is_symlink() or not stat.S_ISREG(status.st_mode) or status.st_size > maximum:
        raise ValueError("the diagnostic state file is unsafe")
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    try:
        opened = os.fstat(descriptor)
        if not stat.S_ISREG(opened.st_mode):
            raise ValueError("the diagnostic state file is unsafe")
        raw = os.read(descriptor, maximum + 1)
    finally:
        os.close(descriptor)
    if len(raw) > maximum:
        raise ValueError("the diagnostic state file is too large")
    return raw.decode("utf-8")


def _reject_duplicate_keys(pairs: Sequence[tuple[str, object]]) -> dict[str, object]:
    value: dict[str, object] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("the diagnostic state contains duplicate fields")
        value[key] = item
    return value


def _bounded_decode(value: bytes | None) -> str:
    raw = (value or b"")[:MAX_COMMAND_OUTPUT_BYTES]
    return raw.decode("utf-8", errors="replace")


def _terminate_process_group(process: subprocess.Popen[bytes]) -> None:
    try:
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGKILL)
        else:
            process.kill()
    except (OSError, ProcessLookupError):
        pass


def _utc(value: dt.datetime) -> dt.datetime:
    if value.tzinfo is None:
        value = value.replace(tzinfo=dt.timezone.utc)
    return value.astimezone(dt.timezone.utc)


def _iso_utc(value: dt.datetime) -> str:
    return _utc(value).isoformat().replace("+00:00", "Z")


def _json_bytes(value: object) -> bytes:
    return (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True) + "\n").encode("utf-8")


__all__ = [
    "BUNDLE_ENTRIES",
    "COLLECTION_TIMEOUT_SECONDS",
    "COMMAND_TIMEOUT_SECONDS",
    "DIAGNOSTIC_CHECK_NAMES",
    "DIAGNOSTIC_PROTOCOL_VERSION",
    "DIAGNOSTIC_SCHEMA_VERSION",
    "DIAGNOSTIC_STATUSES",
    "CommandResult",
    "DiagnosticCheck",
    "DiagnosticSnapshot",
    "FixedCommandRunner",
    "HostDiagnosticCollector",
    "build_support_bundle",
]
