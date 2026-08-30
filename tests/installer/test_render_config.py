from __future__ import annotations

import importlib.util
import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "deploy" / "render_config.py"
TEMPLATE_PATH = ROOT / "deploy" / "compose.release.yaml"
FIXTURE_PATH = Path(__file__).parent / "fixtures" / "valid-request.json"
IMAGE_DIGEST = (
    "ghcr.io/dragosniamtu/reach-commander@sha256:"
    + "a" * 64
)


def import_renderer():
    if not MODULE_PATH.is_file():
        return None
    spec = importlib.util.spec_from_file_location("reachcommander_render_config", MODULE_PATH)
    if spec is None or spec.loader is None:
        return None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class RendererTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.renderer = import_renderer()

    def require_renderer(self):
        self.assertIsNotNone(self.renderer, "deploy/render_config.py must exist")
        return self.renderer

    def valid_payload(self) -> dict:
        return json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))

    def load_payload(self, payload: dict):
        renderer = self.require_renderer()
        with tempfile.TemporaryDirectory() as directory:
            request_path = Path(directory) / "request.json"
            request_path.write_text(json.dumps(payload), encoding="utf-8")
            return renderer.load_request(request_path)

    def test_public_module_and_dataclasses_exist(self) -> None:
        renderer = self.require_renderer()
        self.assertTrue(hasattr(renderer, "SourceRequest"))
        self.assertTrue(hasattr(renderer, "DeploymentRequest"))

    def test_valid_fixture_loads_as_typed_request(self) -> None:
        renderer = self.require_renderer()
        request = renderer.load_request(FIXTURE_PATH)

        self.assertIsInstance(request, renderer.DeploymentRequest)
        self.assertEqual("secure-https", request.access_mode)
        self.assertEqual("127.0.0.1", request.bind_address)
        self.assertEqual(8092, request.port)
        self.assertFalse(request.allow_insecure_http)
        self.assertEqual(1000, request.uid)
        self.assertEqual(1000, request.gid)
        self.assertEqual(IMAGE_DIGEST, request.image)
        self.assertEqual("media", request.sources[0].id)
        self.assertEqual("/srv/Family Media", request.sources[0].host_path)

    def test_rejects_out_of_range_port_uid_and_gid(self) -> None:
        for field, value in (("port", 0), ("port", 65536), ("uid", 0),
                             ("uid", 2147483648), ("gid", 0),
                             ("gid", 2147483648)):
            with self.subTest(field=field, value=value):
                payload = self.valid_payload()
                payload[field] = value
                with self.assertRaisesRegex(ValueError, field):
                    self.load_payload(payload)

    def test_rejects_invalid_bind_address_and_scalar_types(self) -> None:
        for value in ("127.0.0.1\nINJECTED=1", "localhost", "999.1.1.1"):
            with self.subTest(value=value):
                payload = self.valid_payload()
                payload["bindAddress"] = value
                with self.assertRaisesRegex(ValueError, "bindAddress"):
                    self.load_payload(payload)

        payload = self.valid_payload()
        payload["port"] = "8092"
        with self.assertRaisesRegex(ValueError, "port"):
            self.load_payload(payload)

    def test_rejects_invalid_or_duplicate_source_ids(self) -> None:
        for value in ("Media", "-media", "media.path", "", "a" * 65):
            with self.subTest(value=value):
                payload = self.valid_payload()
                payload["sources"][0]["id"] = value
                with self.assertRaisesRegex(ValueError, "sources.*id"):
                    self.load_payload(payload)

        payload = self.valid_payload()
        payload["sources"].append({**payload["sources"][0], "name": "Duplicate"})
        with self.assertRaisesRegex(ValueError, "sources.*id"):
            self.load_payload(payload)

    def test_rejects_empty_sources_duplicate_paths_and_invalid_names(self) -> None:
        payload = self.valid_payload()
        payload["sources"] = []
        with self.assertRaisesRegex(ValueError, "sources"):
            self.load_payload(payload)

        payload = self.valid_payload()
        payload["sources"].append({
            **payload["sources"][0],
            "id": "duplicate-path",
            "name": "Duplicate path",
            "defaultLeft": False,
            "defaultRight": False,
        })
        with self.assertRaisesRegex(ValueError, "hostPath"):
            self.load_payload(payload)

        for name in ("", "x" * 101, "line\nbreak"):
            with self.subTest(name=name):
                payload = self.valid_payload()
                payload["sources"][0]["name"] = name
                with self.assertRaisesRegex(ValueError, "sources.*name"):
                    self.load_payload(payload)

    def test_requires_exactly_one_default_for_each_pane(self) -> None:
        for key in ("defaultLeft", "defaultRight"):
            payload = self.valid_payload()
            payload["sources"][0][key] = False
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, key):
                self.load_payload(payload)

            payload = self.valid_payload()
            payload["sources"].append({
                **payload["sources"][0],
                "id": "other",
                "name": "Other",
                "hostPath": "/srv/other",
                "defaultLeft": key == "defaultLeft",
                "defaultRight": key == "defaultRight",
            })
            with self.subTest(key=key, case="multiple"), self.assertRaisesRegex(
                ValueError, key
            ):
                self.load_payload(payload)

    def test_accepts_approved_images_and_rejects_other_references(self) -> None:
        approved = (
            "ghcr.io/dragosniamtu/reach-commander:stable",
            "ghcr.io/dragosniamtu/reach-commander:edge",
            "ghcr.io/dragosniamtu/reach-commander:v1.2.3",
            "ghcr.io/dragosniamtu/reach-commander:v1.2.3-beta.1",
            IMAGE_DIGEST,
        )
        for image in approved:
            with self.subTest(image=image):
                payload = self.valid_payload()
                payload["image"] = image
                self.assertEqual(image, self.load_payload(payload).image)

        rejected = (
            "stable",
            "ghcr.io/dragosniamtu/reach-commander:latest",
            "docker.io/dragosniamtu/reach-commander:stable",
            "ghcr.io/dragosniamtu/reach-commander:v01.2.3",
            "ghcr.io/dragosniamtu/reach-commander:v1.2",
            "ghcr.io/dragosniamtu/reach-commander:stable\nOTHER=x",
            "ghcr.io/dragosniamtu/reach-commander@sha256:abc",
        )
        for image in rejected:
            with self.subTest(image=image):
                payload = self.valid_payload()
                payload["image"] = image
                with self.assertRaisesRegex(ValueError, "image"):
                    self.load_payload(payload)

    def test_rejects_relative_and_dangerous_canonical_paths(self) -> None:
        dangerous = (
            "/",
            "/proc",
            "/proc/1",
            "/sys/class",
            "/dev/sda",
            "/run/docker.sock",
            "/var/run/docker.sock",
        )
        for path in dangerous:
            with self.subTest(path=path):
                payload = self.valid_payload()
                payload["sources"][0]["hostPath"] = path
                with self.assertRaisesRegex(ValueError, "hostPath"):
                    self.load_payload(payload)

        payload = self.valid_payload()
        payload["sources"][0]["hostPath"] = "srv/media"
        with self.assertRaisesRegex(ValueError, "hostPath"):
            self.load_payload(payload)

    def test_rejects_symlink_resolution_into_dangerous_path(self) -> None:
        renderer = self.require_renderer()
        payload = self.valid_payload()
        payload["sources"][0]["hostPath"] = "/srv/link"
        with mock.patch.object(renderer.os.path, "realpath", return_value="/proc/1"):
            with self.assertRaisesRegex(ValueError, "hostPath"):
                renderer.DeploymentRequest.from_mapping(payload)

    def test_template_contains_exactly_one_mount_marker(self) -> None:
        self.assertTrue(TEMPLATE_PATH.is_file(), "release Compose template must exist")
        template = TEMPLATE_PATH.read_text(encoding="utf-8")
        self.assertEqual(1, template.count("# installer-source-mounts"))

    def test_render_structurally_separates_container_and_host_paths(self) -> None:
        renderer = self.require_renderer()
        payload = self.valid_payload()
        payload["sources"][0]["name"] = "Media's: #$\" Collection"
        payload["sources"][0]["hostPath"] = "/srv/-Media's: #$\" Collection"

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            request_path = root / "request.json"
            output_path = root / "output"
            request_path.write_text(json.dumps(payload), encoding="utf-8")

            renderer.render_deployment(
                renderer.load_request(request_path), TEMPLATE_PATH, output_path
            )

            sources = json.loads(
                (output_path / "config" / "sources.json").read_text(encoding="utf-8")
            )
            mounts = json.loads(
                (output_path / "state" / "source-mounts.json").read_text(
                    encoding="utf-8"
                )
            )
            compose = (output_path / "compose.yaml").read_text(encoding="utf-8")

            self.assertEqual("/sources/media", sources["sources"][0]["path"])
            self.assertNotIn("hostPath", sources["sources"][0])
            self.assertEqual(payload["sources"][0]["hostPath"], mounts["sources"][0]["hostPath"])
            self.assertEqual("ro", mounts["sources"][0]["access"])
            self.assertIn("source: '/srv/-Media''s: #$\" Collection'", compose)
            self.assertIn("target: '/sources/media'", compose)
            self.assertIn("read_only: true", compose)
            self.assertIn("source: ./data", compose)
            self.assertIn("target: /data", compose)
            self.assertRegex(compose, r"target: /data\s+read_only: false")
            self.assertIn('ReverseProxy__TrustNetworkGateways: "true"', compose)
            self.assertNotIn("source-mounts.json", compose)
            self.assertNotIn("# installer-source-mounts", compose)

    def test_trusted_lan_policy_renders_wildcard_http_configuration(self) -> None:
        renderer = self.require_renderer()
        payload = self.valid_payload()
        payload["accessMode"] = "trusted-lan-http"
        payload["bindAddress"] = "0.0.0.0"
        payload["allowInsecureHttp"] = True

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            request_path = root / "request.json"
            output_path = root / "output"
            request_path.write_text(json.dumps(payload), encoding="utf-8")
            renderer.render_deployment(
                renderer.load_request(request_path), TEMPLATE_PATH, output_path
            )
            environment = (output_path / ".env").read_text(encoding="utf-8")
            compose = (output_path / "compose.yaml").read_text(encoding="utf-8")

        self.assertIn("REACHCOMMANDER_ACCESS_MODE=trusted-lan-http\n", environment)
        self.assertIn("REACHCOMMANDER_BIND_ADDRESS=0.0.0.0\n", environment)
        self.assertIn("REACHCOMMANDER_PORT=8092\n", environment)
        self.assertIn("REACHCOMMANDER_ALLOW_INSECURE_HTTP=true\n", environment)
        self.assertIn(
            'Authentication__AllowInsecureHttp: "${REACHCOMMANDER_ALLOW_INSECURE_HTTP}"',
            compose,
        )

    def test_rejects_contradictory_access_policy(self) -> None:
        cases = (
            ("secure-https", "0.0.0.0", False),
            ("secure-https", "127.0.0.1", True),
            ("trusted-lan-http", "127.0.0.1", True),
            ("trusted-lan-http", "0.0.0.0", False),
            ("unsupported", "127.0.0.1", False),
        )
        for access_mode, bind_address, allow_insecure_http in cases:
            with self.subTest(access_mode=access_mode, bind_address=bind_address):
                payload = self.valid_payload()
                payload["accessMode"] = access_mode
                payload["bindAddress"] = bind_address
                payload["allowInsecureHttp"] = allow_insecure_http
                with self.assertRaisesRegex(ValueError, "accessMode"):
                    self.load_payload(payload)

    def test_yaml_scalar_quotes_apostrophes(self) -> None:
        renderer = self.require_renderer()
        self.assertEqual("'Media''s #1'", renderer.yaml_scalar("Media's #1"))

    def test_set_image_changes_only_the_validated_image_key(self) -> None:
        renderer = self.require_renderer()
        with tempfile.TemporaryDirectory() as directory:
            env_path = Path(directory) / ".env"
            env_path.write_text(
                "REACHCOMMANDER_ACCESS_MODE=secure-https\n"
                "REACHCOMMANDER_BIND_ADDRESS=127.0.0.1\n"
                "REACHCOMMANDER_PORT=8092\n"
                "REACHCOMMANDER_ALLOW_INSECURE_HTTP=false\n"
                "REACHCOMMANDER_UID=1000\n"
                "REACHCOMMANDER_GID=1000\n"
                "REACHCOMMANDER_IMAGE=ghcr.io/dragosniamtu/reach-commander:stable\n",
                encoding="utf-8",
            )
            new_image = "ghcr.io/dragosniamtu/reach-commander@sha256:" + "b" * 64
            renderer.set_env_image(env_path, new_image)
            lines = env_path.read_text(encoding="utf-8").splitlines()

            self.assertEqual(7, len(lines))
            self.assertEqual(f"REACHCOMMANDER_IMAGE={new_image}", lines[-1])
            self.assertEqual("REACHCOMMANDER_PORT=8092", lines[2])

    def test_source_paths_cli_emits_nul_delimited_host_paths(self) -> None:
        self.require_renderer()
        with tempfile.TemporaryDirectory() as directory:
            mounts_path = Path(directory) / "source-mounts.json"
            mounts_path.write_text(
                json.dumps({
                    "sources": [
                        {"id": "one", "hostPath": "/srv/one", "access": "ro"},
                        {"id": "two", "hostPath": "/srv/two files", "access": "rw"},
                    ]
                }),
                encoding="utf-8",
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(MODULE_PATH),
                    "source-paths",
                    "--sources",
                    str(mounts_path),
                ],
                check=False,
                capture_output=True,
            )

            self.assertEqual(0, result.returncode, result.stderr.decode())
            self.assertEqual(b"/srv/one\0/srv/two files\0", result.stdout)

    def test_create_and_add_source_cli_builds_a_valid_request(self) -> None:
        self.require_renderer()
        with tempfile.TemporaryDirectory() as directory:
            request_path = Path(directory) / "request.json"
            create = subprocess.run(
                [
                    sys.executable,
                    str(MODULE_PATH),
                    "create-request",
                    "--output",
                    str(request_path),
                    "--bind-address",
                    "127.0.0.1",
                    "--port",
                    "8092",
                    "--uid",
                    "1000",
                    "--gid",
                    "1000",
                    "--image",
                    "ghcr.io/dragosniamtu/reach-commander:stable",
                ],
                check=False,
                capture_output=True,
            )
            self.assertEqual(0, create.returncode, create.stderr.decode())
            self.assertEqual([], json.loads(request_path.read_text())["sources"])

            add = subprocess.run(
                [
                    sys.executable,
                    str(MODULE_PATH),
                    "add-source",
                    "--request",
                    str(request_path),
                    "--id",
                    "family-media",
                    "--name",
                    "Family Media",
                    "--host-path",
                    "/srv/Family Media",
                    "--access",
                    "rw",
                    "--default-left",
                    "true",
                    "--default-right",
                    "true",
                ],
                check=False,
                capture_output=True,
            )
            self.assertEqual(0, add.returncode, add.stderr.decode())
            request = import_renderer().load_request(request_path)
            self.assertFalse(request.sources[0].read_only)

    def test_render_and_set_image_cli_update_generated_files(self) -> None:
        self.require_renderer()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "deployment"
            render = subprocess.run(
                [
                    sys.executable,
                    str(MODULE_PATH),
                    "render",
                    "--request",
                    str(FIXTURE_PATH),
                    "--template",
                    str(TEMPLATE_PATH),
                    "--output",
                    str(output),
                ],
                check=False,
                capture_output=True,
            )
            self.assertEqual(0, render.returncode, render.stderr.decode())
            self.assertTrue((output / "compose.yaml").is_file())

            new_image = "ghcr.io/dragosniamtu/reach-commander@sha256:" + "c" * 64
            set_image = subprocess.run(
                [
                    sys.executable,
                    str(MODULE_PATH),
                    "set-image",
                    "--env",
                    str(output / ".env"),
                    "--image",
                    new_image,
                ],
                check=False,
                capture_output=True,
            )
            self.assertEqual(0, set_image.returncode, set_image.stderr.decode())
            self.assertIn(
                f"REACHCOMMANDER_IMAGE={new_image}",
                (output / ".env").read_text(encoding="utf-8"),
            )

    @unittest.skipIf(os.name == "nt", "Windows does not expose POSIX host modes")
    def test_rendered_runtime_config_is_readable_by_the_non_root_container(self) -> None:
        renderer = self.require_renderer()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "deployment"
            renderer.render_deployment(
                renderer.load_request(FIXTURE_PATH),
                TEMPLATE_PATH,
                output,
            )

            self.assertEqual(0o755, stat.S_IMODE((output / "config").stat().st_mode))
            self.assertEqual(
                0o644,
                stat.S_IMODE((output / "config" / "sources.json").stat().st_mode),
            )
            self.assertEqual(0o700, stat.S_IMODE((output / "state").stat().st_mode))
            self.assertEqual(
                0o600,
                stat.S_IMODE((output / "state" / "source-mounts.json").stat().st_mode),
            )
            self.assertEqual(0o600, stat.S_IMODE((output / ".env").stat().st_mode))

    def test_add_source_rejects_invalid_access_outside_argparse(self) -> None:
        renderer = self.require_renderer()
        with tempfile.TemporaryDirectory() as directory:
            request_path = Path(directory) / "request.json"
            renderer.create_request(
                request_path,
                "127.0.0.1",
                8092,
                1000,
                1000,
                "ghcr.io/dragosniamtu/reach-commander:stable",
            )
            with self.assertRaisesRegex(ValueError, "access"):
                renderer.add_source(
                    request_path,
                    "media",
                    "Media",
                    "/srv/media",
                    "writeable",
                    True,
                    True,
                )

    def test_load_installed_request_and_append_source_reuse_renderer_invariants(self) -> None:
        renderer = self.require_renderer()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            renderer.render_deployment(
                renderer.load_request(FIXTURE_PATH), TEMPLATE_PATH, root
            )

            installed = renderer.load_installed_request(
                root / ".env",
                root / "config" / "sources.json",
                root / "state" / "source-mounts.json",
            )
            updated = renderer.append_source(
                installed,
                source_id="archive",
                name="Archive",
                host_path="/srv/archive",
                access="rw",
            )

            self.assertEqual(("media", "archive"), tuple(item.id for item in updated.sources))
            self.assertTrue(updated.sources[0].default_left)
            self.assertTrue(updated.sources[0].default_right)
            self.assertFalse(updated.sources[1].default_left)
            self.assertFalse(updated.sources[1].default_right)
            self.assertFalse(updated.sources[1].read_only)

    def test_load_installed_request_rejects_catalog_mount_mismatch(self) -> None:
        renderer = self.require_renderer()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            renderer.render_deployment(
                renderer.load_request(FIXTURE_PATH), TEMPLATE_PATH, root
            )
            catalog_path = root / "config" / "sources.json"
            catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
            catalog["sources"][0]["readOnly"] = False
            catalog_path.write_text(json.dumps(catalog), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "inconsistent"):
                renderer.load_installed_request(
                    root / ".env",
                    catalog_path,
                    root / "state" / "source-mounts.json",
                )


if __name__ == "__main__":
    unittest.main()
