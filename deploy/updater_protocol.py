#!/usr/bin/env python3
"""Bounded protocol and trusted discovery rules for the Ubuntu updater.

This module intentionally contains no HTTP-controller or browser-controlled update
target.  The host service supplies the trusted state and network/image boundaries.
"""

from __future__ import annotations

import dataclasses
import datetime as dt
import json
import os
import re
import stat
import uuid
from pathlib import Path
from typing import Callable, Mapping, Sequence


LEGACY_PROTOCOL_VERSION = 1
PROTOCOL_VERSION = 2
SUPPORTED_PROTOCOL_VERSIONS = frozenset(
    {LEGACY_PROTOCOL_VERSION, PROTOCOL_VERSION}
)
MAX_MESSAGE_BYTES = 65_536
TRUSTED_IMAGE_REPOSITORY = "ghcr.io/dragosniamtu/reach-commander"

STABLE_TAG = re.compile(r"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
PINNED_TAG = re.compile(
    r"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-(?:0|[1-9][0-9]*|[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9][0-9]*|[A-Za-z-][0-9A-Za-z-]*))*)?$"
)
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
REVISION = re.compile(r"^[0-9a-f]{40}$")
EDGE_VERSION = re.compile(r"^edge@[0-9a-f]{12}$")

_REQUEST_FIELDS = frozenset({"protocolVersion", "requestId", "action"})
_ACTIONS = frozenset({"check", "applyConfiguredChannel"})
_MAX_STATE_BYTES = 1_024
_MAX_DETAIL_CHARS = 240


class ProtocolError(ValueError):
    """A stable protocol failure safe to map to a bounded response."""

    def __init__(self, code: str, detail: str) -> None:
        super().__init__(detail)
        self.code = code
        self.detail = detail


class StateError(ValueError):
    """A protected installer-state validation failure."""

    def __init__(self, code: str, detail: str) -> None:
        super().__init__(detail)
        self.code = code
        self.detail = detail


def _reject_duplicate_keys(pairs: Sequence[tuple[str, object]]) -> dict[str, object]:
    value: dict[str, object] = {}
    for key, item in pairs:
        if key in value:
            raise ProtocolError(
                "invalid_request", "The updater request contains duplicate fields."
            )
        value[key] = item
    return value


@dataclasses.dataclass(frozen=True, slots=True)
class UpdaterRequest:
    protocol_version: int
    request_id: str
    action: str

    @classmethod
    def parse(cls, raw: bytes) -> "UpdaterRequest":
        if not isinstance(raw, bytes):
            raise ProtocolError("invalid_request", "The updater request must be bytes.")
        if len(raw) > MAX_MESSAGE_BYTES:
            raise ProtocolError(
                "request_too_large", "The updater request is too large."
            )
        try:
            value = json.loads(raw, object_pairs_hook=_reject_duplicate_keys)
        except ProtocolError:
            raise
        except (json.JSONDecodeError, UnicodeDecodeError, TypeError) as error:
            raise ProtocolError(
                "invalid_request", "The updater request is not valid JSON."
            ) from error

        if not isinstance(value, dict) or set(value) != _REQUEST_FIELDS:
            raise ProtocolError(
                "invalid_request",
                "The updater request contains unexpected fields.",
            )

        protocol_version = value["protocolVersion"]
        if (
            not isinstance(protocol_version, int)
            or isinstance(protocol_version, bool)
            or protocol_version not in SUPPORTED_PROTOCOL_VERSIONS
        ):
            raise ProtocolError(
                "protocol_incompatible",
                "The host updater protocol is incompatible.",
            )

        request_id_value = value["requestId"]
        if not isinstance(request_id_value, str):
            raise ProtocolError(
                "invalid_request", "The updater request identifier is invalid."
            )
        try:
            request_id = str(uuid.UUID(request_id_value))
        except (ValueError, AttributeError) as error:
            raise ProtocolError(
                "invalid_request", "The updater request identifier is invalid."
            ) from error

        action = value["action"]
        if not isinstance(action, str) or action not in _ACTIONS:
            raise ProtocolError(
                "invalid_action", "The updater action is not supported."
            )
        return cls(protocol_version, request_id, action)


@dataclasses.dataclass(frozen=True, slots=True)
class GitHubRelease:
    tag_name: str
    draft: bool = False
    prerelease: bool = False


@dataclasses.dataclass(frozen=True, slots=True)
class ResolvedImage:
    reference: str
    digest: str
    version: str
    revision: str


@dataclasses.dataclass(frozen=True, slots=True)
class InstalledState:
    channel: str
    current_digest: str
    current_version: str | None = None

    @classmethod
    def load(cls, root: Path | str) -> "InstalledState":
        state_root = Path(root)
        try:
            root_status = state_root.lstat()
        except OSError as error:
            raise StateError(
                "invalid_state", "The updater state directory is unavailable."
            ) from error
        if state_root.is_symlink() or not stat.S_ISDIR(root_status.st_mode):
            raise StateError(
                "invalid_state", "The updater state directory is not protected."
            )

        channel = _read_state_line(state_root / "channel", required=True)
        current_image = _read_state_line(
            state_root / "current-image", required=True
        )
        current_version = _read_state_line(
            state_root / "current-version", required=False
        )
        assert channel is not None
        assert current_image is not None

        if not _valid_channel(channel):
            raise StateError(
                "invalid_state", "The configured update channel is invalid."
            )

        image_prefix = f"{TRUSTED_IMAGE_REPOSITORY}@"
        if not current_image.startswith(image_prefix):
            raise StateError(
                "invalid_state", "The installed image state is not trusted."
            )
        current_digest = current_image[len(image_prefix) :]
        if not DIGEST.fullmatch(current_digest):
            raise StateError(
                "invalid_state", "The installed image digest is invalid."
            )

        if current_version is not None and not _valid_display_version(current_version):
            raise StateError(
                "invalid_state", "The installed display version is invalid."
            )
        return cls(channel, current_digest, current_version)


def _read_state_line(path: Path, *, required: bool) -> str | None:
    try:
        status = path.lstat()
    except FileNotFoundError as error:
        if not required:
            return None
        raise StateError(
            "invalid_state", "A required updater state file is missing."
        ) from error
    except OSError as error:
        raise StateError(
            "invalid_state", "An updater state file is unavailable."
        ) from error

    if path.is_symlink() or not stat.S_ISREG(status.st_mode):
        raise StateError(
            "invalid_state", "Updater state must be a regular protected file."
        )

    flags = os.O_RDONLY
    flags |= getattr(os, "O_CLOEXEC", 0)
    flags |= getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags)
        try:
            opened_status = os.fstat(descriptor)
            if not stat.S_ISREG(opened_status.st_mode):
                raise StateError(
                    "invalid_state", "Updater state must be a regular protected file."
                )
            raw = os.read(descriptor, _MAX_STATE_BYTES + 1)
        finally:
            os.close(descriptor)
    except StateError:
        raise
    except OSError as error:
        raise StateError(
            "invalid_state", "An updater state file cannot be read safely."
        ) from error

    if len(raw) > _MAX_STATE_BYTES:
        raise StateError("invalid_state", "An updater state value is too large.")
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as error:
        raise StateError(
            "invalid_state", "An updater state value is not valid UTF-8."
        ) from error
    lines = text.splitlines()
    if len(lines) != 1 or not lines[0] or lines[0] != lines[0].strip():
        raise StateError(
            "invalid_state", "An updater state value must contain one line."
        )
    return lines[0]


@dataclasses.dataclass(frozen=True, slots=True)
class UpdateSnapshot:
    supported: bool
    channel: str | None
    current_version: str | None
    target_version: str | None
    current_digest: str | None
    target_digest: str | None
    phase: str
    reason_code: str
    detail: str
    target_reference: str | None = None
    operation_id: str | None = None
    last_checked_at: str | None = None
    updated_at: str | None = None
    progress_stage: str | None = None

    @property
    def update_available(self) -> bool:
        return self.phase == "available"

    @property
    def can_apply(self) -> bool:
        return self.supported and self.update_available

    def to_journal(self) -> dict[str, object]:
        return {
            "supported": self.supported,
            "channel": self.channel,
            "currentVersion": self.current_version,
            "targetVersion": self.target_version,
            "currentDigest": self.current_digest,
            "targetDigest": self.target_digest,
            "phase": self.phase,
            "reasonCode": self.reason_code,
            "detail": _bounded_detail(self.detail),
            "operationId": self.operation_id,
            "lastCheckedAt": self.last_checked_at,
            "updatedAt": self.updated_at,
            "progressStage": self.progress_stage,
        }


ReleaseProvider = Callable[[], str | GitHubRelease | Mapping[str, object]]
ImageResolver = Callable[[str], ResolvedImage]
Clock = Callable[[], dt.datetime]


class UpdateDiscovery:
    def __init__(
        self,
        state: object,
        latest_release: ReleaseProvider,
        resolve_image: ImageResolver,
        clock: Clock | None = None,
    ) -> None:
        self._state = state
        self._latest_release = latest_release
        self._resolve_image = resolve_image
        self._clock = clock or (lambda: dt.datetime.now(dt.timezone.utc))

    def check(self) -> UpdateSnapshot:
        now = _iso_utc(self._clock())
        try:
            state = self._load_state()
            self._validate_state(state)
        except (StateError, TypeError, ValueError, AttributeError):
            return self._unavailable(
                now,
                reason="invalid_state",
                detail="The trusted installer state is unavailable or invalid.",
            )

        current_version = _current_display_version(state.current_version)
        if PINNED_TAG.fullmatch(state.channel):
            return UpdateSnapshot(
                True,
                state.channel,
                current_version,
                None,
                state.current_digest,
                None,
                "unavailable",
                "version_pinned",
                "Updates are disabled while this deployment is version-pinned.",
                last_checked_at=now,
                updated_at=now,
            )

        if state.channel == "stable":
            try:
                release = _coerce_release(self._latest_release())
            except Exception:
                return self._unavailable(
                    now,
                    state,
                    "release_unavailable",
                    "The stable release could not be checked.",
                )
            if (
                release.draft
                or release.prerelease
                or not isinstance(release.tag_name, str)
                or not STABLE_TAG.fullmatch(release.tag_name)
            ):
                return self._unavailable(
                    now,
                    state,
                    "release_invalid",
                    "The stable release metadata is invalid.",
                )
            target_reference = f"{TRUSTED_IMAGE_REPOSITORY}:{release.tag_name}"
            target_version = release.tag_name
        elif state.channel == "edge":
            target_reference = f"{TRUSTED_IMAGE_REPOSITORY}:edge"
            target_version = None
        else:
            return self._unavailable(
                now,
                state,
                "invalid_state",
                "The trusted installer state is unavailable or invalid.",
            )

        try:
            resolved = self._resolve_image(target_reference)
        except Exception:
            return self._unavailable(
                now,
                state,
                "manifest_unavailable",
                "The trusted container manifest could not be checked.",
            )

        if not _valid_resolved_image(
            resolved,
            target_reference,
            stable_version=target_version,
        ):
            return self._unavailable(
                now,
                state,
                "manifest_invalid",
                "The trusted container manifest metadata is invalid.",
            )

        if state.channel == "edge":
            target_version = f"edge@{resolved.revision[:12]}"
        assert target_version is not None

        phase = "current" if resolved.digest == state.current_digest else "available"
        reason = "up_to_date" if phase == "current" else "update_available"
        detail = (
            "ReachCommander is up to date."
            if phase == "current"
            else "A trusted ReachCommander update is available."
        )
        return UpdateSnapshot(
            True,
            state.channel,
            current_version,
            target_version,
            state.current_digest,
            resolved.digest,
            phase,
            reason,
            detail,
            target_reference=target_reference,
            last_checked_at=now,
            updated_at=now,
        )

    def _load_state(self) -> InstalledState:
        if isinstance(self._state, InstalledState):
            return self._state
        loader = getattr(self._state, "load", None)
        if callable(loader):
            loaded = loader()
            if isinstance(loaded, InstalledState):
                return loaded
            return InstalledState(
                loaded.channel, loaded.current_digest, loaded.current_version
            )
        return InstalledState(
            getattr(self._state, "channel"),
            getattr(self._state, "current_digest"),
            getattr(self._state, "current_version", None),
        )

    @staticmethod
    def _validate_state(state: InstalledState) -> None:
        if not _valid_channel(state.channel):
            raise StateError("invalid_state", "The configured update channel is invalid.")
        if not isinstance(state.current_digest, str) or not DIGEST.fullmatch(
            state.current_digest
        ):
            raise StateError("invalid_state", "The installed image digest is invalid.")
        if state.current_version is not None and not _valid_display_version(
            state.current_version
        ):
            raise StateError("invalid_state", "The installed display version is invalid.")

    @staticmethod
    def _unavailable(
        now: str,
        state: InstalledState | None = None,
        reason: str = "invalid_state",
        detail: str = "System updates are unavailable.",
    ) -> UpdateSnapshot:
        return UpdateSnapshot(
            True,
            state.channel if state else None,
            _current_display_version(state.current_version) if state else None,
            None,
            state.current_digest if state else None,
            None,
            "unavailable",
            reason,
            _bounded_detail(detail),
            last_checked_at=now,
            updated_at=now,
        )


def _coerce_release(value: str | GitHubRelease | Mapping[str, object]) -> GitHubRelease:
    if isinstance(value, str):
        return GitHubRelease(value)
    if isinstance(value, GitHubRelease):
        return value
    if isinstance(value, Mapping):
        tag_name = value.get("tag_name", value.get("tagName"))
        draft = value.get("draft", False)
        prerelease = value.get("prerelease", False)
        if (
            not isinstance(tag_name, str)
            or not isinstance(draft, bool)
            or not isinstance(prerelease, bool)
        ):
            raise ValueError("invalid release")
        return GitHubRelease(tag_name, draft, prerelease)
    raise TypeError("invalid release")


def _valid_channel(value: object) -> bool:
    return isinstance(value, str) and (
        value in {"stable", "edge"} or PINNED_TAG.fullmatch(value) is not None
    )


def _valid_display_version(value: object) -> bool:
    return isinstance(value, str) and (
        value == "unknown"
        or PINNED_TAG.fullmatch(value) is not None
        or EDGE_VERSION.fullmatch(value) is not None
    )


def _current_display_version(value: str | None) -> str:
    return value if value and _valid_display_version(value) else "unknown"


def _valid_resolved_image(
    value: object,
    expected_reference: str,
    *,
    stable_version: str | None,
) -> bool:
    if not isinstance(value, ResolvedImage):
        return False
    if value.reference != expected_reference:
        return False
    if not isinstance(value.digest, str) or not DIGEST.fullmatch(value.digest):
        return False
    if not isinstance(value.revision, str) or not REVISION.fullmatch(value.revision):
        return False
    if not isinstance(value.version, str) or not value.version or len(value.version) > 128:
        return False
    if any(character in value.version for character in "\r\n\x00"):
        return False
    if stable_version is not None and value.version != stable_version:
        return False
    return True


def _bounded_detail(value: str) -> str:
    clean = " ".join(str(value).split())
    return clean[:_MAX_DETAIL_CHARS]


def _iso_utc(value: dt.datetime) -> str:
    if value.tzinfo is None:
        value = value.replace(tzinfo=dt.timezone.utc)
    return value.astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z")


__all__ = [
    "DIGEST",
    "LEGACY_PROTOCOL_VERSION",
    "MAX_MESSAGE_BYTES",
    "PROTOCOL_VERSION",
    "SUPPORTED_PROTOCOL_VERSIONS",
    "STABLE_TAG",
    "TRUSTED_IMAGE_REPOSITORY",
    "GitHubRelease",
    "InstalledState",
    "ProtocolError",
    "ResolvedImage",
    "StateError",
    "UpdateDiscovery",
    "UpdateSnapshot",
    "UpdaterRequest",
]
