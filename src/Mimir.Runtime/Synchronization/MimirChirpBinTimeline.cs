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

internal sealed record ChirpBinScore(
    double Energy,
    double PhaseRadians);

internal sealed record ChirpBinTimelinePlan(
    int[] Symbols,
    int Order,
    int[] TimelineSymbols,
    Dictionary<string, int> CodeToIndex);

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
    private const double ProposalBudgetMultiplier = 2.5;

    private static readonly int[] TimelineSymbols = RotateToDistinctOpening(BuildDeBruijn(SymbolCount, TimelineOrder));
    private static readonly Dictionary<int, int> TripleToIndex = BuildTripleIndex(TimelineSymbols);
    private static readonly MimirChirpBinSymbolDefinition[] Symbols = BuildSymbols();
    private static readonly ConcurrentDictionary<int, ChirpBinKernelSet> KernelSets = new();
    private static readonly ConcurrentDictionary<string, ChirpBinTimelinePlan> TimelinePlans = new();

    public static MimirChirpBinTimeline Default { get; } = new();

    public IReadOnlyList<MimirChirpBinSymbolDefinition> Codebook => Symbols;

    public float[] RenderSegmentMonoFloat(ulong segmentIndex)
        => RenderSegmentMonoFloat(segmentIndex, SampleRate);

    public float[] RenderSegmentMonoFloat(
        ulong segmentIndex,
        int sampleRate,
        MimirChirpBinCodebookPlan? codebookPlan = null)
    {
        var segmentStartSeconds = segmentIndex * SegmentSeconds;
        var samples = new float[(int)Math.Round(SegmentSeconds * sampleRate)];
        foreach (var timelineEvent in EventsOverlapping(segmentStartSeconds, SegmentSeconds, codebookPlan))
        {
            AddChirp(samples, sampleRate, timelineEvent, segmentStartSeconds);
        }

        return samples;
    }

    public MimirChirpletStreamDecode DecodeStreamWindow(
        ReadOnlySpan<float> samples,
        int sampleRate,
        MimirChirpBinPathCalibration? calibration = null)
    {
        if (samples.Length == 0)
        {
            return new MimirChirpletStreamDecode([], [], [], null, []);
        }

        var frames = DetectFrames(samples, sampleRate, calibration);
        var symbols = frames
            .Select(frame => new MimirChirpletSymbolObservation(
                frame.BestCandidate.SymbolId,
                frame.SampleOffset,
                frame.BestCandidate.Energy))
            .ToArray();
        var anchors = DecodeAnchors(frames, sampleRate, calibration);
        var clock = FitClock(anchors, sampleRate);
        if (clock != null)
        {
            var refinedOffset = RefineSourceOffset(samples, sampleRate, clock.SourceOffsetSamples);
            clock = clock with { SourceOffsetSamples = refinedOffset };
        }

        return new MimirChirpletStreamDecode(frames, symbols, anchors, clock, EstimateBandResponse(frames));
    }

    public IReadOnlyList<MimirChirpBinConfusionObservation> CalibrationObservations(
        MimirChirpletStreamDecode decode,
        int sampleRate)
    {
        if (decode.ClockFit == null)
        {
            return [];
        }

        var observations = new List<MimirChirpBinConfusionObservation>(decode.Frames.Count);
        foreach (var frame in decode.Frames)
        {
            if (!TryExpectedEventForSample(frame.SampleOffset, sampleRate, decode.ClockFit, null, out var timelineEvent))
            {
                continue;
            }

            var best = frame.BestCandidate;
            var bestBand = best.BandResponses?
                .OrderByDescending(response => response.Energy)
                .FirstOrDefault();
            var expectedSample = decode.ClockFit.SampleForTimelineSeconds(timelineEvent.StartSeconds);
            observations.Add(new MimirChirpBinConfusionObservation(
                timelineEvent.Index,
                timelineEvent.SymbolId,
                best.SymbolId,
                SymbolCenterHz(Symbols[timelineEvent.SymbolId]),
                bestBand?.CenterHz ?? SymbolCenterHz(Symbols[best.SymbolId]),
                bestBand?.Energy ?? best.Energy,
                frame.SampleOffset - expectedSample,
                best.Energy,
                decode.ClockFit.SourceOffsetSamples,
                SymbolShift(best.SymbolId, timelineEvent.SymbolId),
                bestBand?.PhaseRadians ?? 0.0));
        }

        return observations;
    }

    public MimirChirpletTimelineEvent EventForIndex(ulong eventIndex)
        => EventForIndex(eventIndex, null);

    public MimirChirpletTimelineEvent EventForIndex(
        ulong eventIndex,
        MimirChirpBinCodebookPlan? codebookPlan)
    {
        var symbolId = SymbolForEvent(eventIndex, codebookPlan);
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

    public IReadOnlyList<MimirChirpletTimelineEvent> EventsOverlapping(
        double startSeconds,
        double durationSeconds,
        MimirChirpBinCodebookPlan? codebookPlan = null)
    {
        var endSeconds = startSeconds + durationSeconds;
        var firstIndex = Math.Max(0, (long)Math.Floor((startSeconds - FirstEventSeconds - ChirpDurationSeconds) / EventSpacingSeconds) - 2);
        var lastIndex = Math.Max(firstIndex, (long)Math.Ceiling((endSeconds - FirstEventSeconds) / EventSpacingSeconds) + 2);
        var events = new List<MimirChirpletTimelineEvent>((int)(lastIndex - firstIndex + 1));
        for (var eventIndex = firstIndex; eventIndex <= lastIndex; eventIndex++)
        {
            var timelineEvent = EventForIndex((ulong)eventIndex, codebookPlan);
            if (timelineEvent.StartSeconds < endSeconds &&
                timelineEvent.StartSeconds + ChirpDurationSeconds > startSeconds)
            {
                events.Add(timelineEvent);
            }
        }

        return events;
    }

    private static IReadOnlyList<MimirChirpletTransformFrame> DetectFrames(
        ReadOnlySpan<float> samples,
        int sampleRate,
        MimirChirpBinPathCalibration? calibration)
    {
        var chirpSamples = Math.Max(1, (int)Math.Round(ChirpDurationSeconds * sampleRate));
        if (samples.Length < chirpSamples)
        {
            return [];
        }

        var hopSamples = Math.Max(1, sampleRate / 1_000);
        var energyTrace = BuildWindowEnergyTrace(samples, chirpSamples, hopSamples);
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
            var frame = ClassifyAt(samples, sampleRate, proposal, sampleRate / 1_000, calibration);
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
        int searchRadiusSamples,
        MimirChirpBinPathCalibration? calibration)
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
        var step = Math.Max(2, sampleRate / 4_000);
        for (var offset = start; offset <= end; offset += step)
        {
            var candidates = ScoreBins(samples.Slice(offset, chirpSamples), sampleRate, offset, calibration);
            var energy = candidates.Length == 0 ? 0.0 : candidates[0].Energy;
            if (energy > bestEnergy || PreferLaterPlateauOffset(sampleRate, energy, bestEnergy, offset, bestOffset))
            {
                bestEnergy = energy;
                bestOffset = offset;
                bestCandidates = candidates;
            }
        }

        var localStep = Math.Max(1, step / 8);
        var localStart = Math.Max(start, bestOffset - localStep * 4);
        var localEnd = Math.Min(end, bestOffset + localStep * 4);
        for (var offset = localStart; offset <= localEnd; offset += localStep)
        {
            var candidates = ScoreBins(samples.Slice(offset, chirpSamples), sampleRate, offset, calibration);
            var energy = candidates.Length == 0 ? 0.0 : candidates[0].Energy;
            if (energy > bestEnergy || PreferLaterPlateauOffset(sampleRate, energy, bestEnergy, offset, bestOffset))
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
        var timingBinSamples = BinShiftSamples(sampleRate);
        var correctedCandidates = new List<MimirChirpletSymbolCandidate>(bestCandidates.Length * (MaxTimingBinShift * 2 + 1));
        foreach (var candidate in bestCandidates)
        {
            for (var timingBinShift = -MaxTimingBinShift; timingBinShift <= MaxTimingBinShift; timingBinShift++)
            {
                var canonicalSymbol = ShiftSymbol(candidate.SymbolId, -timingBinShift);
                var canonicalOffset = refinedOffset - timingBinShift * timingBinSamples;
                var roundedOffset = (int)Math.Round(canonicalOffset);
                if (roundedOffset <= 0 || roundedOffset >= samples.Length - chirpSamples - 1)
                {
                    continue;
                }

                var correctedOffset = RefineOffset(samples, sampleRate, roundedOffset, canonicalSymbol, chirpSamples);
                var correctedEnergy = ScoreSymbolEnergy(
                    samples.Slice((int)Math.Round(correctedOffset), chirpSamples),
                    sampleRate,
                    canonicalSymbol) * (calibration?.SymbolWeight(canonicalSymbol) ?? 1.0);
                correctedOffset -= calibration?.GroupDelayCorrectionSamples(canonicalSymbol) ?? 0.0;
                correctedCandidates.Add(new MimirChirpletSymbolCandidate(
                    canonicalSymbol,
                    correctedOffset,
                    Math.Max(candidate.Energy, correctedEnergy) / (1.0 + Math.Abs(timingBinShift) * 0.15),
                    candidate.BandResponses));
            }
        }

        return new MimirChirpletTransformFrame(
            refinedOffset,
            correctedCandidates
                .OrderByDescending(candidate => candidate.Energy)
                .ToArray());
    }

    private static MimirChirpletSymbolCandidate[] ScoreBins(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int sampleOffset,
        MimirChirpBinPathCalibration? calibration)
    {
        var kernels = KernelSets.GetOrAdd(sampleRate, BuildKernelSet);
        var scores = new ChirpBinScore[Symbols.Length];
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
            var energy = 2.0 * score.Energy / Math.Sqrt(sampleEnergy * kernels.ReferenceEnergy);
            scores[symbol] = score with { Energy = energy };
        }

        var bandResponses = scores
            .Select((score, symbol) => new MimirChirpletBandResponse(
                SymbolCenterHz(Symbols[symbol]),
                score.Energy,
                score.PhaseRadians))
            .ToArray();

        return scores
            .Select((score, symbol) => new MimirChirpletSymbolCandidate(
                symbol,
                sampleOffset - (calibration?.GroupDelayCorrectionSamples(symbol) ?? 0.0),
                score.Energy * (calibration?.SymbolWeight(symbol) ?? 1.0),
                bandResponses))
            .OrderByDescending(candidate => candidate.Energy)
            .Take(MaxSymbolCandidatesPerFrame)
            .ToArray();
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
        return 2.0 * score.Energy / Math.Sqrt(sampleEnergy * kernels.ReferenceEnergy);
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

    private static bool PreferLaterPlateauOffset(
        int sampleRate,
        double energy,
        double bestEnergy,
        int offset,
        int bestOffset)
    {
        return sampleRate <= SampleRate &&
            energy >= bestEnergy * 0.995 &&
            offset > bestOffset;
    }

    private static ChirpBinScore DechirpedBin(ReadOnlySpan<float> samples, ChirpBinKernel kernel)
    {
        var real = 0.0;
        var imaginary = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            real += samples[index] * kernel.Real[index];
            imaginary += samples[index] * kernel.Imaginary[index];
        }

        return new ChirpBinScore(
            Math.Sqrt(real * real + imaginary * imaginary),
            Math.Atan2(imaginary, real));
    }

    private static IReadOnlyList<MimirChirpletTimelineAnchor> DecodeAnchors(
        IReadOnlyList<MimirChirpletTransformFrame> frames,
        int sampleRate,
        MimirChirpBinPathCalibration? calibration)
    {
        var plan = GetTimelinePlan(calibration?.EmissionPlan);
        if (frames.Count < plan.Order)
        {
            return [];
        }

        var candidates = new List<MimirChirpletTimelineAnchor>();
        for (var index = 0; index <= frames.Count - plan.Order; index++)
        {
            var window = frames.Skip(index).Take(plan.Order).ToArray();
            var consecutive = true;
            for (var gap = 0; gap < window.Length - 1; gap++)
            {
                if (!IsConsecutive(window[gap], window[gap + 1], sampleRate))
                {
                    consecutive = false;
                    break;
                }
            }

            if (!consecutive)
            {
                continue;
            }

            foreach (var candidatePath in CandidateSymbolPaths(window))
            {
                var code = CodeKey(candidatePath.Select(candidate => candidate.SymbolId));
                if (!plan.CodeToIndex.TryGetValue(code, out var eventIndex))
                {
                    continue;
                }

                var firstEvent = Default.EventForIndex((ulong)eventIndex, calibration?.EmissionPlan);
                var gapError = 0.0;
                for (var gap = 0; gap < candidatePath.Count - 1; gap++)
                {
                    var measuredGap = (candidatePath[gap + 1].SampleOffset - candidatePath[gap].SampleOffset) / sampleRate;
                    gapError += Math.Abs(measuredGap - EventSpacingSeconds);
                }

                var gapConfidence = 1.0 / (1.0 + gapError / 0.003);
                if (gapConfidence < 0.55)
                {
                    continue;
                }

                var energyConfidence = Math.Clamp(candidatePath.Average(candidate => candidate.Energy), 0.0, 1.0);
                candidates.Add(new MimirChirpletTimelineAnchor(
                    (ulong)eventIndex,
                    firstEvent.StartSeconds,
                    candidatePath[0].SampleOffset,
                    gapConfidence * 0.60 + energyConfidence * 0.40,
                    candidatePath
                        .Select(candidate => new MimirChirpletSymbolObservation(candidate.SymbolId, candidate.SampleOffset, candidate.Energy))
                        .ToArray()));
            }
        }

        return SelectCoherentAnchorPath(candidates, sampleRate, calibration);
    }

    private static IEnumerable<IReadOnlyList<MimirChirpletSymbolCandidate>> CandidateSymbolPaths(
        IReadOnlyList<MimirChirpletTransformFrame> frames)
    {
        var path = new MimirChirpletSymbolCandidate[frames.Count];
        IEnumerable<IReadOnlyList<MimirChirpletSymbolCandidate>> Recurse(int index)
        {
            if (index == frames.Count)
            {
                yield return path.ToArray();
                yield break;
            }

            foreach (var candidate in frames[index].Candidates.OrderByDescending(candidate => candidate.Energy).Take(2))
            {
                path[index] = candidate;
                foreach (var child in Recurse(index + 1))
                {
                    yield return child;
                }
            }
        }

        return Recurse(0);
    }

    private static IReadOnlyList<MimirChirpletTimelineAnchor> SelectCoherentAnchorPath(
        IReadOnlyList<MimirChirpletTimelineAnchor> candidates,
        int sampleRate,
        MimirChirpBinPathCalibration? calibration)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        List<MimirChirpletTimelineAnchor> bestPath = [];
        var bestScore = double.NegativeInfinity;
        foreach (var seedOffset in CandidateDelaySeeds(candidates, sampleRate, calibration))
        {
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

    private static IEnumerable<double> CandidateDelaySeeds(
        IReadOnlyList<MimirChirpletTimelineAnchor> candidates,
        int sampleRate,
        MimirChirpBinPathCalibration? calibration)
    {
        if (calibration != null)
        {
            foreach (var hypothesis in calibration.DelayHypotheses
                         .OrderByDescending(hypothesis => hypothesis.Confidence * hypothesis.SupportCount)
                         .Take(16))
            {
                yield return hypothesis.DelaySamples;
            }
        }

        foreach (var seed in candidates.OrderByDescending(anchor => anchor.Confidence).Take(64))
        {
            yield return seed.SampleOffset - seed.TimelineSeconds * sampleRate;
        }
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

    private static IReadOnlyList<MimirChirpletBandResponse> EstimateBandResponse(IReadOnlyList<MimirChirpletTransformFrame> frames)
    {
        var responses = frames
            .SelectMany(frame => frame.BestCandidate.BandResponses ?? [])
            .GroupBy(response => response.CenterHz)
            .Select(group => new MimirChirpletBandResponse(group.Key, group.Average(response => response.Energy)))
            .OrderBy(response => response.CenterHz)
            .ToArray();
        return responses;
    }

    private static double RefineSourceOffset(ReadOnlySpan<float> samples, int sampleRate, double initialOffsetSamples)
    {
        var radius = Math.Max(4, (int)Math.Ceiling(sampleRate * 0.00008));
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
            AddChirp(reference, sampleRate, timelineEvent, timelineStartSeconds);
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

    private static double SymbolCenterHz(MimirChirpBinSymbolDefinition symbol) =>
        (BaseStartHz + BaseEndHz) * 0.5 + symbol.OffsetHz;

    private static int ShiftSymbol(int symbolId, int shift) =>
        (symbolId + shift + SymbolCount) % SymbolCount;

    private static int SymbolShift(int observedSymbolId, int expectedSymbolId)
    {
        var shift = observedSymbolId - expectedSymbolId;
        if (shift > SymbolCount / 2)
        {
            shift -= SymbolCount;
        }
        else if (shift < -SymbolCount / 2)
        {
            shift += SymbolCount;
        }

        return shift;
    }

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

    private static int SymbolForEvent(ulong eventIndex, MimirChirpBinCodebookPlan? codebookPlan)
    {
        var plan = GetTimelinePlan(codebookPlan);
        return plan.TimelineSymbols[(int)(eventIndex % (ulong)plan.TimelineSymbols.Length)];
    }

    private static double EventStartSeconds(ulong eventIndex) =>
        FirstEventSeconds + eventIndex * EventSpacingSeconds;

    private static bool TryExpectedEventForSample(
        double sampleOffset,
        int sampleRate,
        MimirChirpletClockFit clockFit,
        MimirChirpBinCodebookPlan? codebookPlan,
        out MimirChirpletTimelineEvent timelineEvent)
    {
        var timelineSeconds = (sampleOffset - clockFit.SourceOffsetSamples) / clockFit.EffectiveSampleRate;
        var eventIndex = (long)Math.Round((timelineSeconds - FirstEventSeconds) / EventSpacingSeconds);
        if (eventIndex < 0)
        {
            timelineEvent = default!;
            return false;
        }

        timelineEvent = Default.EventForIndex((ulong)eventIndex, codebookPlan);
        return Math.Abs(timelineSeconds - timelineEvent.StartSeconds) <= Math.Max(0.012, 6.0 / sampleRate);
    }

    private static ChirpBinTimelinePlan GetTimelinePlan(MimirChirpBinCodebookPlan? codebookPlan)
    {
        if (codebookPlan is not { IsAdaptive: true })
        {
            return TimelinePlans.GetOrAdd("default", _ => new ChirpBinTimelinePlan(
                Enumerable.Range(0, SymbolCount).ToArray(),
                TimelineOrder,
                TimelineSymbols,
                BuildCodeIndex(TimelineSymbols, TimelineOrder)));
        }

        var key = $"{codebookPlan.RecommendedOrder}:{string.Join(",", codebookPlan.ReliableSymbolIds)}";
        return TimelinePlans.GetOrAdd(key, _ =>
        {
            var symbols = codebookPlan.ReliableSymbolIds.Distinct().Order().ToArray();
            var alphabetSequence = RotateToDistinctOpening(BuildDeBruijn(symbols.Length, codebookPlan.RecommendedOrder));
            var timelineSymbols = alphabetSequence.Select(index => symbols[index]).ToArray();
            return new ChirpBinTimelinePlan(
                symbols,
                codebookPlan.RecommendedOrder,
                timelineSymbols,
                BuildCodeIndex(timelineSymbols, codebookPlan.RecommendedOrder));
        });
    }

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

    private static Dictionary<string, int> BuildCodeIndex(IReadOnlyList<int> symbols, int order)
    {
        var map = new Dictionary<string, int>(symbols.Count, StringComparer.Ordinal);
        for (var index = 0; index < symbols.Count; index++)
        {
            var code = CodeKey(Enumerable.Range(0, order).Select(offset => symbols[(index + offset) % symbols.Count]));
            if (!map.ContainsKey(code))
            {
                map[code] = index;
            }
        }

        return map;
    }

    private static string CodeKey(IEnumerable<int> symbols) =>
        string.Join(",", symbols);
}
