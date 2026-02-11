"""
Entry point for the Gridder analysis engine.

Usage: python -m gridder_analysis <audio_file_path> [--first-beat SECONDS]

Outputs JSON to stdout with beat positions, tempo segments, and waveform data.
Progress messages go to stderr.
"""

import argparse
import json
import sys
import os

def main():
    parser = argparse.ArgumentParser(description="Gridder beat analysis engine")
    parser.add_argument("audio_path", help="Path to audio file (MP3 or FLAC)")
    parser.add_argument("--first-beat", type=float, default=None,
                        help="Override first beat position in seconds")
    args = parser.parse_args()

    audio_path = args.audio_path
    first_beat_override = args.first_beat

    if not os.path.exists(audio_path):
        print(f"Error: File not found: {audio_path}", file=sys.stderr)
        sys.exit(1)

    print(f"Analyzing: {os.path.basename(audio_path)}", file=sys.stderr)

    # Import here to give faster error on missing deps
    try:
        import numpy as np
        import librosa
    except ImportError as e:
        print(f"Error: Missing dependency: {e}", file=sys.stderr)
        print("Install with: pip install librosa numpy soundfile", file=sys.stderr)
        sys.exit(2)

    from .beat_detector import detect_beats
    from .mp3_utils import get_mp3_encoder_delay, read_serato_beatgrid, reconstruct_serato_beats
    from .tempo_segmenter import segment_tempo
    from .waveform_generator import generate_waveform

    # Step 1: Load audio
    print("Loading audio...", file=sys.stderr)
    try:
        y, sr = librosa.load(audio_path, sr=44100, mono=True)
    except Exception as e:
        print(f"Error loading audio: {e}", file=sys.stderr)
        sys.exit(3)

    duration = len(y) / sr
    print(f"  Duration: {duration:.1f}s, Sample rate: {sr}Hz", file=sys.stderr)

    # Step 2: Detect beats (madmom primary, librosa fallback)
    beat_times, detector = detect_beats(audio_path, y, sr)
    print(f"  Beat detector: {detector}", file=sys.stderr)

    if len(beat_times) == 0:
        print("Warning: No beats detected!", file=sys.stderr)
        beat_times = np.array([0.0])

    # Step 2a (override): If user specified --first-beat, snap to nearest
    # detected beat (or insert) and discard everything before it.
    if first_beat_override is not None:
        # Find nearest detected beat within 50ms
        distances = np.abs(beat_times - first_beat_override)
        nearest_idx = int(np.argmin(distances))
        nearest_dist = float(distances[nearest_idx])
        nearest_pos = float(beat_times[nearest_idx])

        if nearest_dist <= 0.050:
            print(f"  First beat override: {first_beat_override:.3f}s "
                  f"(nearest detected: {nearest_pos:.3f}s, dist={nearest_dist*1000:.1f}ms)",
                  file=sys.stderr)
            beat_times = beat_times[nearest_idx:]
        else:
            # No detected beat close enough — insert user's position
            print(f"  First beat override: {first_beat_override:.3f}s "
                  f"(inserted, nearest was {nearest_pos:.3f}s at {nearest_dist*1000:.1f}ms)",
                  file=sys.stderr)
            beat_times = beat_times[beat_times >= first_beat_override]
            beat_times = np.insert(beat_times, 0, first_beat_override)

        n_discarded = len(distances) - len(beat_times)
        if n_discarded > 0:
            print(f"  Discarded {n_discarded} beat(s) before override position",
                  file=sys.stderr)

        # Skip steps 2b (weak beat trimming) and 2c (extrapolation)
        # — the user explicitly chose the start position
        print("  Skipping weak-beat trimming and extrapolation (user override)",
              file=sys.stderr)
    else:
        # Step 2b: Trim weak leading beats (quiet percussion/hi-hat intros).
        # Many tracks have a quiet hi-hat or percussion pattern before the
        # actual kick drum comes in. librosa detects beats on these, but
        # the grid should start at the first strong drum beat.
        if len(beat_times) >= 4:
            window = int(0.03 * sr)  # 30ms window around each beat
            beat_rms = np.array([
                float(np.sqrt(np.mean(
                    y[max(0, int(b * sr) - window):int(b * sr) + window] ** 2
                ))) for b in beat_times
            ])

            # Use the median RMS of the louder half of beats as reference
            # (avoids being skewed by quiet intro beats)
            sorted_rms = np.sort(beat_rms)
            upper_half = sorted_rms[len(sorted_rms) // 2:]
            strong_beat_rms = float(np.median(upper_half))

            # Threshold: a beat must be at least 20% of the typical strong
            # beat energy to count. This filters out quiet hi-hats/percussion
            # while keeping real kick/snare hits.
            energy_threshold = strong_beat_rms * 0.20

            first_strong = 0
            for i, rms in enumerate(beat_rms):
                if rms >= energy_threshold:
                    first_strong = i
                    break

            if first_strong > 0:
                trimmed = beat_times[:first_strong]
                beat_times = beat_times[first_strong:]
                print(f"  Trimmed {len(trimmed)} weak intro beat(s) "
                      f"(energy < {energy_threshold:.4f}, threshold={strong_beat_rms:.4f})",
                      file=sys.stderr)
                print(f"  First beat: {float(beat_times[0]):.3f}s "
                      f"(was {float(trimmed[0]):.3f}s)", file=sys.stderr)

        # Step 2c: Extrapolate first beats if the tracker missed them.
        # librosa's beat tracker often skips the first 1-2 beats because the
        # onset envelope hasn't built up enough energy. Keep extrapolating
        # backward until we reach the start of the audio or a quiet region.
        if len(beat_times) >= 2:
            n_check = min(8, len(beat_times) - 1)
            median_interval = float(np.median(np.diff(beat_times[:n_check + 1])))

            # Use the RMS of the first detected strong beat as our threshold
            # for extrapolation - only extrapolate into regions with similar energy
            first_beat_sample = int(beat_times[0] * sr)
            ext_window = int(0.03 * sr)
            first_beat_rms = float(np.sqrt(np.mean(
                y[max(0, first_beat_sample - ext_window):first_beat_sample + ext_window] ** 2
            )))
            extrap_threshold = first_beat_rms * 0.25

            original_first = float(beat_times[0])
            prepend_count = 0

            while True:
                extrapolated = float(beat_times[0]) - median_interval
                if extrapolated < 0:
                    break

                sample_idx = int(extrapolated * sr)
                if sample_idx >= len(y):
                    break
                region = y[max(0, sample_idx - ext_window):sample_idx + ext_window]
                rms = float(np.sqrt(np.mean(region ** 2)))
                if rms < extrap_threshold:
                    break  # Too quiet - would be in the intro section

                beat_times = np.insert(beat_times, 0, extrapolated)
                prepend_count += 1

            if prepend_count > 0:
                print(f"  Prepended {prepend_count} beat(s): first beat "
                      f"{original_first:.3f}s -> {float(beat_times[0]):.3f}s",
                      file=sys.stderr)

    # Step 2d: Clean out false beat detections.
    # A beat is likely a false detection if removing it makes the surrounding
    # interval match the track's tempo, while keeping it creates an interval
    # that's far off. This catches phantom beats from vocal transients, synth
    # stabs, etc. that slip through HPSS. Also useful for madmom in
    # quiet/breakdown sections where RNN activation produces noise.
    # NOTE: This MUST run before grid snapping, because false beats shift
    # indices and ruin the regression fit.
    if len(beat_times) >= 4:
        median_interval = float(np.median(np.diff(beat_times)))
        # Threshold: interval must be within 20% of median to be "correct"
        tolerance = 0.20

        removed = 0
        i = 1
        while i < len(beat_times) - 1:
            interval_before = beat_times[i] - beat_times[i - 1]
            interval_after = beat_times[i + 1] - beat_times[i]
            combined = beat_times[i + 1] - beat_times[i - 1]

            before_ok = abs(interval_before - median_interval) / median_interval <= tolerance
            after_ok = abs(interval_after - median_interval) / median_interval <= tolerance
            combined_ok = abs(combined - median_interval) / median_interval <= tolerance

            # Remove if: at least one neighbor interval is wrong, but removing
            # this beat would make the combined interval correct
            if not (before_ok and after_ok) and combined_ok:
                beat_times = np.delete(beat_times, i)
                removed += 1
                # Don't increment i - check the new beat at this position
            else:
                # Also catch double-hits: both intervals are short but combined = ~2x median
                combined_2x_ok = abs(combined - 2 * median_interval) / median_interval <= tolerance
                both_short = (interval_before < median_interval * (1 - tolerance) and
                              interval_after < median_interval * (1 - tolerance))
                if both_short and combined_2x_ok:
                    beat_times = np.delete(beat_times, i)
                    removed += 1
                else:
                    i += 1

        if removed > 0:
            print(f"  Removed {removed} false beat detection(s) "
                  f"(interval tolerance: ±{tolerance*100:.0f}% of "
                  f"{median_interval*1000:.1f}ms median)",
                  file=sys.stderr)

    # Save pre-snap beats for Serato calibration (step 2f).
    # Grid snapping can change beat positions significantly, making it
    # hard to match against Serato's existing grid. Use raw detections.
    pre_snap_beats = beat_times.copy()

    # Step 2e: Snap beats to regular grid if tempo is constant.
    # For EDM/electronic tracks, beats should be at perfectly even intervals.
    # After false beat cleaning, use cumulative beat numbering + iterative
    # regression. Cumulative numbering assigns each beat a sequential number
    # based on its LOCAL interval to the previous beat (immune to accumulated
    # drift that breaks global phase matching for long tracks).
    if len(beat_times) >= 8:
        intervals = np.diff(beat_times)
        q25, q75 = np.percentile(intervals, [25, 75])
        median_interval = float(np.median(intervals))
        iqr_ratio = (q75 - q25) / median_interval if median_interval > 0 else 1.0

        if iqr_ratio < 0.03:  # IQR < 3% of median = very constant tempo
            # Estimate the best interval using multiple candidate beat counts.
            # At 100fps, frame quantization biases the median by up to ±10ms.
            total_span = float(beat_times[-1] - beat_times[0])
            base_count = round(total_span / median_interval)

            # Try candidates and pick the one with the most beats close to
            # the nearest grid position (modular distance, drift-immune).
            best_est = total_span / base_count if base_count > 0 else median_interval
            best_good = 0
            for delta in range(-5, 6):
                c = base_count + delta
                if c <= 0:
                    continue
                trial = total_span / c
                # Modular distance: how close is each beat to its nearest
                # grid position? This doesn't accumulate drift.
                first_beat = float(beat_times[0])
                offsets = (beat_times - first_beat) % trial
                mod_dist = np.minimum(offsets, trial - offsets)
                n_good = int(np.sum(mod_dist < 0.030))
                if n_good > best_good:
                    best_good = n_good
                    best_est = trial

            est_interval = best_est

            # Cumulative beat numbering: assign each beat its number based
            # on the LOCAL gap to the previous beat. This is immune to
            # accumulated drift (unlike global phase matching which breaks
            # when sections have slightly different detected tempos).
            beat_numbers = np.zeros(len(beat_times), dtype=int)
            for i in range(1, len(beat_times)):
                gap = beat_times[i] - beat_times[i - 1]
                beat_numbers[i] = beat_numbers[i - 1] + max(1, round(gap / est_interval))

            # Remove duplicates (two beats mapped to same number)
            _, unique_idx = np.unique(beat_numbers, return_index=True)
            clean_beats = beat_times[unique_idx]
            clean_numbers = beat_numbers[unique_idx]
            n_dupes = len(beat_times) - len(clean_beats)

            # Seed with trimmed-mean-based estimates
            slope = est_interval
            intercept = float(np.median(clean_beats - clean_numbers * slope))

            # First pass: identify good beats using trimmed-mean grid
            fitted = intercept + clean_numbers * slope
            residuals = np.abs(clean_beats - fitted)
            good_mask = residuals < 0.030

            print(f"  Grid snap seed: interval={slope:.6f}s ({60/slope:.2f} BPM), "
                  f"intercept={intercept:.4f}, "
                  f"initial good={int(np.sum(good_mask))}/{len(clean_beats)}",
                  file=sys.stderr)

            # Iterative regression on good beats only
            for iteration in range(5):
                good_idx = np.where(good_mask)[0]
                if len(good_idx) < 4:
                    break
                slope, intercept = np.polyfit(
                    clean_numbers[good_idx], clean_beats[good_idx], 1
                )
                if slope <= 0:
                    break
                fitted = intercept + clean_numbers * slope
                residuals = np.abs(clean_beats - fitted)
                new_mask = residuals < 0.030
                print(f"  Grid snap iter {iteration}: slope={slope:.6f} ({60/slope:.2f} BPM), "
                      f"good={int(np.sum(new_mask))}/{len(clean_beats)}",
                      file=sys.stderr)
                if np.array_equal(new_mask, good_mask):
                    break
                good_mask = new_mask

            n_good = int(np.sum(good_mask))
            n_total = len(beat_times)

            if n_good >= len(clean_beats) * 0.80 and slope > 0:
                # Generate perfect grid from first to last good beat number
                first_num = int(clean_numbers[good_mask][0])
                last_num = int(clean_numbers[good_mask][-1])
                grid_numbers = np.arange(first_num, last_num + 1)
                grid = intercept + grid_numbers * slope
                grid = grid[grid >= 0]
                grid_bpm = 60.0 / slope
                beat_times = grid
                print(f"  Snapped {len(grid)} beats to {grid_bpm:.2f} BPM grid "
                      f"(was {n_total} beats, {n_dupes} dupes, "
                      f"{n_total - n_dupes - n_good} outliers, "
                      f"IQR ratio: {iqr_ratio:.4f})", file=sys.stderr)
            else:
                print(f"  Grid snap: only {n_good}/{len(clean_beats)} fit "
                      f"(IQR={iqr_ratio:.4f}), deferring to segmenter",
                      file=sys.stderr)

    # Step 2f: Apply Serato position offset.
    #
    # Strategy: if the file already has a Serato BeatGrid, calibrate the
    # offset empirically by matching our detected beats to Serato's existing
    # beat positions. This gives the exact offset for this specific file's
    # encoding/decoder combination.
    #
    # If no existing grid, fall back to computed offset from:
    #   1. MP3 codec delay (encoder delay + decoder delay)
    #   2. Per-track onset-to-peak alignment
    MPEG1_DECODER_DELAY = 529  # standard MPEG1 Layer III decoder delay

    # Try calibration against existing Serato BeatGrid.
    # Use pre_snap_beats (raw detections) for matching, since grid snapping
    # can shift beats to different BPM/phase than Serato's grid.
    calibrated_offset = None
    existing_markers = read_serato_beatgrid(audio_path)
    if existing_markers is not None and len(existing_markers) >= 1:
        serato_beats = reconstruct_serato_beats(existing_markers, max_beats=500,
                                                 duration=duration)
        if len(serato_beats) >= 4:
            serato_arr = np.array(serato_beats)
            # Method 1: Direct matching — find pairs within 100ms
            offsets = []
            for our_beat in pre_snap_beats[:min(100, len(pre_snap_beats))]:
                dists = np.abs(serato_arr - our_beat)
                min_idx = int(np.argmin(dists))
                min_dist = float(dists[min_idx])
                if min_dist < 0.100:
                    offsets.append(float(serato_arr[min_idx]) - float(our_beat))

            # Method 2: Grid-point matching for single-marker grids.
            # When BPM differs slightly, direct matching fails because beats
            # drift apart. Instead, snap each beat to the nearest Serato grid
            # point and compute the offset from there.
            if len(offsets) < 10 and len(existing_markers) == 1 and 'bpm' in existing_markers[0]:
                serato_start = existing_markers[0]['position']
                serato_bpm = existing_markers[0]['bpm']
                if serato_bpm > 0:
                    serato_interval = 60.0 / serato_bpm
                    grid_offsets = []
                    for our_beat in pre_snap_beats[:min(100, len(pre_snap_beats))]:
                        k = round((our_beat - serato_start) / serato_interval)
                        nearest_grid = serato_start + k * serato_interval
                        off = nearest_grid - our_beat
                        # Only count if within 25% of a beat interval
                        if abs(off) < serato_interval * 0.25:
                            grid_offsets.append(off)
                    if len(grid_offsets) >= 10:
                        offsets = grid_offsets
                        print(f"  Serato calibration: used grid-point matching "
                              f"(single-marker, {serato_bpm:.1f} BPM)",
                              file=sys.stderr)

            if len(offsets) >= 10:
                calibrated_offset = float(np.median(offsets))
                # Sanity check: offset should be between -10ms and 60ms
                if -0.010 <= calibrated_offset <= 0.060:
                    iqr = float(np.percentile(offsets, 75) - np.percentile(offsets, 25))
                    print(f"  Serato calibration: {len(offsets)} matched beats, "
                          f"offset={calibrated_offset*1000:.1f}ms "
                          f"(IQR={iqr*1000:.1f}ms)",
                          file=sys.stderr)
                else:
                    print(f"  Serato calibration rejected: offset={calibrated_offset*1000:.1f}ms "
                          f"out of range [-10, 60]ms, using computed offset",
                          file=sys.stderr)
                    calibrated_offset = None
            else:
                print(f"  Serato calibration: only {len(offsets)} matched beats "
                      f"(need 10+), using computed offset", file=sys.stderr)
                calibrated_offset = None
    elif existing_markers is not None and len(existing_markers) == 0:
        print(f"  Serato grid found but empty, using computed offset",
              file=sys.stderr)

    if calibrated_offset is not None:
        total_offset = calibrated_offset
        beat_times = beat_times + total_offset
        print(f"  Serato offset: +{total_offset*1000:.1f}ms [calibrated from existing grid]",
              file=sys.stderr)
    else:
        # Fallback: compute offset from codec delay + onset-to-peak
        # Component 1: codec delay (MP3 only)
        encoder_delay_samples = get_mp3_encoder_delay(audio_path)
        if encoder_delay_samples > 0:
            codec_delay_s = (encoder_delay_samples + MPEG1_DECODER_DELAY) / sr
        else:
            codec_delay_s = 0.0

        # Component 2: per-track onset-to-peak measured from audio content
        onset_to_peak_s = 0.005  # fallback: 5ms
        if len(beat_times) >= 4:
            peak_window = int(0.025 * sr)  # 25ms search window
            peak_offsets = []
            for bt in beat_times:
                start = int(bt * sr)
                end = min(start + peak_window, len(y))
                if end - start < 10:
                    continue
                window = np.abs(y[start:end])
                peak_idx = int(np.argmax(window))
                peak_offsets.append(peak_idx / sr)
            if peak_offsets:
                onset_to_peak_s = float(np.median(peak_offsets))
                onset_to_peak_s = max(0.001, min(0.020, onset_to_peak_s))

        total_offset = codec_delay_s + onset_to_peak_s
        beat_times = beat_times + total_offset

        print(f"  Serato offset: +{total_offset*1000:.1f}ms total "
              f"(codec delay {codec_delay_s*1000:.1f}ms"
              f"{f' [{encoder_delay_samples}+{MPEG1_DECODER_DELAY} samples]' if encoder_delay_samples > 0 else ''}"
              f" + onset-to-peak {onset_to_peak_s*1000:.1f}ms [measured])"
              f" [no existing Serato grid]",
              file=sys.stderr)

    # Step 3: Segment tempo
    print("Analyzing tempo segments...", file=sys.stderr)
    tempo_segments = segment_tempo(beat_times)

    # Step 4: Generate waveform data
    print("Generating waveform...", file=sys.stderr)
    waveform = generate_waveform(y, sr)

    # Step 5: Build output
    result = {
        "version": 1,
        "file_path": audio_path,
        "sample_rate": sr,
        "duration_seconds": round(duration, 3),
        "beat_detector": detector,
        "beats": [round(float(b), 4) for b in beat_times],
        "tempo_segments": tempo_segments,
        "waveform": waveform,
    }

    # Output JSON to stdout
    print("Analysis complete!", file=sys.stderr)
    json.dump(result, sys.stdout, separators=(",", ":"))
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
