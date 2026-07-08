using Aquarium.Engine.Render;
using GameCult.Mesh;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirMoveProofPipelineResult(
    MimirMuninnMoveEvidenceAdmission Admission,
    MimirMoveFusionResult Fusion,
    MimirMoveControllerPoseStreamFrame PoseFrame,
    MimirMoveProofSurfaceDocument ProofSurface,
    AquariumSplineFrame FensalirProbeFrame);

public sealed record MimirMoveProofPipelineOptions(
    string MimirEvidenceSourceId,
    string MimirEvidenceFrameId,
    string MimirPoseFrameId,
    string MimirPoseProducerPeerId,
    string FensalirFrameId,
    ulong FensalirPresentedAtNs,
    string FusionAuthorityId = "mimir.runtime.move-fusion",
    string ConsumerContract = "fensalir.move-controller-input");

public static class MimirMoveProofPipeline
{
    public static bool TryBuildLatestFromRing(
        CultMeshSharedMemoryFrameRing ring,
        MimirNativeReservoirRuntime runtime,
        MimirMoveFusionRigCalibration calibration,
        MimirMoveProofPipelineOptions options,
        out MimirMoveProofPipelineResult? result)
    {
        ArgumentNullException.ThrowIfNull(ring);
        result = null;
        if (!ring.TryAcquireLatestRead(out var lease))
        {
            return false;
        }

        using (lease)
        {
            var frame = MimirMuninnMoveEvidenceAdapter.DeserializeStreamFrame(lease.Memory[..lease.Handle.ByteLength]);
            return TryBuild(
                frame,
                runtime,
                calibration,
                options,
                out result);
        }
    }

    public static bool TryBuild(
        MimirMuninnMoveEvidenceStreamFrame frame,
        MimirNativeReservoirRuntime runtime,
        MimirMoveFusionRigCalibration calibration,
        MimirMoveProofPipelineOptions options,
        out MimirMoveProofPipelineResult? result)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(options);
        result = null;

        var mimirReadAtNs = NowUnixNs();
        var samples = MimirMuninnMoveEvidenceAdapter.BuildNativeSamples(frame.MarkerCandidates, frame.ControllerStates);
        if (samples.Count == 0)
        {
            return false;
        }

        var handle = runtime.AdmitMoveEvidence(
            options.MimirEvidenceSourceId,
            samples,
            calibration.CalibrationId,
            calibration.TrackingSpaceId);
        var status = runtime.Status;
        var admission = new MimirMuninnMoveEvidenceAdmission(
            FrameId: frame.FrameId,
            ProducerPeerId: frame.ProducerPeerId,
            PublishedAtNs: frame.PublishedAtNs,
            MimirReadAtNs: mimirReadAtNs,
            SampleCount: samples.Count,
            OpticalMarkerCount: samples.Count(sample => sample.EvidenceKind == (uint)MimirNativeMoveEvidenceKind.OpticalMarker),
            ControllerStateCount: samples.Count(sample => sample.EvidenceKind == (uint)MimirNativeMoveEvidenceKind.ControllerState),
            SourceTimeMinNs: samples.Min(sample => sample.SourceTimestampNs),
            SourceTimeMaxNs: samples.Max(sample => sample.SourceTimestampNs),
            ArrivalMinNs: samples.Min(sample => sample.ArrivalNs),
            ArrivalMaxNs: samples.Max(sample => sample.ArrivalNs),
            Handle: handle,
            ReservoirEdgeNs: status.EdgeNs,
            ReservoirWindowStartNs: status.WindowStartNs,
            ReservoirMoveEvidenceCount: status.MoveEvidenceCount.ToUInt64());
        var fusion = MimirMoveFusion.Fuse(
            samples,
            calibration,
            options.FusionAuthorityId,
            options.ConsumerContract);
        var poseFrame = MimirMovePoseStream.CreateFrame(
            options.MimirPoseFrameId,
            options.MimirPoseProducerPeerId,
            publishedAtNs: fusion.Poses.Count == 0
                ? checked((long)Math.Min(admission.ArrivalMaxNs, (ulong)long.MaxValue))
                : fusion.Poses.Max(pose => pose.EstimatedAtNs),
            calibration.TrackingSpaceId,
            calibration.CalibrationId,
            fusion.Poses);
        var proofSurface = MimirMoveProofSurface.Create(
            admission,
            poseFrame,
            options.FensalirFrameId,
            options.FensalirPresentedAtNs,
            options.MimirEvidenceFrameId);
        var probeFrame = MimirMoveProofSurface.BuildFensalirProbeFrame(proofSurface);
        result = new MimirMoveProofPipelineResult(
            admission,
            fusion,
            poseFrame,
            proofSurface,
            probeFrame);
        return true;
    }

    private static ulong NowUnixNs()
    {
        var now = DateTimeOffset.UtcNow;
        var secondsNs = checked((ulong)now.ToUnixTimeSeconds() * 1_000_000_000UL);
        var tickRemainderNs = checked((ulong)(now.Ticks % TimeSpan.TicksPerSecond) * 100UL);
        return checked(secondsNs + tickRemainderNs);
    }
}
