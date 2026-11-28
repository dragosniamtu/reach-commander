"""Real offline CPU inference must leave silent gaps between generated captions."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import wave

worker_path = Path(__file__).resolve().parents[1] / "worker/subtitles"
sys.path.insert(0, str(worker_path if worker_path.is_dir() else Path("/app")))
import worker


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", type=Path, required=True)
    args = parser.parse_args()
    worker.offline_environment()
    with tempfile.TemporaryDirectory(prefix="subtitle-timing-") as temporary:
        root = Path(temporary)
        phrases = []
        for index, text in enumerate(("The first sentence ends here.",
                                      "After a quiet pause the second sentence begins.")):
            path = root / f"phrase-{index}.wav"
            subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-n", "-f", "lavfi", "-i",
                            f"flite=text={text}:voice=slt", "-ar", "16000", "-ac", "1",
                            "-c:a", "pcm_s16le", str(path)], check=True, timeout=30)
            with wave.open(str(path), "rb") as reader:
                phrases.append(reader.readframes(reader.getnframes()))
        first_end = 1 + len(phrases[0]) / 32000
        second_start = first_end + 8
        second_end = second_start + len(phrases[1]) / 32000
        audio = root / "pauses.wav"
        with wave.open(str(audio), "wb") as writer:
            writer.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
            writer.writeframes(bytes(32000) + phrases[0] + bytes(8 * 32000) +
                               phrases[1] + bytes(6 * 32000))
        config = worker.Config(root / "mailbox", (root,), args.models.resolve(), root, threads=2)
        model = worker.load_whisper(config)
        cues = worker.transcribe_english(model, audio, second_end + 6, lambda *_: None)
        del model
        first_cues = [cue for cue in cues if cue.start < first_end + 4]
        second_cues = [cue for cue in cues if cue.start >= first_end + 4]
        assert first_cues and second_cues, "Both spoken phrases must be transcribed"
        assert all(cue.end <= first_end + .5 for cue in first_cues), \
            "First caption persisted into the eight-second silence"
        assert all(cue.end <= second_end + .5 for cue in second_cues), \
            "Final caption persisted into trailing silence"
        for silent_time in (first_end + 4, second_end + 3):
            assert not any(cue.start <= silent_time < cue.end for cue in cues), \
                "A caption is active during silence"
        romanian = worker.translate_romanian(cues, config, lambda *_: None)
        english_srt, romanian_srt = worker.render_subtitles(cues, romanian)
        en_timings = [line for line in english_srt.splitlines() if " --> " in line]
        ro_timings = [line for line in romanian_srt.splitlines() if " --> " in line]
        assert en_timings == ro_timings, "Romanian subtitles must retain the corrected ends"
        for cue in (first_cues[-1], second_cues[-1]):
            buffered_end = worker.timestamp(round(cue.end * 1000) + 1000)
            assert any(timing.endswith(f" --> {buffered_end}") for timing in en_timings), \
                "Rendered captions must include one extra second before the long silence"
        assert english_srt != romanian_srt, "Real Romanian translation must run"
        print(json.dumps({"state": "passed", "timings": en_timings,
                          "silentGapSeconds": 8, "readingBufferSeconds": 1,
                          "romanianTimingsMatch": True}))


if __name__ == "__main__":
    main()
