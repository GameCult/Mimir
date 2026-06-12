using System.Numerics;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirMoveFusionCameraCalibration(
    string CameraId,
    ulong WitnessIdHash,
    MimirVector3Snapshot PositionMeters,
    MimirQuaternionSnapshot Orientation,
    double FocalLengthXPx,
    double FocalLengthYPx,
    double PrincipalPointXPx,
    double PrincipalPointYPx);

public sealed record MimirMoveFusionRigCalibration(
    string CalibrationId,
    string TrackingSpaceId,
    IReadOnlyList<MimirMoveFusionCameraCalibration> Cameras,
    double GyroUnitsPerRadianPerSecond = 1.0,
    double MaximumAssociationSkewMilliseconds = 20.0,
    double SingleRayFallbackDepthMeters = 1.5);

public sealed record MimirMoveFusionResult(
    IReadOnlyList<MimirMoveControllerPoseDocument> Poses,
    int ControllerEvidenceCount,
    int OpticalEvidenceCount,
    int CalibratedOpticalEvidenceCount);

public static class MimirMoveFusion
{
    public static MimirMoveFusionResult Fuse(
        IReadOnlyList<MimirNativeMoveEvidenceSample> samples,
        MimirMoveFusionRigCalibration calibration,
        string fusionAuthorityId = "mimir.runtime.move-fusion",
        string consumerContract = "fensalir.move-controller-input")
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentException.ThrowIfNullOrWhiteSpace(calibration.CalibrationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(calibration.TrackingSpaceId);

        var controllerStates = samples
            .Where(sample => sample.EvidenceKind == (uint)MimirNativeMoveEvidenceKind.ControllerState &&
                sample.ControllerIdHash != 0)
            .OrderBy(sample => sample.SourceTimestampNs)
            .ThenBy(sample => sample.Sequence)
            .ToArray();
        var opticalMarkers = samples
            .Where(sample => sample.EvidenceKind == (uint)MimirNativeMoveEvidenceKind.OpticalMarker)
            .OrderBy(sample => sample.SourceTimestampNs)
            .ThenBy(sample => sample.Sequence)
            .ToArray();
        var cameras = calibration.Cameras
            .Where(IsCameraUsable)
            .ToDictionary(camera => camera.WitnessIdHash, camera => camera);
        var maxSkewNs = (ulong)Math.Max(0.0, calibration.MaximumAssociationSkewMilliseconds * 1_000_000.0);

        var poses = new List<MimirMoveControllerPoseDocument>();
        foreach (var controller in controllerStates)
        {
            var associatedMarkers = opticalMarkers
                .Where(marker => IsAssociated(marker, controller, controllerStates.Length, maxSkewNs))
                .Select(marker => (Marker: marker, Camera: cameras.GetValueOrDefault(marker.WitnessIdHash)))
                .Where(pair => pair.Camera is not null)
                .ToArray();
            if (associatedMarkers.Length == 0)
            {
                continue;
            }

            var rays = associatedMarkers
                .Select(pair => BuildRay(pair.Camera!, pair.Marker))
                .Where(ray => ray.HasValue)
                .Select(ray => ray!.Value)
                .ToArray();
            if (rays.Length == 0)
            {
                continue;
            }

            var position = rays.Length >= 2
                ? ClosestPoint(rays)
                : rays[0].Origin + rays[0].Direction * (float)Math.Max(0.05, calibration.SingleRayFallbackDepthMeters);
            var opticalConfidence = Math.Clamp(
                associatedMarkers.Average(pair => pair.Marker.Confidence) * (rays.Length >= 2 ? 1.0 : 0.35),
                0.0,
                1.0);
            var confidence = Math.Clamp(opticalConfidence * ControllerConfidence(controller), 0.0, 1.0);
            var sourceNs = Math.Max(controller.SourceTimestampNs, associatedMarkers.Max(pair => pair.Marker.SourceTimestampNs));
            var arrivalNs = Math.Max(controller.ArrivalNs, associatedMarkers.Max(pair => pair.Marker.ArrivalNs));
            var evidenceStreamIds = associatedMarkers
                .Select(pair => $"witness:0x{pair.Marker.WitnessIdHash:X16}")
                .Append($"controller:0x{controller.ControllerIdHash:X16}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var evidenceKinds = new[]
            {
                rays.Length >= 2 ? "optical-marker:triangulated" : "optical-marker:single-ray-depth-prior",
                "controller-state:buttons-imu",
                "orientation:imu-unresolved"
            };

            poses.Add(new MimirMoveControllerPoseDocument(
                PoseId: $"move:0x{controller.ControllerIdHash:X16}:{controller.Sequence}",
                WandId: $"move:0x{controller.ControllerIdHash:X16}",
                TrackingSpaceId: calibration.TrackingSpaceId,
                CalibrationId: calibration.CalibrationId,
                FusionAuthorityId: fusionAuthorityId,
                SourceTimestampNs: checked((long)Math.Min(sourceNs, (ulong)long.MaxValue)),
                EstimatedAtNs: checked((long)Math.Min(arrivalNs, (ulong)long.MaxValue)),
                Sequence: controller.Sequence,
                PositionMeters: ToSnapshot(position),
                Orientation: new MimirQuaternionSnapshot(0.0, 0.0, 0.0, 1.0),
                LinearVelocityMetersPerSecond: new MimirVector3Snapshot(0.0, 0.0, 0.0),
                AngularVelocityRadiansPerSecond: new MimirVector3Snapshot(
                    ScaleGyro(controller.GyroX, calibration),
                    ScaleGyro(controller.GyroY, calibration),
                    ScaleGyro(controller.GyroZ, calibration)),
                Confidence: confidence,
                LatencyMilliseconds: arrivalNs >= sourceNs ? (arrivalNs - sourceNs) / 1_000_000.0 : 0.0,
                Battery01: float.IsFinite(controller.Battery01) ? Math.Clamp(controller.Battery01, 0.0f, 1.0f) : double.NaN,
                Buttons: ButtonsFromMask(controller.ButtonsMask, controller.Trigger),
                EvidenceStreamIds: evidenceStreamIds,
                EvidenceKinds: evidenceKinds,
                ConsumerContract: consumerContract));
        }

        return new MimirMoveFusionResult(
            poses,
            controllerStates.Length,
            opticalMarkers.Length,
            opticalMarkers.Count(marker => cameras.ContainsKey(marker.WitnessIdHash)));
    }

    private static bool IsAssociated(
        MimirNativeMoveEvidenceSample marker,
        MimirNativeMoveEvidenceSample controller,
        int controllerCount,
        ulong maxSkewNs)
    {
        if (marker.ControllerIdHash != 0 && marker.ControllerIdHash != controller.ControllerIdHash)
        {
            return false;
        }

        if (marker.ControllerIdHash == 0 && controllerCount != 1)
        {
            return false;
        }

        return TimestampDelta(marker.SourceTimestampNs, controller.SourceTimestampNs) <= maxSkewNs;
    }

    private static bool IsCameraUsable(MimirMoveFusionCameraCalibration camera) =>
        camera.WitnessIdHash != 0 &&
        camera.FocalLengthXPx > 0.0 &&
        camera.FocalLengthYPx > 0.0;

    private static MimirRay? BuildRay(MimirMoveFusionCameraCalibration camera, MimirNativeMoveEvidenceSample marker)
    {
        if (!float.IsFinite(marker.ImageX) || !float.IsFinite(marker.ImageY))
        {
            return null;
        }

        var directionCamera = Vector3.Normalize(new Vector3(
            (float)((marker.ImageX - camera.PrincipalPointXPx) / camera.FocalLengthXPx),
            (float)((camera.PrincipalPointYPx - marker.ImageY) / camera.FocalLengthYPx),
            1.0f));
        var orientation = ToQuaternion(camera.Orientation);
        return new MimirRay(
            ToVector(camera.PositionMeters),
            Vector3.Normalize(Vector3.Transform(directionCamera, orientation)));
    }

    private static Vector3 ClosestPoint(IReadOnlyList<MimirRay> rays)
    {
        var matrix = new Matrix3();
        var rhs = Vector3.Zero;
        foreach (var ray in rays)
        {
            var d = Vector3.Normalize(ray.Direction);
            var xx = 1.0f - d.X * d.X;
            var yy = 1.0f - d.Y * d.Y;
            var zz = 1.0f - d.Z * d.Z;
            var xy = -d.X * d.Y;
            var xz = -d.X * d.Z;
            var yz = -d.Y * d.Z;
            matrix.M11 += xx;
            matrix.M12 += xy;
            matrix.M13 += xz;
            matrix.M21 += xy;
            matrix.M22 += yy;
            matrix.M23 += yz;
            matrix.M31 += xz;
            matrix.M32 += yz;
            matrix.M33 += zz;
            rhs += new Vector3(
                xx * ray.Origin.X + xy * ray.Origin.Y + xz * ray.Origin.Z,
                xy * ray.Origin.X + yy * ray.Origin.Y + yz * ray.Origin.Z,
                xz * ray.Origin.X + yz * ray.Origin.Y + zz * ray.Origin.Z);
        }

        return matrix.TrySolve(rhs, out var solved)
            ? solved
            : rays[0].Origin + rays[0].Direction;
    }

    private static double ControllerConfidence(MimirNativeMoveEvidenceSample controller) =>
        Math.Clamp(
            0.75 +
            (float.IsFinite(controller.Battery01) ? 0.10 : 0.0) +
            (controller.ButtonsMask != 0 ? 0.05 : 0.0) +
            (Math.Abs(controller.AccelX) + Math.Abs(controller.AccelY) + Math.Abs(controller.AccelZ) > 0.001 ? 0.10 : 0.0),
            0.0,
            1.0);

    private static MimirTrackingButtonSnapshot[] ButtonsFromMask(uint mask, float trigger)
    {
        var names = new[]
        {
            "select", "l3", "r3", "start", "up", "right", "down", "left",
            "l2", "r2", "l1", "r1", "triangle", "circle", "cross", "square",
            "ps", "move", "trigger"
        };
        var buttons = new List<MimirTrackingButtonSnapshot>();
        for (var bit = 0; bit < names.Length; bit++)
        {
            var pressed = (mask & (1u << bit)) != 0;
            if (pressed || names[bit] == "trigger")
            {
                buttons.Add(new MimirTrackingButtonSnapshot(
                    names[bit],
                    pressed,
                    names[bit] == "trigger" ? Math.Clamp(trigger, 0.0f, 1.0f) : pressed ? 1.0 : 0.0));
            }
        }

        return buttons.ToArray();
    }

    private static ulong TimestampDelta(ulong left, ulong right) => left >= right ? left - right : right - left;

    private static double ScaleGyro(float value, MimirMoveFusionRigCalibration calibration) =>
        calibration.GyroUnitsPerRadianPerSecond <= 0.0
            ? 0.0
            : value / calibration.GyroUnitsPerRadianPerSecond;

    private static Vector3 ToVector(MimirVector3Snapshot value) => new((float)value.X, (float)value.Y, (float)value.Z);

    private static MimirVector3Snapshot ToSnapshot(Vector3 value) => new(value.X, value.Y, value.Z);

    private static Quaternion ToQuaternion(MimirQuaternionSnapshot value)
    {
        var q = new Quaternion((float)value.X, (float)value.Y, (float)value.Z, (float)value.W);
        return q.LengthSquared() <= 1.0e-12f ? Quaternion.Identity : Quaternion.Normalize(q);
    }

    private readonly record struct MimirRay(Vector3 Origin, Vector3 Direction);

    private struct Matrix3
    {
        public float M11;
        public float M12;
        public float M13;
        public float M21;
        public float M22;
        public float M23;
        public float M31;
        public float M32;
        public float M33;

        public readonly bool TrySolve(Vector3 rhs, out Vector3 value)
        {
            var det = Determinant(
                M11, M12, M13,
                M21, M22, M23,
                M31, M32, M33);
            if (Math.Abs(det) <= 1.0e-8f)
            {
                value = default;
                return false;
            }

            value = new Vector3(
                Determinant(rhs.X, M12, M13, rhs.Y, M22, M23, rhs.Z, M32, M33) / det,
                Determinant(M11, rhs.X, M13, M21, rhs.Y, M23, M31, rhs.Z, M33) / det,
                Determinant(M11, M12, rhs.X, M21, M22, rhs.Y, M31, M32, rhs.Z) / det);
            return true;
        }

        private static float Determinant(
            float m11,
            float m12,
            float m13,
            float m21,
            float m22,
            float m23,
            float m31,
            float m32,
            float m33) =>
            m11 * (m22 * m33 - m23 * m32) -
            m12 * (m21 * m33 - m23 * m31) +
            m13 * (m21 * m32 - m22 * m31);
    }
}
