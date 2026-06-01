namespace Mimir.Runtime.Synchronization;

public enum MimirBioacousticProbeScheduleReason
{
    None,
    SyncConfidenceLow,
    FrequencyResponseConfidenceLow,
    PeriodicRecheck
}

public sealed record MimirBioacousticProbeSchedulerOptions(
    int SampleRate = 192_000,
    double TargetSyncConfidence = 0.88,
    double TargetFrequencyResponseConfidence = 0.82,
    double RecheckSyncConfidence = 0.96,
    double RecheckFrequencyResponseConfidence = 0.94,
    double MinimumIntervalSeconds = 0.65,
    double MaximumIntervalSeconds = 8.0,
    int MaxProbeBands = 6);

public sealed record MimirBioacousticProbeConfidenceState(
    string SourceId,
    double SyncConfidence,
    double FrequencyResponseConfidence,
    double AggregateConfidence,
    int WeakBandCount,
    double LowestConfidenceBandHz,
    int ProbeCount,
    long LastProbeAtNs,
    long NextEligibleProbeAtNs);

public sealed record MimirScheduledBioacousticProbeFrame(
    long TimestampNs,
    bool ShouldEmit,
    MimirBioacousticProbeScheduleReason Reason,
    double AggregateSyncConfidence,
    double AggregateFrequencyResponseConfidence,
    double AggregateConfidence,
    double SyncConfidenceDelta,
    double FrequencyResponseConfidenceDelta,
    double ScheduledIntervalSeconds,
    double SecondsUntilEligible,
    IReadOnlyList<MimirBioacousticProbeConfidenceState> Sources,
    MimirTargetedBioacousticProbePlan ProbePlan,
    string Notes);

public sealed class MimirBioacousticProbeScheduler(
    MimirBioacousticProbeSchedulerOptions? options = null,
    MimirTargetedBioacousticProbePlanner? planner = null)
{
    private readonly MimirBioacousticProbeSchedulerOptions options = options ?? new();
    private readonly MimirTargetedBioacousticProbePlanner planner = planner ?? new(new MimirTargetedBioacousticProbeOptions(
        SampleRate: options?.SampleRate ?? 192_000,
        MaxProbeBands: options?.MaxProbeBands ?? 6,
        TargetConfidence: options?.TargetFrequencyResponseConfidence ?? 0.82));
    private readonly Dictionary<string, int> probeCounts = new(StringComparer.Ordinal);
    private long lastProbeAtNs = long.MinValue;
    private double previousSyncConfidence;
    private double previousFrequencyResponseConfidence;
    private bool hasPrevious;

    public MimirScheduledBioacousticProbeFrame Update(
        IReadOnlyList<MimirAudioSynchronizationState> synchronizationStates,
        IReadOnlyList<MimirAudioCalibrationBandResidual> residuals,
        long timestampNs,
        IReadOnlyList<string>? preferredSourceIds = null,
        IReadOnlyList<MimirAudioCalibrationSourcePathology>? sourcePathologies = null)
    {
        var sourceIds = ResolveSourceIds(synchronizationStates, residuals, preferredSourceIds);
        var syncBySource = synchronizationStates
            .GroupBy(state => state.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Math.Clamp(group.Max(state => state.Confidence), 0.0, 1.0), StringComparer.Ordinal);
        var residualsBySource = residuals
            .GroupBy(residual => residual.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var syncConfidence = AverageOrZero(sourceIds.Select(sourceId => syncBySource.TryGetValue(sourceId, out var confidence) ? confidence : 0.0));
        var frequencyConfidence = AverageOrZero(sourceIds.Select(sourceId => residualsBySource.TryGetValue(sourceId, out var sourceResiduals)
            ? FrequencyResponseConfidence(sourceResiduals)
            : 0.0));
        var pressure = Math.Max(
            Math.Max(0.0, options.TargetSyncConfidence - syncConfidence) / Math.Max(0.001, options.TargetSyncConfidence),
            Math.Max(0.0, options.TargetFrequencyResponseConfidence - frequencyConfidence) / Math.Max(0.001, options.TargetFrequencyResponseConfidence));
        var scheduledInterval = options.MaximumIntervalSeconds -
            (options.MaximumIntervalSeconds - options.MinimumIntervalSeconds) * Math.Clamp(pressure, 0.0, 1.0);
        var nextEligibleAtNs = lastProbeAtNs == long.MinValue
            ? timestampNs
            : lastProbeAtNs + SecondsToNanoseconds(scheduledInterval);
        var secondsUntilEligible = Math.Max(0.0, (nextEligibleAtNs - timestampNs) / 1_000_000_000.0);
        var reason = ChooseReason(syncConfidence, frequencyConfidence, pressure, secondsUntilEligible);
        var shouldEmit = reason != MimirBioacousticProbeScheduleReason.None && secondsUntilEligible <= 0.0;
        var plan = shouldEmit
            ? planner.Plan(residuals, sourceIds, sourcePathologies)
            : EmptyPlan(sourceIds);
        if (shouldEmit && plan.Bands.Count > 0)
        {
            lastProbeAtNs = timestampNs;
            foreach (var sourceId in sourceIds)
            {
                probeCounts[sourceId] = probeCounts.GetValueOrDefault(sourceId) + 1;
            }

            nextEligibleAtNs = timestampNs + SecondsToNanoseconds(scheduledInterval);
            secondsUntilEligible = scheduledInterval;
        }
        else if (shouldEmit)
        {
            reason = MimirBioacousticProbeScheduleReason.None;
            shouldEmit = false;
        }

        var sources = sourceIds
            .Select(sourceId => BuildSourceState(
                sourceId,
                syncBySource.TryGetValue(sourceId, out var sourceSync) ? sourceSync : 0.0,
                residualsBySource.TryGetValue(sourceId, out var sourceResiduals) ? sourceResiduals : [],
                nextEligibleAtNs))
            .ToArray();
        var syncDelta = hasPrevious ? syncConfidence - previousSyncConfidence : 0.0;
        var frequencyDelta = hasPrevious ? frequencyConfidence - previousFrequencyResponseConfidence : 0.0;
        previousSyncConfidence = syncConfidence;
        previousFrequencyResponseConfidence = frequencyConfidence;
        hasPrevious = true;

        return new MimirScheduledBioacousticProbeFrame(
            timestampNs,
            shouldEmit,
            reason,
            syncConfidence,
            frequencyConfidence,
            (syncConfidence + frequencyConfidence) * 0.5,
            syncDelta,
            frequencyDelta,
            scheduledInterval,
            secondsUntilEligible,
            sources,
            plan,
            "Probe cadence is derived from runtime confidence. The scheduler owns when to spend active sound; render/output paths own how the scheduled probe is emitted.");
    }

    private MimirBioacousticProbeScheduleReason ChooseReason(
        double syncConfidence,
        double frequencyConfidence,
        double pressure,
        double secondsUntilEligible)
    {
        if (syncConfidence < options.TargetSyncConfidence)
        {
            return MimirBioacousticProbeScheduleReason.SyncConfidenceLow;
        }

        if (frequencyConfidence < options.TargetFrequencyResponseConfidence)
        {
            return MimirBioacousticProbeScheduleReason.FrequencyResponseConfidenceLow;
        }

        if (secondsUntilEligible <= 0.0 &&
            pressure <= 0.0 &&
            (syncConfidence < options.RecheckSyncConfidence ||
                frequencyConfidence < options.RecheckFrequencyResponseConfidence))
        {
            return MimirBioacousticProbeScheduleReason.PeriodicRecheck;
        }

        return MimirBioacousticProbeScheduleReason.None;
    }

    private MimirBioacousticProbeConfidenceState BuildSourceState(
        string sourceId,
        double syncConfidence,
        IReadOnlyList<MimirAudioCalibrationBandResidual> residuals,
        long nextEligibleAtNs)
    {
        var frequencyConfidence = residuals.Count == 0 ? 0.0 : FrequencyResponseConfidence(residuals);
        var weakBands = residuals.Count(residual => BandConfidence(residual) < options.TargetFrequencyResponseConfidence);
        var weakest = residuals
            .OrderBy(BandConfidence)
            .ThenBy(static residual => residual.CenterHz)
            .FirstOrDefault();
        return new MimirBioacousticProbeConfidenceState(
            sourceId,
            syncConfidence,
            frequencyConfidence,
            (syncConfidence + frequencyConfidence) * 0.5,
            weakBands,
            weakest?.CenterHz ?? 0.0,
            probeCounts.GetValueOrDefault(sourceId),
            lastProbeAtNs == long.MinValue ? 0L : lastProbeAtNs,
            nextEligibleAtNs);
    }

    private static IReadOnlyList<string> ResolveSourceIds(
        IReadOnlyList<MimirAudioSynchronizationState> synchronizationStates,
        IReadOnlyList<MimirAudioCalibrationBandResidual> residuals,
        IReadOnlyList<string>? preferredSourceIds)
    {
        if (preferredSourceIds is { Count: > 0 })
        {
            return preferredSourceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }

        return synchronizationStates.Select(state => state.SourceId)
            .Concat(residuals.Select(residual => residual.SourceId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private MimirTargetedBioacousticProbePlan EmptyPlan(IReadOnlyList<string> sourceIds) =>
        new(
            "targeted-bioacoustic-probe.v1",
            options.SampleRate,
            sourceIds,
            [],
            0.0,
            "No active probe is scheduled for this frame.");

    private double FrequencyResponseConfidence(IReadOnlyList<MimirAudioCalibrationBandResidual> residuals) =>
        AverageOrZero(residuals.Select(BandConfidence));

    private double BandConfidence(MimirAudioCalibrationBandResidual residual)
    {
        var magnitudeFitness = 1.0 - Math.Clamp(Math.Max(0.0, Math.Abs(residual.ResidualDb) - 1.5) / 18.0, 0.0, 1.0);
        var phaseFitness = 1.0 - Math.Clamp(Math.Max(0.0, Math.Abs(residual.PhaseResidualRadians) - 0.70) / Math.PI, 0.0, 1.0);
        var delayFitness = 1.0 - Math.Clamp(Math.Abs(residual.DelayResidualMicroseconds) / 45.0, 0.0, 1.0);
        var responseFitness = residual.ResponseEnergy <= 0.0
            ? 0.0
            : Math.Clamp(residual.ResponseEnergy / 0.30, 0.0, 1.0);
        return Math.Clamp(
            residual.Confidence * 0.44 +
            magnitudeFitness * 0.20 +
            phaseFitness * 0.14 +
            delayFitness * 0.10 +
            responseFitness * 0.12,
            0.0,
            1.0);
    }

    private static double AverageOrZero(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? 0.0 : array.Average();
    }

    private static long SecondsToNanoseconds(double seconds) =>
        (long)Math.Round(Math.Clamp(seconds, 0.0, 3600.0) * 1_000_000_000.0);
}
