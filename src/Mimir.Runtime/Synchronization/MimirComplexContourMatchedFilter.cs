using System.Numerics;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirComplexContourFilterOptions(
    int MaxAnchorsPerEvent = 8,
    int MaxCandidatesPerAnchor = 5,
    double MinimumScore = 0.020,
    double CandidateSeparationSamples = 2.0,
    double MinimumWindowSeconds = 0.00075,
    double MaximumWindowSeconds = 0.0060);

public sealed record MimirComplexContourAnchorHit(
    ulong EventIndex,
    MimirBioacousticAnchorKind Kind,
    int SyllableIndex,
    double CenterHz,
    double TimelineSeconds,
    double SampleOffset,
    double Score,
    double PhaseRadians,
    int Rank);

public sealed record MimirAcousticReflectionTap(
    double DelaySamples,
    double RelativeDelaySamples,
    double Weight,
    int HitCount);

public sealed record MimirDirectPathBandObservation(
    double CenterHz,
    double Weight,
    double DelayResidualSamples,
    double PhaseResidualRadians);

public sealed record MimirDirectPathBandCorrection(
    double CenterHz,
    double DelayCorrectionSamples,
    double PhaseCorrectionRadians,
    double Weight);

public sealed record MimirDirectPathEstimate(
    double DelaySamples,
    double DelayMicroseconds,
    double Confidence,
    double MeanAbsoluteErrorSamples,
    double MeanAbsolutePhaseErrorRadians,
    int DirectHitCount,
    IReadOnlyList<MimirDirectPathBandObservation> BandObservations,
    IReadOnlyList<MimirAcousticReflectionTap> ReflectionTaps);

public sealed class MimirDirectPathChannelModel(IReadOnlyList<MimirDirectPathBandCorrection> corrections)
{
    public IReadOnlyList<MimirDirectPathBandCorrection> Corrections { get; } = corrections;

    public static MimirDirectPathChannelModel Empty { get; } = new([]);

    public static MimirDirectPathChannelModel Learn(MimirDirectPathEstimate estimate, double binHz = 250.0)
    {
        var corrections = estimate.BandObservations
            .GroupBy(observation => Math.Round(observation.CenterHz / binHz) * binHz)
            .Select(group =>
            {
                var totalWeight = Math.Max(1.0e-9, group.Sum(observation => Math.Max(observation.Weight, 1.0e-9)));
                return new MimirDirectPathBandCorrection(
                    group.Key,
                    group.Sum(observation => observation.DelayResidualSamples * Math.Max(observation.Weight, 1.0e-9)) / totalWeight,
                    group.Sum(observation => observation.PhaseResidualRadians * Math.Max(observation.Weight, 1.0e-9)) / totalWeight,
                    totalWeight);
            })
            .Where(correction => correction.Weight > 0.0)
            .OrderBy(correction => correction.CenterHz)
            .ToArray();
        return new MimirDirectPathChannelModel(corrections);
    }

    public double DelayCorrectionFor(double centerHz, double maxDistanceHz = 375.0)
    {
        return CorrectionFor(centerHz, maxDistanceHz)?.DelayCorrectionSamples ?? 0.0;
    }

    public MimirDirectPathBandCorrection? CorrectionFor(double centerHz, double maxDistanceHz = 375.0)
    {
        if (Corrections.Count == 0)
        {
            return null;
        }

        var nearest = Corrections
            .Select(correction => (Correction: correction, Distance: Math.Abs(correction.CenterHz - centerHz)))
            .OrderBy(pair => pair.Distance)
            .First();
        return nearest.Distance > maxDistanceHz
            ? null
            : nearest.Correction;
    }
}

public sealed class MimirComplexContourMatchedFilterBank(
    MimirBioacousticContestantRenderer renderer,
    int sampleRate,
    MimirComplexContourFilterOptions? options = null)
{
    private readonly Dictionary<(ulong EventIndex, MimirBioacousticAnchorKind Kind, int SyllableIndex), MimirComplexContourTemplate> templates = [];
    private readonly MimirComplexContourFilterOptions options = options ?? new();

    public IReadOnlyList<MimirComplexContourAnchorHit> AnalyzeEvents(
        ReadOnlySpan<float> samples,
        IEnumerable<ulong> eventIndices,
        double sourceOffsetSamples,
        int searchRadiusSamples)
    {
        var output = new List<MimirComplexContourAnchorHit>();
        foreach (var eventIndex in eventIndices)
        {
            foreach (var template in TemplatesForEvent(eventIndex))
            {
                var center = renderer.EventStartSeconds(eventIndex) * sampleRate +
                    sourceOffsetSamples +
                    template.LocalStartSamples;
                AddBestHits(samples, template, center, searchRadiusSamples, output);
            }
        }

        return output
            .OrderBy(hit => hit.EventIndex)
            .ThenBy(hit => hit.SampleOffset)
            .ThenBy(hit => hit.Rank)
            .ToArray();
    }

    private IReadOnlyList<MimirComplexContourTemplate> TemplatesForEvent(ulong eventIndex)
    {
        var anchors = renderer.AnchorPlan(eventIndex)
            .OrderByDescending(anchor => anchor.Weight)
            .Take(options.MaxAnchorsPerEvent)
            .OrderBy(anchor => anchor.StartSeconds)
            .ToArray();
        var output = new List<MimirComplexContourTemplate>(anchors.Length);
        foreach (var anchor in anchors)
        {
            var key = (eventIndex, anchor.Kind, anchor.SyllableIndex);
            if (!templates.TryGetValue(key, out var template))
            {
                template = BuildTemplate(eventIndex, anchor);
                templates[key] = template;
            }

            output.Add(template);
        }

        return output;
    }

    private MimirComplexContourTemplate BuildTemplate(ulong eventIndex, MimirBioacousticContestantAnchor anchor)
    {
        var eventSamples = renderer.RenderEventMonoFloat(eventIndex, sampleRate);
        var windowSamples = Math.Clamp(
            (int)Math.Round(anchor.DurationSeconds * sampleRate),
            Math.Max(16, (int)Math.Round(options.MinimumWindowSeconds * sampleRate)),
            Math.Max(16, (int)Math.Round(options.MaximumWindowSeconds * sampleRate)));
        var localStart = Math.Clamp(
            (int)Math.Round(anchor.StartSeconds * sampleRate),
            0,
            Math.Max(0, eventSamples.Length - windowSamples));
        var kernel = new Complex[windowSamples];
        var energy = 0.0;
        for (var index = 0; index < windowSamples; index++)
        {
            var t = index / (double)Math.Max(1, windowSamples - 1);
            var taper = Math.Sin(Math.PI * t);
            var sample = eventSamples[localStart + index] * taper;
            var phase = -Math.Tau * anchor.CenterHz * index / sampleRate;
            kernel[index] = new Complex(sample * Math.Cos(phase), sample * Math.Sin(phase));
            energy += sample * sample;
        }

        return new MimirComplexContourTemplate(
            eventIndex,
            anchor.Kind,
            anchor.SyllableIndex,
            anchor.CenterHz,
            renderer.EventStartSeconds(eventIndex) + anchor.StartSeconds,
            localStart,
            kernel,
            Math.Max(energy, 1.0e-18),
            anchor.Weight);
    }

    private void AddBestHits(
        ReadOnlySpan<float> samples,
        MimirComplexContourTemplate template,
        double centerSample,
        int searchRadiusSamples,
        List<MimirComplexContourAnchorHit> output)
    {
        var candidates = new List<MimirComplexContourAnchorHit>();
        var first = Math.Max(0, (int)Math.Floor(centerSample - searchRadiusSamples));
        var last = Math.Min(samples.Length - template.Kernel.Length - 1, (int)Math.Ceiling(centerSample + searchRadiusSamples));
        for (var offset = first; offset <= last; offset++)
        {
            var response = ScoreAt(samples, template, offset);
            if (response.Score < options.MinimumScore)
            {
                continue;
            }

            var refinedOffset = RefineOffset(samples, template, offset, first, last);
            candidates.Add(new MimirComplexContourAnchorHit(
                template.EventIndex,
                template.Kind,
                template.SyllableIndex,
                template.CenterHz,
                template.TimelineSeconds,
                refinedOffset,
                response.Score * template.Weight,
                response.PhaseRadians,
                0));
        }

        var rank = 0;
        foreach (var candidate in candidates
                     .OrderByDescending(candidate => candidate.Score))
        {
            if (output.Any(hit =>
                    hit.EventIndex == candidate.EventIndex &&
                    hit.Kind == candidate.Kind &&
                    hit.SyllableIndex == candidate.SyllableIndex &&
                    Math.Abs(hit.SampleOffset - candidate.SampleOffset) < options.CandidateSeparationSamples))
            {
                continue;
            }

            output.Add(candidate with { Rank = rank });
            rank++;
            if (rank >= options.MaxCandidatesPerAnchor)
            {
                break;
            }
        }
    }

    private double RefineOffset(ReadOnlySpan<float> samples, MimirComplexContourTemplate template, int offset, int first, int last)
    {
        if (offset <= first || offset >= last)
        {
            return offset;
        }

        var left = ScoreAt(samples, template, offset - 1).Score;
        var middle = ScoreAt(samples, template, offset).Score;
        var right = ScoreAt(samples, template, offset + 1).Score;
        var denominator = left - 2.0 * middle + right;
        return Math.Abs(denominator) <= 1.0e-12
            ? offset
            : offset + Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
    }

    private static (double Score, double PhaseRadians) ScoreAt(
        ReadOnlySpan<float> samples,
        MimirComplexContourTemplate template,
        int offset)
    {
        if (offset < 0 || offset + template.Kernel.Length > samples.Length)
        {
            return (0.0, 0.0);
        }

        var dot = Complex.Zero;
        var sampleEnergy = 0.0;
        for (var index = 0; index < template.Kernel.Length; index++)
        {
            var sample = samples[offset + index];
            dot += sample * Complex.Conjugate(template.Kernel[index]);
            sampleEnergy += sample * sample;
        }

        if (sampleEnergy <= 1.0e-18 || template.Energy <= 1.0e-18)
        {
            return (0.0, 0.0);
        }

        var normalizedMagnitude = dot.Magnitude / Math.Sqrt(sampleEnergy * template.Energy);
        return (normalizedMagnitude, Math.Atan2(dot.Imaginary, dot.Real));
    }

    private sealed record MimirComplexContourTemplate(
        ulong EventIndex,
        MimirBioacousticAnchorKind Kind,
        int SyllableIndex,
        double CenterHz,
        double TimelineSeconds,
        int LocalStartSamples,
        Complex[] Kernel,
        double Energy,
        double Weight);
}

public sealed record MimirDirectPathTrackerOptions(
    double DirectClusterRadiusSamples = 6.0,
    double MinimumDirectClusterWeightRatio = 0.25,
    double TrackingLoopBandwidth = 0.20,
    double MaximumTrackingCorrectionSamples = 12.0,
    double PredictionGateSamples = 10.0,
    MimirDirectPathChannelModel? ChannelModel = null,
    int MinimumDirectHits = 8);

public sealed class MimirDirectPathTracker(
    int sampleRate,
    MimirDirectPathTrackerOptions? options = null)
{
    private readonly MimirDirectPathTrackerOptions options = options ?? new();
    private double delaySamples;
    private bool hasLock;

    public MimirDirectPathEstimate? Update(
        IReadOnlyList<MimirComplexContourAnchorHit> referenceHits,
        IReadOnlyList<MimirComplexContourAnchorHit> candidateHits,
        double? predictedDelaySamples = null)
    {
        var referenceByAnchor = referenceHits
            .Where(hit => hit.Rank == 0)
            .GroupBy(hit => (hit.EventIndex, hit.Kind, hit.SyllableIndex))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(hit => hit.Score).First());
        var observations = new List<MimirDirectPathObservation>();
        foreach (var candidate in candidateHits)
        {
            if (!referenceByAnchor.TryGetValue((candidate.EventIndex, candidate.Kind, candidate.SyllableIndex), out var reference))
            {
                continue;
            }

            var weight = Math.Sqrt(Math.Max(0.0, reference.Score) * Math.Max(0.0, candidate.Score));
            var sampleDelay = candidate.SampleOffset - reference.SampleOffset;
            var channelModel = options.ChannelModel ?? MimirDirectPathChannelModel.Empty;
            var channelCorrection = channelModel.CorrectionFor(candidate.CenterHz);
            if (channelModel.Corrections.Count > 0)
            {
                weight *= channelCorrection == null
                    ? 0.25
                    : Math.Clamp(0.50 + channelCorrection.Weight, 0.25, 1.50);
            }

            var phaseDelta = WrapRadians(candidate.PhaseRadians - reference.PhaseRadians - (channelCorrection?.PhaseCorrectionRadians ?? 0.0));
            var phaseDelay = candidate.CenterHz <= 1.0
                ? 0.0
                : phaseDelta * sampleRate / (Math.Tau * candidate.CenterHz);
            var maxPhaseCorrection = sampleRate / Math.Max(1.0, candidate.CenterHz) * 0.5;
            observations.Add(new MimirDirectPathObservation(
                sampleDelay + Math.Clamp(phaseDelay, -maxPhaseCorrection, maxPhaseCorrection) - (channelCorrection?.DelayCorrectionSamples ?? 0.0),
                sampleDelay,
                phaseDelta,
                candidate.CenterHz,
                weight));
        }

        if (observations.Count < options.MinimumDirectHits)
        {
            return null;
        }

        var clusters = ClusterDelays(observations);
        if (clusters.Length == 0)
        {
            return null;
        }

        var gatedClusters = predictedDelaySamples == null
            ? clusters
            : clusters
                .Where(cluster => Math.Abs(cluster.Delay - predictedDelaySamples.Value) <= options.PredictionGateSamples)
                .ToArray();
        if (gatedClusters.Length == 0)
        {
            return null;
        }

        var bestWeight = gatedClusters.Max(cluster => cluster.Weight);
        var directCandidates = gatedClusters
            .Where(cluster => cluster.Count >= options.MinimumDirectHits)
            .Where(cluster => cluster.Weight >= bestWeight * options.MinimumDirectClusterWeightRatio)
            .OrderBy(cluster => predictedDelaySamples == null
                ? cluster.Delay
                : Math.Abs(cluster.Delay - predictedDelaySamples.Value))
            .ToArray();
        var direct = directCandidates.Length == 0
            ? gatedClusters.OrderByDescending(cluster => cluster.Weight).First()
            : directCandidates[0];

        var updatedDelay = direct.Delay;
        if (hasLock)
        {
            var correction = Math.Clamp(updatedDelay - delaySamples, -options.MaximumTrackingCorrectionSamples, options.MaximumTrackingCorrectionSamples);
            updatedDelay = delaySamples + correction * options.TrackingLoopBandwidth;
        }

        delaySamples = updatedDelay;
        hasLock = true;
        var directResiduals = observations
            .Where(observation => Math.Abs(observation.Delay - direct.Delay) <= options.DirectClusterRadiusSamples)
            .ToArray();
        var totalWeight = Math.Max(1.0e-9, directResiduals.Sum(observation => Math.Max(observation.Weight, 1.0e-9)));
        var mae = directResiduals.Sum(observation => Math.Abs(observation.Delay - direct.Delay) * Math.Max(observation.Weight, 1.0e-9)) / totalWeight;
        var bandObservations = directResiduals
            .Select(observation =>
            {
                var delayResidual = observation.Delay - direct.Delay;
                var phaseResidual = WrapRadians(observation.PhaseDeltaRadians -
                    Math.Tau * observation.CenterHz * (direct.Delay - observation.SampleDelay) / sampleRate);
                return new MimirDirectPathBandObservation(
                    observation.CenterHz,
                    observation.Weight,
                    delayResidual,
                    phaseResidual);
            })
            .ToArray();
        var meanAbsolutePhaseError = bandObservations.Length == 0
            ? 0.0
            : bandObservations.Sum(observation => Math.Abs(observation.PhaseResidualRadians) * Math.Max(observation.Weight, 1.0e-9)) /
                Math.Max(1.0e-9, bandObservations.Sum(observation => Math.Max(observation.Weight, 1.0e-9)));
        var reflectionTaps = clusters
            .Where(cluster => cluster.Delay > direct.Delay + options.DirectClusterRadiusSamples)
            .Select(cluster => new MimirAcousticReflectionTap(
                cluster.Delay,
                cluster.Delay - direct.Delay,
                cluster.Weight,
                cluster.Count))
            .OrderBy(tap => tap.RelativeDelaySamples)
            .Take(6)
            .ToArray();
        var confidence = ScoreDirectPathConfidence(
            direct,
            clusters,
            mae,
            meanAbsolutePhaseError,
            predictedDelaySamples);
        return new MimirDirectPathEstimate(
            updatedDelay,
            updatedDelay * 1_000_000.0 / sampleRate,
            confidence,
            mae,
            meanAbsolutePhaseError,
            direct.Count,
            bandObservations,
            reflectionTaps);
    }

    private double ScoreDirectPathConfidence(
        MimirDelayCluster direct,
        IReadOnlyList<MimirDelayCluster> clusters,
        double meanAbsoluteErrorSamples,
        double meanAbsolutePhaseErrorRadians,
        double? predictedDelaySamples)
    {
        var hitConfidence = Math.Clamp(direct.Count / 24.0, 0.0, 1.0);
        var residualConfidence = 1.0 / (1.0 + meanAbsoluteErrorSamples / Math.Max(0.25, sampleRate * 0.000006));
        var phaseConfidence = 1.0 / (1.0 + meanAbsolutePhaseErrorRadians / 0.35);
        var predictionConfidence = predictedDelaySamples == null
            ? 0.75
            : 1.0 / (1.0 + Math.Abs(direct.Delay - predictedDelaySamples.Value) / Math.Max(0.5, options.DirectClusterRadiusSamples));
        var coreConfidence =
            0.30 * hitConfidence +
            0.30 * residualConfidence +
            0.25 * phaseConfidence +
            0.15 * predictionConfidence;
        var nearCompetitorWeight = clusters
            .Where(cluster => !ReferenceEquals(cluster, direct))
            .Where(cluster => Math.Abs(cluster.Delay - direct.Delay) <= options.DirectClusterRadiusSamples * 3.0)
            .Sum(cluster => cluster.Weight);
        var ambiguity = nearCompetitorWeight <= 0.0
            ? 1.0
            : direct.Weight / Math.Max(1.0e-9, direct.Weight + nearCompetitorWeight);
        return Math.Clamp(coreConfidence * (0.70 + 0.30 * ambiguity), 0.0, 1.0);
    }

    private MimirDelayCluster[] ClusterDelays(IReadOnlyList<MimirDirectPathObservation> observations)
    {
        var sorted = observations.OrderBy(observation => observation.Delay).ToArray();
        var clusters = new List<MimirDelayCluster>();
        var index = 0;
        while (index < sorted.Length)
        {
            var members = new List<MimirDirectPathObservation> { sorted[index] };
            var end = index + 1;
            while (end < sorted.Length &&
                   sorted[end].Delay - members[0].Delay <= options.DirectClusterRadiusSamples)
            {
                members.Add(sorted[end]);
                end++;
            }

            var totalWeight = Math.Max(1.0e-9, members.Sum(member => Math.Max(member.Weight, 1.0e-9)));
            clusters.Add(new MimirDelayCluster(
                members.Sum(member => member.Delay * Math.Max(member.Weight, 1.0e-9)) / totalWeight,
                totalWeight,
                members.Count));
            index = end;
        }

        return clusters.ToArray();
    }

    private sealed record MimirDelayCluster(double Delay, double Weight, int Count);

    private sealed record MimirDirectPathObservation(
        double Delay,
        double SampleDelay,
        double PhaseDeltaRadians,
        double CenterHz,
        double Weight);

    private static double WrapRadians(double radians)
    {
        while (radians <= -Math.PI)
        {
            radians += Math.Tau;
        }

        while (radians > Math.PI)
        {
            radians -= Math.Tau;
        }

        return radians;
    }
}
