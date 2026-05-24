namespace Mimir.Runtime.Synchronization;

public sealed record MimirBioacousticWordObservation(
    ulong EventIndex,
    double SampleOffset,
    double Confidence);

public sealed record MimirBioacousticClockAnchor(
    ulong EventIndex,
    double TimelineSeconds,
    double SampleOffset,
    double Confidence);

public sealed record MimirBioacousticClockHypothesis(
    double SourceOffsetSamples,
    double EffectiveSampleRate,
    int AnchorCount,
    double AnchorCoverage,
    double MeanAbsoluteErrorSamples,
    double Confidence,
    double Score,
    IReadOnlyList<MimirBioacousticClockAnchor> Anchors)
{
    public double DelayMicroseconds(int sampleRate) => SourceOffsetSamples * 1_000_000.0 / sampleRate;
}

public sealed record MimirBioacousticClockSolverOptions(
    int MaxObservations = 24,
    double MinimumPairSpacingSeconds = 0.08,
    double InlierToleranceSeconds = 0.012,
    double MinimumRateRatio = 0.98,
    double MaximumRateRatio = 1.02,
    double FullAnchorConfidenceCount = 5.0);

public sealed class MimirBioacousticClockSolver(MimirBioacousticClockSolverOptions? options = null)
{
    private readonly MimirBioacousticClockSolverOptions options = options ?? new();

    public MimirBioacousticClockHypothesis? Fit(
        IReadOnlyList<MimirBioacousticWordObservation> observations,
        MimirBioacousticTimeline timeline,
        int sampleRate,
        int expectedEventCount)
    {
        var anchors = observations
            .Select(observation => new MimirBioacousticClockAnchor(
                observation.EventIndex,
                timeline.EventForIndex(observation.EventIndex).StartSeconds,
                observation.SampleOffset,
                Math.Clamp(observation.Confidence, 0.001, 1.0)))
            .OrderByDescending(anchor => anchor.Confidence)
            .Take(options.MaxObservations)
            .ToArray();
        if (anchors.Length == 0)
        {
            return null;
        }

        var candidates = new List<MimirBioacousticClockHypothesis>();
        foreach (var anchor in anchors)
        {
            AddCandidate(
                candidates,
                anchors,
                sampleRate,
                expectedEventCount,
                anchor.SampleOffset - anchor.TimelineSeconds * sampleRate,
                sampleRate);
        }

        if (anchors.Length >= 3)
        {
            for (var first = 0; first < anchors.Length; first++)
            {
                for (var second = first + 1; second < anchors.Length; second++)
                {
                    var dt = anchors[second].TimelineSeconds - anchors[first].TimelineSeconds;
                    if (Math.Abs(dt) < options.MinimumPairSpacingSeconds)
                    {
                        continue;
                    }

                    var rate = (anchors[second].SampleOffset - anchors[first].SampleOffset) / dt;
                    if (!double.IsFinite(rate) ||
                        rate < sampleRate * options.MinimumRateRatio ||
                        rate > sampleRate * options.MaximumRateRatio)
                    {
                        continue;
                    }

                    AddCandidate(
                        candidates,
                        anchors,
                        sampleRate,
                        expectedEventCount,
                        anchors[first].SampleOffset - anchors[first].TimelineSeconds * rate,
                        rate);
                }
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.MeanAbsoluteErrorSamples)
            .FirstOrDefault();
    }

    private void AddCandidate(
        List<MimirBioacousticClockHypothesis> candidates,
        IReadOnlyList<MimirBioacousticClockAnchor> anchors,
        int nominalSampleRate,
        int expectedEventCount,
        double sourceOffsetSamples,
        double effectiveSampleRate)
    {
        var tolerance = Math.Max(36.0, nominalSampleRate * options.InlierToleranceSeconds);
        var inliers = anchors
            .Where(anchor => Math.Abs(anchor.SampleOffset - (sourceOffsetSamples + anchor.TimelineSeconds * effectiveSampleRate)) <= tolerance)
            .ToArray();
        var refined = FitInliers(inliers, nominalSampleRate, expectedEventCount);
        if (refined != null)
        {
            candidates.Add(refined);
        }
    }

    private MimirBioacousticClockHypothesis? FitInliers(
        IReadOnlyList<MimirBioacousticClockAnchor> anchors,
        int nominalSampleRate,
        int expectedEventCount)
    {
        if (anchors.Count == 0)
        {
            return null;
        }

        var anchorCoverage = expectedEventCount <= 0 ? 0.0 : anchors.Count / (double)expectedEventCount;
        if (anchors.Count == 1)
        {
            var anchor = anchors[0];
            var offset = anchor.SampleOffset - anchor.TimelineSeconds * nominalSampleRate;
            var singleAnchorConfidence = anchor.Confidence * 0.20;
            return new MimirBioacousticClockHypothesis(
                offset,
                nominalSampleRate,
                1,
                anchorCoverage,
                0.0,
                singleAnchorConfidence,
                singleAnchorConfidence,
                anchors.ToArray());
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

        var effectiveSampleRate = anchors.Count >= 3 && variance > 1.0e-12 ? covariance / variance : nominalSampleRate;
        if (!double.IsFinite(effectiveSampleRate) ||
            effectiveSampleRate < nominalSampleRate * options.MinimumRateRatio ||
            effectiveSampleRate > nominalSampleRate * options.MaximumRateRatio)
        {
            effectiveSampleRate = nominalSampleRate;
        }

        var sourceOffset = meanSample - effectiveSampleRate * meanTimeline;
        var weightedResidual = anchors.Sum(anchor =>
        {
            var predicted = sourceOffset + anchor.TimelineSeconds * effectiveSampleRate;
            return Math.Abs(anchor.SampleOffset - predicted) * Math.Max(1.0e-6, anchor.Confidence);
        }) / totalWeight;
        var residualConfidence = 1.0 / (1.0 + weightedResidual / Math.Max(1.0, nominalSampleRate * 0.001));
        var countConfidence = Math.Clamp(anchors.Count / options.FullAnchorConfidenceCount, 0.0, 1.0);
        var anchorConfidence = Math.Clamp(anchors.Average(anchor => anchor.Confidence), 0.0, 1.0);
        var confidence = residualConfidence * 0.35 + countConfidence * 0.45 + anchorConfidence * 0.20;
        var score = confidence + Math.Min(anchors.Count, 8) * 0.15 - weightedResidual / Math.Max(1.0, nominalSampleRate * 0.010);
        return new MimirBioacousticClockHypothesis(
            sourceOffset,
            effectiveSampleRate,
            anchors.Count,
            anchorCoverage,
            weightedResidual,
            confidence,
            score,
            anchors.ToArray());
    }
}
