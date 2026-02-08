"""
Segment beats into regions of consistent tempo for variable-BPM tracks.

CRITICAL: Verification must match what Serato actually does.
Serato interpolates beats evenly between markers:
  beat[k] = marker_pos + k * (next_marker_pos - marker_pos) / beats_until_next

So we verify against THAT grid (pinned at both endpoints), NOT a
linear-regression grid (which minimizes squared error but isn't anchored
to both ends). The regression grid can differ significantly from Serato's
interpolation, causing cumulative drift across many markers.

Real drummer timing reference:
  - Professional/tight (studio): 1-2 BPM variation
  - Good/standard (solid live):  3-4 BPM variation
  - Live/loose (no click track):  5+ BPM variation
"""

import sys
import numpy as np


def segment_tempo(beat_times: np.ndarray,
                  max_drift_ms: float = 20.0) -> list[dict]:
    """
    Build tempo segments from detected beat positions.

    Pipeline:
      1. Build initial segments (every beat verified against Serato's
         interpolation grid within drift tolerance)
      2. Remove outlier micro-segments (short + BPM far from neighbors)
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

    print(f"  Segmenting {len(beat_times)} beats "
          f"(drift tolerance: {max_drift_ms}ms)...", file=sys.stderr)

    # Step 1: Build initial segments verifying every beat against
    # the Serato interpolation grid (not regression)
    segments = _build_segments(beat_times, max_drift_ms)
    print(f"  Initial: {len(segments)} segments", file=sys.stderr)

    # Step 2: Merge outlier micro-segments
    segments = _merge_outliers(segments, beat_times)
    print(f"  After outlier merge: {len(segments)} segments", file=sys.stderr)

    # Log final segments with implicit BPMs (what Serato will actually use)
    print(f"  Final: {len(segments)} segment(s):", file=sys.stderr)
    for i, seg in enumerate(segments):
        implicit_bpm = seg["bpm"]
        if i + 1 < len(segments):
            # For non-terminal: implicit BPM from positions
            nxt = segments[i + 1]
            span = nxt["start_position"] - seg["start_position"]
            if span > 0 and seg["beat_count"] > 0:
                implicit_bpm = 60.0 * seg["beat_count"] / span
        print(f"    Seg {i+1}: {seg['beat_count']} beats, "
              f"start={seg['start_position']:.3f}s, "
              f"implicit BPM={implicit_bpm:.2f}",
              file=sys.stderr)

    return segments


def _merge_outliers(segments: list[dict], beat_times: np.ndarray,
                    min_beats: int = 8, bpm_outlier_pct: float = 5.0
                    ) -> list[dict]:
    """
    Merge short segments whose BPM is far from the weighted average.
    These are typically false detections in vocal/string/intro sections.

    A segment is an outlier if:
      - It has fewer than min_beats beats AND
      - Its BPM differs from the weighted average by > bpm_outlier_pct %
    """
    if len(segments) <= 1:
        return segments

    total_beats = sum(s["beat_count"] for s in segments)
    if total_beats == 0:
        return segments
    weighted_bpm = sum(s["bpm"] * s["beat_count"] for s in segments) / total_beats

    merged = []
    i = 0
    while i < len(segments):
        seg = segments[i]
        is_outlier = (
            seg["beat_count"] < min_beats and
            abs(seg["bpm"] - weighted_bpm) / weighted_bpm * 100 > bpm_outlier_pct
        )

        if is_outlier and (merged or i + 1 < len(segments)):
            prev_bpm = merged[-1]["bpm"] if merged else None
            next_bpm = segments[i + 1]["bpm"] if i + 1 < len(segments) else None

            merge_into_prev = False
            if prev_bpm is not None and next_bpm is not None:
                merge_into_prev = (abs(prev_bpm - weighted_bpm) <=
                                   abs(next_bpm - weighted_bpm))
            elif prev_bpm is not None:
                merge_into_prev = True

            if merge_into_prev and merged:
                prev = merged[-1]
                new_end = seg["end_beat_index"]
                new_bpm = _implicit_bpm(beat_times, prev["start_beat_index"], new_end)
                prev["end_beat_index"] = new_end
                prev["beat_count"] = new_end - prev["start_beat_index"]
                prev["bpm"] = round(float(new_bpm), 2)
                print(f"    Merged outlier ({seg['bpm']:.1f} BPM, "
                      f"{seg['beat_count']} beats @ {seg['start_position']:.1f}s) "
                      f"<- prev", file=sys.stderr)
            elif i + 1 < len(segments):
                nxt = segments[i + 1]
                new_start_idx = seg["start_beat_index"]
                new_end = nxt["end_beat_index"]
                new_bpm = _implicit_bpm(beat_times, new_start_idx, new_end)
                nxt["start_beat_index"] = new_start_idx
                nxt["start_position"] = round(float(beat_times[new_start_idx]), 6)
                nxt["beat_count"] = new_end - new_start_idx
                nxt["bpm"] = round(float(new_bpm), 2)
                print(f"    Merged outlier ({seg['bpm']:.1f} BPM, "
                      f"{seg['beat_count']} beats @ {seg['start_position']:.1f}s) "
                      f"-> next", file=sys.stderr)
            else:
                merged.append(seg)
        else:
            merged.append(seg)
        i += 1

    return merged


def _build_segments(beat_times: np.ndarray,
                    max_drift_ms: float) -> list[dict]:
    """Build segments verifying every beat against Serato's interpolation grid."""
    max_drift_sec = max_drift_ms / 1000.0
    segments = []
    seg_start = 0

    while seg_start < len(beat_times) - 1:
        seg_end = _find_segment_end(beat_times, seg_start, max_drift_sec)
        bpm = _implicit_bpm(beat_times, seg_start, seg_end)

        segments.append({
            "start_beat_index": int(seg_start),
            "end_beat_index": int(seg_end),
            "start_position": round(float(beat_times[seg_start]), 6),
            "bpm": round(float(bpm), 2),
            "beat_count": int(seg_end - seg_start),
        })

        seg_start = seg_end

    # Handle last beat if it's a standalone
    if seg_start == len(beat_times) - 1 and (not segments or
            segments[-1]["end_beat_index"] < len(beat_times) - 1):
        bpm = segments[-1]["bpm"] if segments else 120.0
        segments.append({
            "start_beat_index": int(seg_start),
            "end_beat_index": int(seg_start),
            "start_position": round(float(beat_times[seg_start]), 6),
            "bpm": round(float(bpm), 2),
            "beat_count": 1,
        })

    return segments


def _find_segment_end(beat_times: np.ndarray, start: int,
                      max_drift_sec: float) -> int:
    """
    Find the furthest beat index from 'start' that can be covered by
    Serato's evenly-interpolated grid within drift tolerance.

    Serato interpolates beats between markers:
      grid[k] = start_pos + k * (end_pos - start_pos) / beat_count

    Both endpoints (k=0 and k=beat_count) are pinned with 0 drift.
    We only check intermediate beats (k=1..beat_count-1).
    """
    best_end = start + 1

    for candidate_end in range(start + 2, len(beat_times)):
        n = candidate_end - start  # number of intervals (= beat_count)
        start_pos = beat_times[start]
        end_pos = beat_times[candidate_end]
        interval = (end_pos - start_pos) / n

        # Check intermediate beats against Serato's interpolated grid
        # k=0 is start_pos (pinned), k=n is end_pos (pinned)
        k = np.arange(1, n)
        expected = start_pos + k * interval
        actual = beat_times[start + 1:candidate_end]
        max_drift = float(np.max(np.abs(actual - expected)))

        if max_drift > max_drift_sec:
            break
        best_end = candidate_end

    return best_end


def _implicit_bpm(beat_times: np.ndarray, start_idx: int,
                  end_idx: int) -> float:
    """
    Compute the implicit BPM that Serato would use for a segment.
    This is what Serato actually sees: 60 * beat_count / time_span.
    """
    if end_idx <= start_idx:
        return 120.0
    span = beat_times[end_idx] - beat_times[start_idx]
    if span <= 0:
        return 120.0
    beat_count = end_idx - start_idx
    return 60.0 * beat_count / span


def _best_fit_bpm(seg_beats: np.ndarray) -> float:
    """Compute best-fit BPM via linear regression (for terminal marker only)."""
    n = len(seg_beats)
    if n < 2:
        return 120.0

    indices = np.arange(n, dtype=np.float64)
    b, a = np.polyfit(indices, seg_beats, 1)

    if b <= 0:
        span = seg_beats[-1] - seg_beats[0]
        b = span / (n - 1) if n > 1 else 0.5

    return 60.0 / b
