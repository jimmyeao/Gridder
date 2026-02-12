namespace Gridder.Services.Audio.BeatDetection;

/// <summary>
/// HMM/Viterbi beat decoder — port of madmom's DBNBeatTrackingProcessor.
/// Uses a beat state space with tempo (interval) and position dimensions,
/// a sparse transition model with exponential tempo change penalty,
/// and Viterbi decoding to find optimal beat positions.
/// </summary>
public class DbnBeatTracker
{
    private readonly int _fps;
    private readonly int _minInterval;
    private readonly int _maxInterval;
    private readonly int _numIntervals;
    private readonly double _transitionLambda;

    // State space: for each interval, positions 0..interval-1
    // Beat occurs at position 0. Total states = sum of all intervals.
    private readonly int _totalStates;
    private readonly int[] _intervalLengths;   // actual frame count for each interval index
    private readonly int[] _stateToInterval;   // interval index for each state
    private readonly int[] _stateToPosition;   // position within interval for each state
    private readonly int[] _intervalStartState; // first state index for each interval

    // Pre-computed transition log-probabilities for beat transitions
    // beatTransLogProb[i][j] = log P(transition from interval j to interval i at a beat)
    private readonly double[][] _beatTransLogProb;

    public DbnBeatTracker(int fps = 100, double minBpm = 40, double maxBpm = 240,
        double transitionLambda = 100)
    {
        _fps = fps;
        _transitionLambda = transitionLambda;

        _minInterval = (int)Math.Ceiling(fps * 60.0 / maxBpm);
        _maxInterval = (int)Math.Floor(fps * 60.0 / minBpm);
        _numIntervals = _maxInterval - _minInterval + 1;

        _intervalLengths = new int[_numIntervals];
        for (int i = 0; i < _numIntervals; i++)
            _intervalLengths[i] = _minInterval + i;

        // Build state space
        _totalStates = _intervalLengths.Sum();
        _stateToInterval = new int[_totalStates];
        _stateToPosition = new int[_totalStates];
        _intervalStartState = new int[_numIntervals];

        int state = 0;
        for (int ii = 0; ii < _numIntervals; ii++)
        {
            _intervalStartState[ii] = state;
            for (int pos = 0; pos < _intervalLengths[ii]; pos++)
            {
                _stateToInterval[state] = ii;
                _stateToPosition[state] = pos;
                state++;
            }
        }

        // Pre-compute beat transition log-probabilities
        _beatTransLogProb = new double[_numIntervals][];
        for (int i = 0; i < _numIntervals; i++)
        {
            _beatTransLogProb[i] = new double[_numIntervals];
            double sumProb = 0;
            for (int j = 0; j < _numIntervals; j++)
            {
                double penalty = Math.Exp(-transitionLambda * Math.Abs(i - j) / _numIntervals);
                _beatTransLogProb[i][j] = penalty;
                sumProb += penalty;
            }
            // Normalize and take log
            for (int j = 0; j < _numIntervals; j++)
                _beatTransLogProb[i][j] = Math.Log(_beatTransLogProb[i][j] / sumProb + 1e-30);
        }
    }

    /// <summary>
    /// Decode beat activations into beat times using Viterbi algorithm.
    /// Input: beat activation probabilities per frame (0-1) at _fps frames/second.
    /// Returns: beat times in seconds.
    /// </summary>
    public double[] Track(float[] activations)
    {
        int nFrames = activations.Length;
        if (nFrames < 2) return [];

        // Clamp activations to avoid log(0)
        const double eps = 1e-7;
        var act = new double[nFrames];
        for (int t = 0; t < nFrames; t++)
            act[t] = Math.Max(eps, Math.Min(1 - eps, activations[t]));

        // Viterbi forward pass
        // Memory optimization: only store beat-state backpointers
        var scorePrev = new double[_totalStates];
        var scoreCurr = new double[_totalStates];

        // beatBack[t, intervalIdx] = previous interval index at this beat
        var beatBack = new int[nFrames, _numIntervals];

        // Initialize: uniform prior
        double initLogProb = -Math.Log(_totalStates);
        for (int s = 0; s < _totalStates; s++)
            scorePrev[s] = initLogProb + ObservationLogProb(act[0], s);

        // Forward pass
        for (int t = 1; t < nFrames; t++)
        {
            for (int s = 0; s < _totalStates; s++)
            {
                int pos = _stateToPosition[s];
                int ii = _stateToInterval[s];

                if (pos > 0)
                {
                    // Non-beat transition: deterministic from (pos-1, same interval)
                    int prevState = s - 1;
                    scoreCurr[s] = scorePrev[prevState] + ObservationLogProb(act[t], s);
                }
                else
                {
                    // Beat state (pos = 0): find best previous interval
                    double bestScore = double.NegativeInfinity;
                    int bestPrevInterval = 0;

                    for (int j = 0; j < _numIntervals; j++)
                    {
                        // Previous state was at position (interval_j - 1) in interval j
                        int prevState = _intervalStartState[j] + _intervalLengths[j] - 1;
                        double sc = scorePrev[prevState] + _beatTransLogProb[ii][j];
                        if (sc > bestScore)
                        {
                            bestScore = sc;
                            bestPrevInterval = j;
                        }
                    }

                    scoreCurr[s] = bestScore + ObservationLogProb(act[t], s);
                    beatBack[t, ii] = bestPrevInterval;
                }
            }

            // Swap score arrays
            (scorePrev, scoreCurr) = (scoreCurr, scorePrev);
            Array.Clear(scoreCurr, 0, _totalStates);
        }

        // Find best final state
        int bestFinalState = 0;
        double bestFinalScore = double.NegativeInfinity;
        for (int s = 0; s < _totalStates; s++)
        {
            if (scorePrev[s] > bestFinalScore)
            {
                bestFinalScore = scorePrev[s];
                bestFinalState = s;
            }
        }

        // Backtrack to find beats
        var beats = new List<int>();
        int currentInterval = _stateToInterval[bestFinalState];
        int currentPos = _stateToPosition[bestFinalState];
        int currentFrame = nFrames - 1;

        // Walk back from final position to the last beat
        currentFrame -= currentPos;
        if (currentFrame >= 0)
            beats.Add(currentFrame);

        // Continue backtracking through beats
        while (currentFrame > 0)
        {
            int prevInterval = beatBack[currentFrame, currentInterval];
            currentFrame -= _intervalLengths[prevInterval];
            if (currentFrame >= 0)
                beats.Add(currentFrame);
            currentInterval = prevInterval;
        }

        beats.Reverse();

        // Convert frame indices to times
        return beats.Select(f => (double)f / _fps).ToArray();
    }

    private double ObservationLogProb(double activation, int state)
    {
        int pos = _stateToPosition[state];
        int ii = _stateToInterval[state];
        int interval = _intervalLengths[ii];

        if (pos == 0)
        {
            // Beat state: observation = activation
            return Math.Log(activation);
        }
        else
        {
            // Non-beat state: observation = (1 - activation) / (interval - 1)
            return Math.Log((1.0 - activation) / (interval - 1));
        }
    }
}
