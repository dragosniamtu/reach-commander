from __future__ import annotations

import json
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest import mock

from deploy.updater_protocol import (
    MAX_MESSAGE_BYTES,
    PROTOCOL_VERSION,
    TRUSTED_IMAGE_REPOSITORY,
    GitHubRelease,
    InstalledState,
    ProtocolError,
    ResolvedImage,
    StateError,
    UpdateDiscovery,
    UpdaterRequest,
)


CURRENT_DIGEST = "sha256:" + "1" * 64
TARGET_DIGEST = "sha256:" + "2" * 64
REVISION = "a" * 40


class UpdaterProtocolTests(unittest.TestCase):
    def valid_request(self, action: str = "check") -> dict[str, object]:
        return {
            "protocolVersion": 1,
            "requestId": str(uuid.uuid4()),
            "action": action,
        }

    def test_protocol_constants_are_fixed_and_request_is_immutable(self) -> None:
        self.assertEqual(1, PROTOCOL_VERSION)
        self.assertEqual(65_536, MAX_MESSAGE_BYTES)

        value = self.valid_request("applyConfiguredChannel")
        request = UpdaterRequest.parse(json.dumps(value).encode())

        self.assertEqual(PROTOCOL_VERSION, request.protocol_version)
        self.assertEqual(value["requestId"], request.request_id)
        self.assertEqual("applyConfiguredChannel", request.action)
        with self.assertRaises((AttributeError, TypeError)):
            request.action = "check"  # type: ignore[misc]

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
            ({**self.valid_request(), "protocolVersion": 2}, "incompatible"),
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
