"""Estimate a global SRT timing transform from private PCM and cue intervals."""
from importlib.metadata import version
import math
import re
import wave


FFSUBSYNC_VERSION = "0.4.31"
MAX_SRT_BYTES = 4 * 1024 * 1024
MAX_CUES = 20000
MAX_OFFSET_MS = 600000
MIN_SCALE, MAX_SCALE = 0.8, 1.25
FRAME_MS = 10
PCM_HZ = 16000
PCM_FRAME = PCM_HZ * FRAME_MS // 1000
TIMESTAMP = r"([0-9]{2}):([0-5][0-9]):([0-5][0-9]),([0-9]{3})"
TIMING = re.compile(f"{TIMESTAMP} --> {TIMESTAMP}")


class DependencyUnavailable(Exception):
    pass


def dependencies():
    try:
        if version("ffsubsync") != FFSUBSYNC_VERSION:
            raise ValueError("Unexpected ffsubsync version")
        import numpy
        import webrtcvad
        from ffsubsync.aligners import FFTAligner
        from ffsubsync.constants import FRAMERATE_RATIOS
        return numpy, webrtcvad.Vad, FFTAligner, FRAMERATE_RATIOS
    except Exception as error:
        raise DependencyUnavailable() from error


def available():
    try:
        dependencies()
        return True
    except DependencyUnavailable:
        return False


def cue_intervals(text):
    if (not isinstance(text, str) or len(text) > MAX_SRT_BYTES or len(text.encode("utf-8")) > MAX_SRT_BYTES
            or "\0" in text):
        raise ValueError("Invalid subtitle input")
    normalized = text.replace("\r\n", "\n").rstrip("\n")
    if "\r" in normalized:
        raise ValueError("Invalid line ending")
    cues = normalized.split("\n\n", MAX_CUES)
    if len(cues) > MAX_CUES:
        raise ValueError("Too many subtitle cues")
    intervals = []
    for index, cue in enumerate(cues, 1):
        lines = cue.split("\n", 2)
        if len(lines) != 3 or lines[0] != str(index) or not lines[2].strip():
            raise ValueError("Invalid subtitle cue")
        timing = TIMING.fullmatch(lines[1])
        if timing is None:
            raise ValueError("Invalid cue timestamp")
        h1, m1, s1, ms1, h2, m2, s2, ms2 = map(int, timing.groups())
        start = ((h1 * 60 + m1) * 60 + s1) * 1000 + ms1
        end = ((h2 * 60 + m2) * 60 + s2) * 1000 + ms2
        if end <= start:
            raise ValueError("Invalid cue duration")
        intervals.append((start, end))
    return intervals


def voice_frames(audio, np, vad):
    with wave.open(str(audio), "rb") as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) != (1, 2, PCM_HZ):
            raise ValueError("Unexpected PCM format")
        frame_count = wav.getnframes() // PCM_FRAME

        def frames():
            for _ in range(frame_count):
                yield vad.is_speech(wav.readframes(PCM_FRAME), PCM_HZ)

        speech = np.fromiter(frames(), dtype=np.bool_, count=frame_count)
    if min(int(speech.sum()), int((~speech).sum())) < 20:
        raise ValueError("Insufficient speech activity")
    return speech


def merge_cue_intervals(intervals):
    """Merge overlapping and adjacent intervals before rasterizing at any rate."""
    merged = []
    for start, end in sorted(intervals):
        if merged and start <= merged[-1][1]:
            first, last = merged[-1]
            merged[-1] = (first, max(last, end))
        else:
            merged.append((start, end))
    return merged


def estimate(audio, intervals):
    np, vad, aligner_class, frame_rate_ratios = dependencies()
    speech = voice_frames(audio, np, vad(3))
    duration_ms = len(speech) * FRAME_MS
    coverage_intervals = merge_cue_intervals(intervals)
    last_cue_ms = coverage_intervals[-1][1]
    if last_cue_ms * MIN_SCALE > duration_ms + MAX_OFFSET_MS:
        raise ValueError("Cue outside the alignment range")

    def search(frame_ms, scales):
        stride = frame_ms // FRAME_MS
        usable = len(speech) // stride * stride
        reference = speech[:usable].reshape(-1, stride).mean(axis=1)
        # FFTAligner centers its inputs around 0.5. Zero-mean real audio avoids
        # rewarding long digital silence and neutral padding enables every offset.
        reference = (reference - reference.mean() + 1) / 2
        min_length = MAX_OFFSET_MS // frame_ms + 2
        padded_reference = np.pad(reference, (0, max(0, min_length - len(reference))), constant_values=.5)
        aligner = aligner_class(max_offset_samples=min_length - 1)
        reference_energy = math.sqrt(np.square(2 * reference - 1).sum())
        best = (-math.inf, 1.0, 0)
        for scale in scales:
            cue_frames = max(1, round(last_cue_ms * scale / frame_ms))
            candidate = np.zeros(max(cue_frames, len(reference)))
            for start, end in coverage_intervals:
                candidate[round(start * scale / frame_ms):round(end * scale / frame_ms)] = 1
            coverage = candidate.mean()
            if coverage <= 0 or coverage >= 1 or reference_energy <= 0:
                continue
            candidate_energy = math.sqrt(len(candidate) * coverage * (1 - coverage))
            candidate = (candidate - coverage + 1) / 2
            candidate = np.pad(candidate, (0, max(0, min_length - len(candidate))), constant_values=.5)
            score, offset = aligner.fit_transform(padded_reference, candidate, get_score=True)
            score /= reference_energy * candidate_energy
            offset_ms = offset * frame_ms
            if (abs(offset_ms) <= MAX_OFFSET_MS and
                    (score, -abs(scale - 1)) > (best[0], -abs(best[1] - 1))):
                best = (score, float(scale), offset_ms)
        return best

    # Locate the best region first at lower resolution, then refine the scale
    # and offset using the native WebRTC 10 ms steps.
    if len(intervals) == 1:
        score, scale, offset_ms = search(FRAME_MS, [1.0])
    else:
        common = [1.0, *frame_rate_ratios, *(1 / ratio for ratio in frame_rate_ratios)]
        _, scale, _ = search(50, [*common, *np.linspace(MIN_SCALE, MAX_SCALE, 19)])
        def around(center, radius):
            return sorted({center, *np.linspace(max(MIN_SCALE, center - radius), min(MAX_SCALE, center + radius), 11)})

        _, scale, _ = search(50, around(scale, .025))
        score, scale, offset_ms = search(FRAME_MS, around(scale, .005))
        if last_cue_ms > 60000:
            score, scale, offset_ms = search(FRAME_MS, around(scale, .001))
    if not math.isfinite(score) or score < .1:
        raise ValueError("Audio and subtitle activity do not align")
    return offset_ms, scale
