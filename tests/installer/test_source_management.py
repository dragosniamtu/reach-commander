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
from types import SimpleNamespace
from unittest import mock

from deploy import render_config
from deploy.updater_protocol import SOURCE_MANAGEMENT_PROTOCOL_VERSION


ROOT = Path(__file__).resolve().parents[2]
TEMPLATE = ROOT / "deploy" / "compose.release.yaml"
IMAGE = "ghcr.io/dragosniamtu/reach-commander@sha256:" + "a" * 64
IMAGE_ID = "sha256:" + "a" * 64


def directory_status(*, uid: int = 0, mode: int = 0o755, inode: int = 1):
    return SimpleNamespace(
        st_mode=stat.S_IFDIR | mode,
        st_uid=uid,
        st_dev=7,
        st_ino=inode,
    )


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


def remove_request(source_id: str = "archive") -> bytes:
    return json.dumps(
        {
            "protocolVersion": SOURCE_MANAGEMENT_PROTOCOL_VERSION,
            "requestId": str(uuid.uuid4()),
            "action": "removeSource",
            "sourceId": source_id,
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


class InjectedAtomicWriter:
    def __init__(
        self,
        root: Path,
        *,
        fail_publish_index: int | None = None,
        fail_restore: bool = False,
    ) -> None:
        self.root = root
        self.fail_publish_index = fail_publish_index
        self.fail_restore = fail_restore
        self.live_calls = 0
        self.publish_failed = False
        self.restore_attempted = False

    def __call__(self, path: Path, content: str, mode: int = 0o600) -> None:
        destination = Path(path)
        live_paths = {
            self.root / "config" / "sources.json",
            self.root / "state" / "source-mounts.json",
            self.root / "compose.yaml",
        }
        if destination in live_paths:
            self.live_calls += 1
            if (
                not self.publish_failed
                and self.fail_publish_index == self.live_calls
            ):
                self.publish_failed = True
                raise OSError("write failure contains /private/source")
            if self.publish_failed:
                self.restore_attempted = True
                if self.fail_restore:
                    raise OSError("restore failure contains /private/source")
        render_config.atomic_write(destination, content, mode)


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
        status_reader=None,
        atomic_writer=None,
        fsync_directory=None,
    ):
        commands = commands or FakeCommands()
        canonical = {
            self.existing: self.existing,
            **(canonical or {"/srv/archive": "/srv/archive"}),
        }
        if status_reader is None:
            leaf_inodes = {
                path: 100 + index for index, path in enumerate(canonical.values())
            }

            def status_reader(path: str):
                if path in leaf_inodes:
                    return directory_status(
                        uid=self.runtime_uid,
                        mode=0o750,
                        inode=leaf_inodes[path],
                    )
                return directory_status()

        options = {}
        options["status_reader"] = status_reader
        if atomic_writer is not None:
            options["atomic_writer"] = atomic_writer
        if fsync_directory is not None:
            options["fsync_directory"] = fsync_directory
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
            **options,
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

        with self.assertRaises(module.SourceManagementFailure):
            module.canonical_source_path(
                "/srv/link",
                canonicalizer=lambda _: "/srv/" + "a" * 1_020,
                directory_exists=lambda _: True,
            )

    def test_trusted_source_ancestry_rejects_writable_or_non_root_parent(self) -> None:
        module = self.source_management
        for unsafe_status in (
            directory_status(mode=0o775),
            directory_status(uid=1000),
        ):
            with self.subTest(status=unsafe_status), self.assertRaises(
                module.SourceManagementFailure
            ) as raised:
                module.capture_trusted_source_identity(
                    "/srv/archive",
                    status_reader=lambda path, unsafe=unsafe_status: (
                        directory_status(uid=1000, mode=0o770, inode=40)
                        if path == "/srv/archive"
                        else unsafe
                        if path == "/srv"
                        else directory_status()
                    ),
                )
            self.assertEqual("untrusted_source_ancestry", raised.exception.code)
            self.assertEqual(
                "The source folder's parent directories must be root-owned and not group- or world-writable.",
                str(raised.exception),
            )
            self.assertNotIn("/srv/archive", str(raised.exception))

    def test_trusted_source_ancestry_allows_a_runtime_owned_writable_leaf(self) -> None:
        module = self.source_management

        identity = module.capture_trusted_source_identity(
            "/srv/reachcommander/archive",
            status_reader=lambda path: (
                directory_status(uid=1000, mode=0o770, inode=40)
                if path == "/srv/reachcommander/archive"
                else directory_status(uid=0, mode=0o755)
            ),
        )

        self.assertEqual((7, 40), identity)

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
        if os.name != "nt":
            self.assertEqual(0o755, stat.S_IMODE((self.root / "config").stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE((self.root / "state").stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE(self.root.stat().st_mode))

    def test_remove_unmaps_source_preserves_remaining_defaults_and_never_deletes_host_data(self) -> None:
        manager = self.fixture.manager()
        manager.add(add_request())
        sentinel = self.root / "host-data-sentinel.txt"
        sentinel.write_text("preserve me", encoding="utf-8")

        result = self.fixture.manager().add(remove_request("media"))

        self.assertEqual({"sourceId": "media", "displayName": "Media"}, result)
        self.assertEqual(["archive"], [item["id"] for item in self.read_catalog()["sources"]])
        self.assertTrue(self.read_catalog()["sources"][0]["defaultLeft"])
        self.assertTrue(self.read_catalog()["sources"][0]["defaultRight"])
        self.assertEqual("preserve me", sentinel.read_text(encoding="utf-8"))

    def test_remove_rejects_the_final_source_without_changing_active_files(self) -> None:
        before = self.fingerprint()

        with self.assertRaises(self.source_management.SourceManagementFailure) as raised:
            self.fixture.manager().add(remove_request("media"))

        self.assertEqual("validation_failed", raised.exception.code)
        self.assertEqual(before, self.fingerprint())

    def test_remove_publish_failure_restores_the_mapping_and_can_be_retried(self) -> None:
        self.fixture.manager().add(add_request())
        before = self.fingerprint()
        writer = InjectedAtomicWriter(self.root, fail_publish_index=2)

        with self.assertRaises(self.source_management.SourceManagementFailure) as raised:
            self.fixture.manager(atomic_writer=writer).add(remove_request("archive"))

        self.assertEqual("rolled_back", raised.exception.code)
        self.assertEqual(before, self.fingerprint())
        result = self.fixture.manager().add(remove_request("archive"))
        self.assertEqual("archive", result["sourceId"])
        self.assertEqual(["media"], [item["id"] for item in self.read_catalog()["sources"]])

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

        commands.compose_config_status = 0
        retry = self.fixture.manager(commands).add(add_request())
        self.assertEqual("archive", retry["sourceId"])

    def test_each_partial_publish_write_is_rolled_back_and_a_retry_succeeds(self) -> None:
        for failure_index in (1, 2, 3):
            with self.subTest(failure_index=failure_index):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory) / "install"
                    fixture = SourceFixture(root, self.source_management)
                    before = tuple(
                        (root / relative).read_bytes()
                        for relative in (
                            "config/sources.json",
                            "state/source-mounts.json",
                            "compose.yaml",
                        )
                    )
                    writer = InjectedAtomicWriter(
                        root, fail_publish_index=failure_index
                    )
                    commands = FakeCommands()

                    with self.assertRaises(
                        self.source_management.SourceManagementFailure
                    ) as raised:
                        fixture.manager(
                            commands, atomic_writer=writer
                        ).add(add_request())

                    self.assertEqual("rolled_back", raised.exception.code)
                    self.assertTrue(writer.restore_attempted)
                    self.assertEqual(
                        before,
                        tuple(
                            (root / relative).read_bytes()
                            for relative in (
                                "config/sources.json",
                                "state/source-mounts.json",
                                "compose.yaml",
                            )
                        ),
                    )
                    result = fixture.manager().add(add_request())
                    self.assertEqual("archive", result["sourceId"])

    def test_partial_publish_with_failed_restore_retains_backup_for_retry(self) -> None:
        before = self.fingerprint()
        writer = InjectedAtomicWriter(
            self.root, fail_publish_index=2, fail_restore=True
        )
        with self.assertRaises(self.source_management.SourceManagementFailure) as raised:
            self.fixture.manager(atomic_writer=writer).add(add_request())
        self.assertEqual("recovery_failed", raised.exception.code)
        self.assertTrue((self.root / "backups" / ".source-transaction").is_dir())

        result = self.fixture.manager(
            canonical={"/srv/second": "/srv/second"}
        ).add(add_request("Second", "/srv/second"))
        self.assertEqual("second", result["sourceId"])
        self.assertNotEqual(before, self.fingerprint())

    def test_transaction_root_creation_fsyncs_backups_parent_first(self) -> None:
        fsync_calls: list[Path] = []
        manager = self.fixture.manager(
            fsync_directory=lambda path: fsync_calls.append(Path(path))
        )
        manager._transaction_id = "12345678-1234-4234-8234-123456789abc"
        manager._create_backup()

        self.assertEqual(self.root / "backups", fsync_calls[0])

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

        shutil.rmtree(self.root / "backups" / ".source-transaction")
        with self.assertRaises(self.source_management.SourceManagementFailure) as retry:
            self.fixture.manager().add(add_request())
        self.assertEqual("recovery_failed", retry.exception.code)

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

    def test_existing_persisted_sources_must_be_canonical_and_pairwise_separate(self) -> None:
        request = render_config.DeploymentRequest.from_mapping(
            {
                "accessMode": "secure-https",
                "bindAddress": "127.0.0.1",
                "port": 8092,
                "allowInsecureHttp": False,
                "uid": self.fixture.runtime_uid,
                "gid": self.fixture.runtime_gid,
                "image": IMAGE,
                "sources": [
                    {
                        "id": "media",
                        "name": "Media",
                        "hostPath": "/srv/media",
                        "readOnly": True,
                        "defaultLeft": True,
                        "defaultRight": True,
                    },
                    {
                        "id": "nested",
                        "name": "Nested",
                        "hostPath": "/srv/media/nested",
                        "readOnly": True,
                        "defaultLeft": False,
                        "defaultRight": False,
                    },
                ],
            }
        )
        render_config.render_deployment(request, self.fixture.template, self.root)
        self.fixture.override.write_text("services: {}\n", encoding="utf-8")
        self.fixture.protect()

        with self.assertRaises(self.source_management.SourceManagementFailure):
            self.fixture.manager(
                canonical={
                    "/srv/archive": "/srv/archive",
                    "/srv/media": "/srv/media",
                    "/srv/media/nested": "/srv/media/nested",
                }
            ).add(add_request())

    def test_source_leaf_identity_is_revalidated_before_live_publication(self) -> None:
        leaf_reads = 0

        def status_reader(path: str):
            nonlocal leaf_reads
            if path == "/srv/archive":
                leaf_reads += 1
                return directory_status(uid=1000, mode=0o770, inode=40 + leaf_reads)
            if path == "/srv/media":
                return directory_status(uid=1000, mode=0o750, inode=30)
            return directory_status()

        before = self.fingerprint()
        with self.assertRaises(self.source_management.SourceManagementFailure):
            self.fixture.manager(
                canonical={
                    "/srv/archive": "/srv/archive",
                    "/srv/media": "/srv/media",
                },
                status_reader=status_reader,
            ).add(add_request())

        self.assertEqual(before, self.fingerprint())

    def test_source_leaf_identity_is_revalidated_before_service_recreate(self) -> None:
        leaf_reads = 0

        def status_reader(path: str):
            nonlocal leaf_reads
            if path == "/srv/archive":
                leaf_reads += 1
                inode = 41 if leaf_reads < 3 else 42
                return directory_status(uid=1000, mode=0o770, inode=inode)
            if path == "/srv/media":
                return directory_status(uid=1000, mode=0o750, inode=30)
            return directory_status()

        before = self.fingerprint()
        with self.assertRaises(self.source_management.SourceManagementFailure):
            self.fixture.manager(
                canonical={"/srv/archive": "/srv/archive"},
                status_reader=status_reader,
            ).add(add_request())

        self.assertEqual(before, self.fingerprint())

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

    def test_journal_rejects_duplicates_invalid_fields_and_phase_reason_mismatch(self) -> None:
        manager = self.fixture.manager()
        manager._journal("validating", "archive", "Archive")
        journal_path = self.root / "state" / "source-operation.json"
        valid = self.journal()
        invalid_documents: list[str] = []

        duplicate = journal_path.read_text(encoding="utf-8").replace(
            '"phase": "validating",',
            '"phase": "validating",\n  "phase": "validating",',
        )
        invalid_documents.append(duplicate)
        for key, value in (
            ("sourceId", 7),
            ("sourceId", "Not_Canonical"),
            ("displayName", "x" * 81),
            ("displayName", None),
            ("reasonCode", "completed"),
            ("updatedAt", "2026-08-31T00:00:00+02:00"),
            ("updatedAt", "2026-08-31Z"),
            ("updatedAt", "not-a-timestamp"),
        ):
            changed = dict(valid)
            changed[key] = value
            invalid_documents.append(json.dumps(changed))

        for document in invalid_documents:
            with self.subTest(document=document[:80]):
                journal_path.write_text(document, encoding="utf-8")
                journal_path.chmod(0o600)
                with self.assertRaises(
                    self.source_management.SourceManagementFailure
                ):
                    manager._read_journal()

    def test_backup_manifest_matches_journal_and_mismatch_fails_closed(self) -> None:
        with self.assertRaises(self.source_management.SimulatedInterruption):
            self.fixture.manager(interrupt_after="published").add(add_request())

        journal = self.journal()
        manifest_path = self.root / "backups" / ".source-transaction" / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.assertEqual(journal["transactionId"], manifest["transactionId"])
        self.assertEqual(
            {
                "compose.yaml",
                "config/sources.json",
                "state/source-mounts.json",
            },
            set(manifest["files"]),
        )

        manifest["transactionId"] = "87654321-4321-4321-8321-cba987654321"
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        manifest_path.chmod(0o600)
        with self.assertRaises(self.source_management.SourceManagementFailure) as raised:
            self.fixture.manager().add(add_request())
        self.assertEqual("recovery_failed", raised.exception.code)
        self.assertTrue(manifest_path.exists())

        manifest["transactionId"] = journal["transactionId"]
        manifest["files"]["compose.yaml"] = "0" * 64
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        manifest_path.chmod(0o600)
        with self.assertRaises(self.source_management.SourceManagementFailure):
            self.fixture.manager().add(add_request())

    def test_recovery_revalidates_persisted_source_ancestry_before_recreate(self) -> None:
        with self.assertRaises(self.source_management.SimulatedInterruption):
            self.fixture.manager(interrupt_after="published").add(add_request())

        commands = FakeCommands()

        def status_reader(path: str):
            if path == "/srv/media":
                return directory_status(uid=1000, mode=0o750, inode=30)
            if path == "/srv":
                return directory_status(mode=0o777)
            return directory_status()

        with self.assertRaises(self.source_management.SourceManagementFailure) as raised:
            self.fixture.manager(
                commands, status_reader=status_reader
            ).add(add_request())
        self.assertEqual("recovery_failed", raised.exception.code)
        self.assertFalse(any("up" in call for call in commands.calls))
        self.assertTrue((self.root / "backups" / ".source-transaction").exists())

    def test_doctor_validates_every_restore_bearing_backup_before_calling_it_recoverable(self) -> None:
        def remove_manifest(root: Path) -> None:
            (root / "backups" / ".source-transaction" / "manifest.json").unlink()

        def corrupt_manifest(root: Path) -> None:
            (root / "backups" / ".source-transaction" / "manifest.json").write_text(
                "{not-json", encoding="utf-8"
            )

        def remove_backup_file(root: Path) -> None:
            (root / "backups" / ".source-transaction" / "backup" / "compose.yaml").unlink()

        def replace_manifest_value(root: Path, key: str, value: str) -> None:
            path = root / "backups" / ".source-transaction" / "manifest.json"
            manifest = json.loads(path.read_text(encoding="utf-8"))
            if key == "transactionId":
                manifest[key] = value
            else:
                manifest["files"][key] = value
            path.write_text(json.dumps(manifest), encoding="utf-8")
            path.chmod(0o600)

        corruptions = (
            ("missing manifest", remove_manifest),
            ("corrupt manifest", corrupt_manifest),
            ("missing backup file", remove_backup_file),
            (
                "digest mismatch",
                lambda root: replace_manifest_value(root, "compose.yaml", "0" * 64),
            ),
            (
                "transaction mismatch",
                lambda root: replace_manifest_value(
                    root,
                    "transactionId",
                    "87654321-4321-4321-8321-cba987654321",
                ),
            ),
        )

        for label, corrupt in corruptions:
            with self.subTest(label=label), tempfile.TemporaryDirectory() as directory:
                root = Path(directory) / "install"
                fixture = SourceFixture(root, self.source_management)
                with self.assertRaises(self.source_management.SimulatedInterruption):
                    fixture.manager(interrupt_after="published").add(add_request())
                corrupt(root)
                self.assertEqual("recovery-unavailable", fixture.manager().doctor_state())

        with self.assertRaises(self.source_management.SimulatedInterruption):
            self.fixture.manager(interrupt_after="published").add(add_request())
        self.assertEqual("recovery-required", self.fixture.manager().doctor_state())

    def test_doctor_preserves_cleanup_only_staging_and_terminal_transactions(self) -> None:
        for phase in ("staging", "completed", "rolledBack"):
            with self.subTest(phase=phase), tempfile.TemporaryDirectory() as directory:
                root = Path(directory) / "install"
                fixture = SourceFixture(root, self.source_management)
                manager = fixture.manager()
                manager._journal(phase, "archive", "Archive")
                transaction_root = root / "backups" / ".source-transaction"
                transaction_root.mkdir(mode=0o700)
                transaction_root.chmod(0o700)
                self.assertEqual("recovery-required", manager.doctor_state())

    def test_public_boundary_sanitizes_append_render_and_journal_failures(self) -> None:
        secret = "/private/source/command-output"

        def assert_sanitized(action) -> None:
            with self.assertRaises(self.source_management.SourceManagementFailure) as raised:
                action()
            self.assertEqual("source_management_failed", raised.exception.code)
            self.assertNotIn(secret, str(raised.exception))

        with mock.patch.object(
            self.source_management.renderer,
            "append_source",
            side_effect=RuntimeError(secret),
        ):
            assert_sanitized(lambda: self.fixture.manager().add(add_request()))

        with mock.patch.object(
            self.source_management,
            "_write_json",
            side_effect=OSError(secret),
        ):
            assert_sanitized(lambda: self.fixture.manager().add(add_request()))

        with mock.patch.object(
            self.source_management.renderer,
            "render_deployment",
            side_effect=RuntimeError(secret),
        ):
            assert_sanitized(lambda: self.fixture.manager().add(add_request()))

        original_write = self.source_management._write_json

        def fail_error_journal(path, value, mode=0o600):
            if isinstance(value, dict) and value.get("phase") == "failed":
                raise OSError(secret)
            return original_write(path, value, mode)

        commands = FakeCommands()
        commands.compose_config_status = 1
        with mock.patch.object(
            self.source_management, "_write_json", side_effect=fail_error_journal
        ):
            assert_sanitized(lambda: self.fixture.manager(commands).add(add_request()))

    def test_failed_error_journal_before_first_write_retains_recoverable_backup(self) -> None:
        leaf_reads = 0

        def status_reader(path: str):
            nonlocal leaf_reads
            if path == "/srv/archive":
                leaf_reads += 1
                return directory_status(
                    uid=1000, mode=0o770, inode=40 + leaf_reads
                )
            if path == "/srv/media":
                return directory_status(uid=1000, mode=0o750, inode=30)
            return directory_status()

        original_write = self.source_management._write_json

        def fail_error_journal(path, value, mode=0o600):
            if isinstance(value, dict) and value.get("phase") == "failed":
                raise OSError("/private/source/error-journal")
            return original_write(path, value, mode)

        with mock.patch.object(
            self.source_management, "_write_json", side_effect=fail_error_journal
        ):
            with self.assertRaises(self.source_management.SourceManagementFailure):
                self.fixture.manager(status_reader=status_reader).add(add_request())

        self.assertTrue((self.root / "backups" / ".source-transaction").is_dir())
        result = self.fixture.manager().add(add_request())
        self.assertEqual("archive", result["sourceId"])

    def test_main_sanitizes_unexpected_stdin_failure_without_traceback(self) -> None:
        secret = "/private/source/stdin"
        fake_stdin = SimpleNamespace(
            buffer=SimpleNamespace(read=mock.Mock(side_effect=OSError(secret)))
        )
        with mock.patch.object(self.source_management.sys, "stdin", fake_stdin), mock.patch.object(
            self.source_management.os, "write"
        ) as write:
            status = self.source_management.main()

        self.assertEqual(1, status)
        emitted = b"".join(call.args[1] for call in write.call_args_list)
        self.assertNotIn(secret.encode(), emitted)
        self.assertIn(b"source_management_failed", emitted)

    def test_main_uses_distinct_allowlisted_statuses_for_public_failures(self) -> None:
        expected_statuses = {
            "source_management_failed": 1,
            "invalid_request": 2,
            "validation_failed": 3,
            "busy": 4,
            "rolled_back": 5,
            "recovery_failed": 6,
            "untrusted_source_ancestry": 7,
        }
        fake_stdin = SimpleNamespace(buffer=SimpleNamespace(read=mock.Mock(return_value=b"{}")))
        for code, expected_status in expected_statuses.items():
            with self.subTest(code=code), mock.patch.object(
                self.source_management.sys, "stdin", fake_stdin
            ), mock.patch.object(
                self.source_management.SourceTransaction,
                "add",
                side_effect=self.source_management.SourceManagementFailure(code),
            ), mock.patch.object(self.source_management.os, "write"):
                self.assertEqual(expected_status, self.source_management.main())


if __name__ == "__main__":
    unittest.main()
