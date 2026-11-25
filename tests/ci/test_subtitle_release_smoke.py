import importlib.util
from pathlib import Path
import tempfile
import unittest


MODULE_PATH = Path(__file__).resolve().parents[2] / "tools" / "subtitle_release_smoke.py"


class SubtitleSmokeValidationTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(MODULE_PATH.is_file(), "real inference smoke validator is missing")
        spec = importlib.util.spec_from_file_location("subtitle_release_smoke", MODULE_PATH)
        self.smoke = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.smoke)

    def test_accepts_readable_bilingual_cues_with_identical_timing(self):
        self.smoke.validate_pair("1\n00:00:00,000 --> 00:00:01,000\nHello!\n\n",
                                 "1\n00:00:00,000 --> 00:00:01,000\nBună!\n\n")

    def test_rejects_empty_overlapping_and_unreadable_output(self):
        valid = "1\n00:00:00,000 --> 00:00:01,000\nHello!\n\n"
        for text in ("", valid + "2\n00:00:00,500 --> 00:00:02,000\nBad\n\n",
                     "1\n00:00:00,000 --> 00:00:01,000\n" + "x" * 43 + "\n\n"):
            with self.subTest(text=text), self.assertRaises(AssertionError):
                self.smoke.validate_pair(text, text)

    def test_rejects_mismatched_timing_and_missing_translation(self):
        english = "1\n00:00:00,000 --> 00:00:01,000\nHello!\n\n"
        for romanian in (english.replace("01,000", "02,000"), english, None):
            with self.subTest(romanian=romanian), self.assertRaises(AssertionError):
                self.smoke.validate_pair(english, romanian)

    def test_preservation_detects_changed_video_or_original_backup(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            video = root / "speech.mp4"
            video.write_bytes(b"video")
            original = b"original subtitles"
            backup = root / "speech.existing-20261125-001.srt.bak"
            backup.write_bytes(original)
            digest = self.smoke.file_hash(video)
            self.smoke.assert_preserved(video, digest, original)
            backup.write_bytes(b"changed")
            with self.assertRaises(AssertionError):
                self.smoke.assert_preserved(video, digest, original)
            backup.write_bytes(original)
            video.write_bytes(b"changed")
            with self.assertRaises(AssertionError):
                self.smoke.assert_preserved(video, digest, original)


if __name__ == "__main__":
    unittest.main()
