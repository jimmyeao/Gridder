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

    # Step 2: Detect beats (includes HPSS percussive separation)
    beat_times = detect_beats(audio_path, y, sr)

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
