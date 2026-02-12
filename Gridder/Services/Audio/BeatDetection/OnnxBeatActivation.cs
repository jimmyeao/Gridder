using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Gridder.Services.Audio.BeatDetection;

/// <summary>
/// ONNX Runtime wrapper for madmom's BLSTM beat activation model.
/// Loads beats_blstm.onnx from app resources and runs inference.
/// Returns null gracefully if model is not available.
/// </summary>
public sealed class OnnxBeatActivation : IDisposable
{
    private InferenceSession? _session;
    private bool _initialized;
    private bool _disposed;
    private readonly object _lock = new();

    /// <summary>
    /// Initialize the ONNX session from the model file.
    /// Returns false if the model is not available.
    /// </summary>
    public bool Initialize(string modelPath)
    {
        lock (_lock)
        {
            if (_initialized) return _session != null;

            _initialized = true;

            try
            {
                if (!File.Exists(modelPath))
                    return false;

                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
                };

                _session = new InferenceSession(modelPath, options);
                return true;
            }
            catch
            {
                _session = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Run ONNX inference on preprocessed features.
    /// Input: float[nFrames, nFeatures] from MadmomPreprocessor.
    /// Returns: float[nFrames] beat activation probabilities (0-1), or null on failure.
    /// </summary>
    public float[]? Predict(float[,] features)
    {
        if (_session == null) return null;

        try
        {
            int nFrames = features.GetLength(0);
            int nFeatures = features.GetLength(1);

            // Create input tensor: [1, nFrames, nFeatures] (batch, sequence, features)
            var dims = new[] { 1, nFrames, nFeatures };
            var tensor = new DenseTensor<float>(dims);

            for (int t = 0; t < nFrames; t++)
                for (int f = 0; f < nFeatures; f++)
                    tensor[0, t, f] = features[t, f];

            // Get input name from model
            var inputName = _session.InputMetadata.Keys.First();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, tensor)
            };

            // Run inference
            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();

            // Extract activation values (shape: [1, nFrames, 1] or [1, nFrames])
            var activations = new float[nFrames];
            for (int t = 0; t < nFrames; t++)
            {
                float val = output.Dimensions.Length == 3
                    ? output[0, t, 0]
                    : output[0, t];

                // Sigmoid if model outputs logits
                activations[t] = val > 0 && val < 1 ? val : Sigmoid(val);
            }

            return activations;
        }
        catch
        {
            return null;
        }
    }

    private static float Sigmoid(float x)
        => 1.0f / (1.0f + MathF.Exp(-x));

    public void Dispose()
    {
        if (!_disposed)
        {
            _session?.Dispose();
            _disposed = true;
        }
    }
}
