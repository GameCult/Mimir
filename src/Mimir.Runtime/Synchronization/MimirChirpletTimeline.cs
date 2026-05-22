namespace Mimir.Runtime.Synchronization;

using System.Numerics;

public sealed record MimirChirpletTone(
    double StartSeconds,
    double DurationSeconds,
    double StartHz,
    double EndHz,
    double Gain = 1.0)
{
    public double CenterHz => (StartHz + EndHz) * 0.5;
}

public sealed record MimirChirpletBandResponse(
    double CenterHz,
    double Energy);

public sealed record MimirChirpletTimelineEvent(
    ulong Index,
    int SymbolId,
    double StartSeconds,
    MimirChirpletTone Tone);

public sealed class MimirChirpletTimeline
{
    public const int SampleRate = 48_000;
    public const double SegmentSeconds = 0.5;
    public const double QueueLeadSeconds = 1.0;
    public const int SymbolCount = 32;
    public const int TimelineOrder = 3;
    public static readonly int TimelinePeriod = (int)Math.Pow(SymbolCount, TimelineOrder);

    private const double FirstEventSeconds = 0.08;
    private const double MinEventGapSeconds = 0.088;
    private const double MaxEventGapSeconds = 0.170;
    private const double MaxEventDurationSeconds = 0.078;
    private const double Gain = 0.090;
    private const int MaxSymbolCandidatesPerFrame = 8;
    private const double CandidateOffsetBudgetMultiplier = 1.5;

    private static readonly int[] TimelineSymbols = RotateToDistinctOpening(BuildDeBruijn(SymbolCount, TimelineOrder));
    private static readonly Dictionary<int, int> TripleToIndex = BuildTripleIndex(TimelineSymbols);
    private static readonly MimirChirpletSymbolCodebook Codebook = MimirChirpletSymbolCodebook.Default;
    private static readonly double[] PeriodEventStarts = BuildPeriodEventStarts(TimelineSymbols);
    private static readonly double PeriodDurationSeconds = PeriodEventStarts[^1] + Codebook[TimelineSymbols[^1]].GapSeconds;
    private readonly IReadOnlyList<MimirChirpletToneKernel> symbolKernels;
    private readonly Dictionary<int, IReadOnlyList<MimirChirpletToneKernel>> symbolKernelsBySampleRate = [];

    private MimirChirpletTimeline()
    {
        symbolKernels = Codebook.Symbols.Select(symbol => RenderToneKernel(symbol.Tone, SampleRate)).ToArray();
        symbolKernelsBySampleRate[SampleRate] = symbolKernels;
    }

    public static MimirChirpletTimeline Default { get; } = new();

    public float[] RenderSegmentMonoFloat(ulong segmentIndex)
    {
        var segmentStartSeconds = segmentIndex * SegmentSeconds;
        var samples = new float[(int)Math.Round(SegmentSeconds * SampleRate)];
        foreach (var timelineEvent in EventsOverlapping(segmentStartSeconds, SegmentSeconds))
        {
            AddTone(samples, SampleRate, timelineEvent.Tone, segmentStartSeconds);
        }

        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] *= (float)Gain;
        }

        return samples;
    }

    public string RenderSegmentPcm16Base64(ulong segmentIndex)
    {
        var samples = RenderSegmentMonoFloat(segmentIndex);
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = (short)Math.Round(Math.Clamp(samples[index], -1.0f, 1.0f) * short.MaxValue);
            bytes[index * sizeof(short)] = (byte)(sample & 0xff);
            bytes[index * sizeof(short) + 1] = (byte)((sample >> 8) & 0xff);
        }

        return Convert.ToBase64String(bytes);
    }

    public IReadOnlyList<MimirChirpletBandResponse> EstimateBandResponse(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        var responses = new List<MimirChirpletBandResponse>(Codebook.Symbols.Count);
        var kernels = GetSymbolKernels(sampleRate);
        for (var symbol = 0; symbol < Codebook.Symbols.Count; symbol++)
        {
            var tone = Codebook[symbol].Tone;
            var kernel = kernels[symbol];
            var energy = MaxMatchedEnergy(samples, kernel, Math.Max(1, sampleRate / 1_000));
            responses.Add(new MimirChirpletBandResponse(tone.CenterHz, energy));
        }

        return responses;
    }

    public MimirChirpletStreamDecode DecodeStreamWindow(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return new MimirChirpletStreamDecode([], [], [], null);
        }

        var frames = DetectTransformFrames(samples, sampleRate, Math.Max(1, sampleRate / 200));
        var symbols = frames
            .Select(frame => new MimirChirpletSymbolObservation(
                frame.BestCandidate.SymbolId,
                frame.SampleOffset,
                frame.BestCandidate.Energy))
            .ToArray();
        var anchors = DecodeTrellisAnchors(frames, sampleRate);
        var clock = FitClock(anchors, sampleRate);
        return new MimirChirpletStreamDecode(frames, symbols, anchors, clock);
    }

    public MimirChirpletTimelineEvent EventForIndex(ulong eventIndex)
    {
        var symbolId = SymbolForEvent(eventIndex);
        var tone = Codebook[symbolId].Tone with { StartSeconds = EventStartSeconds(eventIndex) };
        return new MimirChirpletTimelineEvent(eventIndex, symbolId, tone.StartSeconds, tone);
    }

    public IReadOnlyList<MimirChirpletTimelineEvent> EventsOverlapping(double startSeconds, double durationSeconds)
    {
        var endSeconds = startSeconds + durationSeconds;
        var firstIndex = Math.Max(0, (long)Math.Floor((startSeconds - FirstEventSeconds - MaxEventDurationSeconds) / MaxEventGapSeconds) - 2);
        var lastIndex = Math.Max(firstIndex, (long)Math.Ceiling((endSeconds - FirstEventSeconds) / MinEventGapSeconds) + 2);
        var events = new List<MimirChirpletTimelineEvent>((int)(lastIndex - firstIndex + 1));
        for (var eventIndex = firstIndex; eventIndex <= lastIndex; eventIndex++)
        {
            var timelineEvent = EventForIndex((ulong)eventIndex);
            if (timelineEvent.StartSeconds < endSeconds &&
                timelineEvent.StartSeconds + timelineEvent.Tone.DurationSeconds > startSeconds)
            {
                events.Add(timelineEvent);
            }
        }

        return events;
    }

    private IReadOnlyList<MimirChirpletTransformFrame> DetectTransformFrames(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int hopSamples)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        var trace = BuildChirpletEnergyTrace(samples, sampleRate, hopSamples);
        if (trace.Count <= 2)
        {
            return [];
        }

        var frames = new List<MimirChirpletTransformFrame>();
        var candidateScores = new Dictionary<int, double>();
        for (var frame = 1; frame < trace.Count - 1; frame++)
        {
            if (trace[frame] < 0.05 ||
                trace[frame] < trace[frame - 1] ||
                trace[frame] < trace[frame + 1])
            {
                continue;
            }

            AddCandidateScore(candidateScores, frame * hopSamples, trace[frame]);
        }

        var envelope = ContrastNormalize(BuildEnvelopeEnergyTrace(samples, sampleRate, hopSamples));
        for (var frame = 1; frame < envelope.Length - 1; frame++)
        {
            if (envelope[frame] < 0.055 ||
                envelope[frame] < envelope[frame - 1] ||
                envelope[frame] < envelope[frame + 1])
            {
                continue;
            }

            AddCandidateScore(candidateScores, frame * hopSamples, envelope[frame]);
        }

        var maxExpectedEvents = Math.Max(
            8,
            (int)Math.Ceiling(samples.Length / (double)sampleRate / MinEventGapSeconds * CandidateOffsetBudgetMultiplier));
        foreach (var offset in candidateScores
                     .OrderByDescending(pair => pair.Value)
                     .Take(maxExpectedEvents)
                     .Select(pair => pair.Key)
                     .Order())
        {
            var transformFrame = ClassifySymbolsAt(
                samples,
                sampleRate,
                offset,
                hopSamples * 3);
            if (transformFrame != null)
            {
                frames.Add(transformFrame);
            }
        }

        return SuppressNearbyFrames(frames, sampleRate);
    }

    private static void AddCandidateScore(Dictionary<int, double> candidateScores, int offset, double score)
    {
        if (!candidateScores.TryGetValue(offset, out var existing) || score > existing)
        {
            candidateScores[offset] = score;
        }
    }

    private MimirChirpletTransformFrame? ClassifySymbolsAt(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int predictedOffset,
        int searchRadiusSamples)
    {
        var kernels = GetSymbolKernels(sampleRate);
        var candidates = new List<MimirChirpletSymbolCandidate>(Codebook.Symbols.Count);
        for (var symbol = 0; symbol < Codebook.Symbols.Count; symbol++)
        {
            var kernel = kernels[symbol];
            if (kernel.Length == 0 || samples.Length < kernel.Length)
            {
                continue;
            }

            var start = Math.Max(0, predictedOffset - searchRadiusSamples);
            var end = Math.Min(samples.Length - kernel.Length, predictedOffset + searchRadiusSamples);
            var bestEnergy = 0.0;
            var bestOffset = predictedOffset;
            var step = Math.Max(4, searchRadiusSamples / 16);
            for (var offset = start; offset <= end; offset += step)
            {
                var energy = MatchedEnergy(samples.Slice(offset, kernel.Length), kernel);
                if (energy > bestEnergy)
                {
                    bestEnergy = energy;
                    bestOffset = offset;
                }
            }

            var localStart = Math.Max(start, bestOffset - step);
            var localEnd = Math.Min(end, bestOffset + step);
            for (var offset = localStart; offset <= localEnd; offset++)
            {
                var energy = MatchedEnergy(samples.Slice(offset, kernel.Length), kernel);
                if (energy > bestEnergy)
                {
                    bestEnergy = energy;
                    bestOffset = offset;
                }
            }

            if (bestEnergy >= 0.035)
            {
                var refinedOffset = RefineMatchedOffset(samples, kernel, bestOffset, start, end);
                var refinedEnergy = MatchedEnergy(
                    samples.Slice((int)Math.Round(refinedOffset), kernel.Length),
                    kernel);
                candidates.Add(new MimirChirpletSymbolCandidate(symbol, refinedOffset, Math.Max(bestEnergy, refinedEnergy)));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.Energy)
            .Take(MaxSymbolCandidatesPerFrame)
            .ToArray();
        if (ordered[0].Energy < 0.08)
        {
            return null;
        }

        return new MimirChirpletTransformFrame(
            ordered[0].SampleOffset,
            ordered);
    }

    private static double RefineMatchedOffset(
        ReadOnlySpan<float> samples,
        MimirChirpletToneKernel kernel,
        int offset,
        int start,
        int end)
    {
        var center = Math.Clamp(offset, start, end);
        if (center <= start || center >= end)
        {
            return center;
        }

        var left = MatchedEnergy(samples.Slice(center - 1, kernel.Length), kernel);
        var middle = MatchedEnergy(samples.Slice(center, kernel.Length), kernel);
        var right = MatchedEnergy(samples.Slice(center + 1, kernel.Length), kernel);
        var denominator = left - 2.0 * middle + right;
        if (Math.Abs(denominator) <= 1.0e-12)
        {
            return center;
        }

        return center + Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
    }

    private static IReadOnlyList<MimirChirpletTransformFrame> SuppressNearbyFrames(
        IReadOnlyList<MimirChirpletTransformFrame> frames,
        int sampleRate)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var minimumSpacingSamples = MinEventGapSeconds * sampleRate * 0.55;
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

    private static IReadOnlyList<MimirChirpletTimelineAnchor> DecodeTrellisAnchors(
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
            if (!IsConsecutiveByTime(firstFrame, secondFrame, sampleRate) ||
                !IsConsecutiveByTime(secondFrame, thirdFrame, sampleRate))
            {
                continue;
            }

            foreach (var first in firstFrame.Candidates)
            foreach (var second in secondFrame.Candidates)
            foreach (var third in thirdFrame.Candidates)
            {
                var code = TripleCode(first.SymbolId, second.SymbolId, third.SymbolId);
                if (!TripleToIndex.TryGetValue(code, out var eventIndex))
                {
                    continue;
                }

                var firstEvent = Default.EventForIndex((ulong)eventIndex);
                var secondEvent = Default.EventForIndex((ulong)eventIndex + 1UL);
                var thirdEvent = Default.EventForIndex((ulong)eventIndex + 2UL);
                var measuredGapA = (second.SampleOffset - first.SampleOffset) / sampleRate;
                var measuredGapB = (third.SampleOffset - second.SampleOffset) / sampleRate;
                var expectedGapA = secondEvent.StartSeconds - firstEvent.StartSeconds;
                var expectedGapB = thirdEvent.StartSeconds - secondEvent.StartSeconds;
                var gapError = Math.Abs(measuredGapA - expectedGapA) + Math.Abs(measuredGapB - expectedGapB);
                var gapConfidence = 1.0 / (1.0 + gapError / 0.004);
                if (gapConfidence < 0.50)
                {
                    continue;
                }

                var energyConfidence = Math.Clamp((first.Energy + second.Energy + third.Energy) / 3.0, 0.0, 1.0);
                candidates.Add(new MimirChirpletTimelineAnchor(
                    (ulong)eventIndex,
                    firstEvent.StartSeconds,
                    first.SampleOffset,
                    gapConfidence * 0.70 + energyConfidence * 0.30,
                    [
                        new MimirChirpletSymbolObservation(first.SymbolId, first.SampleOffset, first.Energy),
                        new MimirChirpletSymbolObservation(second.SymbolId, second.SampleOffset, second.Energy),
                        new MimirChirpletSymbolObservation(third.SymbolId, third.SampleOffset, third.Energy),
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
                .Where(anchor =>
                    Math.Abs((anchor.SampleOffset - anchor.TimelineSeconds * sampleRate) - seedOffset) <= sampleRate * 0.008)
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

        if (bestPath.Count == 0)
        {
            return [];
        }

        var finalClock = FitClock(bestPath, sampleRate);
        if (finalClock == null)
        {
            return [];
        }

        return bestPath
            .Where(anchor =>
                Math.Abs(anchor.SampleOffset - finalClock.SampleForTimelineSeconds(anchor.TimelineSeconds)) <=
                Math.Max(24.0, sampleRate * 0.002))
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
            effectiveSampleRate < sampleRate * 0.90 ||
            effectiveSampleRate > sampleRate * 1.10)
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
        var residualConfidence = 1.0 / (1.0 + meanAbsoluteError / Math.Max(1.0, sampleRate * 0.0015));
        var countConfidence = Math.Clamp(anchors.Count / 12.0, 0.0, 1.0);
        var anchorConfidence = Math.Clamp(anchors.Average(anchor => anchor.Confidence), 0.0, 1.0);
        return new MimirChirpletClockFit(
            sourceOffset,
            effectiveSampleRate,
            residualConfidence * 0.45 + countConfidence * 0.25 + anchorConfidence * 0.30,
            anchors.Count,
            meanAbsoluteError);
    }

    private static bool IsConsecutiveByTime(
        MimirChirpletSymbolObservation first,
        MimirChirpletSymbolObservation second,
        int sampleRate)
    {
        var seconds = (second.SampleOffset - first.SampleOffset) / sampleRate;
        return seconds >= MinEventGapSeconds * 0.55 && seconds <= MaxEventGapSeconds * 1.45;
    }

    private static bool IsConsecutiveByTime(
        MimirChirpletTransformFrame first,
        MimirChirpletTransformFrame second,
        int sampleRate)
    {
        var seconds = (second.SampleOffset - first.SampleOffset) / sampleRate;
        return seconds >= MinEventGapSeconds * 0.55 && seconds <= MaxEventGapSeconds * 1.45;
    }

    private static int SymbolForEvent(ulong eventIndex) =>
        TimelineSymbols[(int)(eventIndex % (ulong)TimelineSymbols.Length)];

    private static double EventStartSeconds(ulong eventIndex) =>
        FirstEventSeconds +
        (eventIndex / (ulong)TimelineSymbols.Length) * PeriodDurationSeconds +
        PeriodEventStarts[(int)(eventIndex % (ulong)TimelineSymbols.Length)];

    private static int[] BuildDeBruijn(int alphabetSize, int order)
    {
        var a = new int[alphabetSize * order];
        var sequence = new List<int>(TimelinePeriod);

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

    private static double[] BuildPeriodEventStarts(IReadOnlyList<int> symbols)
    {
        var starts = new double[symbols.Count];
        var cursor = 0.0;
        for (var index = 0; index < symbols.Count; index++)
        {
            starts[index] = cursor;
            cursor += Codebook[symbols[index]].GapSeconds;
        }

        return starts;
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
            map[code] = index;
        }

        return map;
    }

    private static int TripleCode(int first, int second, int third) =>
        first * SymbolCount * SymbolCount + second * SymbolCount + third;

    private static MimirChirpletToneKernel RenderToneKernel(MimirChirpletTone tone, int sampleRate)
    {
        var frameCount = Math.Max(1, (int)Math.Ceiling(tone.DurationSeconds * sampleRate));
        var sine = new float[frameCount];
        var cosine = new float[frameCount];
        AddTone(sine, sampleRate, tone with { StartSeconds = 0.0 }, segmentStartSeconds: 0.0, phaseOffsetRadians: 0.0);
        AddTone(cosine, sampleRate, tone with { StartSeconds = 0.0 }, segmentStartSeconds: 0.0, phaseOffsetRadians: Math.PI * 0.5);
        return new MimirChirpletToneKernel(sine, cosine);
    }

    private IReadOnlyList<MimirChirpletToneKernel> GetSymbolKernels(int sampleRate)
    {
        if (symbolKernelsBySampleRate.TryGetValue(sampleRate, out var kernels))
        {
            return kernels;
        }

        kernels = Codebook.Symbols.Select(symbol => RenderToneKernel(symbol.Tone, sampleRate)).ToArray();
        symbolKernelsBySampleRate[sampleRate] = kernels;
        return kernels;
    }

    private static void AddTone(
        float[] samples,
        int sampleRate,
        MimirChirpletTone tone,
        double segmentStartSeconds,
        double phaseOffsetRadians = 0.0)
    {
        var relativeStartSeconds = tone.StartSeconds - segmentStartSeconds;
        var startFrame = (int)Math.Round(relativeStartSeconds * sampleRate);
        var frameCount = Math.Max(1, (int)Math.Round(tone.DurationSeconds * sampleRate));
        for (var frame = 0; frame < frameCount; frame++)
        {
            var outputFrame = startFrame + frame;
            if (outputFrame < 0 || outputFrame >= samples.Length)
            {
                continue;
            }

            var normalized = frameCount <= 1 ? 1.0 : frame / (double)(frameCount - 1);
            var t = frame / (double)sampleRate;
            var envelope = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * normalized);
            var phase = 2.0 * Math.PI * (tone.StartHz * t + 0.5 * (tone.EndHz - tone.StartHz) * t * normalized);
            samples[outputFrame] += (float)(Math.Sin(phase + phaseOffsetRadians) * envelope * tone.Gain);
        }
    }

    private static double MatchedEnergy(ReadOnlySpan<float> samples, MimirChirpletToneKernel kernel)
    {
        var count = Math.Min(samples.Length, kernel.Length);
        if (count == 0)
        {
            return 0.0;
        }

        var sineDot = 0.0;
        var cosineDot = 0.0;
        var sampleEnergy = 0.0;
        var index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var sineDotVector = Vector<float>.Zero;
            var cosineDotVector = Vector<float>.Zero;
            var sampleEnergyVector = Vector<float>.Zero;
            var width = Vector<float>.Count;
            for (; index <= count - width; index += width)
            {
                var sampleVector = new Vector<float>(samples.Slice(index, width));
                sineDotVector += sampleVector * new Vector<float>(kernel.Sine.AsSpan(index, width));
                cosineDotVector += sampleVector * new Vector<float>(kernel.Cosine.AsSpan(index, width));
                sampleEnergyVector += sampleVector * sampleVector;
            }

            for (var lane = 0; lane < width; lane++)
            {
                sineDot += sineDotVector[lane];
                cosineDot += cosineDotVector[lane];
                sampleEnergy += sampleEnergyVector[lane];
            }
        }

        for (; index < count; index++)
        {
            var sample = samples[index];
            var sine = kernel.Sine[index];
            var cosine = kernel.Cosine[index];
            sineDot += sample * sine;
            cosineDot += sample * cosine;
            sampleEnergy += sample * sample;
        }

        var denominator = Math.Sqrt(sampleEnergy * kernel.Energy);
        if (denominator <= 1.0e-12)
        {
            return 0.0;
        }

        return Math.Min(1.0, Math.Sqrt(sineDot * sineDot + cosineDot * cosineDot) / denominator);
    }

    private IReadOnlyList<double> BuildChirpletEnergyTrace(ReadOnlySpan<float> samples, int sampleRate, int hopSamples)
    {
        var kernels = GetSymbolKernels(sampleRate);
        var maxKernelLength = kernels.Select(kernel => kernel.Length).DefaultIfEmpty(0).Max();
        if (samples.Length < maxKernelLength || maxKernelLength == 0)
        {
            return [];
        }

        var output = new double[1 + (samples.Length - maxKernelLength) / hopSamples];
        for (var frame = 0; frame < output.Length; frame++)
        {
            var offset = frame * hopSamples;
            var best = 0.0;
            for (var symbol = 0; symbol < kernels.Count; symbol++)
            {
                var kernel = kernels[symbol];
                if (offset + kernel.Length > samples.Length)
                {
                    continue;
                }

                best = Math.Max(best, MatchedEnergy(samples.Slice(offset, kernel.Length), kernel));
            }

            output[frame] = best;
        }

        return output;
    }

    private static float[] BuildEnvelopeEnergyTrace(ReadOnlySpan<float> samples, int sampleRate, int hopSamples)
    {
        var windowSamples = Math.Max(1, (int)Math.Round(MaxEventDurationSeconds * sampleRate));
        if (samples.Length < windowSamples)
        {
            return [];
        }

        var output = new float[1 + (samples.Length - windowSamples) / hopSamples];
        var energy = 0.0;
        for (var index = 0; index < windowSamples; index++)
        {
            energy += samples[index] * samples[index];
        }

        output[0] = (float)Math.Sqrt(energy / windowSamples);
        for (var frame = 1; frame < output.Length; frame++)
        {
            var previousStart = (frame - 1) * hopSamples;
            var nextStart = frame * hopSamples;
            for (var index = previousStart; index < nextStart; index++)
            {
                energy -= samples[index] * samples[index];
            }

            for (var index = previousStart + windowSamples; index < nextStart + windowSamples; index++)
            {
                energy += samples[index] * samples[index];
            }

            output[frame] = (float)Math.Sqrt(Math.Max(0.0, energy) / windowSamples);
        }

        return output;
    }

    private static double MaxMatchedEnergy(ReadOnlySpan<float> samples, MimirChirpletToneKernel kernel, int hopSamples)
    {
        if (samples.Length < kernel.Length || kernel.Length == 0)
        {
            return 0.0;
        }

        var best = 0.0;
        for (var offset = 0; offset <= samples.Length - kernel.Length; offset += hopSamples)
        {
            best = Math.Max(best, MatchedEnergy(samples.Slice(offset, kernel.Length), kernel));
        }

        return best;
    }

    private static float[] ContrastNormalize(float[] samples)
    {
        if (samples.Length == 0)
        {
            return samples;
        }

        var mean = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            mean += samples[index];
        }

        mean /= samples.Length;
        var variance = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            var centered = samples[index] - mean;
            variance += centered * centered;
        }

        var deviation = Math.Sqrt(variance / samples.Length);
        if (deviation <= 1.0e-12)
        {
            return samples;
        }

        var output = new float[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            var z = (samples[index] - mean) / deviation;
            output[index] = z > 0.0 ? (float)(z * z) : 0.0f;
        }

        return output;
    }

    private sealed record MimirChirpletToneKernel(float[] Sine, float[] Cosine)
    {
        public int Length => Sine.Length;

        public double Energy { get; } = Math.Max(
            Sine.Sum(sample => sample * sample),
            Cosine.Sum(sample => sample * sample));
    }
}
