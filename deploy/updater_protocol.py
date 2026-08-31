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
DETAILED_PROTOCOL_VERSION = 2
PROTOCOL_VERSION = 3
DIAGNOSTIC_PROTOCOL_VERSION = 4
# Source management deliberately uses a separate protocol so an older updater
# helper cannot mistake a browser-controlled source request for an update action.
SOURCE_MANAGEMENT_PROTOCOL_VERSION = 5
SUPPORTED_PROTOCOL_VERSIONS = frozenset(
    {
        LEGACY_PROTOCOL_VERSION,
        DETAILED_PROTOCOL_VERSION,
        PROTOCOL_VERSION,
        DIAGNOSTIC_PROTOCOL_VERSION,
    }
)
MAX_MESSAGE_BYTES = 65_536
MAX_SOURCE_MANAGEMENT_MESSAGE_BYTES = 4_096
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
_UPDATE_ACTIONS = frozenset({"check", "applyConfiguredChannel"})
_DIAGNOSTIC_ACTION = "collectDiagnostics"
_MAX_STATE_BYTES = 1_024
_MAX_DETAIL_CHARS = 240
_MAX_SOURCE_DISPLAY_NAME_CHARS = 80
_MAX_SOURCE_HOST_PATH_CHARS = 1_024
_SOURCE_REQUEST_FIELDS = frozenset({"protocolVersion", "requestId", "action"})
_SOURCE_ADD_FIELDS = _SOURCE_REQUEST_FIELDS | frozenset(
    {"displayName", "hostPath", "access"}
)
_SOURCE_OPERATION_FIELDS = _SOURCE_REQUEST_FIELDS | frozenset({"operationId"})
_SOURCE_RESPONSE_FIELDS = frozenset(
    {"protocolVersion", "requestId", "action", "payload"}
)
_SOURCE_CAPABILITY_FIELDS = frozenset({"supported", "reasonCode", "detail"})
_SOURCE_OPERATION_RESPONSE_FIELDS = frozenset(
    {
        "operationId",
        "sourceId",
        "displayName",
        "phase",
        "reasonCode",
        "detail",
        "createdAt",
        "updatedAt",
    }
)
_SOURCE_STATUS_ACTION = "status"
_SOURCE_ADD_ACTION = "addSource"
_SOURCE_OPERATION_ACTION = "getOperation"
_SOURCE_ACTIONS = frozenset(
    {_SOURCE_STATUS_ACTION, _SOURCE_ADD_ACTION, _SOURCE_OPERATION_ACTION}
)
_SOURCE_ACCESS_VALUES = frozenset({"readOnly", "readWrite"})
_SOURCE_OPERATION_PHASES = frozenset(
    {
        "accepted",
        "validating",
        "applying",
        "restarting",
        "healthChecking",
        "completed",
        "rolledBack",
        "failed",
    }
)
_PUBLIC_SOURCE_ERROR_CODES = frozenset(
    {
        "invalid_request",
        "request_too_large",
        "protocol_incompatible",
        "invalid_action",
        "unsupported",
        "busy",
        "validation_failed",
        "untrusted_source_ancestry",
        "source_management_failed",
    }
)
_PUBLIC_SOURCE_CAPABILITY_DETAILS = {
    "supported": "Source management is available.",
    "installer_upgrade_required": "Source management requires the latest installer.",
    "unsupported_deployment": "Source management is unavailable on this installation.",
    "unsupported_platform": "Source management is unavailable on this platform.",
}
_PUBLIC_SOURCE_ERROR_DETAILS = {
    "invalid_request": "The source-management request is invalid.",
    "request_too_large": "The source-management request is too large.",
    "protocol_incompatible": "The source-management host protocol is incompatible.",
    "invalid_action": "The source-management action is not supported.",
    "unsupported": "Source management is unavailable on this installation.",
    "busy": "Another source-management operation is in progress.",
    "validation_failed": "The source folder could not be accepted.",
    "untrusted_source_ancestry": (
        "The source folder's parent directories must be root-owned and not "
        "group- or world-writable."
    ),
    "source_management_failed": "The source-management operation could not be completed.",
}
_PUBLIC_SOURCE_OPERATION_DETAILS = {
    "accepted": "Source change accepted.",
    "validating": "The source change is being validated.",
    "applying": "The source configuration is being applied.",
    "restarting": "ReachCommander is restarting.",
    "healthChecking": "ReachCommander is being checked.",
    "completed": "The source has been added.",
    "rolledBack": "The source change was rolled back.",
    "failed": "The source-management operation could not be completed.",
}
_PUBLIC_SOURCE_FAILED_OPERATION_DETAILS = {
    "validation_failed": "The source-management operation could not be completed.",
    "untrusted_source_ancestry": (
        "The source folder's parent directories must be root-owned and not "
        "group- or world-writable."
    ),
    "source_management_failed": "The source-management operation could not be completed.",
}
_SOURCE_ID = re.compile(r"^[a-z][a-z0-9-]{0,62}$")
_SOURCE_CAPABILITY_REASON_CODES = frozenset(_PUBLIC_SOURCE_CAPABILITY_DETAILS)
_SOURCE_OPERATION_REASON_CODES = {
    "accepted": frozenset({"accepted"}),
    "validating": frozenset({"in_progress"}),
    "applying": frozenset({"in_progress"}),
    "restarting": frozenset({"in_progress"}),
    "healthChecking": frozenset({"in_progress"}),
    "completed": frozenset({"completed"}),
    "rolledBack": frozenset({"rolled_back"}),
    "failed": frozenset(
        {"validation_failed", "untrusted_source_ancestry", "source_management_failed"}
    ),
}
_SOURCE_ERROR_RESPONSE_FIELDS = frozenset(
    {"requestAction", "operationId", "code", "detail"}
)
_RFC3339_UTC = re.compile(
    r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}"
    r"(?:\.[0-9]{1,6})?Z$"
)


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


def _reject_duplicate_source_management_keys(
    pairs: Sequence[tuple[str, object]],
) -> dict[str, object]:
    value: dict[str, object] = {}
    for key, item in pairs:
        if key in value:
            raise ProtocolError(
                "invalid_request",
                "The source-management message contains duplicate fields.",
            )
        value[key] = item
    return value


def _parse_source_management_json(raw: bytes, *, message: str) -> object:
    if not isinstance(raw, bytes):
        raise ProtocolError("invalid_request", f"The source-management {message} must be bytes.")
    if len(raw) > MAX_SOURCE_MANAGEMENT_MESSAGE_BYTES:
        raise ProtocolError(
            "request_too_large", f"The source-management {message} is too large."
        )
    try:
        return json.loads(raw, object_pairs_hook=_reject_duplicate_source_management_keys)
    except ProtocolError:
        raise
    except (json.JSONDecodeError, UnicodeDecodeError, TypeError) as error:
        raise ProtocolError(
            "invalid_request", f"The source-management {message} is not valid JSON."
        ) from error


def _canonical_uuid(value: object, *, subject: str) -> str:
    if not isinstance(value, str):
        raise ProtocolError("invalid_request", f"The {subject} identifier is invalid.")
    try:
        parsed = str(uuid.UUID(value))
    except (ValueError, AttributeError) as error:
        raise ProtocolError("invalid_request", f"The {subject} identifier is invalid.") from error
    if value != parsed:
        raise ProtocolError("invalid_request", f"The {subject} identifier is invalid.")
    return parsed


def _has_control_characters(value: str) -> bool:
    return any(not character.isprintable() for character in value)


def _bounded_public_detail(value: object) -> str:
    if not isinstance(value, str):
        raise ProtocolError("invalid_request", "The source-management detail is invalid.")
    clean = "".join(" " if _has_control_characters(character) else character for character in value)
    return " ".join(clean.split())[:_MAX_DETAIL_CHARS]


def _parse_public_timestamp(value: object) -> dt.datetime:
    if not isinstance(value, str) or not _RFC3339_UTC.fullmatch(value):
        raise ProtocolError("invalid_request", "The source-management timestamp is invalid.")
    try:
        return dt.datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as error:
        raise ProtocolError(
            "invalid_request", "The source-management timestamp is invalid."
        ) from error


def _public_timestamp(value: object) -> str:
    _parse_public_timestamp(value)
    assert isinstance(value, str)
    return value


def _capability_reason_code(supported: bool, value: object) -> str:
    if not isinstance(value, str) or value not in _SOURCE_CAPABILITY_REASON_CODES:
        raise ProtocolError("invalid_request", "The source-management reason code is invalid.")
    if supported != (value == "supported"):
        raise ProtocolError("invalid_request", "The source-management support state is invalid.")
    return value


def _operation_reason_code(phase: str, value: object) -> str:
    if not isinstance(value, str) or value not in _SOURCE_OPERATION_REASON_CODES[phase]:
        raise ProtocolError("invalid_request", "The source-management reason code is invalid.")
    return value


def _validate_response_correlation(
    *,
    request_id: str | None,
    action: str,
    operation_id: str | None,
    expected_request_id: str | None,
    expected_action: str | None,
    expected_operation_id: str | None,
) -> None:
    if expected_request_id is not None:
        if request_id != _canonical_uuid(
            expected_request_id, subject="source-management request"
        ):
            raise ProtocolError(
                "invalid_request", "The source-management response identifier does not match."
            )
    if expected_action is not None:
        if not isinstance(expected_action, str) or expected_action not in _SOURCE_ACTIONS:
            raise ProtocolError("invalid_request", "The expected source-management action is invalid.")
        if action != expected_action:
            raise ProtocolError("invalid_request", "The source-management response action does not match.")
    if expected_operation_id is not None:
        expected = _canonical_uuid(expected_operation_id, subject="source operation")
        if operation_id != expected:
            raise ProtocolError("invalid_request", "The source-management response operation does not match.")


def _public_capability_detail(supported: bool, reason_code: str) -> str:
    return _PUBLIC_SOURCE_CAPABILITY_DETAILS.get(
        reason_code,
        "Source management is available."
        if supported
        else "Source management is unavailable on this installation.",
    )


def _public_operation_detail(phase: str, reason_code: str) -> str:
    if phase == "failed":
        return _PUBLIC_SOURCE_FAILED_OPERATION_DETAILS[reason_code]
    return _PUBLIC_SOURCE_OPERATION_DETAILS[phase]


@dataclasses.dataclass(frozen=True, slots=True)
class SourceManagementRequest:
    """A strict, non-updater request accepted by the source host service."""

    protocol_version: int
    request_id: str
    action: str
    display_name: str | None = None
    host_path: str | None = None
    access: str | None = None
    operation_id: str | None = None

    @classmethod
    def parse(cls, raw: bytes) -> "SourceManagementRequest":
        value = _parse_source_management_json(raw, message="request")
        if not isinstance(value, dict):
            raise ProtocolError("invalid_request", "The source-management request must be an object.")
        if value.get("protocolVersion") != SOURCE_MANAGEMENT_PROTOCOL_VERSION:
            raise ProtocolError(
                "protocol_incompatible",
                "The source-management host protocol is incompatible.",
            )
        request_id = _canonical_uuid(value.get("requestId"), subject="source-management request")
        action = value.get("action")
        if not isinstance(action, str) or action not in _SOURCE_ACTIONS:
            raise ProtocolError("invalid_action", "The source-management action is not supported.")

        if action == _SOURCE_STATUS_ACTION:
            if set(value) != _SOURCE_REQUEST_FIELDS:
                raise ProtocolError("invalid_request", "The source-management request contains unexpected fields.")
            return cls(SOURCE_MANAGEMENT_PROTOCOL_VERSION, request_id, action)

        if action == _SOURCE_OPERATION_ACTION:
            if set(value) != _SOURCE_OPERATION_FIELDS:
                raise ProtocolError("invalid_request", "The source-management request contains unexpected fields.")
            operation_id = _canonical_uuid(value.get("operationId"), subject="source operation")
            return cls(
                SOURCE_MANAGEMENT_PROTOCOL_VERSION,
                request_id,
                action,
                operation_id=operation_id,
            )

        if set(value) != _SOURCE_ADD_FIELDS:
            raise ProtocolError("invalid_request", "The source-management request contains unexpected fields.")
        display_name = value.get("displayName")
        if not isinstance(display_name, str):
            raise ProtocolError("invalid_request", "The source display name is invalid.")
        display_name = display_name.strip()
        if (
            not display_name
            or len(display_name) > _MAX_SOURCE_DISPLAY_NAME_CHARS
            or _has_control_characters(display_name)
        ):
            raise ProtocolError("invalid_request", "The source display name is invalid.")

        host_path = value.get("hostPath")
        if (
            not isinstance(host_path, str)
            or not host_path
            or len(host_path) > _MAX_SOURCE_HOST_PATH_CHARS
            or not host_path.startswith("/")
            or "\\" in host_path
            or _has_control_characters(host_path)
        ):
            raise ProtocolError("invalid_request", "The source host path is invalid.")
        access = value.get("access")
        if not isinstance(access, str) or access not in _SOURCE_ACCESS_VALUES:
            raise ProtocolError("invalid_request", "The source access policy is invalid.")
        return cls(
            SOURCE_MANAGEMENT_PROTOCOL_VERSION,
            request_id,
            action,
            display_name=display_name,
            host_path=host_path,
            access=access,
        )


@dataclasses.dataclass(frozen=True, slots=True)
class SourceManagementCapability:
    supported: bool
    reason_code: str
    detail: str

    def __post_init__(self) -> None:
        if not isinstance(self.supported, bool):
            raise ProtocolError("invalid_request", "The source-management support state is invalid.")
        object.__setattr__(
            self,
            "reason_code",
            _capability_reason_code(self.supported, self.reason_code),
        )
        if not isinstance(self.detail, str):
            raise ProtocolError("invalid_request", "The source-management detail is invalid.")
        object.__setattr__(
            self,
            "detail",
            _public_capability_detail(self.supported, self.reason_code),
        )

    def to_wire(self) -> dict[str, object]:
        return {
            "supported": self.supported,
            "reasonCode": self.reason_code,
            "detail": self.detail,
        }


@dataclasses.dataclass(frozen=True, slots=True)
class SourceManagementOperation:
    operation_id: str
    source_id: str | None
    display_name: str | None
    phase: str
    reason_code: str
    detail: str
    created_at: str
    updated_at: str

    def __post_init__(self) -> None:
        object.__setattr__(self, "operation_id", _canonical_uuid(self.operation_id, subject="source operation"))
        if (self.source_id is None) != (self.display_name is None):
            raise ProtocolError("invalid_request", "The source operation identity is invalid.")
        if self.source_id is not None and (
            not isinstance(self.source_id, str) or not _SOURCE_ID.fullmatch(self.source_id)
        ):
            raise ProtocolError("invalid_request", "The source identifier is invalid.")
        if self.display_name is not None:
            if (
                not isinstance(self.display_name, str)
                or not self.display_name
                or len(self.display_name) > _MAX_SOURCE_DISPLAY_NAME_CHARS
                or _has_control_characters(self.display_name)
            ):
                raise ProtocolError("invalid_request", "The source display name is invalid.")
        if not isinstance(self.phase, str) or self.phase not in _SOURCE_OPERATION_PHASES:
            raise ProtocolError("invalid_request", "The source operation phase is invalid.")
        if self.phase == "completed" and self.source_id is None:
            raise ProtocolError("invalid_request", "The completed source operation identity is invalid.")
        object.__setattr__(
            self,
            "reason_code",
            _operation_reason_code(self.phase, self.reason_code),
        )
        if not isinstance(self.detail, str):
            raise ProtocolError("invalid_request", "The source-management detail is invalid.")
        object.__setattr__(
            self,
            "detail",
            _public_operation_detail(self.phase, self.reason_code),
        )
        object.__setattr__(self, "created_at", _public_timestamp(self.created_at))
        object.__setattr__(self, "updated_at", _public_timestamp(self.updated_at))
        if _parse_public_timestamp(self.created_at) > _parse_public_timestamp(
            self.updated_at
        ):
            raise ProtocolError("invalid_request", "The source operation timestamps are invalid.")

    def to_wire(self) -> dict[str, object]:
        return {
            "operationId": self.operation_id,
            "sourceId": self.source_id,
            "displayName": self.display_name,
            "phase": self.phase,
            "reasonCode": self.reason_code,
            "detail": self.detail,
            "createdAt": self.created_at,
            "updatedAt": self.updated_at,
        }


@dataclasses.dataclass(frozen=True, slots=True)
class SourceManagementResponse:
    request_id: str
    action: str
    capability: SourceManagementCapability | None = None
    operation: SourceManagementOperation | None = None

    def __post_init__(self) -> None:
        object.__setattr__(self, "request_id", _canonical_uuid(self.request_id, subject="source-management request"))
        if (
            self.action == _SOURCE_STATUS_ACTION
            and isinstance(self.capability, SourceManagementCapability)
            and self.operation is None
        ):
            return
        if (
            self.action in {_SOURCE_ADD_ACTION, _SOURCE_OPERATION_ACTION}
            and isinstance(self.operation, SourceManagementOperation)
            and self.capability is None
        ):
            return
        raise ProtocolError("invalid_request", "The source-management response is invalid.")

    @classmethod
    def from_capability(
        cls, request_id: str, capability: SourceManagementCapability
    ) -> "SourceManagementResponse":
        return cls(request_id, _SOURCE_STATUS_ACTION, capability=capability)

    @classmethod
    def from_operation(
        cls, request_id: str, action: str, operation: SourceManagementOperation
    ) -> "SourceManagementResponse":
        return cls(request_id, action, operation=operation)

    def to_wire(self) -> dict[str, object]:
        if self.capability is not None:
            payload = self.capability.to_wire()
        else:
            assert self.operation is not None
            payload = self.operation.to_wire()
        return {
            "protocolVersion": SOURCE_MANAGEMENT_PROTOCOL_VERSION,
            "requestId": self.request_id,
            "action": self.action,
            "payload": payload,
        }

    @classmethod
    def parse(
        cls,
        raw: bytes,
        *,
        expected_request_id: str | None = None,
        expected_action: str | None = None,
        expected_operation_id: str | None = None,
    ) -> "SourceManagementResponse | SourceManagementErrorResponse":
        value = _parse_source_management_json(raw, message="response")
        if not isinstance(value, dict) or set(value) != _SOURCE_RESPONSE_FIELDS:
            raise ProtocolError("invalid_request", "The source-management response contains unexpected fields.")
        if value.get("protocolVersion") != SOURCE_MANAGEMENT_PROTOCOL_VERSION:
            raise ProtocolError("protocol_incompatible", "The source-management host protocol is incompatible.")
        if value.get("action") == "error":
            return SourceManagementErrorResponse._from_wire_value(
                value,
                expected_request_id=expected_request_id,
                expected_action=expected_action,
                expected_operation_id=expected_operation_id,
            )
        request_id = _canonical_uuid(value.get("requestId"), subject="source-management request")
        action = value.get("action")
        payload = value.get("payload")
        if not isinstance(payload, dict):
            raise ProtocolError("invalid_request", "The source-management response payload is invalid.")
        if action == _SOURCE_STATUS_ACTION:
            if set(payload) != _SOURCE_CAPABILITY_FIELDS:
                raise ProtocolError("invalid_request", "The source-management response payload is invalid.")
            response = cls.from_capability(
                request_id,
                SourceManagementCapability(
                    payload.get("supported"), payload.get("reasonCode"), payload.get("detail")
                ),
            )
            if payload.get("detail") != response.capability.detail:
                raise ProtocolError("invalid_request", "The source-management response payload is invalid.")
            _validate_response_correlation(
                request_id=request_id,
                action=action,
                operation_id=None,
                expected_request_id=expected_request_id,
                expected_action=expected_action,
                expected_operation_id=expected_operation_id,
            )
            return response
        if action not in {_SOURCE_ADD_ACTION, _SOURCE_OPERATION_ACTION} or set(payload) != _SOURCE_OPERATION_RESPONSE_FIELDS:
            raise ProtocolError("invalid_request", "The source-management response payload is invalid.")
        response = cls.from_operation(
            request_id,
            action,
            SourceManagementOperation(
                operation_id=payload.get("operationId"),
                source_id=payload.get("sourceId"),
                display_name=payload.get("displayName"),
                phase=payload.get("phase"),
                reason_code=payload.get("reasonCode"),
                detail=payload.get("detail"),
                created_at=payload.get("createdAt"),
                updated_at=payload.get("updatedAt"),
            ),
        )
        assert response.operation is not None
        if payload.get("detail") != response.operation.detail:
            raise ProtocolError("invalid_request", "The source-management response payload is invalid.")
        _validate_response_correlation(
            request_id=request_id,
            action=action,
            operation_id=response.operation.operation_id,
            expected_request_id=expected_request_id,
            expected_action=expected_action,
            expected_operation_id=expected_operation_id,
        )
        return response


@dataclasses.dataclass(frozen=True, slots=True)
class SourceManagementErrorResponse:
    request_id: str | None
    code: str
    detail: str
    request_action: str = _SOURCE_STATUS_ACTION
    operation_id: str | None = None

    def __post_init__(self) -> None:
        if self.request_id is not None:
            object.__setattr__(
                self,
                "request_id",
                _canonical_uuid(self.request_id, subject="source-management request"),
            )
        if not isinstance(self.request_action, str) or self.request_action not in _SOURCE_ACTIONS:
            raise ProtocolError("invalid_request", "The source-management error action is invalid.")
        if self.operation_id is not None:
            object.__setattr__(
                self,
                "operation_id",
                _canonical_uuid(self.operation_id, subject="source operation"),
            )
        if (self.request_action == _SOURCE_OPERATION_ACTION) != (
            self.operation_id is not None
        ):
            raise ProtocolError("invalid_request", "The source-management error operation is invalid.")
        code = (
            self.code
            if isinstance(self.code, str) and self.code in _PUBLIC_SOURCE_ERROR_CODES
            else "source_management_failed"
        )
        if not isinstance(self.detail, str):
            raise ProtocolError("invalid_request", "The source-management detail is invalid.")
        object.__setattr__(self, "code", code)
        object.__setattr__(self, "detail", _PUBLIC_SOURCE_ERROR_DETAILS[code])

    @classmethod
    def from_error(
        cls,
        request_id: str | None,
        error: ProtocolError,
        *,
        request_action: str = _SOURCE_STATUS_ACTION,
        operation_id: str | None = None,
    ) -> "SourceManagementErrorResponse":
        code = error.code if error.code in _PUBLIC_SOURCE_ERROR_CODES else "source_management_failed"
        return cls(
            request_id,
            code,
            error.detail,
            request_action=request_action,
            operation_id=operation_id,
        )

    def to_wire(self) -> dict[str, object]:
        request_id = (
            _canonical_uuid(self.request_id, subject="source-management request")
            if self.request_id is not None
            else None
        )
        code = self.code if self.code in _PUBLIC_SOURCE_ERROR_CODES else "source_management_failed"
        return {
            "protocolVersion": SOURCE_MANAGEMENT_PROTOCOL_VERSION,
            "requestId": request_id,
            "action": "error",
            "payload": {
                "requestAction": self.request_action,
                "operationId": self.operation_id,
                "code": code,
                "detail": _bounded_public_detail(self.detail),
            },
        }

    @classmethod
    def parse(
        cls,
        raw: bytes,
        *,
        expected_request_id: str | None = None,
        expected_action: str | None = None,
        expected_operation_id: str | None = None,
    ) -> "SourceManagementErrorResponse":
        value = _parse_source_management_json(raw, message="response")
        if not isinstance(value, dict) or set(value) != _SOURCE_RESPONSE_FIELDS:
            raise ProtocolError("invalid_request", "The source-management response contains unexpected fields.")
        if value.get("protocolVersion") != SOURCE_MANAGEMENT_PROTOCOL_VERSION:
            raise ProtocolError("protocol_incompatible", "The source-management host protocol is incompatible.")
        return cls._from_wire_value(
            value,
            expected_request_id=expected_request_id,
            expected_action=expected_action,
            expected_operation_id=expected_operation_id,
        )

    @classmethod
    def _from_wire_value(
        cls,
        value: Mapping[str, object],
        *,
        expected_request_id: str | None,
        expected_action: str | None,
        expected_operation_id: str | None,
    ) -> "SourceManagementErrorResponse":
        if value.get("action") != "error":
            raise ProtocolError("invalid_request", "The source-management response is not an error.")
        request_id_value = value.get("requestId")
        request_id = (
            _canonical_uuid(request_id_value, subject="source-management request")
            if request_id_value is not None
            else None
        )
        payload = value.get("payload")
        if not isinstance(payload, dict) or set(payload) != _SOURCE_ERROR_RESPONSE_FIELDS:
            raise ProtocolError("invalid_request", "The source-management error payload is invalid.")
        if (
            not isinstance(payload.get("code"), str)
            or payload.get("code") not in _PUBLIC_SOURCE_ERROR_CODES
        ):
            raise ProtocolError("invalid_request", "The source-management error payload is invalid.")
        error = cls(
            request_id,
            payload.get("code"),
            payload.get("detail"),
            request_action=payload.get("requestAction"),
            operation_id=payload.get("operationId"),
        )
        if payload.get("detail") != error.detail:
            raise ProtocolError("invalid_request", "The source-management error payload is invalid.")
        _validate_response_correlation(
            request_id=error.request_id,
            action=error.request_action,
            operation_id=error.operation_id,
            expected_request_id=expected_request_id,
            expected_action=expected_action,
            expected_operation_id=expected_operation_id,
        )
        return error


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
        valid_action = isinstance(action, str) and (
            (
                protocol_version == DIAGNOSTIC_PROTOCOL_VERSION
                and action == _DIAGNOSTIC_ACTION
            )
            or (
                protocol_version != DIAGNOSTIC_PROTOCOL_VERSION
                and action in _UPDATE_ACTIONS
            )
        )
        if not valid_action:
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
    "DIAGNOSTIC_PROTOCOL_VERSION",
    "DETAILED_PROTOCOL_VERSION",
    "LEGACY_PROTOCOL_VERSION",
    "MAX_MESSAGE_BYTES",
    "MAX_SOURCE_MANAGEMENT_MESSAGE_BYTES",
    "PROTOCOL_VERSION",
    "SOURCE_MANAGEMENT_PROTOCOL_VERSION",
    "SUPPORTED_PROTOCOL_VERSIONS",
    "STABLE_TAG",
    "TRUSTED_IMAGE_REPOSITORY",
    "GitHubRelease",
    "InstalledState",
    "ProtocolError",
    "ResolvedImage",
    "SourceManagementCapability",
    "SourceManagementErrorResponse",
    "SourceManagementOperation",
    "SourceManagementRequest",
    "SourceManagementResponse",
    "StateError",
    "UpdateDiscovery",
    "UpdateSnapshot",
    "UpdaterRequest",
]
