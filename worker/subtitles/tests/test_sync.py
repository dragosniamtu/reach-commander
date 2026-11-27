"""Repair protocol and offline CPU alignment tests."""
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import worker


SRT = "1\n00:00:03,000 --> 00:00:04,000\nمرحبا 世界 ș\n\n"


class SyncTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.video = self.root / "film.mkv"
        self.video.touch()
        self.config = worker.Config(self.root / "mail", (self.root,), self.root / "models", self.root / "temp")
        self.request = {"version": 2, "id": "a" * 32, "kind": "synchronize",
                        "videoPath": str(self.video), "subtitleSrt": SRT}

    def test_preapproved_repair_protocol_rejects_invalid_inputs(self):
        validate = getattr(worker, "validate_repair_request", worker.validate_request)
        with self.assertRaisesRegex(worker.WorkerError, "invalid_request"):
            validate(self.request | {"extra": True}, self.request["id"])
        self.assertEqual(validate(self.request, self.request["id"]), self.request)
        self.assertEqual(validate(self.request | {"subtitleSrt": SRT.replace("\n", "\r\n")}, self.request["id"])["id"],
                         self.request["id"])
        for changes in ({"version": True}, {"version": 1}, {"kind": "generate"}, {"id": "b" * 32},
                        {"videoPath": "https://example.org/media.mkv"}, {"subtitleSrt": "not an SRT"},
                        {"subtitleSrt": SRT.replace("04,000", "02,000")}, {"subtitleSrt": SRT + "ы" * (2**21)},
                        {"subtitleSrt": "".join(SRT.replace("1\n", f"{index}\n", 1) for index in range(1, 20002))},
                        {"subtitleSrt": SRT + "\ud800"}):
            with self.subTest(keys=list(changes)), self.assertRaises(worker.WorkerError):
                validate(self.request | changes, self.request["id"])

    def test_repair_heartbeat_is_independent_of_models(self):
        supervisor = worker.Supervisor(self.config)
        module = getattr(worker, "subtitle_sync", None)
        self.assertIsNotNone(module)
        with patch.object(module, "available", side_effect=[True, False]):
            for ready in (True, False):
                supervisor.next_heartbeat = 0
                supervisor.publish_status()
                repair = worker.read_json(self.config.shared / "repair-status.json")
                self.assertEqual(set(repair), {"version", "updatedAt", "synchronizationAvailable"})
                self.assertEqual(repair["version"], 1)
                self.assertIs(repair["synchronizationAvailable"], ready)
                generation = worker.read_json(self.config.shared / "status.json")
                self.assertFalse(generation["englishAvailable"])

    def test_repair_returns_direct_transform_and_removes_extracted_audio(self):
        execute = getattr(worker, "execute_repair", None)
        self.assertIsNotNone(execute)
        def extract(source, audio, *_):
            audio.write_bytes(b"private")

        with patch.object(worker, "probe_video", return_value=({"index": 0}, 9)), \
             patch.object(worker, "extract_audio", side_effect=extract), \
             patch.object(worker.subtitle_sync, "available", return_value=True), \
             patch.object(worker.subtitle_sync, "estimate", side_effect=[(-720, 1.04), ValueError()]):
            finished = execute(self.request, self.config, self.root, lambda *_: None)
            self.assertEqual(finished, worker.result_document(self.request["id"], "completed", percent=100)
                             | {"offsetMilliseconds": -720, "timeScale": 1.04})
            self.assertEqual(round(3000 * finished["timeScale"]) + finished["offsetMilliseconds"], 2400)
            self.assertFalse((self.root / "audio.wav").exists())
            failed = execute(self.request, self.config, self.root, lambda *_: None)
            self.assertEqual(failed["errorCode"], "synchronization_failed")
            self.assertFalse((self.root / "audio.wav").exists())

    def test_repair_cancel_marker_has_no_effect_on_generation_queue(self):
        supervisor = worker.Supervisor(self.config)
        self.assertTrue(hasattr(supervisor, "repair_requests"))
        identifier = self.request["id"]
        request_path = supervisor.repair_requests / f"{identifier}.json"
        worker.atomic_json(request_path, self.request)
        request_path.with_suffix(".cancel").touch()
        with patch.object(supervisor, "child_command") as command:
            supervisor.run_once()
            command.assert_not_called()
        self.assertEqual(worker.read_json(supervisor.repair_results / request_path.name)["state"], "cancelled")
        self.assertFalse((supervisor.results / request_path.name).exists())
        self.assertEqual(list(self.config.temp.iterdir()), [])

    def test_running_repair_is_cancelled_after_removing_private_audio(self):
        supervisor = worker.Supervisor(self.config)
        request_path = supervisor.repair_requests / (self.request["id"] + ".json")
        worker.atomic_json(request_path, self.request)
        script = "import pathlib,time,sys; pathlib.Path(sys.argv[1]).write_bytes(b'private'); time.sleep(60)"

        def command(_request, temp):
            return [sys.executable, "-c", script, str(temp / "audio.wav")]

        with patch.object(supervisor, "child_command", side_effect=command):
            thread = threading.Thread(target=supervisor.run_once)
            thread.start()
            try:
                deadline = time.monotonic() + 5
                while not list(self.config.temp.glob("*/audio.wav")) and time.monotonic() < deadline:
                    time.sleep(.02)
                self.assertTrue(list(self.config.temp.glob("*/audio.wav")))
            finally:
                request_path.with_suffix(".cancel").touch()
                thread.join(10)
            self.assertFalse(thread.is_alive())
        self.assertEqual(list(self.config.temp.iterdir()), [])
        self.assertEqual(worker.read_json(supervisor.repair_results / request_path.name)["state"], "cancelled")

    def test_missing_alignment_dependency_fails_before_extraction(self):
        with patch.object(worker.subtitle_sync, "available", return_value=False), \
             patch.object(worker, "probe_video") as probe:
            failed = worker.execute_repair(self.request, self.config, self.root, lambda *_: None)
            self.assertEqual(failed["errorCode"], "alignment_unavailable")
            probe.assert_not_called()

    def test_out_of_range_transforms_never_complete(self):
        with patch.object(worker.subtitle_sync, "available", return_value=True), \
             patch.object(worker, "probe_video", return_value=({"index": 0}, 9)), \
             patch.object(worker, "extract_audio"), \
             patch.object(worker.subtitle_sync, "estimate") as estimate:
            for transform in ((600001, 1), (0, float("nan")), (0, 1.26), (2.5, 1)):
                estimate.return_value = transform
                failed = worker.execute_repair(self.request, self.config, self.root, lambda *_: None)
                self.assertEqual(failed["errorCode"], "synchronization_failed")

    def test_dense_overlapping_cues_reduce_to_disjoint_coverage(self):
        four_hours = 4 * 60 * 60 * 1000
        dense = [(index, four_hours - index) for index in range(19_996, 0, -1)]
        intervals = [*dense, (0, 10), (four_hours - 10, four_hours), (four_hours, four_hours + 100),
                     (four_hours + 200, four_hours + 300)]
        merge = getattr(worker.subtitle_sync, "merge_cue_intervals", list)
        self.assertEqual(merge(intervals), [(0, four_hours + 100), (four_hours + 200, four_hours + 300)])
