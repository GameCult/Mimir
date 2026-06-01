namespace Mimir.Runtime.Synchronization;

public enum MimirBioacousticProbeReason
{
    LowConfidence,
    HighResidual,
    WeakResponse,
    PhaseUnstable,
    UnmeasuredBand
}

public sealed record MimirAudioCalibrationBandResidual(
    string SourceId,
    double CenterHz,
    double Confidence,
    double ResidualDb,
    double DelayResidualMicroseconds = 0.0,
    double PhaseResidualRadians = 0.0,
    double ResponseEnergy = 0.0);

public sealed record MimirAudioCalibrationSourcePathology(
    string SourceId,
    double NoiseFloorRms = 0.0,
    double LowHighTiltDb = 0.0,
    double HighBandConfidenceBias = 0.0);

public sealed record MimirTargetedBioacousticProbeOptions(
    int SampleRate = 192_000,
    int MaxProbeBands = 8,
    double MinFrequencyHz = 120.0,
    double MaxFrequencyHz = 42_000.0,
    double TargetConfidence = 0.82,
    double ResidualDbBudget = 1.5,
    double WeakResponseThreshold = 0.08,
    double PhaseResidualBudgetRadians = 0.80,
    double MinimumBandSpacingFraction = 0.055,
    double BaseGain = 0.018,
    double MaxGain = 0.085);

public sealed record MimirTargetedBioacousticProbeBand(
    double CenterHz,
    double StartHz,
    double EndHz,
    double Gain,
    double Priority,
    MimirBioacousticProbeReason Reason,
    IReadOnlyList<string> SourceIds);

public sealed record MimirTargetedBioacousticProbePlan(
    string Id,
    int SampleRate,
    IReadOnlyList<string> MeasurementSourceIds,
    IReadOnlyList<MimirTargetedBioacousticProbeBand> Bands,
    double EstimatedDurationSeconds,
    string Notes);

public sealed class MimirTargetedBioacousticProbePlanner(MimirTargetedBioacousticProbeOptions? options = null)
{
    private readonly MimirTargetedBioacousticProbeOptions options = options ?? new();

    public MimirTargetedBioacousticProbePlan Plan(
        IReadOnlyList<MimirAudioCalibrationBandResidual> residuals,
        IReadOnlyList<string>? preferredSourceIds = null,
        IReadOnlyList<MimirAudioCalibrationSourcePathology>? sourcePathologies = null)
    {
        var pathologyBySource = (sourcePathologies ?? [])
            .GroupBy(pathology => pathology.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sourceFilter = preferredSourceIds is { Count: > 0 }
            ? preferredSourceIds.ToHashSet(StringComparer.Ordinal)
            : null;
        var usableResiduals = residuals
            .Where(residual => residual.CenterHz >= options.MinFrequencyHz &&
                residual.CenterHz <= Math.Min(options.MaxFrequencyHz, options.SampleRate * 0.45) &&
                (sourceFilter == null || sourceFilter.Contains(residual.SourceId)))
            .ToArray();
        var sourceIds = (sourceFilter ?? usableResiduals.Select(residual => residual.SourceId).ToHashSet(StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var candidates = usableResiduals
            .GroupBy(residual => QuantizeBand(residual.CenterHz))
            .Select(group => BuildCandidate(group.ToArray(), pathologyBySource))
            .Where(candidate => candidate.Priority > 0.0)
            .OrderByDescending(candidate => candidate.Priority)
            .ToArray();
        var selected = new List<MimirTargetedBioacousticProbeBand>(options.MaxProbeBands);
        foreach (var candidate in candidates)
        {
            if (selected.Any(existing => BandsTooClose(existing.CenterHz, candidate.CenterHz)))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count >= options.MaxProbeBands)
            {
                break;
            }
        }

        return new MimirTargetedBioacousticProbePlan(
            "targeted-bioacoustic-probe.v1",
            options.SampleRate,
            sourceIds,
            selected,
            selected.Count == 0 ? 0.0 : 0.105 * selected.Count + 0.080,
            "Probe bands are selected from residual uncertainty. The renderer may shape them as birdcall syllables, but the planner only owns target bands and gain budget.");
    }

    private MimirTargetedBioacousticProbeBand BuildCandidate(
        IReadOnlyList<MimirAudioCalibrationBandResidual> group,
        IReadOnlyDictionary<string, MimirAudioCalibrationSourcePathology> pathologyBySource)
    {
        var centerHz = WeightedAverage(group, residual => residual.CenterHz, residual => BandPriority(residual, pathologyBySource));
        var priority = group.Sum(residual => BandPriority(residual, pathologyBySource)) / Math.Max(1, group.Count);
        var reason = group
            .OrderByDescending(residual => BandPriority(residual, pathologyBySource))
            .Select(ReasonFor)
            .FirstOrDefault();
        var responseEnergy = group
            .Where(residual => residual.ResponseEnergy > 0.0)
            .Select(residual => residual.ResponseEnergy)
            .DefaultIfEmpty(options.WeakResponseThreshold)
            .Average();
        var confidenceGap = group
            .Select(residual => Math.Max(0.0, options.TargetConfidence - residual.Confidence))
            .DefaultIfEmpty(0.0)
            .Average();
        var weakResponseBoost = responseEnergy < options.WeakResponseThreshold
            ? (options.WeakResponseThreshold - responseEnergy) / options.WeakResponseThreshold
            : 0.0;
        var gain = Math.Clamp(
            options.BaseGain * (1.0 + 1.6 * priority + 0.8 * confidenceGap + 0.9 * weakResponseBoost),
            options.BaseGain,
            options.MaxGain);
        var halfWidth = Math.Clamp(centerHz * 0.045, 40.0, 1200.0);
        return new MimirTargetedBioacousticProbeBand(
            centerHz,
            Math.Max(options.MinFrequencyHz, centerHz - halfWidth),
            Math.Min(Math.Min(options.MaxFrequencyHz, options.SampleRate * 0.45), centerHz + halfWidth),
            gain,
            priority,
            reason,
            group.Select(residual => residual.SourceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private double QuantizeBand(double centerHz)
    {
        var semitone = Math.Round(12.0 * Math.Log2(centerHz / 110.0));
        return 110.0 * Math.Pow(2.0, semitone / 12.0);
    }

    private bool BandsTooClose(double leftHz, double rightHz)
    {
        var distance = Math.Abs(Math.Log(leftHz / rightHz));
        return distance < options.MinimumBandSpacingFraction;
    }

    private double BandPriority(
        MimirAudioCalibrationBandResidual residual,
        IReadOnlyDictionary<string, MimirAudioCalibrationSourcePathology> pathologyBySource)
    {
        var confidenceGap = Math.Max(0.0, options.TargetConfidence - residual.Confidence) / Math.Max(0.001, options.TargetConfidence);
        var residualPressure = Math.Max(0.0, Math.Abs(residual.ResidualDb) - options.ResidualDbBudget) / 12.0;
        var weakResponse = residual.ResponseEnergy <= 0.0
            ? 0.35
            : Math.Max(0.0, options.WeakResponseThreshold - residual.ResponseEnergy) / options.WeakResponseThreshold;
        var phasePressure = Math.Max(0.0, Math.Abs(residual.PhaseResidualRadians) - options.PhaseResidualBudgetRadians) / Math.PI;
        var delayPressure = Math.Min(1.0, Math.Abs(residual.DelayResidualMicroseconds) / 25.0);
        var pathologyPressure = 0.0;
        if (pathologyBySource.TryGetValue(residual.SourceId, out var pathology))
        {
            var highBand = residual.CenterHz >= 3_000.0
                ? Math.Clamp(Math.Log2(residual.CenterHz / 3_000.0) / 3.0, 0.0, 1.0)
                : 0.0;
            var noisePressure = Math.Clamp(pathology.NoiseFloorRms / 0.050, 0.0, 1.0);
            var tiltPressure = Math.Clamp(pathology.LowHighTiltDb / 18.0, 0.0, 1.0);
            pathologyPressure = highBand * (noisePressure * 0.35 + tiltPressure * 0.75 + Math.Clamp(pathology.HighBandConfidenceBias, 0.0, 1.0));
        }

        return confidenceGap * 1.20 + residualPressure + weakResponse * 0.85 + phasePressure * 0.75 + delayPressure * 0.40 + pathologyPressure;
    }

    private MimirBioacousticProbeReason ReasonFor(MimirAudioCalibrationBandResidual residual)
    {
        if (residual.Confidence < 0.05 && residual.ResponseEnergy <= 0.0)
        {
            return MimirBioacousticProbeReason.UnmeasuredBand;
        }

        if (residual.ResponseEnergy > 0.0 && residual.ResponseEnergy < options.WeakResponseThreshold)
        {
            return MimirBioacousticProbeReason.WeakResponse;
        }

        if (Math.Abs(residual.PhaseResidualRadians) > options.PhaseResidualBudgetRadians)
        {
            return MimirBioacousticProbeReason.PhaseUnstable;
        }

        if (Math.Abs(residual.ResidualDb) > options.ResidualDbBudget)
        {
            return MimirBioacousticProbeReason.HighResidual;
        }

        return MimirBioacousticProbeReason.LowConfidence;
    }

    private static double WeightedAverage(
        IReadOnlyList<MimirAudioCalibrationBandResidual> group,
        Func<MimirAudioCalibrationBandResidual, double> value,
        Func<MimirAudioCalibrationBandResidual, double> weight)
    {
        var totalWeight = group.Sum(item => Math.Max(1.0e-6, weight(item)));
        return group.Sum(item => value(item) * Math.Max(1.0e-6, weight(item))) / totalWeight;
    }
}
