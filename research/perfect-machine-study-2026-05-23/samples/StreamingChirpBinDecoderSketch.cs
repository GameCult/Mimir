// Sketch only. This shows the state shape for an incremental chirp-bin decoder.
// It is not compiled by Mimir.

namespace Mimir.Research.Samples;

public sealed class StreamingChirpBinDecoderSketch
{
    private readonly ChirpBinPlan plan;
    private readonly Ring<float> pcm;
    private readonly Ring<float> energy;
    private readonly List<CandidateFrame> candidates = [];
    private readonly List<TimelineAnchor> anchors = [];
    private long absoluteSampleCursor;
    private long lastScannedSample;

    public StreamingChirpBinDecoderSketch(ChirpBinPlan plan, int maxWindowSamples)
    {
        this.plan = plan;
        pcm = new Ring<float>(NextPowerOfTwo(maxWindowSamples));
        energy = new Ring<float>(NextPowerOfTwo(maxWindowSamples / plan.HopSamples + 8));
    }

    public void Append(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            pcm.Push(sample);
            absoluteSampleCursor++;
        }
    }

    public DecodeSnapshot DecodeAvailable()
    {
        ScanEnergyIncrementally();
        ClassifyNewProposals();
        PruneOldCandidates();
        SolveAnchorsIncrementally();
        var fit = FitClock(anchors);
        return new DecodeSnapshot([.. candidates], [.. anchors], fit);
    }

    private void ScanEnergyIncrementally()
    {
        var firstPossible = lastScannedSample;
        var lastPossible = absoluteSampleCursor - plan.WindowSamples;
        for (var start = firstPossible; start <= lastPossible; start += plan.HopSamples)
        {
            var window = pcm.ReadAbsolute(start, plan.WindowSamples);
            energy.Push(Rms(window));
            if (energy.Latest > plan.EnergyThreshold)
            {
                candidates.Add(new CandidateFrame(start, Energy: energy.Latest));
            }
        }

        lastScannedSample = Math.Max(lastScannedSample, lastPossible);
    }

    private void ClassifyNewProposals()
    {
        foreach (var candidate in candidates.Where(candidate => candidate.Symbols.Length == 0))
        {
            var window = pcm.ReadAbsolute(candidate.AbsoluteSample, plan.WindowSamples);
            candidate.Symbols = plan.Score(window);
        }
    }

    private void SolveAnchorsIncrementally()
    {
        // Production version should use a bounded trellis over the new tail,
        // not rebuild the entire anchor path.
        var tail = candidates
            .Where(candidate => candidate.Symbols.Length > 0)
            .OrderBy(candidate => candidate.AbsoluteSample)
            .TakeLast(plan.TrellisTailFrames)
            .ToArray();

        foreach (var triple in ConsecutiveTriples(tail))
        {
            if (!plan.TryMapTriplet(triple, out var eventIndex, out var confidence))
            {
                continue;
            }

            anchors.Add(new TimelineAnchor(
                eventIndex,
                triple[1].AbsoluteSample,
                confidence));
        }
    }

    private void PruneOldCandidates()
    {
        var keepAfter = absoluteSampleCursor - pcm.Capacity;
        candidates.RemoveAll(candidate => candidate.AbsoluteSample < keepAfter);
        anchors.RemoveAll(anchor => anchor.ObservedSample < keepAfter);
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / Math.Max(1, samples.Length));
    }

    private static IEnumerable<CandidateFrame[]> ConsecutiveTriples(CandidateFrame[] frames)
    {
        for (var index = 0; index + 2 < frames.Length; index++)
        {
            yield return [frames[index], frames[index + 1], frames[index + 2]];
        }
    }

    private static ClockFit? FitClock(IReadOnlyList<TimelineAnchor> anchors) => null;

    private static int NextPowerOfTwo(int value)
    {
        var power = 1;
        while (power < value)
        {
            power <<= 1;
        }

        return power;
    }
}

public sealed record ChirpBinPlan(
    int WindowSamples,
    int HopSamples,
    int TrellisTailFrames,
    double EnergyThreshold,
    Func<ReadOnlySpan<float>, SymbolScore[]> Score,
    TryMapTripletDelegate TryMapTriplet);

public delegate bool TryMapTripletDelegate(
    IReadOnlyList<CandidateFrame> frames,
    out ulong eventIndex,
    out double confidence);

public sealed record CandidateFrame(long AbsoluteSample, double Energy)
{
    public SymbolScore[] Symbols { get; set; } = [];
}

public sealed record SymbolScore(int SymbolId, double Score, double FractionalOffset);

public sealed record TimelineAnchor(ulong EventIndex, long ObservedSample, double Confidence);

public sealed record ClockFit(double SourceOffsetSamples, double SampleRate, double Confidence);

public sealed record DecodeSnapshot(
    IReadOnlyList<CandidateFrame> Candidates,
    IReadOnlyList<TimelineAnchor> Anchors,
    ClockFit? ClockFit);

public sealed class Ring<T>
{
    private readonly T[] data;
    private long cursor;

    public Ring(int capacity)
    {
        data = new T[capacity];
    }

    public int Capacity => data.Length;

    public T Latest => data[(cursor - 1) & (data.Length - 1)];

    public void Push(T value)
    {
        data[cursor & (data.Length - 1)] = value;
        cursor++;
    }

    public ReadOnlySpan<T> ReadAbsolute(long absoluteStart, int count)
    {
        // Sketch simplification: production should copy wraparound into a
        // scratch span or expose two spans.
        throw new NotImplementedException();
    }
}

