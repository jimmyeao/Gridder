"""
Entry point for the Gridder analysis engine.

Usage: python -m gridder_analysis <audio_file_path>

Outputs JSON to stdout with beat positions, tempo segments, and waveform data.
Progress messages go to stderr.
"""

import json
import sys
import os

def main():
    if len(sys.argv) != 2:
        print("Usage: python -m gridder_analysis <audio_file.mp3|flac>", file=sys.stderr)
        sys.exit(1)

    audio_path = sys.argv[1]

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

    # Step 2e: Snap beats to regular grid if tempo is constant.
    # For EDM/electronic tracks, beats should be at perfectly even intervals.
    # After false beat cleaning, use phase-matched regression: assign each
    # beat its expected "beat number" based on the median interval, then
    # regress beat_number -> beat_time. This is immune to false insertions
    # (which shift array indices but not phase-matched beat numbers).
    if len(beat_times) >= 8:
        intervals = np.diff(beat_times)
        q25, q75 = np.percentile(intervals, [25, 75])
        median_interval = float(np.median(intervals))
        iqr_ratio = (q75 - q25) / median_interval if median_interval > 0 else 1.0

        if iqr_ratio < 0.03:  # IQR < 3% of median = very constant tempo
            # Phase-match each beat to its expected beat number.
            # Only works for very regular tracks (IQR < 3%) where the
            # interval estimate is accurate enough to assign correct numbers.
            #
            # At 100fps, frame quantization biases the median interval by up
            # to ±10ms. Over hundreds of beats this compounds, making the
            # expected beat count off by several. We try multiple candidate
            # counts around the median-based estimate and pick the one where
            # the most beats fall within 30ms of the grid.
            total_span = float(beat_times[-1] - beat_times[0])
            base_count = round(total_span / median_interval)
            first_beat = float(beat_times[0])

            best_est = total_span / base_count if base_count > 0 else median_interval
            best_good = 0
            for delta in range(-5, 6):
                c = base_count + delta
                if c <= 0:
                    continue
                trial = total_span / c
                nums = np.round((beat_times - first_beat) / trial).astype(int)
                _, u_idx = np.unique(nums, return_index=True)
                fitted = first_beat + nums[u_idx] * trial
                n_good = int(np.sum(np.abs(beat_times[u_idx] - fitted) < 0.030))
                if n_good > best_good:
                    best_good = n_good
                    best_est = trial

            est_interval = best_est
            beat_numbers = np.round(
                (beat_times - first_beat) / est_interval
            ).astype(int)

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
