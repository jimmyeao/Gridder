"""Generate waveform peak data and onset envelope for UI rendering."""

import sys
import numpy as np
import librosa


def generate_waveform(y: np.ndarray, sr: int,
                      samples_per_pixel: int = 2048) -> dict:
    """
    Downsample the audio waveform to peak data suitable for rendering.

    For each "pixel" (block of samples_per_pixel samples), compute:
    - peak positive amplitude
    - peak negative amplitude

    Also compute the onset strength envelope at the same resolution.

    Args:
        y: Audio signal (mono, float32, normalized -1..1)
        sr: Sample rate
        samples_per_pixel: Number of audio samples per display pixel

    Returns:
        Dict with keys: samples_per_pixel, peaks_positive, peaks_negative, onset_envelope
    """
    print(f"  Generating waveform data ({samples_per_pixel} samples/pixel)...", file=sys.stderr)

    n_pixels = len(y) // samples_per_pixel
    if n_pixels == 0:
        return {
            "samples_per_pixel": samples_per_pixel,
            "peaks_positive": [],
            "peaks_negative": [],
            "onset_envelope": [],
        }

    # Reshape into blocks and compute peaks
    trimmed = y[:n_pixels * samples_per_pixel]
    blocks = trimmed.reshape(n_pixels, samples_per_pixel)

    peaks_pos = np.max(blocks, axis=1)
    peaks_neg = np.min(blocks, axis=1)

    # Compute onset strength envelope
    onset_env = librosa.onset.onset_strength(y=y, sr=sr, aggregate=np.median)

    # Resample onset envelope to match waveform pixel count
    if len(onset_env) > 0:
        onset_resampled = np.interp(
            np.linspace(0, len(onset_env) - 1, n_pixels),
            np.arange(len(onset_env)),
            onset_env,
        )
        # Normalize onset envelope to 0..1
        onset_max = onset_resampled.max()
        if onset_max > 0:
            onset_resampled = onset_resampled / onset_max
    else:
        onset_resampled = np.zeros(n_pixels)

    print(f"  Waveform: {n_pixels} pixels for {len(y)/sr:.1f}s of audio", file=sys.stderr)

    return {
        "samples_per_pixel": samples_per_pixel,
        "peaks_positive": peaks_pos.astype(np.float32).tolist(),
        "peaks_negative": peaks_neg.astype(np.float32).tolist(),
        "onset_envelope": onset_resampled.astype(np.float32).tolist(),
    }
