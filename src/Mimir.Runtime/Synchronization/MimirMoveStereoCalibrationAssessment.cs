using GameCult.Caching;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

[MessagePackObject]
public sealed record MimirMoveCameraCoverageAssessment(
    [property: Key(0)] string CameraId,
    [property: Key(1)] int ObservationCount,
    [property: Key(2)] double MinimumXPx,
    [property: Key(3)] double MaximumXPx,
    [property: Key(4)] double MinimumYPx,
    [property: Key(5)] double MaximumYPx,
    [property: Key(6)] int OccupiedGridCells,
    [property: Key(7)] int GridCellCount,
    [property: Key(8)] double MinimumRadiusPx,
    [property: Key(9)] double MaximumRadiusPx);

[CultDocument("mimir.move_stereo_calibration_assessment", "mimir.move_stereo_calibration_assessment.v1")]
[MessagePackObject]
public sealed record MimirMoveStereoCalibrationAssessmentDocument(
    [property: Key(0)] string Schema,
    [property: Key(1)] [property: CultName] string AssessmentId,
    [property: Key(2)] string SourceSchema,
    [property: Key(3)] long AssessedAtNs,
    [property: Key(4)] int CorrespondenceCount,
    [property: Key(5)] int SynchronizedCorrespondenceCount,
    [property: Key(6)] int SameFrameCorrespondenceCount,
    [property: Key(7)] int DistinctMoveCount,
    [property: Key(8)] double MedianAbsoluteSkewMilliseconds,
    [property: Key(9)] double MaximumAbsoluteSkewMilliseconds,
    [property: Key(10)] MimirMoveCameraCoverageAssessment[] Cameras,
    [property: Key(11)] bool IntrinsicsAvailable,
    [property: Key(12)] bool OrbRadiusMetersAvailable,
    [property: Key(13)] bool ReadyForRelativePoseFit,
    [property: Key(14)] bool Promoted,
    [property: Key(15)] string[] MissingRequirements);

public static class MimirMoveStereoCalibrationAssessment
{
    public const string Schema = "mimir.move_stereo_calibration_assessment.v1";

    public static MimirMoveStereoCalibrationAssessmentDocument Assess(
        MoveVisibilityWindowReceipt receipt,
        int imageWidth,
        int imageHeight,
        IReadOnlyCollection<string> cameraIds,
        bool intrinsicsAvailable,
        double orbRadiusMeters,
        double maximumAssociationSkewMilliseconds = 20.0,
        DateTimeOffset? assessedAt = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(cameraIds);
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (cameraIds.Count != 2) throw new ArgumentException("Stereo calibration requires exactly two camera IDs.", nameof(cameraIds));

        var cameras = cameraIds.Distinct(StringComparer.Ordinal).ToArray();
        if (cameras.Length != 2) throw new ArgumentException("Stereo camera IDs must be distinct.", nameof(cameraIds));
        var maximumSkewNs = maximumAssociationSkewMilliseconds * 1_000_000.0;
        var correspondences = receipt.Correspondences
            .Where(value => cameras.Contains(value.First.CameraId, StringComparer.Ordinal) &&
                cameras.Contains(value.Second.CameraId, StringComparer.Ordinal))
            .ToArray();
        var synchronized = correspondences.Where(value => value.AbsoluteSkewNs <= maximumSkewNs).ToArray();
        var sameFrame = synchronized.Where(value => string.Equals(value.First.FrameId, value.Second.FrameId, StringComparison.Ordinal)).ToArray();
        var coverage = cameras.Select(cameraId => BuildCoverage(
            cameraId,
            receipt.Observations.Where(value => string.Equals(value.CameraId, cameraId, StringComparison.Ordinal)).ToArray(),
            imageWidth,
            imageHeight)).ToArray();
        var missing = new List<string>();
        if (!intrinsicsAvailable) missing.Add("calibrated-intrinsics-for-both-eyes");
        if (!(double.IsFinite(orbRadiusMeters) && orbRadiusMeters > 0.0)) missing.Add("measured-orb-radius-meters");
        if (synchronized.Length < 300) missing.Add("at-least-300-synchronized-correspondences");
        if (sameFrame.Length < 100) missing.Add("at-least-100-same-frame-correspondences");
        if (synchronized.Select(value => value.MoveId).Distinct(StringComparer.Ordinal).Count() < 2) missing.Add("at-least-two-stable-move-identities");
        foreach (var camera in coverage)
        {
            if (camera.OccupiedGridCells < 6) missing.Add($"{camera.CameraId}:coverage-at-least-6-of-12-grid-cells");
            if (camera.MaximumRadiusPx <= camera.MinimumRadiusPx * 1.25) missing.Add($"{camera.CameraId}:radius-range-at-least-1.25x");
        }

        var skews = correspondences.Select(value => value.AbsoluteSkewNs / 1_000_000.0).Order().ToArray();
        var ready = missing.Count == 0;
        return new MimirMoveStereoCalibrationAssessmentDocument(
            Schema,
            $"move-stereo-assessment:{receipt.StartedAtNs}:{receipt.EndedAtNs}",
            receipt.Schema,
            (assessedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() * 1_000_000,
            correspondences.Length,
            synchronized.Length,
            sameFrame.Length,
            synchronized.Select(value => value.MoveId).Distinct(StringComparer.Ordinal).Count(),
            Median(skews),
            skews.Length == 0 ? double.NaN : skews[^1],
            coverage,
            intrinsicsAvailable,
            double.IsFinite(orbRadiusMeters) && orbRadiusMeters > 0.0,
            ready,
            Promoted: false,
            missing.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static MimirMoveCameraCoverageAssessment BuildCoverage(
        string cameraId,
        MoveVisibilityObservation[] observations,
        int imageWidth,
        int imageHeight)
    {
        var valid = observations.Where(value =>
            float.IsFinite(value.CenterXPx) && float.IsFinite(value.CenterYPx) &&
            float.IsFinite(value.RadiusPx) && value.RadiusPx > 0.0f).ToArray();
        var cells = valid.Select(value =>
        {
            var column = Math.Clamp((int)(value.CenterXPx * 4.0 / imageWidth), 0, 3);
            var row = Math.Clamp((int)(value.CenterYPx * 3.0 / imageHeight), 0, 2);
            return row * 4 + column;
        }).Distinct().Count();
        return new MimirMoveCameraCoverageAssessment(
            cameraId,
            valid.Length,
            valid.Length == 0 ? double.NaN : valid.Min(value => value.CenterXPx),
            valid.Length == 0 ? double.NaN : valid.Max(value => value.CenterXPx),
            valid.Length == 0 ? double.NaN : valid.Min(value => value.CenterYPx),
            valid.Length == 0 ? double.NaN : valid.Max(value => value.CenterYPx),
            cells,
            12,
            valid.Length == 0 ? double.NaN : valid.Min(value => value.RadiusPx),
            valid.Length == 0 ? double.NaN : valid.Max(value => value.RadiusPx));
    }

    private static double Median(double[] sorted) => sorted.Length switch
    {
        0 => double.NaN,
        _ when sorted.Length % 2 == 1 => sorted[sorted.Length / 2],
        _ => (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) * 0.5
    };
}
