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
      2. Bridge over outlier micro-segments by connecting good neighbors
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

    # Step 2: Bridge over outlier micro-segments
    segments = _bridge_outliers(segments, beat_times, max_drift_ms)
    print(f"  After bridging: {len(segments)} segments", file=sys.stderr)

    # Step 3: Consolidate if all segments have similar BPMs.
    # For constant-tempo tracks (EDM/electronic), the segmenter may create
    # many segments because individual beat timing variations exceed the
    # per-beat drift tolerance. But if all segments are within a narrow BPM
    # range, the track is effectively constant-tempo and should be one segment.
    segments = _consolidate_constant_tempo(segments, beat_times)

    # Log final segments with implicit BPMs (what Serato will actually use)
    print(f"  Final: {len(segments)} segment(s):", file=sys.stderr)
    for i, seg in enumerate(segments):
        implicit_bpm = seg["bpm"]
        if i + 1 < len(segments):
            nxt = segments[i + 1]
            span = nxt["start_position"] - seg["start_position"]
            if span > 0 and seg["beat_count"] > 0:
                implicit_bpm = 60.0 * seg["beat_count"] / span
        print(f"    Seg {i+1}: {seg['beat_count']} beats, "
              f"start={seg['start_position']:.3f}s, "
              f"implicit BPM={implicit_bpm:.2f}",
              file=sys.stderr)

    return segments


def _bridge_outliers(segments: list[dict], beat_times: np.ndarray,
                     max_drift_ms: float,
                     min_beats: int = 8, bpm_outlier_pct: float = 5.0
                     ) -> list[dict]:
    """
    Bridge over outlier micro-segments by connecting good neighbors.

    Instead of merging outlier beats INTO neighbors (which can ruin the
    neighbor's grid), we try to extend the previous good segment directly
    to the start of the next good segment, effectively absorbing the
    outlier beats into a larger interpolation grid.

    The bridge is accepted if the Serato interpolation drift for the
    good beats within the bridged segment stays within tolerance.
    The outlier beats may have high drift, but that's OK because they
    were false detections anyway.
    """
    if len(segments) <= 1:
        return segments

    total_beats = sum(s["beat_count"] for s in segments)
    if total_beats == 0:
        return segments
    weighted_bpm = sum(s["bpm"] * s["beat_count"] for s in segments) / total_beats
    max_drift_sec = max_drift_ms / 1000.0

    # Mark which segments are outliers
    is_outlier = []
    for seg in segments:
        outlier = (
            seg["beat_count"] < min_beats and
            abs(seg["bpm"] - weighted_bpm) / weighted_bpm * 100 > bpm_outlier_pct
        )
        is_outlier.append(outlier)

    # Log outliers
    for i, (seg, out) in enumerate(zip(segments, is_outlier)):
        if out:
            print(f"    Outlier seg {i+1}: {seg['bpm']:.1f} BPM, "
                  f"{seg['beat_count']} beats @ {seg['start_position']:.1f}s",
                  file=sys.stderr)

    # Build result by bridging over consecutive outliers
    result = []
    i = 0
    while i < len(segments):
        if is_outlier[i]:
            # Find the end of this outlier run
            j = i
            while j < len(segments) and is_outlier[j]:
                j += 1

            # We have outlier segments from i to j-1
            # Try to bridge: extend previous good segment to next good segment
            prev_good = result[-1] if result else None
            next_good = segments[j] if j < len(segments) else None

            bridged = False
            if prev_good is not None and next_good is not None:
                # Try bridging from prev_good's start to next_good's start
                bridge_start = prev_good["start_beat_index"]
                bridge_end = next_good["start_beat_index"]
                bridge_count = bridge_end - bridge_start

                if bridge_count >= 2:
                    # Check drift of Serato grid, but only for beats that
                    # were in good segments (not the outlier beats)
                    drift = _serato_max_drift_good_only(
                        beat_times, bridge_start, bridge_end,
                        segments[i]["start_beat_index"],
                        segments[j-1]["end_beat_index"]
                    )

                    if drift <= max_drift_sec * 2:  # Allow 2x tolerance for bridging
                        new_bpm = _implicit_bpm(beat_times, bridge_start, bridge_end)
                        prev_good["end_beat_index"] = bridge_end
                        prev_good["beat_count"] = bridge_count
                        prev_good["bpm"] = round(float(new_bpm), 2)
                        print(f"    Bridged over {j-i} outlier seg(s) "
                              f"({segments[i]['start_position']:.1f}s-"
                              f"{segments[j-1]['start_position']:.1f}s), "
                              f"drift={drift*1000:.1f}ms", file=sys.stderr)
                        bridged = True

            if not bridged:
                # Can't bridge to good neighbors. Combine consecutive
                # outlier segments into a single transition segment to
                # reduce wild BPM jumps (one marker at ~140 BPM is much
                # better than three at 161/143/139).
                if j - i > 1:
                    first_out = segments[i]
                    last_out = segments[j - 1]
                    combined_start = first_out["start_beat_index"]
                    combined_end = last_out["end_beat_index"]
                    combined_bpm = _implicit_bpm(beat_times, combined_start, combined_end)
                    combined = {
                        "start_beat_index": combined_start,
                        "end_beat_index": combined_end,
                        "start_position": first_out["start_position"],
                        "bpm": round(float(combined_bpm), 2),
                        "beat_count": combined_end - combined_start,
                    }
                    result.append(combined)
                    print(f"    Combined {j-i} outlier seg(s) into 1 transition "
                          f"({combined_bpm:.1f} BPM, {combined['beat_count']} beats)",
                          file=sys.stderr)
                else:
                    result.append(segments[i])

            i = j
        else:
            result.append(segments[i])
            i += 1

    return result


def _consolidate_constant_tempo(segments: list[dict], beat_times: np.ndarray,
                                 bpm_range_pct: float = 1.5) -> list[dict]:
    """
    If all non-outlier segments have BPMs within a narrow range, consolidate
    into a single segment. This handles constant-tempo EDM tracks where
    per-beat timing variations create many segments.

    Args:
        bpm_range_pct: Max BPM deviation (%) from weighted average for a
                       segment to be considered "normal" (not an outlier).
                       At least 85% of beats must be in normal segments.
    """
    if len(segments) <= 1:
        return segments

    # Compute weighted average BPM
    total_beats = sum(s["beat_count"] for s in segments)
    if total_beats == 0:
        return segments
    weighted_bpm = sum(s["bpm"] * s["beat_count"] for s in segments) / total_beats

    # Classify segments as normal or outlier based on BPM
    normal_beats = 0
    for seg in segments:
        deviation_pct = abs(seg["bpm"] - weighted_bpm) / weighted_bpm * 100
        if deviation_pct <= bpm_range_pct:
            normal_beats += seg["beat_count"]

    normal_ratio = normal_beats / total_beats

    if normal_ratio < 0.85:
        # Too many beats in outlier segments - not truly constant tempo
        return segments

    # Check Serato grid drift for only "normal" beats (skip outlier segments).
    # For constant-tempo tracks with noisy detection, normal beat drift is low.
    # For genuinely variable-tempo tracks (live drummer), drift is high.
    first = segments[0]
    last = segments[-1]
    start_idx = first["start_beat_index"]
    end_idx = last["end_beat_index"]
    total_beat_count = end_idx - start_idx
    total_span = beat_times[end_idx] - beat_times[start_idx]

    if total_span <= 0 or total_beat_count <= 0:
        return segments

    # Identify beat indices that are in outlier segments
    outlier_beats = set()
    for seg in segments:
        deviation_pct = abs(seg["bpm"] - weighted_bpm) / weighted_bpm * 100
        if deviation_pct > bpm_range_pct:
            for bi in range(seg["start_beat_index"], seg["end_beat_index"] + 1):
                outlier_beats.add(bi)

    # Compute max drift of normal beats against the consolidated grid
    n = end_idx - start_idx
    start_pos = beat_times[start_idx]
    end_pos = beat_times[end_idx]
    interval = (end_pos - start_pos) / n
    max_drift = 0.0
    for k in range(1, n):
        beat_idx = start_idx + k
        if beat_idx in outlier_beats:
            continue
        expected = start_pos + k * interval
        actual = beat_times[beat_idx]
        drift = abs(actual - expected)
        if drift > max_drift:
            max_drift = drift
    max_drift_ms = max_drift * 1000

    # Allow up to 40ms drift for consolidation (2x the per-segment tolerance)
    # This accepts noisy detection (EDM tracks with ~10-20ms jitter) but
    # rejects genuinely variable tempo (live drummers with 50ms+ drift)
    if max_drift_ms > 40.0:
        print(f"  Consolidation: BPM range OK but normal-beat drift={max_drift_ms:.1f}ms "
              f"(>{40.0}ms), keeping {len(segments)} segments",
              file=sys.stderr)
        return segments

    overall_bpm = 60.0 * total_beat_count / total_span

    print(f"  Consolidating {len(segments)} segments into 1 "
          f"({normal_ratio*100:.0f}% of beats within ±{bpm_range_pct}% "
          f"of {weighted_bpm:.1f} BPM, drift={max_drift_ms:.1f}ms, "
          f"overall={overall_bpm:.2f} BPM)",
          file=sys.stderr)

    return [{
        "start_beat_index": first["start_beat_index"],
        "end_beat_index": last["end_beat_index"],
        "start_position": first["start_position"],
        "bpm": round(float(overall_bpm), 2),
        "beat_count": total_beat_count,
    }]


def _serato_max_drift_good_only(beat_times: np.ndarray, start_idx: int,
                                 end_idx: int, outlier_start: int,
                                 outlier_end: int) -> float:
    """
    Max drift of Serato interpolation grid, ignoring beats within the
    outlier range (since those are false detections).
    """
    n = end_idx - start_idx
    if n < 2:
        return 0.0
    start_pos = beat_times[start_idx]
    end_pos = beat_times[end_idx]
    interval = (end_pos - start_pos) / n

    max_drift = 0.0
    for k in range(1, n):
        beat_idx = start_idx + k
        # Skip beats in the outlier range
        if outlier_start <= beat_idx <= outlier_end:
            continue
        expected = start_pos + k * interval
        actual = beat_times[beat_idx]
        drift = abs(actual - expected)
        if drift > max_drift:
            max_drift = drift

    return max_drift


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


def _serato_max_drift(beat_times: np.ndarray, start_idx: int,
                      end_idx: int) -> float:
    """Max drift of any intermediate beat from Serato's interpolation grid."""
    n = end_idx - start_idx
    if n < 2:
        return 0.0
    start_pos = beat_times[start_idx]
    end_pos = beat_times[end_idx]
    interval = (end_pos - start_pos) / n
    k = np.arange(1, n)
    expected = start_pos + k * interval
    actual = beat_times[start_idx + 1:end_idx]
    return float(np.max(np.abs(actual - expected)))


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
