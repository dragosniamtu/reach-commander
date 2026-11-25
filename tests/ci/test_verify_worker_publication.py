import unittest
from unittest.mock import patch


class WorkerPublicationTests(unittest.TestCase):
    def test_rejects_nonapproved_or_mutable_worker_reference(self):
        from tools.verify_worker_publication import validate_worker_reference
        reference = "ghcr.io/dragosniamtu/reach-commander@sha256:" + "a" * 64
        self.assertEqual(reference, validate_worker_reference(reference))
        for invalid in (reference + "\n", reference.replace("dragosniamtu", "attacker"),
                        "ghcr.io/dragosniamtu/reach-commander:subtitles-stable"):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                validate_worker_reference(invalid)

    def test_requires_both_runnable_platforms_and_per_platform_attestations(self):
        from tools.verify_worker_publication import runnable_manifests
        runnable = [{"digest": "sha256:" + character * 64,
                     "platform": {"os": "linux", "architecture": arch}}
                    for character, arch in (("a", "amd64"), ("b", "arm64"))]
        attestations = [{"platform": {"os": "unknown", "architecture": "unknown"},
                         "annotations": {"vnd.docker.reference.type": "attestation-manifest",
                                         "vnd.docker.reference.digest": item["digest"]}}
                        for item in runnable]
        self.assertEqual(runnable, runnable_manifests({"manifests": runnable + attestations}))
        for entries in (runnable, runnable[:1] + attestations, runnable + attestations[:1]):
            with self.assertRaises(ValueError):
                runnable_manifests({"manifests": entries})


if __name__ == "__main__":
    unittest.main()
