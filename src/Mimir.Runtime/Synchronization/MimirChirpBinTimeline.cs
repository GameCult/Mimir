using System.Collections.Concurrent;
using System.Numerics;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirChirpBinSymbolDefinition(
    int SymbolId,
    double OffsetHz);

internal sealed record ChirpBinKernel(
    double[] Real,
    double[] Imaginary);

internal sealed record ChirpBinKernelSet(
    ChirpBinKernel[] Symbols,
    double ReferenceEnergy);

public sealed class MimirChirpBinTimeline
{
    public const int SampleRate = 48_000;
    public const double SegmentSeconds = 0.5;
    public const int SymbolCount = 32;
    public const int TimelineOrder = 3;

    private const double FirstEventSeconds = 0.08;
    private const double EventSpacingSeconds = 0.125;
    private const double ChirpDurationSeconds = 0.064;
    private const double BaseStartHz = 7_600.0;
    private const double BaseEndHz = 13_600.0;
    private const double BinSpacingHz = 450.0;
    private const double Gain = 0.070;
    private const int MaxSymbolCandidatesPerFrame = 1;
    private const int MaxTimingBinShift = 4;

    private static readonly int[] TimelineSymbols = RotateToDistinctOpening(BuildDeBruijn(SymbolCount, TimelineOrder));
    private static readonly Dictionary<int, int> TripleToIndex = BuildTripleIndex(TimelineSymbols);
    private static readonly MimirChirpBinSymbolDefinition[] Symbols = BuildSymbols();
    private static readonly ConcurrentDictionary<int, ChirpBinKernelSet> KernelSets = new();

    public static MimirChirpBinTimeline Default { get; } = new();

    public IReadOnlyList<MimirChirpBinSymbolDefinition> Codebook => Symbols;

    public float[] RenderSegmentMonoFloat(ulong segmentIndex)
    {
        var segmentStartSeconds = segmentIndex * SegmentSeconds;
        var samples = new float[(int)Math.Round(SegmentSeconds * SampleRate)];
        foreach (var timelineEvent in EventsOverlapping(segmentStartSeconds, SegmentSeconds))
        {
            AddChirp(samples, SampleRate, timelineEvent, segmentStartSeconds);
        }

        return samples;
    }

    public MimirChirpletStreamDecode DecodeStreamWindow(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return new MimirChirpletStreamDecode([], [], [], null);
        }

        var frames = DetectFrames(samples, sampleRate);
        var symbols = frames
            .Select(frame => new MimirChirpletSymbolObservation(
                frame.BestCandidate.SymbolId,
                frame.SampleOffset,
                frame.BestCandidate.Energy))
            .ToArray();
        var anchors = DecodeAnchors(frames, sampleRate);
        var clock = FitClock(anchors, sampleRate);
        return new MimirChirpletStreamDecode(frames, symbols, anchors, clock);
    }

    public MimirChirpletTimelineEvent EventForIndex(ulong eventIndex)
    {
        var symbolId = SymbolForEvent(eventIndex);
        var startSeconds = EventStartSeconds(eventIndex);
        var symbol = Symbols[symbolId];
        return new MimirChirpletTimelineEvent(
            eventIndex,
            symbolId,
            startSeconds,
            new MimirChirpletTone(
                startSeconds,
                ChirpDurationSeconds,
                BaseStartHz + symbol.OffsetHz,
                BaseEndHz + symbol.OffsetHz,
                Gain));
    }

    public IReadOnlyList<MimirChirpletTimelineEvent> EventsOverlapping(double startSeconds, double durationSeconds)
    {
        var endSeconds = startSeconds + durationSeconds;
        var firstIndex = Math.Max(0, (long)Math.Floor((startSeconds - FirstEventSeconds - ChirpDurationSeconds) / EventSpacingSeconds) - 2);
        var lastIndex = Math.Max(firstIndex, (long)Math.Ceiling((endSeconds - FirstEventSeconds) / EventSpacingSeconds) + 2);
        var events = new List<MimirChirpletTimelineEvent>((int)(lastIndex - firstIndex + 1));
        for (var eventIndex = firstIndex; eventIndex <= lastIndex; eventIndex++)
        {
            var timelineEvent = EventForIndex((ulong)eventIndex);
            if (timelineEvent.StartSeconds < endSeconds &&
                timelineEvent.StartSeconds + ChirpDurationSeconds > startSeconds)
            {
                events.Add(timelineEvent);
            }
        }

        return events;
    }

    private static IReadOnlyList<MimirChirpletTransformFrame> DetectFrames(ReadOnlySpan<float> samples, int sampleRate)
    {
        var chirpSamples = Math.Max(1, (int)Math.Round(ChirpDurationSeconds * sampleRate));
        if (samples.Length < chirpSamples)
        {
            return [];
        }

        var hopSamples = Math.Max(1, sampleRate / 1_200);
        var energyTrace = BuildDechirpedEnergyTrace(samples, sampleRate, chirpSamples, hopSamples);
        var threshold = AdaptiveThreshold(energyTrace);
        var proposals = new List<int>();
        for (var index = 1; index < energyTrace.Length - 1; index++)
        {
            if (energyTrace[index] >= threshold &&
                energyTrace[index] >= energyTrace[index - 1] &&
                energyTrace[index] >= energyTrace[index + 1])
            {
                proposals.Add(index * hopSamples);
            }
        }

        var frames = new List<MimirChirpletTransformFrame>();
        foreach (var proposal in proposals)
        {
            var frame = ClassifyAt(samples, sampleRate, proposal, sampleRate / 120);
            if (frame != null)
            {
                frames.Add(frame);
            }
        }

        return SuppressNearbyFrames(frames, sampleRate);
    }

    private static MimirChirpletTransformFrame? ClassifyAt(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int predictedOffset,
        int searchRadiusSamples)
    {
        var chirpSamples = Math.Max(1, (int)Math.Round(ChirpDurationSeconds * sampleRate));
        if (samples.Length < chirpSamples)
        {
            return null;
        }

        var start = Math.Max(0, predictedOffset - searchRadiusSamples);
        var end = Math.Min(samples.Length - chirpSamples, predictedOffset + searchRadiusSamples);
        var bestOffset = predictedOffset;
        MimirChirpletSymbolCandidate[] bestCandidates = [];
        var bestEnergy = 0.0;
        var step = Math.Max(2, searchRadiusSamples / 24);
        for (var offset = start; offset <= end; offset += step)
        {
            var candidates = ScoreBins(samples.Slice(offset, chirpSamples), sampleRate, offset);
            var energy = candidates.Length == 0 ? 0.0 : candidates[0].Energy;
            if (energy > bestEnergy || (energy >= bestEnergy * 0.995 && offset > bestOffset))
            {
                bestEnergy = energy;
                bestOffset = offset;
                bestCandidates = candidates;
            }
        }

        var localStart = Math.Max(start, bestOffset - step);
        var localEnd = Math.Min(end, bestOffset + step);
        for (var offset = localStart; offset <= localEnd; offset++)
        {
            var candidates = ScoreBins(samples.Slice(offset, chirpSamples), sampleRate, offset);
            var energy = candidates.Length == 0 ? 0.0 : candidates[0].Energy;
            if (energy > bestEnergy || (energy >= bestEnergy * 0.995 && offset > bestOffset))
            {
                bestEnergy = energy;
                bestOffset = offset;
                bestCandidates = candidates;
            }
        }

        if (bestCandidates.Length == 0 || bestEnergy < 0.055)
        {
            return null;
        }

        var refinedOffset = RefineOffset(samples, sampleRate, bestOffset, bestCandidates[0].SymbolId, chirpSamples);
        return new MimirChirpletTransformFrame(
            refinedOffset,
            bestCandidates.Select(candidate => candidate with { SampleOffset = refinedOffset }).ToArray());
    }

    private static MimirChirpletSymbolCandidate[] ScoreBins(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int sampleOffset)
    {
        var kernels = KernelSets.GetOrAdd(sampleRate, BuildKernelSet);
        var scores = new MimirChirpletSymbolCandidate[Symbols.Length];
        var sampleEnergy = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            sampleEnergy += samples[index] * samples[index];
        }

        if (sampleEnergy <= 1.0e-12 || kernels.ReferenceEnergy <= 1.0e-12)
        {
            return [];
        }

        for (var symbol = 0; symbol < Symbols.Length; symbol++)
        {
            var score = DechirpedBin(samples, kernels.Symbols[symbol]);
            scores[symbol] = new MimirChirpletSymbolCandidate(
                symbol,
                sampleOffset,
                2.0 * score / Math.Sqrt(sampleEnergy * kernels.ReferenceEnergy));
        }

        return scores
            .OrderByDescending(candidate => candidate.Energy)
            .Take(MaxSymbolCandidatesPerFrame)
            .ToArray();
    }

    private static double ScoreBestBinEnergy(ReadOnlySpan<float> samples, int sampleRate)
    {
        var kernels = KernelSets.GetOrAdd(sampleRate, BuildKernelSet);
        var sampleEnergy = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            sampleEnergy += samples[index] * samples[index];
        }

        if (sampleEnergy <= 1.0e-12 || kernels.ReferenceEnergy <= 1.0e-12)
        {
            return 0.0;
        }

        var best = 0.0;
        foreach (var kernel in kernels.Symbols)
        {
            best = Math.Max(best, DechirpedBin(samples, kernel));
        }

        return 2.0 * best / Math.Sqrt(sampleEnergy * kernels.ReferenceEnergy);
    }

    private static double ScoreSymbolEnergy(ReadOnlySpan<float> samples, int sampleRate, int symbolId)
    {
        var kernels = KernelSets.GetOrAdd(sampleRate, BuildKernelSet);
        var sampleEnergy = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            sampleEnergy += samples[index] * samples[index];
        }

        if (sampleEnergy <= 1.0e-12 || kernels.ReferenceEnergy <= 1.0e-12)
        {
            return 0.0;
        }

        var score = DechirpedBin(samples, kernels.Symbols[symbolId]);
        return 2.0 * score / Math.Sqrt(sampleEnergy * kernels.ReferenceEnergy);
    }

    private static double RefineOffset(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int bestOffset,
        int symbolId,
        int chirpSamples)
    {
        if (bestOffset <= 0 || bestOffset >= samples.Length - chirpSamples - 1)
        {
            return bestOffset;
        }

        var left = ScoreSymbolEnergy(samples.Slice(bestOffset - 1, chirpSamples), sampleRate, symbolId);
        var center = ScoreSymbolEnergy(samples.Slice(bestOffset, chirpSamples), sampleRate, symbolId);
        var right = ScoreSymbolEnergy(samples.Slice(bestOffset + 1, chirpSamples), sampleRate, symbolId);
        var denominator = left - 2.0 * center + right;
        if (Math.Abs(denominator) <= 1.0e-12)
        {
            return bestOffset;
        }

        var delta = 0.5 * (left - right) / denominator;
        return bestOffset + Math.Clamp(delta, -0.5, 0.5);
    }

    private static double DechirpedBin(ReadOnlySpan<float> samples, ChirpBinKernel kernel)
    {
        var real = 0.0;
        var imaginary = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            real += samples[index] * kernel.Real[index];
            imaginary += samples[index] * kernel.Imaginary[index];
        }

        return Math.Sqrt(real * real + imaginary * imaginary);
    }

    private static IReadOnlyList<MimirChirpletTimelineAnchor> DecodeAnchors(
        IReadOnlyList<MimirChirpletTransformFrame> frames,
        int sampleRate)
    {
        if (frames.Count < TimelineOrder)
        {
            return [];
        }

        var candidates = new List<MimirChirpletTimelineAnchor>();
        for (var index = 0; index <= frames.Count - TimelineOrder; index++)
        {
            var firstFrame = frames[index];
            var secondFrame = frames[index + 1];
            var thirdFrame = frames[index + 2];
            if (!IsConsecutive(firstFrame, secondFrame, sampleRate) ||
                !IsConsecutive(secondFrame, thirdFrame, sampleRate))
            {
                continue;
            }

            foreach (var first in firstFrame.Candidates)
            foreach (var second in secondFrame.Candidates)
            foreach (var third in thirdFrame.Candidates)
            for (var timingBinShift = -MaxTimingBinShift; timingBinShift <= MaxTimingBinShift; timingBinShift++)
            {
                var code = TripleCode(
                    ShiftSymbol(first.SymbolId, -timingBinShift),
                    ShiftSymbol(second.SymbolId, -timingBinShift),
                    ShiftSymbol(third.SymbolId, -timingBinShift));
                if (!TripleToIndex.TryGetValue(code, out var eventIndex))
                {
                    continue;
                }

                var firstEvent = Default.EventForIndex((ulong)eventIndex);
                var correctedFirstOffset = first.SampleOffset - timingBinShift * BinShiftSamples(sampleRate);
                var measuredGapA = (second.SampleOffset - first.SampleOffset) / sampleRate;
                var measuredGapB = (third.SampleOffset - second.SampleOffset) / sampleRate;
                var gapError = Math.Abs(measuredGapA - EventSpacingSeconds) + Math.Abs(measuredGapB - EventSpacingSeconds);
                var gapConfidence = 1.0 / (1.0 + gapError / 0.003);
                if (gapConfidence < 0.55)
                {
                    continue;
                }

                var energyConfidence = Math.Clamp((first.Energy + second.Energy + third.Energy) / 3.0, 0.0, 1.0);
                candidates.Add(new MimirChirpletTimelineAnchor(
                    (ulong)eventIndex,
                    firstEvent.StartSeconds,
                    correctedFirstOffset,
                    (gapConfidence * 0.60 + energyConfidence * 0.40) / (1.0 + Math.Abs(timingBinShift) * 0.15),
                    [
                        new MimirChirpletSymbolObservation(ShiftSymbol(first.SymbolId, -timingBinShift), correctedFirstOffset, first.Energy),
                        new MimirChirpletSymbolObservation(ShiftSymbol(second.SymbolId, -timingBinShift), second.SampleOffset - timingBinShift * BinShiftSamples(sampleRate), second.Energy),
                        new MimirChirpletSymbolObservation(ShiftSymbol(third.SymbolId, -timingBinShift), third.SampleOffset - timingBinShift * BinShiftSamples(sampleRate), third.Energy),
                    ]));
            }
        }

        return SelectCoherentAnchorPath(candidates, sampleRate);
    }

    private static IReadOnlyList<MimirChirpletTimelineAnchor> SelectCoherentAnchorPath(
        IReadOnlyList<MimirChirpletTimelineAnchor> candidates,
        int sampleRate)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        List<MimirChirpletTimelineAnchor> bestPath = [];
        var bestScore = double.NegativeInfinity;
        foreach (var seed in candidates.OrderByDescending(anchor => anchor.Confidence).Take(64))
        {
            var seedOffset = seed.SampleOffset - seed.TimelineSeconds * sampleRate;
            var path = candidates
                .Where(anchor => Math.Abs((anchor.SampleOffset - anchor.TimelineSeconds * sampleRate) - seedOffset) <= sampleRate * 0.006)
                .GroupBy(anchor => anchor.EventIndex)
                .Select(group => group.OrderByDescending(anchor => anchor.Confidence).First())
                .OrderBy(anchor => anchor.EventIndex)
                .ToList();
            var clock = FitClock(path, sampleRate);
            if (clock == null)
            {
                continue;
            }

            var score = path.Sum(anchor => anchor.Confidence) +
                Math.Min(path.Count, 12) * 0.20 -
                clock.MeanAbsoluteErrorSamples / Math.Max(1.0, sampleRate * 0.001);
            if (score > bestScore)
            {
                bestScore = score;
                bestPath = path;
            }
        }

        var finalClock = FitClock(bestPath, sampleRate);
        if (finalClock == null)
        {
            return [];
        }

        return bestPath
            .Where(anchor => Math.Abs(anchor.SampleOffset - finalClock.SampleForTimelineSeconds(anchor.TimelineSeconds)) <= Math.Max(16.0, sampleRate * 0.0015))
            .OrderBy(anchor => anchor.SampleOffset)
            .ToArray();
    }

    private static MimirChirpletClockFit? FitClock(
        IReadOnlyList<MimirChirpletTimelineAnchor> anchors,
        int sampleRate)
    {
        if (anchors.Count == 0)
        {
            return null;
        }

        if (anchors.Count == 1)
        {
            var anchor = anchors[0];
            return new MimirChirpletClockFit(
                anchor.SampleOffset - anchor.TimelineSeconds * sampleRate,
                sampleRate,
                anchor.Confidence * 0.35,
                1,
                0.0);
        }

        var totalWeight = anchors.Sum(anchor => Math.Max(1.0e-6, anchor.Confidence));
        var meanTimeline = anchors.Sum(anchor => anchor.TimelineSeconds * Math.Max(1.0e-6, anchor.Confidence)) / totalWeight;
        var meanSample = anchors.Sum(anchor => anchor.SampleOffset * Math.Max(1.0e-6, anchor.Confidence)) / totalWeight;
        var covariance = 0.0;
        var variance = 0.0;
        foreach (var anchor in anchors)
        {
            var weight = Math.Max(1.0e-6, anchor.Confidence);
            var dt = anchor.TimelineSeconds - meanTimeline;
            covariance += weight * dt * (anchor.SampleOffset - meanSample);
            variance += weight * dt * dt;
        }

        var effectiveSampleRate = variance > 1.0e-12 ? covariance / variance : sampleRate;
        if (!double.IsFinite(effectiveSampleRate) ||
            effectiveSampleRate < sampleRate * 0.98 ||
            effectiveSampleRate > sampleRate * 1.02)
        {
            effectiveSampleRate = sampleRate;
        }

        var sourceOffset = meanSample - effectiveSampleRate * meanTimeline;
        var meanAbsoluteError = anchors.Sum(anchor =>
        {
            var weight = Math.Max(1.0e-6, anchor.Confidence);
            var predicted = sourceOffset + anchor.TimelineSeconds * effectiveSampleRate;
            return Math.Abs(anchor.SampleOffset - predicted) * weight;
        }) / totalWeight;
        var residualConfidence = 1.0 / (1.0 + meanAbsoluteError / Math.Max(1.0, sampleRate * 0.00075));
        var countConfidence = Math.Clamp(anchors.Count / 12.0, 0.0, 1.0);
        var anchorConfidence = Math.Clamp(anchors.Average(anchor => anchor.Confidence), 0.0, 1.0);
        return new MimirChirpletClockFit(
            sourceOffset,
            effectiveSampleRate,
            residualConfidence * 0.45 + countConfidence * 0.25 + anchorConfidence * 0.30,
            anchors.Count,
            meanAbsoluteError);
    }

    private static float[] BuildDechirpedEnergyTrace(ReadOnlySpan<float> samples, int sampleRate, int windowSamples, int hopSamples)
    {
        var output = new float[1 + (samples.Length - windowSamples) / hopSamples];
        for (var frame = 0; frame < output.Length; frame++)
        {
            var offset = frame * hopSamples;
            output[frame] = (float)ScoreBestBinEnergy(samples.Slice(offset, windowSamples), sampleRate);
        }

        return output;
    }

    private static double AdaptiveThreshold(float[] energyTrace)
    {
        if (energyTrace.Length == 0)
        {
            return double.PositiveInfinity;
        }

        var mean = energyTrace.Average(value => (double)value);
        var variance = energyTrace.Sum(value => (value - mean) * (value - mean)) / energyTrace.Length;
        return mean + Math.Sqrt(Math.Max(0.0, variance)) * 0.75;
    }

    private static IReadOnlyList<MimirChirpletTransformFrame> SuppressNearbyFrames(
        IReadOnlyList<MimirChirpletTransformFrame> frames,
        int sampleRate)
    {
        var minimumSpacingSamples = EventSpacingSeconds * sampleRate * 0.65;
        var kept = new List<MimirChirpletTransformFrame>();
        foreach (var frame in frames.OrderByDescending(frame => frame.BestCandidate.Energy))
        {
            if (kept.Any(existing => Math.Abs(existing.SampleOffset - frame.SampleOffset) < minimumSpacingSamples))
            {
                continue;
            }

            kept.Add(frame);
        }

        return kept.OrderBy(frame => frame.SampleOffset).ToArray();
    }

    private static bool IsConsecutive(
        MimirChirpletTransformFrame first,
        MimirChirpletTransformFrame second,
        int sampleRate)
    {
        var seconds = (second.SampleOffset - first.SampleOffset) / sampleRate;
        return seconds >= EventSpacingSeconds * 0.70 && seconds <= EventSpacingSeconds * 1.30;
    }

    private static void AddChirp(
        float[] samples,
        int sampleRate,
        MimirChirpletTimelineEvent timelineEvent,
        double segmentStartSeconds)
    {
        var symbol = Symbols[timelineEvent.SymbolId];
        var startFrame = (int)Math.Round((timelineEvent.StartSeconds - segmentStartSeconds) * sampleRate);
        var frameCount = Math.Max(1, (int)Math.Round(ChirpDurationSeconds * sampleRate));
        for (var frame = 0; frame < frameCount; frame++)
        {
            var outputFrame = startFrame + frame;
            if (outputFrame < 0 || outputFrame >= samples.Length)
            {
                continue;
            }

            var normalized = frameCount <= 1 ? 1.0 : frame / (double)(frameCount - 1);
            var t = frame / (double)sampleRate;
            var envelope = Envelope(normalized);
            var phase = BasePhase(t) + 2.0 * Math.PI * symbol.OffsetHz * t;
            samples[outputFrame] += (float)(Math.Sin(phase) * envelope * Gain);
        }
    }

    private static double Envelope(double normalized)
    {
        var attack = Math.Clamp(normalized / 0.10, 0.0, 1.0);
        var release = Math.Clamp((1.0 - normalized) / 0.90, 0.0, 1.0);
        return attack * Math.Sqrt(release);
    }

    private static double BasePhase(double t)
    {
        var slope = (BaseEndHz - BaseStartHz) / ChirpDurationSeconds;
        return 2.0 * Math.PI * (BaseStartHz * t + 0.5 * slope * t * t);
    }

    private static int ShiftSymbol(int symbolId, int shift) =>
        (symbolId + shift + SymbolCount) % SymbolCount;

    private static double BinShiftSamples(int sampleRate)
    {
        var slope = (BaseEndHz - BaseStartHz) / ChirpDurationSeconds;
        return BinSpacingHz * sampleRate / slope;
    }

    private static ChirpBinKernelSet BuildKernelSet(int sampleRate)
    {
        var chirpSamples = Math.Max(1, (int)Math.Round(ChirpDurationSeconds * sampleRate));
        var symbolKernels = new ChirpBinKernel[Symbols.Length];
        var referenceEnergy = 0.0;
        for (var symbol = 0; symbol < Symbols.Length; symbol++)
        {
            var real = new double[chirpSamples];
            var imaginary = new double[chirpSamples];
            for (var index = 0; index < chirpSamples; index++)
            {
                var t = index / (double)sampleRate;
                var normalized = chirpSamples <= 1 ? 1.0 : index / (double)(chirpSamples - 1);
                var window = Envelope(normalized);
                var phase = -(BasePhase(t) + 2.0 * Math.PI * Symbols[symbol].OffsetHz * t);
                real[index] = window * Math.Cos(phase);
                imaginary[index] = window * Math.Sin(phase);
                if (symbol == 0)
                {
                    referenceEnergy += window * window * 0.5;
                }
            }

            symbolKernels[symbol] = new ChirpBinKernel(real, imaginary);
        }

        return new ChirpBinKernelSet(symbolKernels, referenceEnergy);
    }

    private static MimirChirpBinSymbolDefinition[] BuildSymbols()
    {
        var symbols = new MimirChirpBinSymbolDefinition[SymbolCount];
        var firstOffset = -(SymbolCount - 1) * BinSpacingHz * 0.5;
        for (var symbol = 0; symbol < symbols.Length; symbol++)
        {
            symbols[symbol] = new MimirChirpBinSymbolDefinition(symbol, firstOffset + symbol * BinSpacingHz);
        }

        return symbols;
    }

    private static int SymbolForEvent(ulong eventIndex) =>
        TimelineSymbols[(int)(eventIndex % (ulong)TimelineSymbols.Length)];

    private static double EventStartSeconds(ulong eventIndex) =>
        FirstEventSeconds + eventIndex * EventSpacingSeconds;

    private static int[] BuildDeBruijn(int alphabetSize, int order)
    {
        var a = new int[alphabetSize * order];
        var sequence = new List<int>((int)Math.Pow(alphabetSize, order));

        void Db(int t, int p)
        {
            if (t > order)
            {
                if (order % p == 0)
                {
                    for (var index = 1; index <= p; index++)
                    {
                        sequence.Add(a[index]);
                    }
                }

                return;
            }

            a[t] = a[t - p];
            Db(t + 1, p);
            for (var j = a[t - p] + 1; j < alphabetSize; j++)
            {
                a[t] = j;
                Db(t + 1, t);
            }
        }

        Db(1, 1);
        return sequence.ToArray();
    }

    private static int[] RotateToDistinctOpening(int[] sequence)
    {
        for (var index = 0; index < sequence.Length; index++)
        {
            var first = sequence[index];
            var second = sequence[(index + 1) % sequence.Length];
            var third = sequence[(index + 2) % sequence.Length];
            if (first != second && first != third && second != third)
            {
                return sequence.Skip(index).Concat(sequence.Take(index)).ToArray();
            }
        }

        return sequence;
    }

    private static Dictionary<int, int> BuildTripleIndex(IReadOnlyList<int> symbols)
    {
        var map = new Dictionary<int, int>(symbols.Count);
        for (var index = 0; index < symbols.Count; index++)
        {
            var code = TripleCode(
                symbols[index],
                symbols[(index + 1) % symbols.Count],
                symbols[(index + 2) % symbols.Count]);
            if (!map.ContainsKey(code))
            {
                map[code] = index;
            }
        }

        return map;
    }

    private static int TripleCode(int first, int second, int third) =>
        first * SymbolCount * SymbolCount + second * SymbolCount + third;
}
