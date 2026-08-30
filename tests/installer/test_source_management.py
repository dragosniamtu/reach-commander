from __future__ import annotations

import importlib
import json
import os
import shutil
import stat
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest import mock

from deploy import render_config
from deploy.updater_protocol import SOURCE_MANAGEMENT_PROTOCOL_VERSION


ROOT = Path(__file__).resolve().parents[2]
TEMPLATE = ROOT / "deploy" / "compose.release.yaml"
IMAGE = "ghcr.io/dragosniamtu/reach-commander@sha256:" + "a" * 64
IMAGE_ID = "sha256:" + "a" * 64


def add_request(
    name: str = "Archive",
    path: str = "/srv/archive",
    access: str = "readOnly",
) -> bytes:
    return json.dumps(
        {
            "protocolVersion": SOURCE_MANAGEMENT_PROTOCOL_VERSION,
            "requestId": str(uuid.uuid4()),
            "action": "addSource",
            "displayName": name,
            "hostPath": path,
            "access": access,
        }
    ).encode()


class FakeCommands:
    def __init__(self) -> None:
        self.calls: list[tuple[str, ...]] = []
        self.compose_config_status = 0
        self.up_statuses: list[int] = []
        self.health_statuses: list[str] = []
        self.running_image_ids: list[str] = []

    def __call__(self, arguments: list[str], timeout: float) -> str:
        del timeout
        call = tuple(arguments)
        self.calls.append(call)
        if call[:2] == ("docker", "compose") and "config" in call:
            if self.compose_config_status:
                raise RuntimeError("compose output containing /private/path")
            return ""
        if call[:2] == ("docker", "compose") and "up" in call:
            status = self.up_statuses.pop(0) if self.up_statuses else 0
            if status:
                raise RuntimeError("compose output containing /private/path")
            return ""
        if call[:3] == ("docker", "image", "inspect"):
            return IMAGE_ID + "\n"
        if call[:2] == ("docker", "inspect") and "{{.Image}}" in call:
            return (self.running_image_ids.pop(0) if self.running_image_ids else IMAGE_ID) + "\n"
        if call[:2] == ("docker", "inspect"):
            return (self.health_statuses.pop(0) if self.health_statuses else "healthy") + "\n"
        raise AssertionError(f"unexpected command: {call!r}")


class SourceFixture:
    def __init__(self, root: Path, source_management) -> None:
        self.root = root
        self.module = source_management
        self.template = root / "lib" / "compose.release.yaml"
        self.protocol = root / "lib" / "updater_protocol.py"
        self.renderer = root / "bin" / "render_config.py"
        self.helper = root / "bin" / "source_management.py"
        self.override = root / "compose.override.yaml"
        self.existing = "/srv/media"
        (root / "bin").mkdir(parents=True)
        (root / "lib").mkdir()
        shutil.copyfile(TEMPLATE, self.template)
        shutil.copyfile(ROOT / "deploy" / "updater_protocol.py", self.protocol)
        shutil.copyfile(ROOT / "deploy" / "render_config.py", self.renderer)
        self.helper.write_text("# installed source helper\n", encoding="utf-8")
        self.override.write_text("services: {}\n", encoding="utf-8")
        request = render_config.DeploymentRequest.from_mapping(
            {
                "accessMode": "secure-https",
                "bindAddress": "127.0.0.1",
                "port": 8092,
                "allowInsecureHttp": False,
                "uid": self.runtime_uid,
                "gid": self.runtime_gid,
                "image": IMAGE,
                "sources": [
                    {
                        "id": "media",
                        "name": "Media",
                        "hostPath": self.existing,
                        "readOnly": True,
                        "defaultLeft": True,
                        "defaultRight": True,
                    }
                ],
            }
        )
        render_config.render_deployment(request, self.template, root)
        self.override.write_text("services: {}\n", encoding="utf-8")
        self.protect()

    @property
    def runtime_uid(self) -> int:
        return os.geteuid() if hasattr(os, "geteuid") and os.geteuid() > 0 else 1000

    @property
    def runtime_gid(self) -> int:
        return os.getegid() if hasattr(os, "getegid") and os.getegid() > 0 else 1000

    def protect(self) -> None:
        modes = {
            self.root: 0o700,
            self.root / "bin": 0o700,
            self.root / "lib": 0o700,
            self.root / "config": 0o755,
            self.root / "state": 0o700,
            self.root / "backups": 0o700,
            self.root / ".env": 0o600,
            self.root / "compose.yaml": 0o600,
            self.override: 0o600,
            self.root / "config" / "sources.json": 0o644,
            self.root / "state" / "source-mounts.json": 0o600,
            self.template: 0o600,
            self.protocol: 0o644,
            self.renderer: 0o755,
            self.helper: 0o755,
        }
        (self.root / "backups").mkdir(exist_ok=True)
        for path, mode in modes.items():
            path.chmod(mode)

    def manager(
        self,
        commands: FakeCommands | None = None,
        *,
        canonical: dict[str, str] | None = None,
        access_allowed: bool = True,
        interrupt_after: str | None = None,
        uuid_factory=None,
    ):
        commands = commands or FakeCommands()
        canonical = canonical or {"/srv/archive": "/srv/archive"}
        return self.module.SourceTransaction(
            self.root,
            command_runner=commands,
            canonicalizer=lambda value: canonical[value],
            directory_exists=lambda value: value in canonical.values(),
            access_checker=lambda path, uid, gid, writable: access_allowed,
            expected_owner=os.geteuid() if hasattr(os, "geteuid") else None,
            uuid_factory=uuid_factory
            or (lambda: uuid.UUID("12345678-1234-4234-8234-123456789abc")),
            interrupt_after=interrupt_after,
            sleep=lambda _: None,
        )


class SourceValidationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source_management = importlib.import_module("deploy.source_management")

    def test_canonicalization_requires_existing_directory_and_rejects_protected_or_broad_roots(self) -> None:
        module = self.source_management
        self.assertEqual(
            "/srv/media",
            module.canonical_source_path(
                "/srv/link",
                canonicalizer=lambda _: "/srv/media",
                directory_exists=lambda _: True,
            ),
        )
        for requested, canonical in (
            ("relative", "/srv/media"),
            ("/missing", "/missing"),
            ("/proc/link", "/proc/1"),
            ("/data", "/"),
            ("/home", "/home"),
            ("/srv", "/srv"),
            ("/mnt", "/mnt"),
        ):
            with self.subTest(requested=requested), self.assertRaises(module.SourceManagementFailure):
                module.canonical_source_path(
                    requested,
                    canonicalizer=lambda _, result=canonical: result,
                    directory_exists=lambda _: requested != "/missing",
                )

    def test_rejects_installer_overlap_and_duplicate_or_nested_sources(self) -> None:
        module = self.source_management
        for candidate in (
            "/opt/reachcommander/media",
            "/opt",
            "/srv/media",
            "/srv/media/archive",
            "/srv",
        ):
            with self.subTest(candidate=candidate), self.assertRaises(module.SourceManagementFailure):
                module.validate_source_separation(
                    candidate,
                    existing=("/srv/media",),
                    installer_owned=("/opt/reachcommander",),
                )

    def test_generated_ids_are_normalized_bounded_and_collision_safe(self) -> None:
        module = self.source_management
        fixed = lambda: uuid.UUID("12345678-1234-4234-8234-123456789abc")
        self.assertEqual("family-media", module.generate_source_id(" Family MEDIA ", set(), fixed))
        self.assertEqual("source-2026", module.generate_source_id("2026", set(), fixed))
        self.assertEqual("source", module.generate_source_id("***", set(), fixed))
        collision = module.generate_source_id("Family Media", {"family-media"}, fixed)
        self.assertEqual("family-media-12345678", collision)
        self.assertLessEqual(len(module.generate_source_id("x" * 200, set(), fixed)), 63)

    def test_source_count_and_runtime_access_are_enforced(self) -> None:
        module = self.source_management
        with self.assertRaises(module.SourceManagementFailure):
            module.require_source_capacity(module.MAX_SOURCES)
        calls: list[tuple[str, int, int, bool]] = []
        module.require_runtime_access(
            "/srv/archive",
            1000,
            1001,
            True,
            lambda *args: calls.append(args) or True,
        )
        self.assertEqual([("/srv/archive", 1000, 1001, True)], calls)
        with self.assertRaises(module.SourceManagementFailure):
            module.require_runtime_access(
                "/srv/archive", 1000, 1001, False, lambda *args: False
            )

    def test_test_mode_uses_the_current_owner_without_weakening_production(self) -> None:
        module = self.source_management
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(0, module.installed_state_owner())
        with mock.patch.dict(os.environ, {"REACHCOMMANDER_TESTING": "1"}, clear=True):
            with mock.patch.object(module.os, "geteuid", return_value=1234, create=True):
                self.assertEqual(1234, module.installed_state_owner())


class SourceTransactionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source_management = importlib.import_module("deploy.source_management")

    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name) / "install"
        self.fixture = SourceFixture(self.root, self.source_management)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def read_catalog(self) -> dict:
        return json.loads((self.root / "config" / "sources.json").read_text(encoding="utf-8"))

    def journal(self) -> dict:
        return json.loads((self.root / "state" / "source-operation.json").read_text(encoding="utf-8"))

    def fingerprint(self) -> tuple[bytes, bytes, bytes]:
        return tuple(
            (self.root / relative).read_bytes()
            for relative in (
                "config/sources.json",
                "state/source-mounts.json",
                "compose.yaml",
            )
        )

    def test_success_validates_staged_compose_atomically_publishes_and_recreates_only_service(self) -> None:
        commands = FakeCommands()
        result = self.fixture.manager(commands).add(add_request())

        self.assertEqual({"sourceId": "archive", "displayName": "Archive"}, result)
        self.assertEqual(["media", "archive"], [item["id"] for item in self.read_catalog()["sources"]])
        self.assertEqual("completed", self.journal()["phase"])
        self.assertFalse((self.root / "backups" / ".source-transaction").exists())
        config_calls = [call for call in commands.calls if "config" in call]
        self.assertEqual(1, len(config_calls))
        up_calls = [call for call in commands.calls if "up" in call]
        self.assertEqual(1, len(up_calls))
        self.assertEqual(("up", "-d", "reachcommander"), up_calls[0][-3:])
        self.assertNotIn("restart", up_calls[0])

    def test_compose_validation_failure_leaves_active_files_unchanged_and_sanitizes_error(self) -> None:
        commands = FakeCommands()
        commands.compose_config_status = 1
        before = self.fingerprint()
        with self.assertRaises(self.source_management.SourceManagementFailure) as raised:
            self.fixture.manager(commands).add(add_request())
        self.assertEqual(before, self.fingerprint())
        self.assertNotIn("/private/path", str(raised.exception))
        self.assertNotIn("/srv/archive", str(raised.exception))
        self.assertFalse(any("up" in call for call in commands.calls))

    def test_unhealthy_candidate_rolls_back_exact_files_and_verifies_recovery(self) -> None:
        commands = FakeCommands()
        commands.health_statuses = ["unhealthy", "healthy"]
        before = self.fingerprint()
        with self.assertRaises(self.source_management.SourceManagementFailure) as raised:
            self.fixture.manager(commands).add(add_request(access="readWrite"))
        self.assertEqual("rolled_back", raised.exception.code)
        self.assertEqual(before, self.fingerprint())
        self.assertEqual("rolledBack", self.journal()["phase"])
        self.assertFalse((self.root / "backups" / ".source-transaction").exists())
        self.assertEqual(2, len([call for call in commands.calls if "up" in call]))

    def test_failed_rollback_keeps_durable_backup_for_manual_or_next_run_recovery(self) -> None:
        commands = FakeCommands()
        commands.health_statuses = ["unhealthy", "unhealthy"]
        with self.assertRaises(self.source_management.SourceManagementFailure) as raised:
            self.fixture.manager(commands).add(add_request())
        self.assertEqual("recovery_failed", raised.exception.code)
        self.assertEqual("failed", self.journal()["phase"])
        self.assertTrue((self.root / "backups" / ".source-transaction").is_dir())

    def test_interrupted_publish_is_recovered_before_the_next_add(self) -> None:
        before = self.fingerprint()
        with self.assertRaises(self.source_management.SimulatedInterruption):
            self.fixture.manager(interrupt_after="published").add(add_request())
        self.assertNotEqual(before, self.fingerprint())
        self.assertTrue((self.root / "backups" / ".source-transaction").is_dir())

        commands = FakeCommands()
        result = self.fixture.manager(
            commands,
            canonical={"/srv/second": "/srv/second"},
        ).add(add_request("Second", "/srv/second"))

        self.assertEqual("second", result["sourceId"])
        self.assertEqual(["media", "second"], [item["id"] for item in self.read_catalog()["sources"]])
        self.assertGreaterEqual(len([call for call in commands.calls if "up" in call]), 2)

    def test_unsafe_installer_symlink_or_mode_fails_before_commands(self) -> None:
        commands = FakeCommands()
        catalog = self.root / "config" / "sources.json"
        if os.name != "nt":
            catalog.chmod(0o666)
            with self.assertRaises(self.source_management.SourceManagementFailure):
                self.fixture.manager(commands).add(add_request())
            self.assertEqual([], commands.calls)
            self.fixture.protect()

        replacement = self.root / "catalog-replacement"
        replacement.write_bytes(catalog.read_bytes())
        try:
            catalog.unlink()
            catalog.symlink_to(replacement)
        except OSError:
            self.skipTest("filesystem does not expose symlinks")
        with self.assertRaises(self.source_management.SourceManagementFailure):
            self.fixture.manager(commands).add(add_request())
        self.assertEqual([], commands.calls)

    def test_active_install_or_update_transaction_fails_closed(self) -> None:
        for marker in ("install-transaction", "update-transaction"):
            with self.subTest(marker=marker):
                path = self.root / "state" / marker
                path.write_text("active\n", encoding="utf-8")
                path.chmod(0o600)
                with self.assertRaises(self.source_management.SourceManagementFailure):
                    self.fixture.manager().add(add_request())
                path.unlink()

    def test_access_policy_is_checked_as_runtime_identity(self) -> None:
        with self.assertRaises(self.source_management.SourceManagementFailure):
            self.fixture.manager(access_allowed=False).add(add_request(access="readWrite"))

    def test_journal_retains_one_transaction_identity_and_rejects_invalid_identity(self) -> None:
        identities = iter(
            (
                uuid.UUID("12345678-1234-4234-8234-123456789abc"),
                uuid.UUID("87654321-4321-4321-8321-cba987654321"),
            )
        )
        manager = self.fixture.manager(uuid_factory=lambda: next(identities))
        manager._journal("validating", "archive", "Archive")
        first = self.journal()
        manager._journal("staging", "archive", "Archive")
        second = self.journal()

        self.assertEqual(first["transactionId"], second["transactionId"])

        second["transactionId"] = "not-a-uuid"
        (self.root / "state" / "source-operation.json").write_text(
            json.dumps(second), encoding="utf-8"
        )
        (self.root / "state" / "source-operation.json").chmod(0o600)
        with self.assertRaises(self.source_management.SourceManagementFailure):
            manager._read_journal()


if __name__ == "__main__":
    unittest.main()
