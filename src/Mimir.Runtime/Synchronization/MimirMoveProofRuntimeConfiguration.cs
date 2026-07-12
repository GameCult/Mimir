using GameCult.Mesh;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirMoveProofRuntimeConfiguration
{
    public bool Enabled { get; init; } = true;

    public string EvidenceStreamId { get; init; } = "muninn:nightwing:move-evidence";

    public string EvidenceSnapshotPath { get; init; } = "";

    public string OdinCultMeshUri { get; init; } = "";

    public string MuninnProviderId { get; init; } = "muninn.telemetry.nightwing";

    public string NativeReservoirPath { get; init; } = "";

    public string MimirEvidenceSourceId { get; init; } = "mimir:starfire:move-evidence";

    public string MimirEvidenceFramePrefix { get; init; } = "mimir:starfire:move-evidence";

    public string MimirPoseFramePrefix { get; init; } = "mimir:starfire:move-pose";

    public string MimirPoseProducerPeerId { get; init; } = "mimir:starfire";

    public string FensalirFramePrefix { get; init; } = "fensalir:starfire:presented-frame";

    public string FusionAuthorityId { get; init; } = "mimir.runtime.move-fusion";

    public string ConsumerContract { get; init; } = "fensalir.move-controller-input";

    public MimirMoveProofCalibrationConfiguration Calibration { get; init; } = new();

    public string[] Validate()
    {
        if (!Enabled)
        {
            return [];
        }

        var errors = new List<string>();
        Require(EvidenceStreamId, nameof(EvidenceStreamId), errors);
        if (!string.IsNullOrWhiteSpace(OdinCultMeshUri))
        {
            Require(MuninnProviderId, nameof(MuninnProviderId), errors);
        }
        Require(NativeReservoirPath, nameof(NativeReservoirPath), errors);
        Require(MimirEvidenceSourceId, nameof(MimirEvidenceSourceId), errors);
        Require(MimirEvidenceFramePrefix, nameof(MimirEvidenceFramePrefix), errors);
        Require(MimirPoseFramePrefix, nameof(MimirPoseFramePrefix), errors);
        Require(MimirPoseProducerPeerId, nameof(MimirPoseProducerPeerId), errors);
        Require(FensalirFramePrefix, nameof(FensalirFramePrefix), errors);
        Require(FusionAuthorityId, nameof(FusionAuthorityId), errors);
        Require(ConsumerContract, nameof(ConsumerContract), errors);
        errors.AddRange(Calibration.Validate().Select(error => $"{nameof(Calibration)}.{error}"));
        return errors.ToArray();
    }

    public MimirMoveProofRuntimeDriverOptions ToDriverOptions() => new(
        MimirEvidenceSourceId,
        MimirEvidenceFramePrefix,
        MimirPoseFramePrefix,
        MimirPoseProducerPeerId,
        FensalirFramePrefix,
        FusionAuthorityId,
        ConsumerContract);

    public MimirMoveProofRuntimeDriver CreateDriver(
        CultMeshSharedMemoryFrameRing ring,
        MimirNativeReservoirRuntime reservoir)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(reservoir);
        var errors = Validate();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException($"Invalid Move proof runtime configuration: {string.Join("; ", errors)}");
        }

        if (!string.Equals(ring.StreamId, EvidenceStreamId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Move proof runtime configuration expects evidence stream '{EvidenceStreamId}', but ring is '{ring.StreamId}'.");
        }

        return new MimirMoveProofRuntimeDriver(
            ring,
            reservoir,
            Calibration.ToRigCalibration(),
            ToDriverOptions());
    }

    private static void Require(string value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required");
        }
    }
}

public sealed class MimirMoveProofCalibrationConfiguration
{
    public string CalibrationId { get; init; } = "mimir-move-stage-calibration-v1";

    public string TrackingSpaceId { get; init; } = "mimir-stage-space";

    public List<MimirMoveProofCameraCalibrationConfiguration> Cameras { get; init; } = [];

    public double GyroUnitsPerRadianPerSecond { get; init; } = 1.0;

    public double MaximumAssociationSkewMilliseconds { get; init; } = 20.0;

    public double SingleRayFallbackDepthMeters { get; init; } = 1.5;

    public string[] Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(CalibrationId))
        {
            errors.Add($"{nameof(CalibrationId)} is required");
        }

        if (string.IsNullOrWhiteSpace(TrackingSpaceId))
        {
            errors.Add($"{nameof(TrackingSpaceId)} is required");
        }

        if (Cameras.Count < 2)
        {
            errors.Add($"{nameof(Cameras)} must contain at least two calibrated camera witnesses for full-pose proof");
        }

        for (var index = 0; index < Cameras.Count; index++)
        {
            errors.AddRange(Cameras[index].Validate().Select(error => $"{nameof(Cameras)}[{index}].{error}"));
        }

        return errors.ToArray();
    }

    public MimirMoveFusionRigCalibration ToRigCalibration() => new(
        CalibrationId,
        TrackingSpaceId,
        Cameras.Select(camera => camera.ToCameraCalibration()).ToArray(),
        GyroUnitsPerRadianPerSecond,
        MaximumAssociationSkewMilliseconds,
        SingleRayFallbackDepthMeters);
}

public sealed class MimirMoveProofCameraCalibrationConfiguration
{
    public string CameraId { get; init; } = "";

    public ulong WitnessIdHash { get; init; }

    public MimirVector3Snapshot PositionMeters { get; init; } = new(0.0, 0.0, 0.0);

    public MimirQuaternionSnapshot Orientation { get; init; } = new(0.0, 0.0, 0.0, 1.0);

    public double FocalLengthXPx { get; init; }

    public double FocalLengthYPx { get; init; }

    public double PrincipalPointXPx { get; init; }

    public double PrincipalPointYPx { get; init; }

    public string[] Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(CameraId))
        {
            errors.Add($"{nameof(CameraId)} is required");
        }

        if (WitnessIdHash == 0UL)
        {
            errors.Add($"{nameof(WitnessIdHash)} is required");
        }

        if (!double.IsFinite(FocalLengthXPx) || FocalLengthXPx <= 0.0)
        {
            errors.Add($"{nameof(FocalLengthXPx)} must be positive");
        }

        if (!double.IsFinite(FocalLengthYPx) || FocalLengthYPx <= 0.0)
        {
            errors.Add($"{nameof(FocalLengthYPx)} must be positive");
        }

        return errors.ToArray();
    }

    public MimirMoveFusionCameraCalibration ToCameraCalibration() => new(
        CameraId,
        WitnessIdHash,
        PositionMeters,
        Orientation,
        FocalLengthXPx,
        FocalLengthYPx,
        PrincipalPointXPx,
        PrincipalPointYPx);
}
