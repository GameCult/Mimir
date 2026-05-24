using System.Collections.Concurrent;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirBioacousticSyllable(
    double StartSeconds,
    double DurationSeconds,
    double StartHz,
    double EndHz,
    double Weight);

public sealed record MimirBioacousticMotifDefinition(
    int SymbolId,
    IReadOnlyList<MimirBioacousticSyllable> Syllables);

internal sealed record BioacousticKernel(
    double[] Samples,
    IReadOnlyList<double> CenterFrequencies,
    double Energy);

internal sealed record BioacousticKernelSet(
    BioacousticKernel[] Symbols);

public enum MimirBioacousticSpeaker
{
    Left = 0,
    Right = 1,
}

public sealed class MimirBioacousticTimeline
{
    public const int SampleRate = 48_000;
    public const double SegmentSeconds = 0.5;
    public const int WordCount = 128;
    public const int SpeakerCount = 2;
    public const int SymbolCount = WordCount * SpeakerCount;
    public const int TimelineOrder = 1;

    private const double FirstEventSeconds = 0.08;
    private const double EventSpacingSeconds = 0.16;
    private const double MotifDurationSeconds = 0.118;
    private const double LowestRootHz = 2_600.0;
    private const double HighestRootHz = 9_600.0;
    private const double Gain = 0.030;
    private const int MaxSymbolCandidatesPerFrame = 3;
    private const double ProposalBudgetMultiplier = 4.0;

    private static readonly MimirBioacousticMotifDefinition[] Motifs = BuildMotifs();
    private static readonly ConcurrentDictionary<int, BioacousticKernelSet> KernelSets = new();

    public static MimirBioacousticTimeline Default { get; } = new();

    public IReadOnlyList<MimirBioacousticMotifDefinition> Codebook => Motifs;

    public float[] RenderSegmentMonoFloat(ulong segmentIndex)
        => RenderSegmentMonoFloat(segmentIndex, SampleRate);

    public float[] RenderSegmentMonoFloat(ulong segmentIndex, int sampleRate)
    {
        var segmentStartSeconds = segmentIndex * SegmentSeconds;
        var samples = new float[(int)Math.Round(SegmentSeconds * sampleRate)];
        foreach (var timelineEvent in EventsOverlapping(segmentStartSeconds, SegmentSeconds))
        {
            AddMotif(samples, sampleRate, timelineEvent, segmentStartSeconds, Gain);
        }

        return samples;
    }

    public float[] RenderSegmentMonoFloat(ulong segmentIndex, int sampleRate, MimirBioacousticSpeaker speaker)
    {
        var segmentStartSeconds = segmentIndex * SegmentSeconds;
        var samples = new float[(int)Math.Round(SegmentSeconds * sampleRate)];
        foreach (var timelineEvent in EventsOverlapping(segmentStartSeconds, SegmentSeconds)
                     .Where(timelineEvent => SpeakerForSymbol(timelineEvent.SymbolId) == speaker))
        {
            AddMotif(samples, sampleRate, timelineEvent, segmentStartSeconds, Gain);
        }

        return samples;
    }

    public float[] RenderEventMonoFloat(ulong eventIndex, int sampleRate)
    {
        var samples = new float[(int)Math.Round(MotifDurationSeconds * sampleRate)];
        AddMotif(samples, sampleRate, EventForIndex(eventIndex), EventStartSeconds(eventIndex), Gain);
        return samples;
    }

    public MimirChirpletStreamDecode DecodeStreamWindow(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return new MimirChirpletStreamDecode([], [], [], null, []);
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
        if (clock != null)
        {
            clock = clock with
            {
                SourceOffsetSamples = RefineSourceOffset(samples, sampleRate, clock.SourceOffsetSamples),
            };
        }

        return new MimirChirpletStreamDecode(frames, symbols, anchors, clock, EstimateBandResponse(frames));
    }

    public MimirChirpletTimelineEvent EventForIndex(ulong eventIndex)
    {
        var symbolId = SymbolForEvent(eventIndex);
        var startSeconds = EventStartSeconds(eventIndex);
        var motif = Motifs[symbolId];
        var first = motif.Syllables[0];
        return new MimirChirpletTimelineEvent(
            eventIndex,
            symbolId,
            startSeconds,
            new MimirChirpletTone(
                startSeconds,
                MotifDurationSeconds,
                first.StartHz,
                motif.Syllables[^1].EndHz,
                Gain));
    }

    public IReadOnlyList<MimirChirpletTimelineEvent> EventsOverlapping(double startSeconds, double durationSeconds)
    {
        var endSeconds = startSeconds + durationSeconds;
        var firstIndex = Math.Max(0, (long)Math.Floor((startSeconds - FirstEventSeconds - MotifDurationSeconds) / EventSpacingSeconds) - 2);
        var lastIndex = Math.Max(firstIndex, (long)Math.Ceiling((endSeconds - FirstEventSeconds) / EventSpacingSeconds) + 2);
        var events = new List<MimirChirpletTimelineEvent>((int)(lastIndex - firstIndex + 1));
        for (var eventIndex = firstIndex; eventIndex <= lastIndex; eventIndex++)
        {
            var timelineEvent = EventForIndex((ulong)eventIndex);
            if (timelineEvent.StartSeconds < endSeconds &&
                timelineEvent.StartSeconds + MotifDurationSeconds > startSeconds)
            {
                events.Add(timelineEvent);
            }
        }

        return events;
    }

    private static IReadOnlyList<MimirChirpletTransformFrame> DetectFrames(ReadOnlySpan<float> samples, int sampleRate)
    {
        var motifSamples = Math.Max(1, (int)Math.Round(MotifDurationSeconds * sampleRate));
        if (samples.Length < motifSamples)
        {
            return [];
        }

        var hopSamples = Math.Max(1, sampleRate / 1_000);
        var energyTrace = BuildWindowEnergyTrace(samples, motifSamples, hopSamples);
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

        var maxExpectedEvents = Math.Max(
            8,
            (int)Math.Ceiling(samples.Length / (double)sampleRate / EventSpacingSeconds * ProposalBudgetMultiplier));
        var frames = new List<MimirChirpletTransformFrame>();
        foreach (var proposal in proposals
                     .OrderByDescending(offset => energyTrace[Math.Clamp(offset / hopSamples, 0, energyTrace.Length - 1)])
                     .Take(maxExpectedEvents)
                     .Order())
        {
            var frame = ClassifyAt(samples, sampleRate, proposal, Math.Max(2, sampleRate / 600));
            if (frame != null)
            {
                frames.Add(frame);
            }
        }

        if (frames.Count < Math.Max(3, maxExpectedEvents / 3))
        {
            var denseStep = Math.Max(1, sampleRate / 250);
            for (var offset = 0; offset <= samples.Length - motifSamples; offset += denseStep)
            {
                var frame = ClassifyAt(samples, sampleRate, offset, denseStep);
                if (frame != null)
                {
                    frames.Add(frame);
                }
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
        var motifSamples = Math.Max(1, (int)Math.Round(MotifDurationSeconds * sampleRate));
        var start = Math.Max(0, predictedOffset - searchRadiusSamples);
        var end = Math.Min(samples.Length - motifSamples, predictedOffset + searchRadiusSamples);
        if (start > end)
        {
            return null;
        }

        var bestOffset = predictedOffset;
        MimirChirpletSymbolCandidate[] bestCandidates = [];
        var bestEnergy = 0.0;
        var step = Math.Max(1, sampleRate / 8_000);
        for (var offset = start; offset <= end; offset += step)
        {
            var candidates = ScoreMotifs(samples.Slice(offset, motifSamples), sampleRate, offset);
            var energy = candidates.Length == 0 ? 0.0 : candidates[0].Energy;
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestOffset = offset;
                bestCandidates = candidates;
            }
        }

        if (bestCandidates.Length == 0 || bestEnergy < 0.075)
        {
            return null;
        }

        var refinedOffset = RefineOffset(samples, sampleRate, bestOffset, bestCandidates[0].SymbolId, motifSamples);
        return new MimirChirpletTransformFrame(
            refinedOffset,
            bestCandidates
                .Select(candidate => candidate with { SampleOffset = refinedOffset })
                .OrderByDescending(candidate => candidate.Energy)
                .ToArray());
    }

    private static MimirChirpletSymbolCandidate[] ScoreMotifs(
        ReadOnlySpan<float> samples,
        int sampleRate,
        double sampleOffset)
    {
        var kernels = KernelSets.GetOrAdd(sampleRate, BuildKernelSet);
        var sampleEnergy = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            sampleEnergy += samples[index] * samples[index];
        }

        if (sampleEnergy <= 1.0e-12)
        {
            return [];
        }

        var candidates = new List<MimirChirpletSymbolCandidate>(Motifs.Length);
        for (var symbol = 0; symbol < kernels.Symbols.Length; symbol++)
        {
            var kernel = kernels.Symbols[symbol];
            var dot = 0.0;
            for (var index = 0; index < samples.Length; index++)
            {
                dot += samples[index] * kernel.Samples[index];
            }

            var energy = Math.Abs(dot) / Math.Sqrt(sampleEnergy * kernel.Energy);
            var bandResponses = kernel.CenterFrequencies
                .Select(center => new MimirChirpletBandResponse(center, energy))
                .ToArray();
            candidates.Add(new MimirChirpletSymbolCandidate(symbol, sampleOffset, energy, bandResponses));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Energy)
            .Take(MaxSymbolCandidatesPerFrame)
            .ToArray();
    }

    private static double RefineOffset(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int bestOffset,
        int symbolId,
        int motifSamples)
    {
        if (bestOffset <= 0 || bestOffset >= samples.Length - motifSamples - 1)
        {
            return bestOffset;
        }

        var left = ScoreSymbolEnergy(samples.Slice(bestOffset - 1, motifSamples), sampleRate, symbolId);
        var center = ScoreSymbolEnergy(samples.Slice(bestOffset, motifSamples), sampleRate, symbolId);
        var right = ScoreSymbolEnergy(samples.Slice(bestOffset + 1, motifSamples), sampleRate, symbolId);
        var denominator = left - 2.0 * center + right;
        if (Math.Abs(denominator) <= 1.0e-12)
        {
            return bestOffset;
        }

        return bestOffset + Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
    }

    private static double ScoreSymbolEnergy(ReadOnlySpan<float> samples, int sampleRate, int symbolId)
    {
        var kernels = KernelSets.GetOrAdd(sampleRate, BuildKernelSet);
        var kernel = kernels.Symbols[symbolId];
        var sampleEnergy = 0.0;
        var dot = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            sampleEnergy += samples[index] * samples[index];
            dot += samples[index] * kernel.Samples[index];
        }

        return sampleEnergy <= 1.0e-12 || kernel.Energy <= 1.0e-12
            ? 0.0
            : Math.Abs(dot) / Math.Sqrt(sampleEnergy * kernel.Energy);
    }

    private static IReadOnlyList<MimirChirpletTimelineAnchor> DecodeAnchors(
        IReadOnlyList<MimirChirpletTransformFrame> frames,
        int sampleRate)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var candidates = new List<MimirChirpletTimelineAnchor>();
        foreach (var frame in frames)
        {
            foreach (var candidate in frame.Candidates)
            {
                var eventIndex = EventIndexForSymbol(candidate.SymbolId);
                var expectedEvent = Default.EventForIndex((ulong)eventIndex);
                var speakerConfidence = SpeakerForSymbol(candidate.SymbolId) == SpeakerForEvent((ulong)eventIndex)
                    ? 1.0
                    : 0.25;
                var energyConfidence = Math.Clamp(candidate.Energy, 0.0, 1.0);
                candidates.Add(new MimirChirpletTimelineAnchor(
                    (ulong)eventIndex,
                    expectedEvent.StartSeconds,
                    candidate.SampleOffset,
                    energyConfidence * speakerConfidence,
                    [new MimirChirpletSymbolObservation(
                        candidate.SymbolId,
                        candidate.SampleOffset,
                        candidate.Energy)]));
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
        foreach (var seedOffset in candidates
                     .OrderByDescending(anchor => anchor.Confidence)
                     .Take(64)
                     .Select(anchor => anchor.SampleOffset - anchor.TimelineSeconds * sampleRate))
        {
            var path = candidates
                .Where(anchor => Math.Abs((anchor.SampleOffset - anchor.TimelineSeconds * sampleRate) - seedOffset) <= sampleRate * 0.010)
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
                clock.MeanAbsoluteErrorSamples / Math.Max(1.0, sampleRate * 0.0015);
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
            .Where(anchor => Math.Abs(anchor.SampleOffset - finalClock.SampleForTimelineSeconds(anchor.TimelineSeconds)) <= Math.Max(24.0, sampleRate * 0.002))
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
        var residualConfidence = 1.0 / (1.0 + meanAbsoluteError / Math.Max(1.0, sampleRate * 0.001));
        var countConfidence = Math.Clamp(anchors.Count / 12.0, 0.0, 1.0);
        var anchorConfidence = Math.Clamp(anchors.Average(anchor => anchor.Confidence), 0.0, 1.0);
        return new MimirChirpletClockFit(
            sourceOffset,
            effectiveSampleRate,
            residualConfidence * 0.45 + countConfidence * 0.25 + anchorConfidence * 0.30,
            anchors.Count,
            meanAbsoluteError);
    }

    private static IReadOnlyList<MimirChirpletBandResponse> EstimateBandResponse(IReadOnlyList<MimirChirpletTransformFrame> frames)
    {
        return frames
            .SelectMany(frame => frame.BestCandidate.BandResponses ?? [])
            .GroupBy(response => response.CenterHz)
            .Select(group => new MimirChirpletBandResponse(group.Key, group.Average(response => response.Energy)))
            .OrderBy(response => response.CenterHz)
            .ToArray();
    }

    private static double RefineSourceOffset(ReadOnlySpan<float> samples, int sampleRate, double initialOffsetSamples)
    {
        var radius = Math.Max(4, (int)Math.Ceiling(sampleRate * 0.00010));
        var center = (int)Math.Round(initialOffsetSamples);
        var firstOffset = center - radius;
        var lastOffset = center + radius;
        var bestOffset = center;
        var bestScore = double.NegativeInfinity;
        for (var offset = firstOffset; offset <= lastOffset; offset++)
        {
            var score = ScheduledWaveformScore(samples, sampleRate, offset);
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }

        if (bestOffset <= firstOffset || bestOffset >= lastOffset)
        {
            return bestOffset;
        }

        var left = ScheduledWaveformScore(samples, sampleRate, bestOffset - 1);
        var middle = ScheduledWaveformScore(samples, sampleRate, bestOffset);
        var right = ScheduledWaveformScore(samples, sampleRate, bestOffset + 1);
        var denominator = left - 2.0 * middle + right;
        if (Math.Abs(denominator) <= 1.0e-12)
        {
            return bestOffset;
        }

        return bestOffset + Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
    }

    private static double ScheduledWaveformScore(ReadOnlySpan<float> samples, int sampleRate, int sourceOffsetSamples)
    {
        var timelineStartSeconds = -sourceOffsetSamples / (double)sampleRate;
        var timelineDurationSeconds = samples.Length / (double)sampleRate;
        var reference = new float[samples.Length];
        foreach (var timelineEvent in Default.EventsOverlapping(timelineStartSeconds, timelineDurationSeconds))
        {
            AddMotif(reference, sampleRate, timelineEvent, timelineStartSeconds, 1.0);
        }

        var dot = 0.0;
        var sampleEnergy = 0.0;
        var referenceEnergy = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            dot += samples[index] * reference[index];
            sampleEnergy += samples[index] * samples[index];
            referenceEnergy += reference[index] * reference[index];
        }

        return sampleEnergy <= 1.0e-12 || referenceEnergy <= 1.0e-12
            ? double.NegativeInfinity
            : dot / Math.Sqrt(sampleEnergy * referenceEnergy);
    }

    private static float[] BuildWindowEnergyTrace(ReadOnlySpan<float> samples, int windowSamples, int hopSamples)
    {
        var output = new float[1 + (samples.Length - windowSamples) / hopSamples];
        var prefix = new double[samples.Length + 1];
        for (var index = 0; index < samples.Length; index++)
        {
            prefix[index + 1] = prefix[index] + samples[index] * samples[index];
        }

        for (var frame = 0; frame < output.Length; frame++)
        {
            var offset = frame * hopSamples;
            output[frame] = (float)((prefix[offset + windowSamples] - prefix[offset]) / windowSamples);
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
        return mean + Math.Sqrt(Math.Max(0.0, variance)) * 0.18;
    }

    private static IReadOnlyList<MimirChirpletTransformFrame> SuppressNearbyFrames(
        IReadOnlyList<MimirChirpletTransformFrame> frames,
        int sampleRate)
    {
        var minimumSpacingSamples = EventSpacingSeconds * sampleRate * 0.55;
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

    private static void AddMotif(
        float[] samples,
        int sampleRate,
        MimirChirpletTimelineEvent timelineEvent,
        double segmentStartSeconds,
        double gain)
    {
        var motif = Motifs[timelineEvent.SymbolId];
        foreach (var syllable in motif.Syllables)
        {
            AddSyllable(samples, sampleRate, timelineEvent.StartSeconds - segmentStartSeconds, syllable, gain);
        }
    }

    private static void AddSyllable(
        float[] samples,
        int sampleRate,
        double motifStartSeconds,
        MimirBioacousticSyllable syllable,
        double gain)
    {
        var startFrame = (int)Math.Round((motifStartSeconds + syllable.StartSeconds) * sampleRate);
        var frameCount = Math.Max(1, (int)Math.Round(syllable.DurationSeconds * sampleRate));
        var slope = Math.Log(syllable.EndHz / syllable.StartHz) / syllable.DurationSeconds;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var outputFrame = startFrame + frame;
            if (outputFrame < 0 || outputFrame >= samples.Length)
            {
                continue;
            }

            var normalized = frameCount <= 1 ? 1.0 : frame / (double)(frameCount - 1);
            var t = frame / (double)sampleRate;
            var envelope = RaisedCosineEnvelope(normalized);
            var instantaneous = syllable.StartHz * Math.Exp(slope * t);
            var phase = 2.0 * Math.PI * syllable.StartHz * (Math.Exp(slope * t) - 1.0) / slope;
            var voiced = Math.Sin(phase);
            voiced += 0.42 * Math.Sin(phase * 1.5 + 0.35);
            voiced += 0.24 * Math.Sin(phase * 2.0 + 0.72);
            voiced += 0.10 * Math.Sin(phase * 2.75 + 1.10);
            var air = Math.Sin(2.0 * Math.PI * instantaneous * t * 0.071 + syllable.StartHz * 0.0003);
            samples[outputFrame] += (float)((voiced + 0.08 * air) * envelope * syllable.Weight * gain);
        }
    }

    private static double RaisedCosineEnvelope(double normalized) =>
        0.5 - 0.5 * Math.Cos(2.0 * Math.PI * Math.Clamp(normalized, 0.0, 1.0));

    private static BioacousticKernelSet BuildKernelSet(int sampleRate)
    {
        var motifSamples = Math.Max(1, (int)Math.Round(MotifDurationSeconds * sampleRate));
        var kernels = new BioacousticKernel[Motifs.Length];
        for (var symbol = 0; symbol < Motifs.Length; symbol++)
        {
            var samples = new float[motifSamples];
            AddMotif(
                samples,
                sampleRate,
                new MimirChirpletTimelineEvent(0, symbol, 0.0, new MimirChirpletTone(0.0, MotifDurationSeconds, 0.0, 0.0, 1.0)),
                0.0,
                1.0);
            var kernel = new double[motifSamples];
            var energy = 0.0;
            for (var index = 0; index < motifSamples; index++)
            {
                kernel[index] = samples[index];
                energy += kernel[index] * kernel[index];
            }

            kernels[symbol] = new BioacousticKernel(
                kernel,
                Motifs[symbol].Syllables.Select(s => Math.Sqrt(s.StartHz * s.EndHz)).ToArray(),
                Math.Max(energy, 1.0e-12));
        }

        return new BioacousticKernelSet(kernels);
    }

    private static MimirBioacousticMotifDefinition[] BuildMotifs()
    {
        var contourBank = new[]
        {
            new[] { 1.00, 1.22, 1.09, 1.48, 1.18, 1.03, 1.34, 1.18 },
            new[] { 1.00, 0.84, 1.20, 1.07, 0.93, 1.32, 1.12, 0.98 },
            new[] { 1.00, 1.38, 1.60, 1.26, 1.07, 1.24, 1.45, 1.16 },
            new[] { 1.00, 1.11, 0.81, 1.02, 1.36, 1.57, 1.20, 1.42 },
            new[] { 1.00, 0.92, 1.42, 1.18, 1.53, 1.10, 0.96, 1.28 },
            new[] { 1.00, 1.30, 0.88, 1.16, 1.05, 1.51, 1.39, 1.08 },
            new[] { 1.00, 1.56, 1.24, 0.91, 1.15, 1.37, 0.98, 1.22 },
            new[] { 1.00, 0.78, 0.96, 1.44, 1.62, 1.25, 1.08, 1.33 },
        };
        var rhythmOffsets = new[]
        {
            new[] { 0.000, 0.024, 0.057, 0.091 },
            new[] { 0.000, 0.031, 0.049, 0.087 },
            new[] { 0.000, 0.019, 0.061, 0.082 },
            new[] { 0.000, 0.036, 0.064, 0.096 },
            new[] { 0.000, 0.027, 0.071, 0.094 },
            new[] { 0.000, 0.041, 0.059, 0.089 },
            new[] { 0.000, 0.022, 0.052, 0.103 },
            new[] { 0.000, 0.034, 0.076, 0.098 },
        };
        var motifs = new MimirBioacousticMotifDefinition[SymbolCount];
        var logLowest = Math.Log(LowestRootHz);
        var logStep = Math.Log(HighestRootHz / LowestRootHz) / (WordCount - 1);
        for (var symbol = 0; symbol < motifs.Length; symbol++)
        {
            var word = EventIndexForSymbol(symbol);
            var speaker = SpeakerForSymbol(symbol);
            var speakerShift = speaker == MimirBioacousticSpeaker.Left ? 0.94 : 1.08;
            var root = Math.Exp(logLowest + word * logStep) * speakerShift;
            var contour = contourBank[HashToRange(word, speaker, 0, contourBank.Length)];
            var rhythm = rhythmOffsets[HashToRange(word, speaker, 1, rhythmOffsets.Length)];
            var syllables = new MimirBioacousticSyllable[4];
            for (var syllable = 0; syllable < syllables.Length; syllable++)
            {
                var start = root * contour[syllable * 2];
                var end = root * contour[syllable * 2 + 1];
                var duration = 0.020 + 0.004 * HashToRange(word, speaker, 10 + syllable, 4);
                var weight = 1.0 - syllable * 0.08;
                if (speaker == MimirBioacousticSpeaker.Right)
                {
                    weight *= syllable % 2 == 0 ? 0.90 : 1.08;
                }

                syllables[syllable] = new MimirBioacousticSyllable(
                    rhythm[syllable],
                    duration,
                    Math.Clamp(start, 2_300.0, 14_600.0),
                    Math.Clamp(end, 2_300.0, 14_600.0),
                    weight);
            }

            motifs[symbol] = new MimirBioacousticMotifDefinition(symbol, syllables);
        }

        return motifs;
    }

    private static int SymbolForEvent(ulong eventIndex)
    {
        var word = (int)((eventIndex / SpeakerCount) % WordCount);
        return word * SpeakerCount + (int)SpeakerForEvent(eventIndex);
    }

    private static int EventIndexForSymbol(int symbolId) =>
        Math.Clamp(symbolId / SpeakerCount, 0, WordCount - 1) * SpeakerCount + (symbolId & 1);

    private static MimirBioacousticSpeaker SpeakerForEvent(ulong eventIndex) =>
        (eventIndex & 1UL) == 0UL ? MimirBioacousticSpeaker.Left : MimirBioacousticSpeaker.Right;

    private static MimirBioacousticSpeaker SpeakerForSymbol(int symbolId) =>
        (symbolId & 1) == 0 ? MimirBioacousticSpeaker.Left : MimirBioacousticSpeaker.Right;

    private static double EventStartSeconds(ulong eventIndex) =>
        FirstEventSeconds + eventIndex * EventSpacingSeconds;

    private static int HashToRange(int word, MimirBioacousticSpeaker speaker, int salt, int range)
    {
        var value = (uint)(word * 0x45d9f3b + ((int)speaker + 1) * 0x119de1f3 + salt * 0x27d4eb2d);
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        value ^= value >> 16;
        return (int)(value % (uint)Math.Max(1, range));
    }
}
