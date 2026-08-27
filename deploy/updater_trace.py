#!/usr/bin/env python3
"""Protected, bounded traces for installer-managed ReachCommander updates."""

from __future__ import annotations

import dataclasses
import datetime as dt
import json
import os
import stat
import threading
import uuid
from pathlib import Path
from typing import Mapping, Sequence


TRACE_SCHEMA_VERSION = 1
MAX_TRACE_FILES = 10
MAX_TRACE_DIRECTORY_BYTES = 10 * 1024 * 1024
MAX_TRACE_BYTES = 1024 * 1024
MAX_TRACE_EVENTS = 512
MAX_PUBLIC_EVENTS = 32

EVENT_CODES = frozenset(
    {
        "operationAccepted",
        "downloadStarted",
        "hostActivity",
        "downloadCompleted",
        "backupStarted",
        "backupCompleted",
        "installStarted",
        "installCompleted",
        "candidateRestartStarted",
        "candidateRestartCompleted",
        "candidateImageVerified",
        "candidateHealthStarted",
        "candidateHealthActivity",
        "candidateHealthSucceeded",
        "candidateHealthFailed",
        "rollbackStarted",
        "rollbackStateRestored",
        "previousRestartStarted",
        "previousRestartCompleted",
        "previousImageVerified",
        "recoveryHealthStarted",
        "recoveryHealthActivity",
        "recoveryHealthSucceeded",
        "recoveryHealthFailed",
        "commandTimedOut",
        "terminationRequested",
        "terminationForced",
        "operationCompleted",
        "operationRolledBack",
        "operationFailed",
    }
)
OUTCOMES = frozenset({"started", "activity", "succeeded", "failed", "timedOut"})
EVENT_OUTCOMES = {
    "operationAccepted": frozenset({"started"}),
    "downloadStarted": frozenset({"started"}),
    "hostActivity": frozenset({"activity"}),
    "downloadCompleted": frozenset({"succeeded"}),
    "backupStarted": frozenset({"started"}),
    "backupCompleted": frozenset({"succeeded"}),
    "installStarted": frozenset({"started"}),
    "installCompleted": frozenset({"succeeded"}),
    "candidateRestartStarted": frozenset({"started"}),
    "candidateRestartCompleted": frozenset({"succeeded", "failed"}),
    "candidateImageVerified": frozenset({"succeeded", "failed"}),
    "candidateHealthStarted": frozenset({"started"}),
    "candidateHealthActivity": frozenset({"activity"}),
    "candidateHealthSucceeded": frozenset({"succeeded"}),
    "candidateHealthFailed": frozenset({"failed"}),
    "rollbackStarted": frozenset({"started"}),
    "rollbackStateRestored": frozenset({"succeeded", "failed"}),
    "previousRestartStarted": frozenset({"started"}),
    "previousRestartCompleted": frozenset({"succeeded", "failed"}),
    "previousImageVerified": frozenset({"succeeded", "failed"}),
    "recoveryHealthStarted": frozenset({"started"}),
    "recoveryHealthActivity": frozenset({"activity"}),
    "recoveryHealthSucceeded": frozenset({"succeeded"}),
    "recoveryHealthFailed": frozenset({"failed"}),
    "commandTimedOut": frozenset({"timedOut"}),
    "terminationRequested": frozenset({"started"}),
    "terminationForced": frozenset({"started"}),
    "operationCompleted": frozenset({"succeeded"}),
    "operationRolledBack": frozenset({"succeeded"}),
    "operationFailed": frozenset({"failed"}),
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
TERMINAL_CODES = frozenset(
    {"operationCompleted", "operationRolledBack", "operationFailed"}
)

_EVENT_FIELDS = frozenset(
    {
        "schemaVersion",
        "sequence",
        "operationId",
        "timestamp",
        "elapsedMilliseconds",
        "code",
        "stage",
        "outcome",
        "exitCode",
        "timeoutSeconds",
    }
)


class TraceError(ValueError):
    """A fixed trace validation or persistence failure."""


@dataclasses.dataclass(frozen=True, slots=True)
class TraceEvent:
    schema_version: int
    sequence: int
    operation_id: str
    timestamp: str
    elapsed_milliseconds: int
    code: str
    stage: str | None
    outcome: str
    exit_code: int | None = None
    timeout_seconds: int | None = None

    def to_storage(self) -> dict[str, object]:
        return {
            "schemaVersion": self.schema_version,
            "sequence": self.sequence,
            "operationId": self.operation_id,
            "timestamp": self.timestamp,
            "elapsedMilliseconds": self.elapsed_milliseconds,
            "code": self.code,
            "stage": self.stage,
            "outcome": self.outcome,
            "exitCode": self.exit_code,
            "timeoutSeconds": self.timeout_seconds,
        }


@dataclasses.dataclass(frozen=True, slots=True)
class PublicTraceEvent:
    sequence: int
    timestamp: str
    elapsed_seconds: int
    code: str
    stage: str | None
    outcome: str

    def to_protocol(self) -> dict[str, object]:
        return {
            "sequence": self.sequence,
            "timestamp": self.timestamp,
            "elapsedSeconds": self.elapsed_seconds,
            "code": self.code,
            "stage": self.stage,
            "outcome": self.outcome,
        }


@dataclasses.dataclass(frozen=True, slots=True)
class PublicTraceSnapshot:
    started_at: str
    elapsed_seconds: int
    last_activity_at: str | None
    events: tuple[PublicTraceEvent, ...]

    def to_protocol(self) -> dict[str, object]:
        return {
            "startedAt": self.started_at,
            "elapsedSeconds": self.elapsed_seconds,
            "lastActivityAt": self.last_activity_at,
            "events": [event.to_protocol() for event in self.events],
        }


class ProtectedUpdateTraceStore:
    def __init__(
        self,
        root: Path | str,
        *,
        clock: object | None = None,
    ) -> None:
        self.root = Path(root)
        self._clock = clock or (lambda: dt.datetime.now(dt.timezone.utc))
        self._lock = threading.RLock()

    def start(self, operation_id: str, now: dt.datetime | None = None) -> TraceEvent:
        timestamp = _utc(now or self._clock())  # type: ignore[operator]
        path = _trace_path(self.root, operation_id)
        with self._lock:
            self._ensure_root(create=True)
            self._trace_files(allow_limits_exceeded=False)
            event = TraceEvent(
                TRACE_SCHEMA_VERSION,
                1,
                operation_id,
                _iso_utc(timestamp),
                0,
                "operationAccepted",
                None,
                "started",
            )
            self._create_trace(path, event)
            self.prune(operation_id)
            return event

    def append(
        self,
        operation_id: str,
        code: str,
        outcome: str,
        now: dt.datetime | None = None,
        *,
        stage: str | None = None,
        exit_code: int | None = None,
        timeout_seconds: int | None = None,
    ) -> TraceEvent:
        timestamp = _utc(now or self._clock())  # type: ignore[operator]
        path = _trace_path(self.root, operation_id)
        with self._lock:
            events = self._read_trace(path, operation_id)
            if not events:
                raise TraceError("the update trace is empty")
            if events[-1].code in TERMINAL_CODES:
                raise TraceError("the update trace is already terminal")
            if len(events) >= MAX_TRACE_EVENTS:
                raise TraceError("the update trace contains too many events")
            _validate_event_values(code, outcome, stage, exit_code, timeout_seconds)
            started = _parse_timestamp(events[0].timestamp)
            previous = _parse_timestamp(events[-1].timestamp)
            if timestamp < started or timestamp < previous:
                raise TraceError("the update trace timestamp is invalid")
            event = TraceEvent(
                TRACE_SCHEMA_VERSION,
                events[-1].sequence + 1,
                operation_id,
                _iso_utc(timestamp),
                int((timestamp - started).total_seconds() * 1000),
                code,
                stage,
                outcome,
                exit_code,
                timeout_seconds,
            )
            self._append_trace(path, event)
            return event

    def public_snapshot(
        self,
        operation_id: str,
        now: dt.datetime | None = None,
        *,
        blocking: bool = True,
    ) -> PublicTraceSnapshot | None:
        path = _trace_path(self.root, operation_id)
        acquired = self._lock.acquire(blocking=blocking)
        if not acquired:
            return None
        try:
            if not path.exists():
                return None
            events = self._read_trace(path, operation_id)
        finally:
            self._lock.release()
        if not events:
            return None
        current = _utc(now or self._clock())  # type: ignore[operator]
        started = _parse_timestamp(events[0].timestamp)
        if current < started:
            current = started
        activity = next(
            (event.timestamp for event in reversed(events) if event.outcome == "activity"),
            None,
        )
        public = tuple(
            PublicTraceEvent(
                event.sequence,
                event.timestamp,
                event.elapsed_milliseconds // 1000,
                event.code,
                event.stage,
                event.outcome,
            )
            for event in events[-MAX_PUBLIC_EVENTS:]
        )
        return PublicTraceSnapshot(
            events[0].timestamp,
            int((current - started).total_seconds()),
            activity,
            public,
        )

    def latest_path(self) -> Path | None:
        with self._lock:
            files = self._trace_files(allow_limits_exceeded=False)
            latest: tuple[dt.datetime, int, Path] | None = None
            for path in files:
                events = self._read_trace(path, path.stem)
                if not events:
                    continue
                candidate = (
                    _parse_timestamp(events[-1].timestamp),
                    events[-1].sequence,
                    path,
                )
                if latest is None or candidate[:2] > latest[:2]:
                    latest = candidate
            return None if latest is None else latest[2]

    def validate_tree(self) -> tuple[bool, str]:
        with self._lock:
            try:
                if not self.root.exists():
                    return True, "Update trace storage is empty."
                files = self._trace_files(allow_limits_exceeded=False)
                for path in files:
                    self._read_trace(path, path.stem)
            except TraceError:
                return False, "Update trace storage contains an unsafe entry."
        return True, "Update trace storage is structurally safe."

    def prune(self, active_operation_id: str | None) -> None:
        active_path = (
            None if active_operation_id is None else _trace_path(self.root, active_operation_id)
        )
        with self._lock:
            files = self._trace_files(allow_limits_exceeded=True)
            records: list[tuple[dt.datetime, bool, Path, int]] = []
            for path in files:
                events = self._read_trace(path, path.stem)
                if not events:
                    raise TraceError("the update trace is empty")
                terminal = events[-1].code in TERMINAL_CODES
                records.append(
                    (_parse_timestamp(events[-1].timestamp), terminal, path, path.stat().st_size)
                )
            total = sum(item[3] for item in records)
            while len(records) > MAX_TRACE_FILES or total > MAX_TRACE_DIRECTORY_BYTES:
                removable = sorted(
                    (
                        item
                        for item in records
                        if item[1] and (active_path is None or item[2] != active_path)
                    ),
                    key=lambda item: (item[0], item[2].name),
                )
                if not removable:
                    raise TraceError("the update trace retention limit cannot be satisfied")
                oldest = removable[0]
                self._unlink_regular(oldest[2])
                records.remove(oldest)
                total -= oldest[3]

    def _ensure_root(self, *, create: bool) -> None:
        try:
            status = self.root.lstat()
        except FileNotFoundError:
            if not create:
                raise TraceError("the update trace directory is unavailable")
            self.root.mkdir(parents=True, mode=0o700)
            if os.name != "nt":
                os.chmod(self.root, 0o700, follow_symlinks=False)
            status = self.root.lstat()
        except OSError as error:
            raise TraceError("the update trace directory is unavailable") from error
        if self.root.is_symlink() or not stat.S_ISDIR(status.st_mode):
            raise TraceError("the update trace directory is unsafe")
        if os.name != "nt":
            if stat.S_IMODE(status.st_mode) != 0o700 or status.st_uid != os.geteuid():
                raise TraceError("the update trace directory is unsafe")

    def _trace_files(self, *, allow_limits_exceeded: bool) -> list[Path]:
        self._ensure_root(create=False)
        files: list[Path] = []
        total = 0
        try:
            entries = list(self.root.iterdir())
        except OSError as error:
            raise TraceError("the update trace directory cannot be read") from error
        for path in entries:
            try:
                status = path.lstat()
            except OSError as error:
                raise TraceError("an update trace is unavailable") from error
            try:
                canonical = str(uuid.UUID(path.stem))
            except (ValueError, AttributeError) as error:
                raise TraceError("the update trace directory contains an unsafe entry") from error
            if (
                path.name != f"{canonical}.jsonl"
                or path.is_symlink()
                or not stat.S_ISREG(status.st_mode)
                or status.st_size > MAX_TRACE_BYTES
            ):
                raise TraceError("the update trace directory contains an unsafe entry")
            if os.name != "nt" and (
                stat.S_IMODE(status.st_mode) != 0o600 or status.st_uid != os.geteuid()
            ):
                raise TraceError("the update trace directory contains an unsafe entry")
            files.append(path)
            total += status.st_size
        if not allow_limits_exceeded and (
            len(files) > MAX_TRACE_FILES or total > MAX_TRACE_DIRECTORY_BYTES
        ):
            raise TraceError("the update trace retention limit is exceeded")
        return files

    def _create_trace(self, path: Path, event: TraceEvent) -> None:
        payload = _event_payload(event)
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
        flags |= getattr(os, "O_CLOEXEC", 0)
        flags |= getattr(os, "O_NOFOLLOW", 0)
        descriptor: int | None = None
        try:
            descriptor = os.open(path, flags, 0o600)
            os.write(descriptor, payload)
            os.fsync(descriptor)
            if os.name != "nt":
                os.chmod(path, 0o600, follow_symlinks=False)
        except OSError as error:
            raise TraceError("the update trace cannot be created") from error
        finally:
            if descriptor is not None:
                os.close(descriptor)

    def _append_trace(self, path: Path, event: TraceEvent) -> None:
        payload = _event_payload(event)
        try:
            current_size = path.lstat().st_size
        except OSError as error:
            raise TraceError("the update trace is unavailable") from error
        if current_size + len(payload) > MAX_TRACE_BYTES:
            raise TraceError("the update trace is too large")
        flags = os.O_WRONLY | os.O_APPEND
        flags |= getattr(os, "O_CLOEXEC", 0)
        flags |= getattr(os, "O_NOFOLLOW", 0)
        descriptor: int | None = None
        try:
            descriptor = os.open(path, flags)
            opened = os.fstat(descriptor)
            if not stat.S_ISREG(opened.st_mode):
                raise TraceError("the update trace is unsafe")
            os.write(descriptor, payload)
            os.fsync(descriptor)
        except TraceError:
            raise
        except OSError as error:
            raise TraceError("the update trace cannot be appended") from error
        finally:
            if descriptor is not None:
                os.close(descriptor)

    def _read_trace(self, path: Path, operation_id: str) -> list[TraceEvent]:
        expected = _trace_path(self.root, operation_id)
        if path != expected:
            raise TraceError("the update trace path is invalid")
        flags = os.O_RDONLY
        flags |= getattr(os, "O_CLOEXEC", 0)
        flags |= getattr(os, "O_NOFOLLOW", 0)
        descriptor: int | None = None
        try:
            descriptor = os.open(path, flags)
            opened = os.fstat(descriptor)
            if not stat.S_ISREG(opened.st_mode) or opened.st_size > MAX_TRACE_BYTES:
                raise TraceError("the update trace is unsafe")
            raw = os.read(descriptor, MAX_TRACE_BYTES + 1)
        except FileNotFoundError:
            raise TraceError("the update trace is unavailable")
        except TraceError:
            raise
        except OSError as error:
            raise TraceError("the update trace cannot be read") from error
        finally:
            if descriptor is not None:
                os.close(descriptor)
        if len(raw) > MAX_TRACE_BYTES:
            raise TraceError("the update trace is too large")
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError as error:
            raise TraceError("the update trace is invalid") from error
        if not text.endswith("\n"):
            raise TraceError("the update trace is invalid")
        lines = text.splitlines()
        if not lines or len(lines) > MAX_TRACE_EVENTS:
            raise TraceError("the update trace event count is invalid")
        events: list[TraceEvent] = []
        for line in lines:
            try:
                value = json.loads(line, object_pairs_hook=_reject_duplicate_keys)
            except (json.JSONDecodeError, UnicodeDecodeError, TypeError, TraceError) as error:
                raise TraceError("the update trace is invalid") from error
            events.append(_parse_event(value, operation_id))
        _validate_event_order(events)
        return events

    @staticmethod
    def _unlink_regular(path: Path) -> None:
        status = path.lstat()
        if path.is_symlink() or not stat.S_ISREG(status.st_mode):
            raise TraceError("the update trace is unsafe")
        try:
            path.unlink()
        except OSError as error:
            raise TraceError("the update trace cannot be pruned") from error


def _trace_path(root: Path, operation_id: str) -> Path:
    try:
        canonical = str(uuid.UUID(operation_id))
    except (ValueError, AttributeError) as error:
        raise TraceError("the update trace operation identifier is invalid") from error
    if canonical != operation_id:
        raise TraceError("the update trace operation identifier is invalid")
    return root / f"{canonical}.jsonl"


def _event_payload(event: TraceEvent) -> bytes:
    return (json.dumps(event.to_storage(), separators=(",", ":"), sort_keys=True) + "\n").encode()


def _reject_duplicate_keys(pairs: Sequence[tuple[str, object]]) -> dict[str, object]:
    value: dict[str, object] = {}
    for key, item in pairs:
        if key in value:
            raise TraceError("the update trace contains duplicate fields")
        value[key] = item
    return value


def _parse_event(value: object, operation_id: str) -> TraceEvent:
    if not isinstance(value, dict) or set(value) != _EVENT_FIELDS:
        raise TraceError("the update trace event schema is invalid")
    schema_version = _required_int(value, "schemaVersion", minimum=1, maximum=1)
    sequence = _required_int(value, "sequence", minimum=1, maximum=MAX_TRACE_EVENTS)
    stored_operation = value.get("operationId")
    if stored_operation != operation_id:
        raise TraceError("the update trace operation identifier is invalid")
    timestamp = value.get("timestamp")
    if not isinstance(timestamp, str):
        raise TraceError("the update trace timestamp is invalid")
    _parse_timestamp(timestamp)
    elapsed = _required_int(
        value,
        "elapsedMilliseconds",
        minimum=0,
        maximum=24 * 60 * 60 * 1000,
    )
    code = value.get("code")
    outcome = value.get("outcome")
    stage = value.get("stage")
    exit_code = _optional_int(value, "exitCode", minimum=-255, maximum=255)
    timeout_seconds = _optional_int(value, "timeoutSeconds", minimum=1, maximum=3600)
    _validate_event_values(code, outcome, stage, exit_code, timeout_seconds)
    return TraceEvent(
        schema_version,
        sequence,
        operation_id,
        timestamp,
        elapsed,
        code,  # type: ignore[arg-type]
        stage,  # type: ignore[arg-type]
        outcome,  # type: ignore[arg-type]
        exit_code,
        timeout_seconds,
    )


def _validate_event_values(
    code: object,
    outcome: object,
    stage: object,
    exit_code: object,
    timeout_seconds: object,
) -> None:
    if not isinstance(code, str) or code not in EVENT_CODES:
        raise TraceError("the update trace event code is invalid")
    if not isinstance(outcome, str) or outcome not in OUTCOMES:
        raise TraceError("the update trace event outcome is invalid")
    if outcome not in EVENT_OUTCOMES[code]:
        raise TraceError("the update trace event outcome is invalid")
    if stage is not None and (not isinstance(stage, str) or stage not in PROGRESS_STAGES):
        raise TraceError("the update trace progress stage is invalid")
    if exit_code is not None and (
        not isinstance(exit_code, int) or isinstance(exit_code, bool) or not -255 <= exit_code <= 255
    ):
        raise TraceError("the update trace exit code is invalid")
    if timeout_seconds is not None and (
        not isinstance(timeout_seconds, int)
        or isinstance(timeout_seconds, bool)
        or not 1 <= timeout_seconds <= 3600
    ):
        raise TraceError("the update trace timeout is invalid")


def _validate_event_order(events: list[TraceEvent]) -> None:
    previous_sequence = 0
    previous_elapsed = -1
    previous_timestamp: dt.datetime | None = None
    terminal_seen = False
    for event in events:
        timestamp = _parse_timestamp(event.timestamp)
        if (
            event.sequence != previous_sequence + 1
            or event.elapsed_milliseconds < previous_elapsed
            or (previous_timestamp is not None and timestamp < previous_timestamp)
            or terminal_seen
        ):
            raise TraceError("the update trace event order is invalid")
        previous_sequence = event.sequence
        previous_elapsed = event.elapsed_milliseconds
        previous_timestamp = timestamp
        terminal_seen = event.code in TERMINAL_CODES
    if events[0].code != "operationAccepted" or events[0].elapsed_milliseconds != 0:
        raise TraceError("the update trace start event is invalid")


def _required_int(
    value: Mapping[str, object],
    name: str,
    *,
    minimum: int,
    maximum: int,
) -> int:
    item = value.get(name)
    if (
        not isinstance(item, int)
        or isinstance(item, bool)
        or not minimum <= item <= maximum
    ):
        raise TraceError(f"the update trace field '{name}' is invalid")
    return item


def _optional_int(
    value: Mapping[str, object],
    name: str,
    *,
    minimum: int,
    maximum: int,
) -> int | None:
    item = value.get(name)
    if item is None:
        return None
    return _required_int(value, name, minimum=minimum, maximum=maximum)


def _utc(value: dt.datetime) -> dt.datetime:
    if not isinstance(value, dt.datetime) or value.tzinfo is None:
        raise TraceError("the update trace timestamp is invalid")
    return value.astimezone(dt.timezone.utc)


def _iso_utc(value: dt.datetime) -> str:
    return value.isoformat(timespec="microseconds").replace(".000000+00:00", "Z").replace(
        "+00:00", "Z"
    )


def _parse_timestamp(value: str) -> dt.datetime:
    if not isinstance(value, str) or len(value) > 40 or not value.endswith("Z"):
        raise TraceError("the update trace timestamp is invalid")
    try:
        parsed = dt.datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as error:
        raise TraceError("the update trace timestamp is invalid") from error
    return _utc(parsed)
