"""Setup manifest contracts use a fake Hub; no network or model imports."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("download_models", Path(__file__).resolve().parents[1] / "download_models.py")
download_models = importlib.util.module_from_spec(spec)
spec.loader.exec_module(download_models)


class ModelSetupTests(unittest.TestCase):
    def test_setup_downloads_only_requested_pinned_revisions_and_records_hashes(self):
        calls = []
        def fake_download(**kwargs):
            calls.append(kwargs)
            target = Path(kwargs["local_dir"])
            target.mkdir()
            for name in kwargs["allow_patterns"]:
                (target / name).write_bytes(b"test-model-fixture")
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict("sys.modules", {"huggingface_hub": SimpleNamespace(snapshot_download=fake_download)}), \
             patch.object(download_models.importlib.metadata, "version", return_value="test"), \
             patch("builtins.print"):
            target = Path(temporary)
            download_models.download(target, romanian=True)
            self.assertEqual(len(calls), 2)
            for directory, model in download_models.MODELS.items():
                self.assertRegex(model["revision"], "^[a-f0-9]{40}$")
                manifest = json.loads((target / directory / "download-manifest.json").read_text())
                self.assertEqual(manifest["revision"], model["revision"])
                self.assertEqual(manifest["license"], model["license"])
                self.assertEqual(manifest["sha256"][model["files"][0]], hashlib.sha256(b"test-model-fixture").hexdigest())


if __name__ == "__main__":
    unittest.main()
