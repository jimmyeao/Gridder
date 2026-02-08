"""
Beat detection using librosa with percussive source separation.

Uses Harmonic-Percussive Source Separation (HPSS) to isolate drum content
before beat detection. This prevents vocals, guitar, synth etc. from
confusing the beat tracker - especially important for songs with
instrumental/vocal intros before the drums come in.
"""

import sys
import numpy as np
import librosa


def detect_beats_librosa(y: np.ndarray, sr: int) -> np.ndarray:
    """
    Detect beat positions using librosa's beat tracker with percussive
    source separation and frequency-band weighting for kick/snare.
    """
    print("  Separating percussive content (HPSS)...", file=sys.stderr)

    # --- Step 1: Harmonic-Percussive Source Separation ---
    # Compute STFT
    stft = librosa.stft(y)

    # Separate into harmonic and percussive spectrograms
    # margin parameter controls separation aggressiveness:
    #   higher = cleaner separation but may lose some transients
    H, P = librosa.decompose.hpss(stft, margin=2.0)

    # Convert percussive spectrogram back to audio for onset detection
    y_perc = librosa.istft(P, length=len(y))

    print("  Computing percussive onset envelope...", file=sys.stderr)

    # --- Step 2: Multi-band onset detection focused on drums ---
    # We compute onset strength from specific frequency bands:
    #   - Low band (kick drum): ~40-150 Hz -> mel bands roughly 0-10
    #   - Mid band (snare body): ~150-500 Hz -> mel bands roughly 10-30
    #   - High band (snare snap/hi-hat): ~2-8 kHz -> mel bands roughly 60-100
    #
    # The percussive audio already filters out harmonic content,
    # so we weight these bands for a drum-focused onset envelope.

    # Compute mel spectrogram of percussive component
    S_perc = librosa.feature.melspectrogram(y=y_perc, sr=sr, n_mels=128)

    # Onset strength from percussive signal with mel weighting
    onset_env = librosa.onset.onset_strength(
        S=librosa.power_to_db(S_perc),
        sr=sr,
        aggregate=np.median,
    )

    # Also compute onset from just the low-frequency percussive content
    # (kick drum) for a secondary reference
    onset_env_low = librosa.onset.onset_strength(
        S=librosa.power_to_db(S_perc[:30, :]),  # low mel bands
        sr=sr,
        aggregate=np.median,
    )

    # Blend: primarily percussive onsets, boosted by kick detection
    onset_combined = onset_env + 0.5 * onset_env_low

    print("  Running beat tracker on percussive onsets...", file=sys.stderr)

    # --- Step 3: Beat tracking ---
    tempo, beat_frames = librosa.beat.beat_track(
        onset_envelope=onset_combined,
        sr=sr,
        units="frames",
        tightness=100,
    )

    # librosa >= 0.10 returns tempo as an ndarray
    if isinstance(tempo, np.ndarray):
        tempo = float(tempo[0]) if len(tempo) > 0 else 0.0

    # Convert frame indices to time
    beat_times = librosa.frames_to_time(beat_frames, sr=sr)

    print(f"  Detected {len(beat_times)} beats, estimated tempo: {tempo:.1f} BPM",
          file=sys.stderr)

    # --- Step 4: Verify beat quality ---
    # Check that beats align with percussive onsets. If the onset strength
    # at a beat position is very low, the beat may be a false positive
    # (tracker interpolating through a quiet section).
    if len(beat_times) > 0 and len(onset_combined) > 0:
        beat_onset_values = onset_combined[
            np.clip(beat_frames, 0, len(onset_combined) - 1)]
        onset_median = np.median(onset_combined[onset_combined > 0]) \
            if np.any(onset_combined > 0) else 0
        weak_beats = np.sum(beat_onset_values < onset_median * 0.1)
        if weak_beats > 0:
            print(f"  Note: {weak_beats} beats have weak percussive onset "
                  f"(may be in non-drum sections)", file=sys.stderr)

    return beat_times


def detect_beats_madmom(audio_path: str) -> np.ndarray | None:
    """
    Detect beat positions using madmom's DBN beat tracker (more accurate
    for variable tempo). Returns None if madmom is not available.
    """
    try:
        from madmom.features.beats import RNNBeatProcessor, DBNBeatTrackingProcessor

        print("  Using madmom DBN beat tracker...", file=sys.stderr)

        proc = DBNBeatTrackingProcessor(
            fps=100,
            min_bpm=40,
            max_bpm=240,
            transition_lambda=100,
        )
        act = RNNBeatProcessor()(audio_path)
        beats = proc(act)

        print(f"  Detected {len(beats)} beats via madmom", file=sys.stderr)
        return beats

    except ImportError:
        print("  madmom not available, using librosa", file=sys.stderr)
        return None
    except Exception as e:
        print(f"  madmom failed: {e}, using librosa", file=sys.stderr)
        return None


def detect_beats(audio_path: str, y: np.ndarray, sr: int) -> np.ndarray:
    """
    Detect beats using the best available method.
    Tries madmom first (better for variable tempo), falls back to librosa.
    """
    print("Detecting beats...", file=sys.stderr)

    # Try madmom first
    beats = detect_beats_madmom(audio_path)
    if beats is not None and len(beats) > 0:
        return beats

    # Fall back to librosa with percussive separation
    return detect_beats_librosa(y, sr)
