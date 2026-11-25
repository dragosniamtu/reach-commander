"""Offline readiness verifies the pinned local artifacts and supervisor heartbeat."""
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import download_models
import worker


class ReadinessTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.models = self.root / "models"

    def prepare(self, name):
        catalog = download_models.MODELS[name]
        target = self.models / name
        target.mkdir(parents=True, exist_ok=True)
        hashes = {}
        for filename in catalog["files"]:
            data = (name + filename).encode()
            (target / filename).write_bytes(data)
            hashes[filename] = hashlib.sha256(data).hexdigest()
        (target / "download-manifest.json").write_text(json.dumps(catalog | {"sha256": hashes}))
        return target

    def test_models_require_all_pinned_files_and_hashes(self):
        from model_readiness import validate_models
        self.assertEqual(validate_models(self.models), (False, False))
        whisper = self.prepare("whisper-small")
        self.assertEqual(validate_models(self.models), (True, False))
        self.prepare("marian-en-ro")
        self.assertEqual(validate_models(self.models), (True, True))
        (whisper / "model.bin").write_bytes(b"corrupt")
        self.assertEqual(validate_models(self.models), (False, False))

    def test_stale_revision_missing_manifest_and_files_are_unavailable(self):
        from model_readiness import validate_models
        for damage in ("revision", "manifest", "config", "empty", "oversize"):
            target = self.prepare("whisper-small")
            manifest = target / "download-manifest.json"
            if damage == "revision":
                value = json.loads(manifest.read_text())
                value["revision"] = "0" * 40
                manifest.write_text(json.dumps(value))
            elif damage == "manifest":
                manifest.unlink()
            elif damage == "config":
                (target / "config.json").unlink()
            elif damage == "empty":
                (target / "model.bin").write_bytes(b"")
            else:
                manifest.write_bytes(b" " * 65537)
            with self.subTest(damage=damage):
                self.assertEqual(validate_models(self.models), (False, False))

    def test_cached_models_are_revalidated_after_artifacts_change(self):
        from model_readiness import ModelReadiness
        target = self.prepare("whisper-small")
        cache = ModelReadiness(self.models)
        self.assertEqual(cache.snapshot(), (True, False))
        with patch("hashlib.file_digest", side_effect=AssertionError("Unchanged model was hashed again")):
            self.assertEqual(cache.snapshot(), (True, False))
        (target / "model.bin").write_bytes(b"modified")
        self.assertEqual(cache.snapshot(), (False, False))

    def test_invalid_cached_models_are_not_rehashed_until_they_change(self):
        from model_readiness import ModelReadiness
        target = self.prepare("whisper-small")
        (target / "model.bin").write_bytes(b"corrupt")
        cache = ModelReadiness(self.models)
        self.assertEqual(cache.snapshot(), (False, False))
        with patch("hashlib.file_digest", side_effect=AssertionError("Unchanged corrupt model was hashed again")):
            self.assertEqual(cache.snapshot(), (False, False))

    def test_invalid_artifacts_are_rejected_before_runtime_model_load(self):
        target = self.prepare("whisper-small")
        (target / "config.json").unlink()
        config = self.supervisor().config
        with self.assertRaisesRegex(worker.WorkerError, "models_missing"):
            worker.load_whisper(config)

    def test_invalid_translation_manifest_is_rejected_before_runtime_model_load(self):
        target = self.prepare("marian-en-ro")
        (target / "download-manifest.json").unlink()
        config = self.supervisor().config
        with self.assertRaisesRegex(worker.WorkerError, "models_missing"):
            worker.translate_romanian([worker.Cue(0, 1, "Hello")], config, lambda *_: None)

    @unittest.skipIf(os.name == "nt", "Creating Windows symlinks requires host privileges")
    def test_model_symlinks_are_unavailable(self):
        from model_readiness import validate_models
        target = self.prepare("whisper-small")
        path = target / "model.bin"
        original = self.root / "outside.bin"
        path.rename(original)
        path.symlink_to(original)
        self.assertEqual(validate_models(self.models), (False, False))

    def supervisor(self):
        return worker.Supervisor(worker.Config(self.root / "mail", (self.root,), self.models,
                                               self.root / "temp", poll_seconds=.01))

    def test_startup_heartbeat_is_healthy_while_model_verification_is_busy(self):
        supervisor = self.supervisor()
        entered = threading.Event()
        release = threading.Event()
        def slow_verification(*_):
            entered.set()
            release.wait(5)
            return False, False
        with patch.object(supervisor.model_readiness, "snapshot", side_effect=slow_verification):
            thread = threading.Thread(target=supervisor.serve)
            thread.start()
            try:
                self.assertTrue(entered.wait(2))
                deadline = time.monotonic() + 2
                while not (supervisor.config.shared / "status.json").exists() and time.monotonic() < deadline:
                    time.sleep(.01)
                self.assertTrue(worker.healthcheck(supervisor.config.shared))
                value = worker.read_json(supervisor.config.shared / "status.json")
                self.assertEqual(set(value), {"version", "updatedAt", "englishAvailable", "romanianAvailable"})
                self.assertFalse(value["englishAvailable"])
                self.assertLess((supervisor.config.shared / "status.json").stat().st_size, 4096)
            finally:
                supervisor.stop.set()
                release.set()
                thread.join(5)
        self.assertFalse(thread.is_alive())
        self.assertFalse((supervisor.config.shared / "status.json").exists())

    def test_healthcheck_rejects_missing_stale_future_invalid_and_oversize_status(self):
        supervisor = self.supervisor()
        path = supervisor.config.shared / "status.json"
        self.assertFalse(worker.healthcheck(supervisor.config.shared))
        base = {"version": 1, "updatedAt": datetime.now(timezone.utc).isoformat(),
                "englishAvailable": False, "romanianAvailable": False}
        worker.atomic_json(path, base)
        self.assertTrue(worker.healthcheck(supervisor.config.shared))
        for status in ({}, base | {"version": True}, base | {"englishAvailable": "false"},
                       base | {"updatedAt": (datetime.now(timezone.utc) - timedelta(seconds=31)).isoformat()},
                       base | {"updatedAt": (datetime.now(timezone.utc) + timedelta(seconds=10)).isoformat()}):
            worker.atomic_json(path, status)
            self.assertFalse(worker.healthcheck(supervisor.config.shared))
        path.write_bytes(b" " * 4097)
        self.assertFalse(worker.healthcheck(supervisor.config.shared))

    def test_supervisor_updates_heartbeat_during_silent_inference(self):
        supervisor = self.supervisor()
        video = self.root / "movie.mkv"
        video.write_bytes(b"video")
        worker.atomic_json(supervisor.requests / ("a" * 32 + ".json"),
                           {"version": 1, "id": "a" * 32, "videoPath": str(video), "romanian": False})
        with patch.object(worker, "HEARTBEAT_INTERVAL", .03), \
             patch.object(supervisor, "child_command", return_value=[sys.executable, "-c", "import time; time.sleep(.3)"]):
            supervisor.run_once()
        value = worker.read_json(supervisor.config.shared / "status.json")
        self.assertTrue(worker.healthcheck(supervisor.config.shared))
        self.assertLess((datetime.now(timezone.utc) - datetime.fromisoformat(value["updatedAt"])).total_seconds(), .2)


if __name__ == "__main__":
    unittest.main()
