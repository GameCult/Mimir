using GameCult.Caching;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

public enum MimirSensorCalibrationSessionPhase
{
    Collecting,
    Fitting,
    Validating,
    Promoted,
    Rejected,
    Canceled
}

[CultDocument("mimir.sensor_calibration_session", "mimir.sensor_calibration_session.v1")]
[MessagePackObject]
public sealed record MimirSensorCalibrationSessionDocument(
    [property: Key(0)][property: CultName] string SessionId,
    [property: Key(1)] string TrackingSpaceId,
    [property: Key(2)] string Owner,
    [property: Key(3)] string StartedAtUtc,
    [property: Key(4)] double MaximumDurationSeconds,
    [property: Key(5)] string[] RequiredWandIds,
    [property: Key(6)] string[] SensorIds,
    [property: Key(7)] MimirSensorCalibrationAcceptance Acceptance,
    [property: Key(8)] MimirSensorCalibrationSessionPhase Phase,
    [property: Key(9)] MimirSensorCalibrationSensorProgress[] Sensors,
    [property: Key(10)] string[] MissingRequirements,
    [property: Key(11)] string Detail);

[MessagePackObject]
public sealed record MimirSensorCalibrationAcceptance(
    [property: Key(0)] int GridColumns,
    [property: Key(1)] int GridRows,
    [property: Key(2)] int MinimumOccupiedGridCellsPerSensor,
    [property: Key(3)] int MinimumObservationsPerSensor,
    [property: Key(4)] int MinimumSameFrameCorrespondences,
    [property: Key(5)] int MinimumDistinctWands,
    [property: Key(6)] double MinimumRadiusRatio,
    [property: Key(7)] double HeldOutFraction,
    [property: Key(8)] double MaximumMedianReprojectionErrorPx,
    [property: Key(9)] double MaximumP95ReprojectionErrorPx,
    [property: Key(10)] double MaximumAssociationSkewMilliseconds);

[MessagePackObject]
public sealed record MimirSensorCalibrationSensorProgress(
    [property: Key(0)] string SensorId,
    [property: Key(1)] int ObservationCount,
    [property: Key(2)] int OccupiedGridCells,
    [property: Key(3)] int GridCellCount,
    [property: Key(4)] int DistinctWandCount,
    [property: Key(5)] double MinimumRadiusPx,
    [property: Key(6)] double MaximumRadiusPx,
    [property: Key(7)] int[] MissingGridCells,
    [property: Key(8)] bool CollectionComplete);

[CultDocument("mimir.sensor_calibration_receipt", "mimir.sensor_calibration_receipt.v1")]
[MessagePackObject]
public sealed record MimirSensorCalibrationReceiptDocument(
    [property: Key(0)][property: CultName] string ReceiptId,
    [property: Key(1)] string SessionId,
    [property: Key(2)] string TrackingSpaceId,
    [property: Key(3)] string CompletedAtUtc,
    [property: Key(4)] MimirSensorCalibrationSessionPhase Verdict,
    [property: Key(5)] int TrainingCorrespondenceCount,
    [property: Key(6)] int HeldOutCorrespondenceCount,
    [property: Key(7)] MimirSensorCalibrationFitResiduals Residuals,
    [property: Key(8)] MimirMoveFusionRigCalibration? PromotedCalibration,
    [property: Key(9)] string[] RejectionReasons,
    [property: Key(10)] string Solver);

[MessagePackObject]
public sealed record MimirSensorCalibrationFitResiduals(
    [property: Key(0)] double TrainingMedianReprojectionErrorPx,
    [property: Key(1)] double TrainingP95ReprojectionErrorPx,
    [property: Key(2)] double HeldOutMedianReprojectionErrorPx,
    [property: Key(3)] double HeldOutP95ReprojectionErrorPx,
    [property: Key(4)] double MedianAssociationSkewMilliseconds,
    [property: Key(5)] double MaximumAssociationSkewMilliseconds,
    [property: Key(6)] double ConditionEstimate,
    [property: Key(7)] int Iterations,
    [property: Key(8)] double TrainingInlierFraction = 0.0,
    [property: Key(9)] double HeldOutInlierFraction = 0.0);

public static class MimirSensorCalibrationSessions
{
    public static MimirSensorCalibrationSessionDocument CreateFourWandOpticalSession(
        IEnumerable<string> wandIds,
        IEnumerable<string> sensorIds,
        DateTimeOffset? startedAt = null,
        double maximumDurationSeconds = 120.0)
    {
        var wands = wandIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order().ToArray();
        var sensors = sensorIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order().ToArray();
        if (wands.Length != 4) throw new ArgumentException("A four-wand calibration session requires exactly four stable wand IDs.", nameof(wandIds));
        if (sensors.Length < 2) throw new ArgumentException("Optical calibration requires at least two sensors.", nameof(sensorIds));
        if (!double.IsFinite(maximumDurationSeconds) || maximumDurationSeconds <= 0.0) throw new ArgumentOutOfRangeException(nameof(maximumDurationSeconds));
        var start = startedAt ?? DateTimeOffset.UtcNow;
        return new MimirSensorCalibrationSessionDocument(
            $"mimir-sensor-calibration:{start.ToUnixTimeMilliseconds()}",
            "mimir-stage-space",
            "Mimir.Runtime",
            start.ToString("O"),
            maximumDurationSeconds,
            wands,
            sensors,
            new MimirSensorCalibrationAcceptance(
                GridColumns: 4,
                GridRows: 3,
                MinimumOccupiedGridCellsPerSensor: 9,
                MinimumObservationsPerSensor: 1_000,
                MinimumSameFrameCorrespondences: 500,
                MinimumDistinctWands: 4,
                MinimumRadiusRatio: 1.5,
                HeldOutFraction: 0.2,
                MaximumMedianReprojectionErrorPx: 1.5,
                MaximumP95ReprojectionErrorPx: 4.0,
                MaximumAssociationSkewMilliseconds: 20.0),
            MimirSensorCalibrationSessionPhase.Collecting,
            sensors.Select(sensor => new MimirSensorCalibrationSensorProgress(
                sensor, 0, 0, 12, 0, double.NaN, double.NaN, Enumerable.Range(0, 12).ToArray(), false)).ToArray(),
            ["wand-motion-not-yet-captured"],
            "Wave one or two uniquely identified wands at a time through the shared sensor volume; Mimir accumulates successive passes until every wand and sensor has sufficient coverage.");
    }

    public static MimirSensorCalibrationSessionDocument UpdateCollection(
        MimirSensorCalibrationSessionDocument session,
        MoveVisibilityWindowReceipt evidence,
        int imageWidth,
        int imageHeight,
        DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(evidence);
        if (session.Phase != MimirSensorCalibrationSessionPhase.Collecting) return session;
        if (imageWidth <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageHeight));

        var acceptance = session.Acceptance;
        var requiredWands = session.RequiredWandIds.ToHashSet(StringComparer.Ordinal);
        var progress = session.SensorIds.Select(sensorId =>
        {
            var observations = evidence.Observations.Where(value =>
                string.Equals(value.CameraId, sensorId, StringComparison.Ordinal) &&
                requiredWands.Contains(value.MoveId) &&
                float.IsFinite(value.CenterXPx) && float.IsFinite(value.CenterYPx) &&
                float.IsFinite(value.RadiusPx) && value.RadiusPx > 0.0f).ToArray();
            var occupied = observations.Select(value => GridCell(
                value.CenterXPx, value.CenterYPx, imageWidth, imageHeight,
                acceptance.GridColumns, acceptance.GridRows)).Distinct().Order().ToArray();
            var missing = Enumerable.Range(0, acceptance.GridColumns * acceptance.GridRows)
                .Except(occupied).ToArray();
            var minimumRadius = observations.Length == 0 ? double.NaN : observations.Min(value => value.RadiusPx);
            var maximumRadius = observations.Length == 0 ? double.NaN : observations.Max(value => value.RadiusPx);
            var radiusRatio = observations.Length == 0 ? 0.0 : maximumRadius / minimumRadius;
            var distinctWands = observations.Select(value => value.MoveId).Distinct(StringComparer.Ordinal).Count();
            var complete = observations.Length >= acceptance.MinimumObservationsPerSensor &&
                occupied.Length >= acceptance.MinimumOccupiedGridCellsPerSensor &&
                distinctWands >= acceptance.MinimumDistinctWands &&
                radiusRatio >= acceptance.MinimumRadiusRatio;
            return new MimirSensorCalibrationSensorProgress(
                sensorId, observations.Length, occupied.Length,
                acceptance.GridColumns * acceptance.GridRows, distinctWands,
                minimumRadius, maximumRadius, missing, complete);
        }).ToArray();

        var correspondenceCount = evidence.Correspondences.Count(value =>
            requiredWands.Contains(value.MoveId) &&
            value.AbsoluteSkewNs <= acceptance.MaximumAssociationSkewMilliseconds * 1_000_000.0 &&
            string.Equals(value.First.FrameId, value.Second.FrameId, StringComparison.Ordinal));
        var missingRequirements = new List<string>();
        foreach (var sensor in progress)
        {
            if (sensor.ObservationCount < acceptance.MinimumObservationsPerSensor)
                missingRequirements.Add($"{sensor.SensorId}:observations-{sensor.ObservationCount}-of-{acceptance.MinimumObservationsPerSensor}");
            if (sensor.OccupiedGridCells < acceptance.MinimumOccupiedGridCellsPerSensor)
                missingRequirements.Add($"{sensor.SensorId}:grid-cells-{sensor.OccupiedGridCells}-of-{acceptance.MinimumOccupiedGridCellsPerSensor}");
            if (sensor.DistinctWandCount < acceptance.MinimumDistinctWands)
                missingRequirements.Add($"{sensor.SensorId}:wands-{sensor.DistinctWandCount}-of-{acceptance.MinimumDistinctWands}");
            var radiusRatio = sensor.ObservationCount == 0 ? 0.0 : sensor.MaximumRadiusPx / sensor.MinimumRadiusPx;
            if (radiusRatio < acceptance.MinimumRadiusRatio)
                missingRequirements.Add($"{sensor.SensorId}:radius-ratio-{radiusRatio:F2}-of-{acceptance.MinimumRadiusRatio:F2}");
        }
        if (correspondenceCount < acceptance.MinimumSameFrameCorrespondences)
            missingRequirements.Add($"same-frame-correspondences-{correspondenceCount}-of-{acceptance.MinimumSameFrameCorrespondences}");

        var elapsed = (observedAt ?? DateTimeOffset.UtcNow) - DateTimeOffset.Parse(session.StartedAtUtc);
        var collectionComplete = progress.All(value => value.CollectionComplete) &&
            correspondenceCount >= acceptance.MinimumSameFrameCorrespondences;
        var expired = elapsed.TotalSeconds >= session.MaximumDurationSeconds;
        var phase = collectionComplete
            ? MimirSensorCalibrationSessionPhase.Fitting
            : expired ? MimirSensorCalibrationSessionPhase.Rejected : MimirSensorCalibrationSessionPhase.Collecting;
        var detail = phase switch
        {
            MimirSensorCalibrationSessionPhase.Fitting => "Collection complete; Mimir owns the retained evidence and may begin fitting.",
            MimirSensorCalibrationSessionPhase.Rejected => "Collection window expired before coverage requirements were met; no calibration was promoted.",
            _ => "Collection active. Continue sweeping one or two wands at a time through missing image regions and depth ranges."
        };
        return session with { Phase = phase, Sensors = progress, MissingRequirements = missingRequirements.ToArray(), Detail = detail };
    }

    private static int GridCell(float x, float y, int width, int height, int columns, int rows)
    {
        var column = Math.Clamp((int)(x * columns / width), 0, columns - 1);
        var row = Math.Clamp((int)(y * rows / height), 0, rows - 1);
        return row * columns + column;
    }
}
