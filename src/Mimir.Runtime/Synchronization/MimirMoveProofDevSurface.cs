namespace Mimir.Runtime.Synchronization;

public static class MimirMoveProofDevSurface
{
    public const string EnableEnvironmentVariable = "MIMIR_MOVE_PROOF_DEV_SURFACE";

    public static bool IsEnabled() =>
        IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable));

    public static MimirMoveProofSurfaceDocument Create(int sequence = 79)
    {
        var sourceTimestampNs = 1_781_202_600_004_000_000L + sequence;
        var pose = new MimirMoveControllerPoseDocument(
            PoseId: $"move:0xA11CE:{sequence}",
            WandId: "move:0xA11CE",
            TrackingSpaceId: "mimir-stage-space",
            CalibrationId: "mimir-move-stage-calibration-v1",
            FusionAuthorityId: "mimir.runtime.move-fusion",
            SourceTimestampNs: sourceTimestampNs,
            EstimatedAtNs: sourceTimestampNs + 2_200_000L,
            Sequence: (ulong)sequence,
            PositionMeters: new MimirVector3Snapshot(0.0, 0.0, 4.0),
            Orientation: new MimirQuaternionSnapshot(0.0, 0.0, 0.0, 1.0),
            LinearVelocityMetersPerSecond: new MimirVector3Snapshot(0.0, 0.0, 0.0),
            AngularVelocityRadiansPerSecond: new MimirVector3Snapshot(0.02, -0.01, 0.04),
            Confidence: 0.82,
            LatencyMilliseconds: 2.2,
            Battery01: 0.72,
            Buttons:
            [
                new MimirTrackingButtonSnapshot("move", true, 1.0),
                new MimirTrackingButtonSnapshot("trigger", true, 0.5)
            ],
            EvidenceStreamIds:
            [
                "witness:0x00000000000A11CE",
                "witness:0x0000000000000B0B",
                "controller:0x00000000000A11CE"
            ],
            EvidenceKinds:
            [
                "optical-marker:triangulated",
                "controller-state:buttons-imu",
                "orientation:imu-unresolved"
            ],
            ConsumerContract: "fensalir.move-controller-input");
        var admission = new MimirMuninnMoveEvidenceAdmission(
            FrameId: $"muninn:nightwing:move-evidence:{sequence}",
            ProducerPeerId: "muninn:nightwing",
            PublishedAtNs: sourceTimestampNs + 1_000_000L,
            MimirReadAtNs: checked((ulong)(sourceTimestampNs + 1_700_000L)),
            SampleCount: 3,
            OpticalMarkerCount: 2,
            ControllerStateCount: 1,
            SourceTimeMinNs: checked((ulong)sourceTimestampNs),
            SourceTimeMaxNs: checked((ulong)sourceTimestampNs),
            ArrivalMinNs: checked((ulong)(sourceTimestampNs + 500_000L)),
            ArrivalMaxNs: checked((ulong)(sourceTimestampNs + 1_700_000L)),
            Handle: new MimirNativeSampleHandle(
                SensorIdHash: 0xF00D,
                TimestampNs: checked((ulong)sourceTimestampNs),
                ArrivalNs: checked((ulong)(sourceTimestampNs + 1_700_000L)),
                Sequence: (ulong)sequence,
                PayloadHandle: 0xBEEF,
                Flags: 0,
                Reserved: 0),
            ReservoirEdgeNs: checked((ulong)sourceTimestampNs),
            ReservoirWindowStartNs: checked((ulong)(sourceTimestampNs - 5_000_000_000L)),
            ReservoirMoveEvidenceCount: 1);
        var poseFrame = MimirMovePoseStream.CreateFrame(
            frameId: $"mimir:starfire:move-pose:{sequence}",
            producerPeerId: "mimir:starfire",
            publishedAtNs: pose.EstimatedAtNs,
            trackingSpaceId: pose.TrackingSpaceId,
            calibrationId: pose.CalibrationId,
            poses: [pose]);

        return MimirMoveProofSurface.Create(
            admission,
            poseFrame,
            $"fensalir:starfire:presented-frame:{sequence}",
            fensalirPresentedAtNs: checked((ulong)(sourceTimestampNs + 3_100_000L)));
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
