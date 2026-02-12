using Gridder.Services.Audio.Dsp;

namespace Gridder.Services.Audio;

/// <summary>
/// Generate waveform peak data and onset envelope for UI rendering.
/// Port of waveform_generator.py.
/// </summary>
public static class WaveformGenerator
{
    /// <summary>
    /// Downsample audio to peak data and onset envelope for waveform display.
    /// Returns (samplesPerPixel, peaksPositive, peaksNegative, onsetEnvelope).
    /// </summary>
    public static (int SamplesPerPixel, float[] PeaksPositive, float[] PeaksNegative,
        float[] OnsetEnvelope) Generate(float[] audio, int sr, int samplesPerPixel = 2048)
    {
        int nPixels = audio.Length / samplesPerPixel;
        if (nPixels == 0)
            return (samplesPerPixel, [], [], []);

        // Compute peaks per block
        var peaksPos = new float[nPixels];
        var peaksNeg = new float[nPixels];

        for (int p = 0; p < nPixels; p++)
        {
            int start = p * samplesPerPixel;
            int end = start + samplesPerPixel;
            float maxVal = float.MinValue;
            float minVal = float.MaxValue;

            for (int i = start; i < end && i < audio.Length; i++)
            {
                if (audio[i] > maxVal) maxVal = audio[i];
                if (audio[i] < minVal) minVal = audio[i];
            }

            peaksPos[p] = maxVal;
            peaksNeg[p] = minVal;
        }

        // Compute onset strength envelope from audio
        var audioDouble = new double[audio.Length];
        for (int i = 0; i < audio.Length; i++)
            audioDouble[i] = audio[i];

        var onsetEnv = SpectrogramUtils.OnsetStrengthFromAudio(audioDouble, sr, useMedian: true);

        // Resample onset envelope to match pixel count
        float[] onsetResampled;
        if (onsetEnv.Length > 0)
        {
            onsetResampled = new float[nPixels];
            for (int p = 0; p < nPixels; p++)
            {
                double srcPos = (double)p * (onsetEnv.Length - 1) / (nPixels - 1);
                int srcIdx = (int)srcPos;
                double frac = srcPos - srcIdx;

                if (srcIdx + 1 < onsetEnv.Length)
                    onsetResampled[p] = (float)(onsetEnv[srcIdx] * (1 - frac) + onsetEnv[srcIdx + 1] * frac);
                else if (srcIdx < onsetEnv.Length)
                    onsetResampled[p] = (float)onsetEnv[srcIdx];
            }

            // Normalize to [0, 1]
            float maxOnset = 0;
            for (int i = 0; i < onsetResampled.Length; i++)
                if (onsetResampled[i] > maxOnset) maxOnset = onsetResampled[i];

            if (maxOnset > 0)
                for (int i = 0; i < onsetResampled.Length; i++)
                    onsetResampled[i] /= maxOnset;
        }
        else
        {
            onsetResampled = new float[nPixels];
        }

        return (samplesPerPixel, peaksPos, peaksNeg, onsetResampled);
    }
}
