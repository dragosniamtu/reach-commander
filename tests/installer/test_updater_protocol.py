from __future__ import annotations

import json
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest import mock

import deploy.updater_protocol as updater_protocol
from deploy.updater_protocol import (
    DIAGNOSTIC_PROTOCOL_VERSION,
    MAX_MESSAGE_BYTES,
    PROTOCOL_VERSION,
    SOURCE_MANAGEMENT_PROTOCOL_VERSION,
    TRUSTED_IMAGE_REPOSITORY,
    GitHubRelease,
    InstalledState,
    ProtocolError,
    ResolvedImage,
    SourceManagementCapability,
    SourceManagementErrorResponse,
    SourceManagementOperation,
    SourceManagementRequest,
    SourceManagementResponse,
    StateError,
    UpdateDiscovery,
    UpdaterRequest,
)


CURRENT_DIGEST = "sha256:" + "1" * 64
TARGET_DIGEST = "sha256:" + "2" * 64
REVISION = "a" * 40


class UpdaterProtocolTests(unittest.TestCase):
    def valid_request(
        self,
        action: str = "check",
        protocol_version: int = PROTOCOL_VERSION,
    ) -> dict[str, object]:
        return {
            "protocolVersion": protocol_version,
            "requestId": str(uuid.uuid4()),
            "action": action,
        }

    def test_protocol_constants_are_fixed_and_request_is_immutable(self) -> None:
        self.assertEqual(3, PROTOCOL_VERSION)
        self.assertEqual(4, DIAGNOSTIC_PROTOCOL_VERSION)
        self.assertEqual(1, getattr(updater_protocol, "LEGACY_PROTOCOL_VERSION", None))
        self.assertEqual(2, getattr(updater_protocol, "DETAILED_PROTOCOL_VERSION", None))
        self.assertEqual(
            frozenset({1, 2, 3, 4}),
            getattr(updater_protocol, "SUPPORTED_PROTOCOL_VERSIONS", None),
        )
        self.assertEqual(65_536, MAX_MESSAGE_BYTES)

        value = self.valid_request("applyConfiguredChannel")
        request = UpdaterRequest.parse(json.dumps(value).encode())

        self.assertEqual(PROTOCOL_VERSION, request.protocol_version)
        self.assertEqual(value["requestId"], request.request_id)
        self.assertEqual("applyConfiguredChannel", request.action)
        with self.assertRaises((AttributeError, TypeError)):
            request.action = "check"  # type: ignore[misc]

    def test_accepts_legacy_detailed_and_trace_protocols(self) -> None:
        for version in (1, 2, 3):
            with self.subTest(version=version):
                request = UpdaterRequest.parse(
                    json.dumps(self.valid_request(protocol_version=version)).encode()
                )
                self.assertEqual(version, request.protocol_version)

        diagnostics = UpdaterRequest.parse(
            json.dumps(
                self.valid_request(
                    "collectDiagnostics",
                    protocol_version=DIAGNOSTIC_PROTOCOL_VERSION,
                )
            ).encode()
        )
        self.assertEqual("collectDiagnostics", diagnostics.action)

    def test_rejects_unadvertised_protocol_versions(self) -> None:
        for version in (0, 5, True):
            with self.subTest(version=version):
                with self.assertRaisesRegex(ProtocolError, "incompatible"):
                    UpdaterRequest.parse(
                        json.dumps(self.valid_request(protocol_version=version)).encode()
                    )

    def test_diagnostics_action_is_scoped_to_protocol_v4(self) -> None:
        for action, version in (
            ("collectDiagnostics", 1),
            ("collectDiagnostics", 2),
            ("collectDiagnostics", 3),
            ("check", 4),
            ("applyConfiguredChannel", 4),
        ):
            with self.subTest(action=action, version=version):
                with self.assertRaisesRegex(ProtocolError, "not supported"):
                    UpdaterRequest.parse(
                        json.dumps(self.valid_request(action, version)).encode()
                    )

    def test_apply_rejects_browser_controlled_target_fields(self) -> None:
        for field, value in (
            ("image", "attacker.example/root:latest"),
            ("channel", "edge"),
            ("repository", "attacker/example"),
            ("url", "https://attacker.example/update"),
            ("command", "id"),
            ("path", "/etc/shadow"),
        ):
            with self.subTest(field=field):
                payload = self.valid_request("applyConfiguredChannel")
                payload[field] = value
                with self.assertRaisesRegex(ProtocolError, "unexpected fields"):
                    UpdaterRequest.parse(json.dumps(payload).encode())

    def test_rejects_oversized_malformed_duplicate_and_non_object_json(self) -> None:
        invalid = (
            b"{",
            b"[]",
            b"null",
            (
                '{"protocolVersion":1,"protocolVersion":1,'
                f'"requestId":"{uuid.uuid4()}","action":"check"}}'
            ).encode(),
        )
        for raw in invalid:
            with self.subTest(raw=raw[:30]):
                with self.assertRaises(ProtocolError):
                    UpdaterRequest.parse(raw)

        with self.assertRaisesRegex(ProtocolError, "too large"):
            UpdaterRequest.parse(b"{" + b" " * MAX_MESSAGE_BYTES)

    def test_rejects_missing_fields_wrong_types_protocol_action_and_uuid(self) -> None:
        mutations = (
            ({"requestId": str(uuid.uuid4()), "action": "check"}, "unexpected fields"),
            ({**self.valid_request(), "protocolVersion": 4}, "not supported"),
            ({**self.valid_request(), "protocolVersion": True}, "incompatible"),
            ({**self.valid_request(), "requestId": "not-a-uuid"}, "request identifier"),
            ({**self.valid_request(), "requestId": 123}, "request identifier"),
            ({**self.valid_request(), "action": "update"}, "not supported"),
            ({**self.valid_request(), "action": 1}, "not supported"),
        )
        for payload, message in mutations:
            with self.subTest(payload=payload):
                with self.assertRaisesRegex(ProtocolError, message):
                    UpdaterRequest.parse(json.dumps(payload).encode())


class SourceManagementProtocolTests(unittest.TestCase):
    def request(
        self,
        action: str = "status",
        request_id: str | None = None,
        **fields: object,
    ) -> dict[str, object]:
        return {
            "protocolVersion": SOURCE_MANAGEMENT_PROTOCOL_VERSION,
            "requestId": request_id or str(uuid.uuid4()),
            "action": action,
            **fields,
        }

    def test_source_management_is_protocol_v5_not_an_updater_protocol(self) -> None:
        self.assertEqual(5, SOURCE_MANAGEMENT_PROTOCOL_VERSION)
        source_request = self.request(
            "addSource",
            displayName="Archive",
            hostPath="/srv/reachcommander/archive",
            access="readOnly",
        )

        parsed = SourceManagementRequest.parse(json.dumps(source_request).encode())

        self.assertEqual("addSource", parsed.action)
        self.assertEqual("Archive", parsed.display_name)
        self.assertEqual("/srv/reachcommander/archive", parsed.host_path)
        self.assertEqual("readOnly", parsed.access)
        with self.assertRaises(ProtocolError):
            UpdaterRequest.parse(json.dumps(source_request).encode())

    def test_source_management_requests_use_an_exact_action_schema(self) -> None:
        status = SourceManagementRequest.parse(json.dumps(self.request()).encode())
        self.assertEqual("status", status.action)
        self.assertIsNone(status.operation_id)

        operation_id = str(uuid.uuid4())
        operation = SourceManagementRequest.parse(
            json.dumps(self.request("getOperation", operationId=operation_id)).encode()
        )
        self.assertEqual(operation_id, operation.operation_id)

        invalid = (
            self.request("addSource", displayName="Archive", hostPath="/srv/archive"),
            self.request("addSource", displayName="Archive", hostPath="/srv/archive", access="readOnly", command="id"),
            self.request("status", hostPath="/srv/archive"),
            self.request("getOperation", operationId=operation_id, access="readOnly"),
            self.request("deleteSource"),
        )
        for payload in invalid:
            with self.subTest(payload=payload):
                with self.assertRaises(ProtocolError):
                    SourceManagementRequest.parse(json.dumps(payload).encode())

    def test_source_management_rejects_duplicate_fields_and_noncanonical_uuids(self) -> None:
        request_id = str(uuid.uuid4())
        duplicate = (
            '{"protocolVersion":5,"requestId":"'
            + request_id
            + '","action":"status","action":"status"}'
        ).encode()
        with self.assertRaisesRegex(ProtocolError, "duplicate"):
            SourceManagementRequest.parse(duplicate)

        for value in (request_id.upper(), "{" + request_id + "}", "not-a-uuid"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ProtocolError, "identifier"):
                    SourceManagementRequest.parse(
                        json.dumps(self.request(request_id=value)).encode()
                    )

    def test_source_management_rejects_incompatible_versions_and_oversized_payloads(self) -> None:
        for version in (0, 4, 6, True):
            with self.subTest(version=version):
                payload = self.request()
                payload["protocolVersion"] = version
                with self.assertRaisesRegex(ProtocolError, "incompatible"):
                    SourceManagementRequest.parse(json.dumps(payload).encode())

        with self.assertRaisesRegex(ProtocolError, "too large"):
            SourceManagementRequest.parse(b"{" + b" " * 4_096)

    def test_source_management_bounds_add_source_fields_before_filesystem_validation(self) -> None:
        valid = self.request(
            "addSource",
            displayName="  Archive  ",
            hostPath="/srv/reachcommander/archive",
            access="readWrite",
        )
        parsed = SourceManagementRequest.parse(json.dumps(valid).encode())
        self.assertEqual("Archive", parsed.display_name)

        invalid = (
            self.request("addSource", displayName="", hostPath="/srv/archive", access="readOnly"),
            self.request("addSource", displayName="x" * 81, hostPath="/srv/archive", access="readOnly"),
            self.request("addSource", displayName="bad\nname", hostPath="/srv/archive", access="readOnly"),
            self.request("addSource", displayName="bad\u0085name", hostPath="/srv/archive", access="readOnly"),
            self.request("addSource", displayName="Archive", hostPath="relative/path", access="readOnly"),
            self.request("addSource", displayName="Archive", hostPath="C:\\archive", access="readOnly"),
            self.request("addSource", displayName="Archive", hostPath="/srv/\x00archive", access="readOnly"),
            self.request("addSource", displayName="Archive", hostPath="/" + "x" * 1_024, access="readOnly"),
            self.request("addSource", displayName="Archive", hostPath="/srv/archive", access="rw"),
        )
        for payload in invalid:
            with self.subTest(payload=payload):
                with self.assertRaises(ProtocolError):
                    SourceManagementRequest.parse(json.dumps(payload).encode())

    def test_source_management_responses_round_trip_exactly(self) -> None:
        request_id = str(uuid.uuid4())
        capability = SourceManagementCapability(True, "supported", "Source management is available.")
        status = SourceManagementResponse.from_capability(request_id, capability)
        self.assertEqual(
            {
                "protocolVersion": 5,
                "requestId": request_id,
                "action": "status",
                "payload": {
                    "supported": True,
                    "reasonCode": "supported",
                    "detail": "Source management is available.",
                },
            },
            status.to_wire(),
        )
        self.assertEqual(status, SourceManagementResponse.parse(json.dumps(status.to_wire()).encode()))

        operation = SourceManagementOperation(
            operation_id=str(uuid.uuid4()),
            source_id="archive",
            display_name="Archive",
            phase="accepted",
            reason_code="accepted",
            detail="Source change accepted.",
            created_at="2026-08-31T10:00:00Z",
            updated_at="2026-08-31T10:00:00Z",
        )
        response = SourceManagementResponse.from_operation(request_id, "addSource", operation)
        parsed = SourceManagementResponse.parse(json.dumps(response.to_wire()).encode())
        self.assertEqual(operation, parsed.operation)
        self.assertEqual("addSource", parsed.action)

    def test_source_management_responses_reject_request_id_mismatch_and_unsafe_public_fields(self) -> None:
        request_id = str(uuid.uuid4())
        with self.assertRaisesRegex(ProtocolError, "identifier"):
            SourceManagementResponse.parse(
                json.dumps(
                    {
                        "protocolVersion": 5,
                        "requestId": request_id.upper(),
                        "action": "status",
                        "payload": {"supported": True, "reasonCode": "supported", "detail": "ok"},
                    }
                ).encode()
            )

        with self.assertRaises(ProtocolError):
            SourceManagementResponse.parse(
                json.dumps(
                    {
                        "protocolVersion": 5,
                        "requestId": request_id,
                        "action": "status",
                        "payload": {"supported": True, "reasonCode": "supported", "detail": "ok", "hostPath": "/secret"},
                    }
                ).encode()
            )

        response = SourceManagementResponse.from_capability(
            request_id, SourceManagementCapability(True, "supported", "ok")
        )
        with self.assertRaisesRegex(ProtocolError, "does not match"):
            SourceManagementResponse.parse(
                json.dumps(response.to_wire()).encode(),
                expected_request_id=str(uuid.uuid4()),
            )

        error = SourceManagementErrorResponse.from_error(
            request_id,
            ProtocolError("invalid_request", "Rejected /srv/private because command output said nope"),
        )
        wire = error.to_wire()
        self.assertEqual("invalid_request", wire["payload"]["code"])
        self.assertNotIn("/srv/private", json.dumps(wire))
        self.assertNotIn("command output", json.dumps(wire))
        self.assertLessEqual(len(str(wire["payload"]["detail"])), 240)

        direct_error = SourceManagementErrorResponse(
            request_id, "invalid_request", "failed at /srv/private with command output"
        )
        self.assertNotIn("/srv/private", json.dumps(direct_error.to_wire()))

    def test_source_management_status_and_operation_details_never_echo_host_data(self) -> None:
        unsafe_detail = "Command output named /srv/private/source and runtime token abc"
        capability = SourceManagementCapability(
            False, "unsupported_deployment", unsafe_detail
        )
        operation = SourceManagementOperation(
            operation_id=str(uuid.uuid4()),
            source_id=None,
            display_name=None,
            phase="failed",
            reason_code="validation_failed",
            detail=unsafe_detail,
            created_at="2026-08-31T10:00:00Z",
            updated_at="2026-08-31T10:00:01Z",
        )

        wire = json.dumps(
            SourceManagementResponse.from_operation(
                str(uuid.uuid4()), "getOperation", operation
            ).to_wire()
        ) + json.dumps(SourceManagementResponse.from_capability(str(uuid.uuid4()), capability).to_wire())

        self.assertNotIn("/srv/private/source", wire)
        self.assertNotIn("Command output", wire)
        self.assertNotIn("runtime token", wire)

    def test_source_management_response_parser_discriminates_and_correlates_error_envelopes(self) -> None:
        request_id = str(uuid.uuid4())
        operation_id = str(uuid.uuid4())
        error_wire = {
            "protocolVersion": 5,
            "requestId": request_id,
            "action": "error",
            "payload": {
                "requestAction": "getOperation",
                "operationId": operation_id,
                "code": "validation_failed",
                "detail": "The source folder could not be accepted.",
            },
        }

        parsed = SourceManagementResponse.parse(
            json.dumps(error_wire).encode(),
            expected_request_id=request_id,
            expected_action="getOperation",
            expected_operation_id=operation_id,
        )
        self.assertIsInstance(parsed, SourceManagementErrorResponse)
        assert isinstance(parsed, SourceManagementErrorResponse)
        self.assertEqual("getOperation", parsed.request_action)
        self.assertEqual(operation_id, parsed.operation_id)
        for expected_action, expected_operation_id in (
            ("status", operation_id),
            ("getOperation", str(uuid.uuid4())),
        ):
            with self.subTest(
                error_expected_action=expected_action,
                error_expected_operation_id=expected_operation_id,
            ):
                with self.assertRaises(ProtocolError):
                    SourceManagementResponse.parse(
                        json.dumps(error_wire).encode(),
                        expected_request_id=request_id,
                        expected_action=expected_action,
                        expected_operation_id=expected_operation_id,
                    )

        accepted = SourceManagementOperation(
            operation_id=operation_id,
            source_id="archive",
            display_name="Archive",
            phase="accepted",
            reason_code="accepted",
            detail="ignored",
            created_at="2026-08-31T10:00:00Z",
            updated_at="2026-08-31T10:00:00Z",
        )
        success_wire = SourceManagementResponse.from_operation(
            request_id, "getOperation", accepted
        ).to_wire()
        self.assertIsInstance(
            SourceManagementResponse.parse(
                json.dumps(success_wire).encode(),
                expected_request_id=request_id,
                expected_action="getOperation",
                expected_operation_id=operation_id,
            ),
            SourceManagementResponse,
        )
        for expected_action, expected_operation_id in (
            ("status", operation_id),
            ("getOperation", str(uuid.uuid4())),
        ):
            with self.subTest(
                expected_action=expected_action,
                expected_operation_id=expected_operation_id,
            ):
                with self.assertRaises(ProtocolError):
                    SourceManagementResponse.parse(
                        json.dumps(success_wire).encode(),
                        expected_request_id=request_id,
                        expected_action=expected_action,
                        expected_operation_id=expected_operation_id,
                    )

    def test_source_management_rejects_nonpublic_reasons_and_semantically_invalid_operations(self) -> None:
        invalid_capabilities = (
            (True, "session_token_abc123"),
            (False, "supported"),
            (True, "unsupported_deployment"),
        )
        for supported, reason_code in invalid_capabilities:
            with self.subTest(supported=supported, reason_code=reason_code):
                with self.assertRaises(ProtocolError):
                    SourceManagementCapability(supported, reason_code, "ignored")

        invalid_operations = (
            {
                "phase": "accepted",
                "reason_code": "session_token_abc123",
                "source_id": "archive",
                "display_name": "Archive",
            },
            {
                "phase": "accepted",
                "reason_code": "accepted",
                "source_id": None,
                "display_name": "Archive",
            },
            {
                "phase": "failed",
                "reason_code": "validation_failed",
                "source_id": "archive",
                "display_name": None,
            },
            {
                "phase": "completed",
                "reason_code": "completed",
                "source_id": "archive",
                "display_name": "Archive",
                "created_at": "2026-02-30T10:00:00Z",
            },
            {
                "phase": "completed",
                "reason_code": "completed",
                "source_id": "archive",
                "display_name": "Archive",
                "created_at": "2026-08-31T10:00:01Z",
                "updated_at": "2026-08-31T10:00:00Z",
            },
        )
        for values in invalid_operations:
            with self.subTest(values=values):
                with self.assertRaises(ProtocolError):
                    SourceManagementOperation(
                        operation_id=str(uuid.uuid4()),
                        source_id=values["source_id"],
                        display_name=values["display_name"],
                        phase=values["phase"],
                        reason_code=values["reason_code"],
                        detail="ignored",
                        created_at=values.get("created_at", "2026-08-31T10:00:00Z"),
                        updated_at=values.get("updated_at", "2026-08-31T10:00:00Z"),
                    )

    def test_source_management_parser_rejects_nested_duplicates_and_round_trips_errors(self) -> None:
        request_id = str(uuid.uuid4())
        operation_id = str(uuid.uuid4())
        nested_duplicates = (
            (
                '{"protocolVersion":5,"requestId":"'
                + request_id
                + '","action":"status","payload":{"supported":true,"reasonCode":"supported","detail":"Source management is available.","detail":"Source management is available."}}'
            ).encode(),
            (
                '{"protocolVersion":5,"requestId":"'
                + request_id
                + '","action":"getOperation","payload":{"operationId":"'
                + operation_id
                + '","sourceId":"archive","displayName":"Archive","phase":"accepted","reasonCode":"accepted","detail":"Source change accepted.","createdAt":"2026-08-31T10:00:00Z","updatedAt":"2026-08-31T10:00:00Z","phase":"accepted"}}'
            ).encode(),
            (
                '{"protocolVersion":5,"requestId":"'
                + request_id
                + '","action":"error","payload":{"requestAction":"status","operationId":null,"code":"unsupported","detail":"Source management is unavailable on this installation.","code":"unsupported"}}'
            ).encode(),
        )
        for raw in nested_duplicates:
            with self.subTest(raw=raw):
                with self.assertRaisesRegex(ProtocolError, "duplicate"):
                    SourceManagementResponse.parse(raw)

        error = SourceManagementErrorResponse(
            request_id,
            "validation_failed",
            "ignored",
            request_action="getOperation",
            operation_id=operation_id,
        )
        parsed = SourceManagementResponse.parse(
            json.dumps(error.to_wire()).encode(),
            expected_request_id=request_id,
            expected_action="getOperation",
            expected_operation_id=operation_id,
        )
        self.assertEqual(error, parsed)
        self.assertEqual(
            error,
            SourceManagementErrorResponse.parse(
                json.dumps(error.to_wire()).encode(),
                expected_request_id=request_id,
                expected_action="getOperation",
                expected_operation_id=operation_id,
            ),
        )


class UpdaterDiscoveryTests(unittest.TestCase):
    def state(
        self,
        channel: str = "stable",
        digest: str = CURRENT_DIGEST,
        version: str | None = "v1.3.0",
    ) -> InstalledState:
        return InstalledState(channel, digest, version)

    @staticmethod
    def resolved(reference: str, *, digest: str = TARGET_DIGEST) -> ResolvedImage:
        return ResolvedImage(reference, digest, "v1.4.0", REVISION)

    def test_stable_uses_latest_release_and_matching_trusted_digest(self) -> None:
        requested: list[str] = []

        def resolve(reference: str) -> ResolvedImage:
            requested.append(reference)
            return self.resolved(reference)

        result = UpdateDiscovery(
            state=self.state(),
            latest_release=lambda: "v1.4.0",
            resolve_image=resolve,
        ).check()

        expected = f"{TRUSTED_IMAGE_REPOSITORY}:v1.4.0"
        self.assertEqual("available", result.phase)
        self.assertEqual("v1.4.0", result.target_version)
        self.assertEqual(expected, result.target_reference)
        self.assertEqual([expected], requested)
        self.assertEqual(TARGET_DIGEST, result.target_digest)

    def test_stable_equal_digest_is_current_even_if_display_version_changed(self) -> None:
        reference = f"{TRUSTED_IMAGE_REPOSITORY}:v1.4.0"
        result = UpdateDiscovery(
            state=self.state(version=None),
            latest_release=lambda: GitHubRelease("v1.4.0"),
            resolve_image=lambda _: self.resolved(reference, digest=CURRENT_DIGEST),
        ).check()

        self.assertEqual("current", result.phase)
        self.assertEqual("up_to_date", result.reason_code)
        self.assertEqual("unknown", result.current_version)
        self.assertFalse(result.update_available)

    def test_edge_uses_only_edge_reference_and_revision_display(self) -> None:
        reference = f"{TRUSTED_IMAGE_REPOSITORY}:edge"
        resolver = mock.Mock(
            return_value=ResolvedImage(reference, TARGET_DIGEST, "v1.4.0-dev", REVISION)
        )

        result = UpdateDiscovery(
            state=self.state(channel="edge", version="edge@111111111111"),
            latest_release=mock.Mock(side_effect=AssertionError("GitHub called")),
            resolve_image=resolver,
        ).check()

        self.assertEqual("available", result.phase)
        self.assertEqual("edge@aaaaaaaaaaaa", result.target_version)
        self.assertEqual(reference, result.target_reference)
        resolver.assert_called_once_with(reference)

    def test_exact_version_is_pinned_without_network_access(self) -> None:
        latest = mock.Mock(side_effect=AssertionError("network called"))
        resolver = mock.Mock(side_effect=AssertionError("network called"))

        for channel in ("v1.3.0", "v1.3.0-beta.1"):
            with self.subTest(channel=channel):
                result = UpdateDiscovery(
                    state=self.state(channel=channel, version=channel),
                    latest_release=latest,
                    resolve_image=resolver,
                ).check()
                self.assertEqual("unavailable", result.phase)
                self.assertEqual("version_pinned", result.reason_code)
                self.assertEqual(channel, result.channel)

        latest.assert_not_called()
        resolver.assert_not_called()

    def test_rejects_draft_prerelease_and_invalid_stable_release_metadata(self) -> None:
        resolver = mock.Mock(side_effect=AssertionError("image resolver called"))
        releases = (
            GitHubRelease("v1.4.0", draft=True),
            GitHubRelease("v1.4.0", prerelease=True),
            GitHubRelease("v1.4.0-beta.1"),
            GitHubRelease("1.4.0"),
            GitHubRelease("v01.4.0"),
            GitHubRelease("v1.4.0\nedge"),
        )
        for release in releases:
            with self.subTest(release=release):
                result = UpdateDiscovery(
                    state=self.state(),
                    latest_release=lambda release=release: release,
                    resolve_image=resolver,
                ).check()
                self.assertEqual("unavailable", result.phase)
                self.assertEqual("release_invalid", result.reason_code)
                self.assertFalse(result.update_available)
        resolver.assert_not_called()

    def test_rejects_untrusted_or_invalid_resolved_image_metadata(self) -> None:
        trusted = f"{TRUSTED_IMAGE_REPOSITORY}:v1.4.0"
        bad_images = (
            ResolvedImage("attacker.example/root:v1.4.0", TARGET_DIGEST, "v1.4.0", REVISION),
            ResolvedImage(trusted, "sha256:abc", "v1.4.0", REVISION),
            ResolvedImage(trusted, TARGET_DIGEST, "v1.5.0", REVISION),
            ResolvedImage(trusted, TARGET_DIGEST, "v1.4.0", "not-a-revision"),
        )
        for image in bad_images:
            with self.subTest(image=image):
                result = UpdateDiscovery(
                    state=self.state(),
                    latest_release=lambda: "v1.4.0",
                    resolve_image=lambda _, image=image: image,
                ).check()
                self.assertEqual("unavailable", result.phase)
                self.assertEqual("manifest_invalid", result.reason_code)

    def test_missing_or_invalid_state_fails_closed_without_network(self) -> None:
        network = mock.Mock(side_effect=AssertionError("network called"))
        states = (
            self.state(channel="latest"),
            self.state(digest="sha256:abc"),
            mock.Mock(load=mock.Mock(side_effect=StateError("invalid_state", "bad state"))),
        )
        for state in states:
            with self.subTest(state=state):
                result = UpdateDiscovery(state, network, network).check()
                self.assertEqual("unavailable", result.phase)
                self.assertEqual("invalid_state", result.reason_code)
                self.assertNotIn("bad state", result.detail)
        network.assert_not_called()

    def test_filesystem_state_requires_regular_non_symlinked_single_line_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "channel").write_text("stable\n", encoding="utf-8")
            (root / "current-image").write_text(
                f"{TRUSTED_IMAGE_REPOSITORY}@{CURRENT_DIGEST}\n", encoding="utf-8"
            )
            (root / "current-version").write_text("v1.3.0\n", encoding="utf-8")

            loaded = InstalledState.load(root)
            self.assertEqual("stable", loaded.channel)
            self.assertEqual(CURRENT_DIGEST, loaded.current_digest)
            self.assertEqual("v1.3.0", loaded.current_version)

            (root / "current-version").unlink()
            self.assertIsNone(InstalledState.load(root).current_version)

            outside = root / "outside"
            outside.write_text("edge\n", encoding="utf-8")
            (root / "channel").unlink()
            try:
                (root / "channel").symlink_to(outside)
            except OSError as error:
                self.skipTest(f"symlinks unavailable: {error}")
            with self.assertRaisesRegex(StateError, "regular protected file"):
                InstalledState.load(root)

    def test_missing_files_multi_line_values_and_untrusted_current_image_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for current_image in (
                None,
                "attacker.example/root@" + CURRENT_DIGEST,
                f"{TRUSTED_IMAGE_REPOSITORY}@{CURRENT_DIGEST}\nextra",
            ):
                with self.subTest(current_image=current_image):
                    (root / "channel").write_text("stable\n", encoding="utf-8")
                    image_path = root / "current-image"
                    if current_image is None:
                        image_path.unlink(missing_ok=True)
                    else:
                        image_path.write_text(current_image + "\n", encoding="utf-8")
                    with self.assertRaises(StateError):
                        InstalledState.load(root)

    def test_journal_is_lower_camel_bounded_and_contains_no_physical_state_root(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            physical_root = str(Path(directory).resolve())
            result = UpdateDiscovery(
                state=self.state(),
                latest_release=lambda: "v1.4.0",
                resolve_image=lambda reference: self.resolved(reference),
            ).check()

            journal = result.to_journal()
            encoded = json.dumps(journal)
            self.assertEqual("available", journal["phase"])
            self.assertEqual("v1.4.0", journal["targetVersion"])
            self.assertEqual(TARGET_DIGEST, journal["targetDigest"])
            self.assertNotIn("target_reference", journal)
            self.assertNotIn("targetReference", journal)
            self.assertNotIn(physical_root, encoded)
            self.assertLessEqual(len(journal["detail"]), 240)


if __name__ == "__main__":
    unittest.main()
