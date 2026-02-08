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

    # Step 2b: Extrapolate first beat if the tracker missed it.
    # librosa's beat tracker often skips the first beat because the onset
    # envelope hasn't built up enough energy. Check if there should be a
    # beat before the first detected one by looking at the initial interval.
    if len(beat_times) >= 2:
        first_interval = beat_times[1] - beat_times[0]
        # Use median of first few intervals for a stable estimate
        n_check = min(8, len(beat_times) - 1)
        median_interval = float(np.median(np.diff(beat_times[:n_check + 1])))
        extrapolated = beat_times[0] - median_interval

        if extrapolated >= 0 and beat_times[0] > median_interval * 1.3:
            # First beat is suspiciously late - prepend the extrapolated beat
            # Check if there's audio energy near the extrapolated position
            sample_idx = int(extrapolated * sr)
            window = int(0.05 * sr)  # 50ms window
            if sample_idx < len(y):
                region = y[max(0, sample_idx - window):sample_idx + window]
                rms = float(np.sqrt(np.mean(region**2)))
                # Only prepend if there's actual audio energy there
                if rms > 0.01:
                    beat_times = np.insert(beat_times, 0, extrapolated)
                    print(f"  Prepended first beat at {extrapolated:.3f}s "
                          f"(original first: {beat_times[1]:.3f}s)", file=sys.stderr)

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
