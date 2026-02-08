"""Segment beats into regions of consistent tempo for variable-BPM tracks."""

import sys
import numpy as np

# Serato has practical limits on marker count. Tracks with too many markers
# either fail to load or display incorrectly. Real Serato files typically
# have 1-10 markers; we allow up to 32 for heavily variable-tempo tracks.
MAX_SERATO_MARKERS = 32


def segment_tempo(beat_times: np.ndarray,
                  max_drift_ms: float = 20.0) -> list[dict]:
    """
    Given an array of beat timestamps, build tempo segments by verifying
    every beat's position against the segment's grid.

    If the initial pass produces too many segments (> MAX_SERATO_MARKERS),
    progressively relaxes tolerance until we're within limits.

    Args:
        beat_times: Sorted array of beat positions in seconds.
        max_drift_ms: Starting maximum allowed drift in ms per beat.

    Returns:
        List of segment dicts with keys:
            start_beat_index, end_beat_index, start_position, bpm, beat_count
    """
    if len(beat_times) < 2:
        if len(beat_times) == 1:
            return [{
                "start_beat_index": 0,
                "end_beat_index": 0,
                "start_position": float(beat_times[0]),
                "bpm": 120.0,
                "beat_count": 1,
            }]
        return []

    # Try segmenting, relaxing tolerance if too many segments
    tolerance = max_drift_ms
    for attempt in range(10):
        segments = _build_segments(beat_times, tolerance)
        if len(segments) <= MAX_SERATO_MARKERS:
            break
        old_tolerance = tolerance
        tolerance *= 1.5
        print(f"  {len(segments)} segments exceeds limit of {MAX_SERATO_MARKERS}, "
              f"relaxing tolerance {old_tolerance:.0f}ms -> {tolerance:.0f}ms",
              file=sys.stderr)

    print(f"  Final: {len(segments)} segment(s) (tolerance: {tolerance:.0f}ms):",
          file=sys.stderr)
    for i, seg in enumerate(segments):
        print(f"    Seg {i+1}: {seg['bpm']:.2f} BPM, "
              f"{seg['beat_count']} beats, "
              f"start={seg['start_position']:.3f}s, "
              f"max_drift={seg.get('_max_drift_ms', 0):.1f}ms",
              file=sys.stderr)

    # Strip internal debug keys before returning
    for seg in segments:
        seg.pop("_max_drift_ms", None)

    return segments


def _build_segments(beat_times: np.ndarray,
                    max_drift_ms: float) -> list[dict]:
    """Build segments with the given drift tolerance."""
    max_drift_sec = max_drift_ms / 1000.0
    segments = []
    seg_start = 0

    while seg_start < len(beat_times) - 1:
        seg_end = _find_segment_end(beat_times, seg_start, max_drift_sec)
        seg_beats = beat_times[seg_start:seg_end + 1]

        if len(seg_beats) >= 2:
            bpm = _best_fit_bpm(seg_beats)
        else:
            if seg_start + 1 < len(beat_times):
                bpm = 60.0 / (beat_times[seg_start + 1] - beat_times[seg_start])
            else:
                bpm = segments[-1]["bpm"] if segments else 120.0

        max_actual_drift = _max_segment_drift(seg_beats, bpm)

        segments.append({
            "start_beat_index": int(seg_start),
            "end_beat_index": int(seg_end),
            "start_position": round(float(beat_times[seg_start]), 6),
            "bpm": round(float(bpm), 2),
            "beat_count": int(seg_end - seg_start),
            "_max_drift_ms": round(max_actual_drift * 1000, 1),
        })

        seg_start = seg_end

    # Handle last beat
    if seg_start == len(beat_times) - 1 and (not segments or
            segments[-1]["end_beat_index"] < len(beat_times) - 1):
        bpm = segments[-1]["bpm"] if segments else 120.0
        segments.append({
            "start_beat_index": int(seg_start),
            "end_beat_index": int(seg_start),
            "start_position": round(float(beat_times[seg_start]), 6),
            "bpm": round(float(bpm), 2),
            "beat_count": 1,
            "_max_drift_ms": 0.0,
        })

    return segments


def _find_segment_end(beat_times: np.ndarray, start: int,
                      max_drift_sec: float) -> int:
    """
    Find the furthest beat index from 'start' that can be covered by a
    single evenly-spaced grid within drift tolerance.
    """
    best_end = start + 1

    for candidate_end in range(start + 2, len(beat_times)):
        seg_beats = beat_times[start:candidate_end + 1]
        bpm = _best_fit_bpm(seg_beats)
        max_drift = _max_segment_drift(seg_beats, bpm)

        if max_drift > max_drift_sec:
            break
        best_end = candidate_end

    return best_end


def _best_fit_bpm(seg_beats: np.ndarray) -> float:
    """
    Compute best-fit BPM via linear regression.
    beat_time[i] ≈ start_time + i * (60/BPM)
    """
    n = len(seg_beats)
    if n < 2:
        return 120.0

    indices = np.arange(n, dtype=np.float64)
    b, a = np.polyfit(indices, seg_beats, 1)

    if b <= 0:
        span = seg_beats[-1] - seg_beats[0]
        b = span / (n - 1) if n > 1 else 0.5

    return 60.0 / b


def _max_segment_drift(seg_beats: np.ndarray, bpm: float) -> float:
    """Max absolute drift of any beat from the evenly-spaced grid."""
    if len(seg_beats) < 2:
        return 0.0

    beat_period = 60.0 / bpm
    start = seg_beats[0]
    drifts = np.abs(seg_beats - (start + np.arange(len(seg_beats)) * beat_period))
    return float(np.max(drifts))
