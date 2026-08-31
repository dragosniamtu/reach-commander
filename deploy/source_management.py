#!/usr/bin/env python3
"""Durable, installer-owned transaction for adding one Ubuntu host source."""

from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import time
import uuid
from pathlib import Path, PurePosixPath
from typing import Callable, Iterable

if __package__:
    from . import render_config as renderer
    from .updater_protocol import (
        MAX_SOURCE_MANAGEMENT_MESSAGE_BYTES,
        ProtocolError,
        SourceManagementRequest,
    )
else:
    import render_config as renderer

    _INSTALL_LIB = Path(__file__).resolve().parent.parent / "lib"
    sys.path.insert(0, str(_INSTALL_LIB))
    from updater_protocol import (
        MAX_SOURCE_MANAGEMENT_MESSAGE_BYTES,
        ProtocolError,
        SourceManagementRequest,
    )


MAX_SOURCES = 32
MAX_CANONICAL_SOURCE_CHARS = 1_024
_SOURCE_ID = re.compile(r"^[a-z][a-z0-9-]{0,62}$")
_PROTECTED_ROOTS = ("/", "/proc", "/sys", "/dev", "/run", "/var/run")
_BROAD_ROOTS = frozenset({"/home", "/srv", "/mnt"})
_PRODUCTION_OWNED = (
    "/opt/reachcommander",
    "/opt/.reachcommander.lock",
    "/usr/local/bin/reachcommander",
    "/var/backups/reachcommander",
    "/etc/systemd/system/reachcommander-updater.service",
    "/run/reachcommander-updater",
)
_TRANSACTION_FILES = (
    "config/sources.json",
    "state/source-mounts.json",
    "compose.yaml",
)
_JOURNAL_MAX_BYTES = 4_096
_PHASE_REASON_CODES = {
    "validating": frozenset({"in_progress"}),
    "staging": frozenset({"in_progress"}),
    "publishing": frozenset({"recovery_required"}),
    "published": frozenset({"recovery_required"}),
    "restarting": frozenset({"recovery_required"}),
    "healthChecking": frozenset({"recovery_required"}),
    "completed": frozenset({"completed"}),
    "rolledBack": frozenset({"rolled_back"}),
    "failed": frozenset({"source_management_failed", "recovery_failed"}),
}
_PUBLIC_DETAILS = {
    "invalid_request": "The source-management request is invalid.",
    "validation_failed": "The source folder could not be accepted.",
    "busy": "Another ReachCommander operation is already running.",
    "source_management_failed": "The source-management operation could not be completed.",
    "rolled_back": "The source change was rolled back.",
    "recovery_failed": "The source change requires manual recovery.",
}


class SourceManagementFailure(RuntimeError):
    """A bounded failure whose message is safe for a public caller."""

    def __init__(self, code: str = "source_management_failed") -> None:
        self.code = code if code in _PUBLIC_DETAILS else "source_management_failed"
        super().__init__(_PUBLIC_DETAILS[self.code])


class SimulatedInterruption(BaseException):
    """Test-only crash boundary that deliberately bypasses rollback handlers."""


def _is_posix_absolute(value: object) -> bool:
    return (
        isinstance(value, str)
        and value.startswith("/")
        and "\\" not in value
        and "\x00" not in value
        and not any(ord(character) < 32 or ord(character) == 127 for character in value)
    )


def _is_at_or_below(path: str, root: str) -> bool:
    return path == root or (root != "/" and path.startswith(root + "/"))


def _paths_overlap(first: str, second: str) -> bool:
    return _is_at_or_below(first, second) or _is_at_or_below(second, first)


def canonical_source_path(
    requested: str,
    *,
    canonicalizer: Callable[[str], str] = os.path.realpath,
    directory_exists: Callable[[str], bool] = os.path.isdir,
) -> str:
    if not _is_posix_absolute(requested):
        raise SourceManagementFailure("validation_failed")
    try:
        canonical = canonicalizer(requested)
    except (OSError, RuntimeError, ValueError):
        raise SourceManagementFailure("validation_failed") from None
    if (
        not _is_posix_absolute(canonical)
        or len(canonical) > MAX_CANONICAL_SOURCE_CHARS
        or not directory_exists(canonical)
    ):
        raise SourceManagementFailure("validation_failed")
    normalized = str(PurePosixPath(canonical))
    if normalized in _BROAD_ROOTS or any(
        _is_at_or_below(normalized, root) for root in _PROTECTED_ROOTS
    ):
        raise SourceManagementFailure("validation_failed")
    return normalized


def capture_trusted_source_identity(
    path: str,
    *,
    status_reader: Callable[[str], os.stat_result] = os.lstat,
) -> tuple[int, int]:
    """Require an unprivileged process cannot replace the persisted path entry."""
    try:
        leaf_status = status_reader(path)
        if not stat.S_ISDIR(leaf_status.st_mode):
            raise SourceManagementFailure("validation_failed")
        parent = PurePosixPath(path).parent
        while True:
            parent_status = status_reader(str(parent))
            if (
                not stat.S_ISDIR(parent_status.st_mode)
                or parent_status.st_uid != 0
                or stat.S_IMODE(parent_status.st_mode) & 0o022
            ):
                raise SourceManagementFailure("validation_failed")
            if parent == PurePosixPath("/"):
                break
            parent = parent.parent
    except SourceManagementFailure:
        raise
    except (OSError, AttributeError, TypeError, ValueError):
        raise SourceManagementFailure("validation_failed") from None
    return (leaf_status.st_dev, leaf_status.st_ino)


def validate_installed_source_paths(
    paths: Iterable[str],
    *,
    installer_owned: Iterable[str],
    canonicalizer: Callable[[str], str] = os.path.realpath,
    directory_exists: Callable[[str], bool] = os.path.isdir,
    status_reader: Callable[[str], os.stat_result] = os.lstat,
) -> dict[str, tuple[int, int]]:
    canonical_paths: list[str] = []
    identities: dict[str, tuple[int, int]] = {}
    for persisted in paths:
        canonical = canonical_source_path(
            persisted,
            canonicalizer=canonicalizer,
            directory_exists=directory_exists,
        )
        if canonical != persisted:
            raise SourceManagementFailure("validation_failed")
        validate_source_separation(
            canonical,
            existing=canonical_paths,
            installer_owned=installer_owned,
        )
        identities[canonical] = capture_trusted_source_identity(
            canonical, status_reader=status_reader
        )
        canonical_paths.append(canonical)
    return identities


def validate_source_separation(
    candidate: str,
    *,
    existing: Iterable[str],
    installer_owned: Iterable[str],
) -> None:
    if any(_paths_overlap(candidate, path) for path in existing):
        raise SourceManagementFailure("validation_failed")
    if any(_paths_overlap(candidate, path) for path in installer_owned):
        raise SourceManagementFailure("validation_failed")


def _source_slug(display_name: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "-", display_name.lower()).strip("-")
    if not slug:
        slug = "source"
    if not slug[0].isalpha():
        slug = "source-" + slug
    return slug[:63].rstrip("-") or "source"


def generate_source_id(
    display_name: str,
    existing_ids: set[str],
    uuid_factory: Callable[[], uuid.UUID] = uuid.uuid4,
) -> str:
    base = _source_slug(display_name)
    if base not in existing_ids and _SOURCE_ID.fullmatch(base):
        return base
    for _ in range(16):
        suffix = uuid_factory().hex[:8]
        candidate = f"{base[:54].rstrip('-')}-{suffix}"
        if candidate not in existing_ids and _SOURCE_ID.fullmatch(candidate):
            return candidate
    raise SourceManagementFailure("validation_failed")


def require_source_capacity(count: int) -> None:
    if not isinstance(count, int) or count < 0 or count >= MAX_SOURCES:
        raise SourceManagementFailure("validation_failed")


def require_runtime_access(
    path: str,
    uid: int,
    gid: int,
    writable: bool,
    checker: Callable[[str, int, int, bool], bool],
) -> None:
    try:
        allowed = checker(path, uid, gid, writable)
    except (OSError, RuntimeError, subprocess.SubprocessError):
        allowed = False
    if not allowed:
        raise SourceManagementFailure("validation_failed")


def installed_state_owner() -> int:
    if os.environ.get("REACHCOMMANDER_TESTING") == "1" and hasattr(os, "geteuid"):
        effective_uid = os.geteuid()
        if effective_uid != 0:
            return effective_uid
    return 0


def _default_access_checker(path: str, uid: int, gid: int, writable: bool) -> bool:
    access_test = 'test -r "$1" && test -x "$1"'
    if writable:
        access_test += ' && test -w "$1"'
    completed = subprocess.run(
        [
            "setpriv",
            f"--reuid={uid}",
            f"--regid={gid}",
            "--clear-groups",
            "--",
            "sh",
            "-c",
            access_test,
            "reachcommander-source-access",
            path,
        ],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        timeout=10,
        check=False,
    )
    return completed.returncode == 0


def _default_command_runner(arguments: list[str], timeout: float) -> str:
    try:
        completed = subprocess.run(
            arguments,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.SubprocessError):
        raise SourceManagementFailure() from None
    if completed.returncode != 0:
        raise SourceManagementFailure()
    return completed.stdout


def _fsync_directory(path: Path) -> None:
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0)
    try:
        descriptor = os.open(path, flags)
    except OSError:
        if os.name != "nt":
            raise
        return
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def _write_json(path: Path, value: object, mode: int = 0o600) -> None:
    renderer.atomic_write(
        path,
        json.dumps(value, indent=2, ensure_ascii=False, sort_keys=True) + "\n",
        mode,
    )
    path.chmod(mode)


def _strict_json_pairs(pairs: list[tuple[str, object]]) -> dict[str, object]:
    value: dict[str, object] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate JSON key")
        value[key] = item
    return value


def _read_bounded_json(path: Path, maximum_bytes: int) -> object:
    if not 0 < path.stat().st_size <= maximum_bytes:
        raise ValueError("invalid JSON size")
    return json.loads(
        path.read_text(encoding="utf-8"),
        object_pairs_hook=_strict_json_pairs,
    )


def _is_utc_timestamp(value: object) -> bool:
    if type(value) is not str or not 1 <= len(value) <= 40 or not value.endswith("Z"):
        return False
    try:
        parsed = dt.datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        return False
    return parsed.tzinfo is not None and parsed.utcoffset() == dt.timedelta(0)


def _safe_status(path: Path, *, kind: str, mode: int, owner: int | None) -> None:
    try:
        status = path.lstat()
    except OSError:
        raise SourceManagementFailure("validation_failed") from None
    expected_kind = stat.S_ISDIR if kind == "directory" else stat.S_ISREG
    if path.is_symlink() or not expected_kind(status.st_mode):
        raise SourceManagementFailure("validation_failed")
    if os.name != "nt" and stat.S_IMODE(status.st_mode) != mode:
        raise SourceManagementFailure("validation_failed")
    if owner is not None and hasattr(status, "st_uid") and status.st_uid != owner:
        raise SourceManagementFailure("validation_failed")


class SourceTransaction:
    def __init__(
        self,
        install_root: Path | str,
        *,
        command_runner: Callable[[list[str], float], str] = _default_command_runner,
        canonicalizer: Callable[[str], str] = os.path.realpath,
        directory_exists: Callable[[str], bool] = os.path.isdir,
        access_checker: Callable[[str, int, int, bool], bool] = _default_access_checker,
        status_reader: Callable[[str], os.stat_result] = os.lstat,
        atomic_writer: Callable[[Path | str, str, int], None] = renderer.atomic_write,
        fsync_directory: Callable[[Path], None] = _fsync_directory,
        expected_owner: int | None = None,
        uuid_factory: Callable[[], uuid.UUID] = uuid.uuid4,
        interrupt_after: str | None = None,
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        self.root = Path(install_root)
        self._run = command_runner
        self._canonicalizer = canonicalizer
        self._directory_exists = directory_exists
        self._access = access_checker
        self._status = status_reader
        self._atomic_write = atomic_writer
        self._fsync_directory = fsync_directory
        self._owner = installed_state_owner() if expected_owner is None else expected_owner
        self._uuid = uuid_factory
        self._interrupt_after = interrupt_after
        self._sleep = sleep
        self._transaction_id: str | None = None
        self._journal_path = self.root / "state" / "source-operation.json"
        self._transaction_root = self.root / "backups" / ".source-transaction"
        self._backup_root = self._transaction_root / "backup"
        self._manifest_path = self._transaction_root / "manifest.json"

    def add(self, raw_request: bytes) -> dict[str, str]:
        try:
            return self._add(raw_request)
        except SimulatedInterruption:
            raise
        except SourceManagementFailure:
            raise
        except Exception:
            raise SourceManagementFailure() from None

    def _add(self, raw_request: bytes) -> dict[str, str]:
        request = self._parse_request(raw_request)
        self._validate_installer_state()
        self._recover_interrupted_transaction()
        self._transaction_id = None
        self._validate_installer_state()
        installed = self._load_installed_request()
        require_source_capacity(len(installed.sources))
        installer_owned = self._installer_owned_paths()
        source_identities = self._capture_installed_source_identities(installed)
        canonical = canonical_source_path(
            request.host_path or "",
            canonicalizer=self._canonicalizer,
            directory_exists=self._directory_exists,
        )
        validate_source_separation(
            canonical,
            existing=(source.host_path for source in installed.sources),
            installer_owned=installer_owned,
        )
        source_identities[canonical] = capture_trusted_source_identity(
            canonical, status_reader=self._status
        )
        source_id = generate_source_id(
            request.display_name or "",
            {source.id for source in installed.sources},
            self._uuid,
        )
        writable = request.access == "readWrite"
        require_runtime_access(
            canonical,
            installed.uid,
            installed.gid,
            writable,
            self._access,
        )
        updated = renderer.append_source(
            installed,
            source_id=source_id,
            name=request.display_name or "",
            host_path=canonical,
            access="rw" if writable else "ro",
        )
        if updated.sources[-1].host_path != canonical:
            raise SourceManagementFailure("validation_failed")
        self._apply(
            updated,
            source_id,
            request.display_name or "",
            source_identities,
        )
        return {"sourceId": source_id, "displayName": request.display_name or ""}

    @staticmethod
    def _parse_request(raw_request: bytes) -> SourceManagementRequest:
        try:
            request = SourceManagementRequest.parse(raw_request)
        except ProtocolError:
            raise SourceManagementFailure("invalid_request") from None
        if request.action != "addSource":
            raise SourceManagementFailure("invalid_request")
        return request

    def _validate_installer_state(self) -> None:
        directory_modes = {
            self.root: 0o700,
            self.root / "bin": 0o700,
            self.root / "lib": 0o700,
            self.root / "config": 0o755,
            self.root / "state": 0o700,
            self.root / "backups": 0o700,
        }
        file_modes = {
            self.root / ".env": 0o600,
            self.root / "compose.yaml": 0o600,
            self.root / "compose.override.yaml": 0o600,
            self.root / "config" / "sources.json": 0o644,
            self.root / "state" / "source-mounts.json": 0o600,
            self.root / "lib" / "compose.release.yaml": 0o600,
            self.root / "lib" / "updater_protocol.py": 0o644,
            self.root / "bin" / "render_config.py": 0o755,
            self.root / "bin" / "source_management.py": 0o755,
        }
        for path, mode in directory_modes.items():
            _safe_status(path, kind="directory", mode=mode, owner=self._owner)
        for path, mode in file_modes.items():
            _safe_status(path, kind="file", mode=mode, owner=self._owner)
        for marker in ("install-transaction", "update-transaction"):
            if (self.root / "state" / marker).exists() or (
                self.root / "state" / marker
            ).is_symlink():
                raise SourceManagementFailure("busy")

    def _load_installed_request(self):
        try:
            return renderer.load_installed_request(
                self.root / ".env",
                self.root / "config" / "sources.json",
                self.root / "state" / "source-mounts.json",
            )
        except (OSError, UnicodeError, ValueError):
            raise SourceManagementFailure("validation_failed") from None

    def _installer_owned_paths(self) -> list[str]:
        installer_owned = list(_PRODUCTION_OWNED)
        root_text = str(self.root.resolve())
        if root_text.startswith("/"):
            installer_owned.append(root_text)
        return installer_owned

    def _capture_installed_source_identities(
        self, installed
    ) -> dict[str, tuple[int, int]]:
        return validate_installed_source_paths(
            (source.host_path for source in installed.sources),
            installer_owned=self._installer_owned_paths(),
            canonicalizer=self._canonicalizer,
            directory_exists=self._directory_exists,
            status_reader=self._status,
        )

    def _journal(
        self,
        phase: str,
        source_id: str | None,
        display_name: str | None,
        *,
        reason_code: str | None = None,
    ) -> None:
        if self._transaction_id is None:
            self._transaction_id = str(self._uuid())
        now = dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
        reason = reason_code or {
            "completed": "completed",
            "rolledBack": "rolled_back",
            "failed": "source_management_failed",
            "publishing": "recovery_required",
            "published": "recovery_required",
            "restarting": "recovery_required",
            "healthChecking": "recovery_required",
        }.get(phase, "in_progress")
        _write_json(
            self._journal_path,
            {
                "schemaVersion": 1,
                "transactionId": self._transaction_id,
                "sourceId": source_id,
                "displayName": display_name,
                "phase": phase,
                "reasonCode": reason,
                "updatedAt": now,
            },
        )

    def _best_effort_journal(
        self,
        phase: str,
        source_id: str | None,
        display_name: str | None,
        *,
        reason_code: str | None = None,
    ) -> bool:
        try:
            self._journal(
                phase,
                source_id,
                display_name,
                reason_code=reason_code,
            )
        except Exception:
            return False
        return True

    def _read_journal(self) -> dict[str, object] | None:
        if not self._journal_path.exists() and not self._journal_path.is_symlink():
            return None
        _safe_status(self._journal_path, kind="file", mode=0o600, owner=self._owner)
        try:
            value = _read_bounded_json(self._journal_path, _JOURNAL_MAX_BYTES)
        except (OSError, UnicodeError, ValueError, json.JSONDecodeError):
            raise SourceManagementFailure("recovery_failed") from None
        expected = {
            "schemaVersion",
            "transactionId",
            "sourceId",
            "displayName",
            "phase",
            "reasonCode",
            "updatedAt",
        }
        if (
            type(value) is not dict
            or set(value) != expected
            or type(value["schemaVersion"]) is not int
            or value["schemaVersion"] != 1
        ):
            raise SourceManagementFailure("recovery_failed")
        transaction_id = value["transactionId"]
        try:
            parsed_transaction_id = uuid.UUID(transaction_id) if isinstance(transaction_id, str) else None
        except (ValueError, AttributeError):
            parsed_transaction_id = None
        if (
            parsed_transaction_id is None
            or parsed_transaction_id.version != 4
            or str(parsed_transaction_id) != transaction_id
        ):
            raise SourceManagementFailure("recovery_failed")
        source_id = value["sourceId"]
        display_name = value["displayName"]
        phase = value["phase"]
        reason_code = value["reasonCode"]
        if (
            type(source_id) is not str
            or _SOURCE_ID.fullmatch(source_id) is None
            or type(display_name) is not str
            or not 1 <= len(display_name) <= 80
            or display_name != display_name.strip()
            or any(not character.isprintable() for character in display_name)
            or type(phase) is not str
            or phase not in _PHASE_REASON_CODES
            or type(reason_code) is not str
            or reason_code not in _PHASE_REASON_CODES[phase]
            or not _is_utc_timestamp(value["updatedAt"])
        ):
            raise SourceManagementFailure("recovery_failed")
        self._transaction_id = transaction_id
        return value

    def _revalidate_source_identities(
        self, expected: dict[str, tuple[int, int]]
    ) -> None:
        for path, identity in expected.items():
            canonical = canonical_source_path(
                path,
                canonicalizer=self._canonicalizer,
                directory_exists=self._directory_exists,
            )
            if canonical != path or capture_trusted_source_identity(
                path, status_reader=self._status
            ) != identity:
                raise SourceManagementFailure("validation_failed")

    def _apply(
        self,
        updated,
        source_id: str,
        display_name: str,
        source_identities: dict[str, tuple[int, int]],
    ) -> None:
        publication_started = False
        self._journal("validating", source_id, display_name)
        try:
            self._journal("staging", source_id, display_name)
            self._create_backup()
            stage_root = self._transaction_root / "stage"
            stage_root.mkdir(mode=0o700)
            renderer.render_deployment(
                updated,
                self.root / "lib" / "compose.release.yaml",
                stage_root,
            )
            shutil.copyfile(self.root / "compose.override.yaml", stage_root / "compose.override.yaml")
            (stage_root / "compose.override.yaml").chmod(0o600)
            self._run(
                [
                    "docker",
                    "compose",
                    "--project-directory",
                    str(stage_root),
                    "config",
                    "--quiet",
                ],
                30,
            )
            self._journal("publishing", source_id, display_name)
            self._revalidate_source_identities(source_identities)
            publication_started = True
            self._publish(stage_root)
            self._journal("published", source_id, display_name)
            self._interrupt("published")
            self._journal("restarting", source_id, display_name)
            self._revalidate_source_identities(source_identities)
            self._recreate_service()
            self._journal("healthChecking", source_id, display_name)
            self._verify_identity_and_health(updated.image)
            self._journal("completed", source_id, display_name)
            self._remove_transaction_root()
        except SimulatedInterruption:
            raise
        except Exception:
            if not publication_started:
                journal_recorded = self._best_effort_journal(
                    "failed", source_id, display_name
                )
                if journal_recorded:
                    try:
                        self._remove_transaction_root()
                    except Exception:
                        pass
                raise SourceManagementFailure() from None
            try:
                self._restore_backup()
                previous = self._load_installed_request()
                self._capture_installed_source_identities(previous)
                self._recreate_service()
                self._verify_identity_and_health(previous.image)
                if self._best_effort_journal(
                    "rolledBack", source_id, display_name
                ):
                    self._remove_transaction_root()
            except Exception:
                self._best_effort_journal(
                    "failed",
                    source_id,
                    display_name,
                    reason_code="recovery_failed",
                )
                raise SourceManagementFailure("recovery_failed") from None
            raise SourceManagementFailure("rolled_back") from None

    def _create_backup(self) -> None:
        if self._transaction_root.exists() or self._transaction_root.is_symlink():
            raise SourceManagementFailure("recovery_failed")
        self._transaction_root.mkdir(mode=0o700)
        self._transaction_root.chmod(0o700)
        self._fsync_directory(self.root / "backups")
        self._backup_root.mkdir(mode=0o700)
        self._backup_root.chmod(0o700)
        for relative in _TRANSACTION_FILES:
            source = self.root / relative
            destination = self._backup_root / relative
            destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            destination.parent.chmod(0o700)
            shutil.copyfile(source, destination)
            destination.chmod(0o600)
            with destination.open("r+b") as stream:
                os.fsync(stream.fileno())
            self._fsync_directory(destination.parent)
        if self._transaction_id is None:
            raise SourceManagementFailure("recovery_failed")
        _write_json(
            self._manifest_path,
            {
                "schemaVersion": 1,
                "transactionId": self._transaction_id,
                "files": {
                    relative: hashlib.sha256(
                        (self._backup_root / relative).read_bytes()
                    ).hexdigest()
                    for relative in _TRANSACTION_FILES
                },
            },
        )
        self._fsync_directory(self._backup_root)
        self._fsync_directory(self._transaction_root)

    def _publish(self, stage_root: Path) -> None:
        for relative in _TRANSACTION_FILES:
            source = stage_root / relative
            destination = self.root / relative
            mode = 0o644 if relative == "config/sources.json" else 0o600
            self._atomic_write(destination, source.read_text(encoding="utf-8"), mode)
            destination.chmod(mode)

    def _validate_backup(self) -> None:
        _safe_status(self._transaction_root, kind="directory", mode=0o700, owner=self._owner)
        _safe_status(self._backup_root, kind="directory", mode=0o700, owner=self._owner)
        _safe_status(self._manifest_path, kind="file", mode=0o600, owner=self._owner)
        expected_files = {self._backup_root / relative for relative in _TRANSACTION_FILES}
        actual_files: set[Path] = set()
        for path in self._backup_root.rglob("*"):
            if path.is_symlink():
                raise SourceManagementFailure("recovery_failed")
            if path.is_file():
                actual_files.add(path)
            elif not path.is_dir():
                raise SourceManagementFailure("recovery_failed")
        if actual_files != expected_files:
            raise SourceManagementFailure("recovery_failed")
        for path in expected_files:
            _safe_status(path, kind="file", mode=0o600, owner=self._owner)
        try:
            manifest = _read_bounded_json(self._manifest_path, _JOURNAL_MAX_BYTES)
        except (OSError, UnicodeError, ValueError, json.JSONDecodeError):
            raise SourceManagementFailure("recovery_failed") from None
        if (
            type(manifest) is not dict
            or set(manifest) != {"schemaVersion", "transactionId", "files"}
            or type(manifest["schemaVersion"]) is not int
            or manifest["schemaVersion"] != 1
            or manifest["transactionId"] != self._transaction_id
            or type(manifest["files"]) is not dict
            or set(manifest["files"]) != set(_TRANSACTION_FILES)
        ):
            raise SourceManagementFailure("recovery_failed")
        for relative, digest in manifest["files"].items():
            if (
                type(digest) is not str
                or re.fullmatch(r"[0-9a-f]{64}", digest) is None
                or hashlib.sha256(
                    (self._backup_root / relative).read_bytes()
                ).hexdigest()
                != digest
            ):
                raise SourceManagementFailure("recovery_failed")

    def _restore_backup(self) -> None:
        self._validate_backup()
        for relative in _TRANSACTION_FILES:
            source = self._backup_root / relative
            destination = self.root / relative
            mode = 0o644 if relative == "config/sources.json" else 0o600
            self._atomic_write(destination, source.read_text(encoding="utf-8"), mode)
            destination.chmod(mode)

    def _remove_transaction_root(self) -> None:
        if not self._transaction_root.exists() and not self._transaction_root.is_symlink():
            return
        if self._transaction_root.is_symlink():
            raise SourceManagementFailure("recovery_failed")
        for path in self._transaction_root.rglob("*"):
            if path.is_symlink():
                raise SourceManagementFailure("recovery_failed")
        shutil.rmtree(self._transaction_root)
        self._fsync_directory(self.root / "backups")

    def _recover_interrupted_transaction(self) -> None:
        journal = self._read_journal()
        backup_exists = self._transaction_root.exists() or self._transaction_root.is_symlink()
        if journal is None:
            if backup_exists:
                raise SourceManagementFailure("recovery_failed")
            return
        phase = journal["phase"]
        if not backup_exists:
            if phase in {"publishing", "published", "restarting", "healthChecking"} or (
                phase == "failed" and journal["reasonCode"] == "recovery_failed"
            ):
                raise SourceManagementFailure("recovery_failed")
            return
        source_id = journal["sourceId"] if isinstance(journal["sourceId"], str) else None
        display_name = (
            journal["displayName"] if isinstance(journal["displayName"], str) else None
        )
        if phase == "staging":
            if self._best_effort_journal("rolledBack", source_id, display_name):
                self._remove_transaction_root()
            return
        if phase in {"completed", "rolledBack"}:
            self._remove_transaction_root()
            return
        try:
            self._restore_backup()
            previous = self._load_installed_request()
            self._capture_installed_source_identities(previous)
            self._recreate_service()
            self._verify_identity_and_health(previous.image)
            if self._best_effort_journal("rolledBack", source_id, display_name):
                self._remove_transaction_root()
        except Exception:
            self._best_effort_journal(
                "failed",
                source_id,
                display_name,
                reason_code="recovery_failed",
            )
            raise SourceManagementFailure("recovery_failed") from None

    def doctor_state(self) -> str:
        """Return one fixed, non-mutating source-transaction diagnostic token."""
        self._validate_installer_state()
        journal = self._read_journal()
        backup_exists = self._transaction_root.exists() or self._transaction_root.is_symlink()
        if journal is None:
            if backup_exists:
                raise SourceManagementFailure("recovery_failed")
            return "clear"
        if backup_exists:
            _safe_status(
                self._transaction_root,
                kind="directory",
                mode=0o700,
                owner=self._owner,
            )
            return "recovery-required"
        if journal["phase"] in {
            "publishing",
            "published",
            "restarting",
            "healthChecking",
        } or (
            journal["phase"] == "failed"
            and journal["reasonCode"] == "recovery_failed"
        ):
            return "recovery-unavailable"
        return "journal"

    def _recreate_service(self) -> None:
        self._run(
            [
                "docker",
                "compose",
                "--project-directory",
                str(self.root),
                "up",
                "-d",
                "reachcommander",
            ],
            90,
        )

    def _verify_identity_and_health(self, image: str) -> None:
        expected = self._run(
            ["docker", "image", "inspect", "--format", "{{.Id}}", image], 15
        ).strip()
        running = self._run(
            ["docker", "inspect", "--format", "{{.Image}}", "reachcommander"], 15
        ).strip()
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", expected) or running != expected:
            raise SourceManagementFailure()
        for attempt in range(61):
            health = self._run(
                [
                    "docker",
                    "inspect",
                    "--format",
                    "{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}",
                    "reachcommander",
                ],
                15,
            ).strip()
            if health == "healthy":
                return
            if health not in {"starting", "created", "restarting"}:
                raise SourceManagementFailure()
            if attempt < 60:
                self._sleep(1)
        raise SourceManagementFailure()

    def _interrupt(self, phase: str) -> None:
        if self._interrupt_after == phase:
            raise SimulatedInterruption()


def _installed_root() -> Path:
    return Path(__file__).resolve().parent.parent


def main(arguments: list[str] | None = None) -> int:
    arguments = [] if arguments is None else arguments
    try:
        if arguments == ["--doctor-state"]:
            state = SourceTransaction(_installed_root()).doctor_state()
            os.write(1, state.encode("ascii") + b"\n")
            return 0
        if arguments:
            raise SourceManagementFailure("invalid_request")
        raw = sys.stdin.buffer.read(MAX_SOURCE_MANAGEMENT_MESSAGE_BYTES + 1)
        result = SourceTransaction(_installed_root()).add(raw)
    except SourceManagementFailure as error:
        os.write(
            2,
            json.dumps({"code": error.code, "detail": str(error)}).encode("utf-8")
            + b"\n",
        )
        return 2 if error.code in {"invalid_request", "validation_failed"} else 1
    except Exception:
        error = SourceManagementFailure()
        os.write(
            2,
            json.dumps({"code": error.code, "detail": str(error)}).encode("utf-8")
            + b"\n",
        )
        return 1
    os.write(1, json.dumps(result, separators=(",", ":")).encode("utf-8") + b"\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
