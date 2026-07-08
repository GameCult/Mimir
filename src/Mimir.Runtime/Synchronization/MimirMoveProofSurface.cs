using Aquarium.Engine.Render;
using GameCult.Caching;
using MessagePack;
using System.Numerics;

namespace Mimir.Runtime.Synchronization;

public enum MimirMoveProofVerdict
{
    Unknown,
    NoPose,
    SingleRayFallback,
    FullPose
}

[CultDocument("mimir.move_proof_surface", "mimir.move_proof_surface.v1")]
[MessagePackObject]
public sealed record MimirMoveProofSurfaceDocument(
    [property: Key(0)]
    [property: CultName]
    string ProofId,
    [property: Key(1)] string MuninnEvidenceFrameId,
    [property: Key(2)] string MimirEvidenceFrameId,
    [property: Key(3)] string MimirPoseFrameId,
    [property: Key(4)] string FensalirFrameId,
    [property: Key(5)] string ProducerPeerId,
    [property: Key(6)] string FusionAuthorityId,
    [property: Key(7)] string TrackingSpaceId,
    [property: Key(8)] string CalibrationId,
    [property: Key(9)] long SourceTimestampNs,
    [property: Key(10)] long MuninnPublishedAtNs,
    [property: Key(11)] ulong MimirReadAtNs,
    [property: Key(12)] ulong MimirAdmittedAtNs,
    [property: Key(13)] long FusionEstimatedAtNs,
    [property: Key(14)] ulong FensalirPresentedAtNs,
    [property: Key(15)] ulong ReservoirEdgeNs,
    [property: Key(16)] ulong ReservoirWindowStartNs,
    [property: Key(17)] int OpticalMarkerCount,
    [property: Key(18)] int ControllerStateCount,
    [property: Key(19)] int PoseCount,
    [property: Key(20)] double PoseConfidence,
    [property: Key(21)] double LatencyMilliseconds,
    [property: Key(22)] MimirMoveProofVerdict Verdict,
    [property: Key(23)] string FailureReason,
    [property: Key(24)] MimirMoveControllerPoseDocument[] Poses)
{
    [IgnoreMember]
    public bool IsFullPose => Verdict == MimirMoveProofVerdict.FullPose;
}

public static class MimirMoveProofSurface
{
    public const string ConsumerContract = "fensalir.move-proof-surface";

    public static MimirMoveProofSurfaceDocument Create(
        MimirMuninnMoveEvidenceAdmission admission,
        MimirMoveControllerPoseStreamFrame poseFrame,
        string fensalirFrameId,
        ulong fensalirPresentedAtNs,
        string? mimirEvidenceFrameId = null)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(poseFrame);
        ArgumentException.ThrowIfNullOrWhiteSpace(fensalirFrameId);

        var poses = poseFrame.Poses
            .OrderBy(pose => pose.SourceTimestampNs)
            .ThenBy(pose => pose.Sequence)
            .ToArray();
        var selectedPose = poses
            .OrderByDescending(pose => pose.Confidence)
            .FirstOrDefault();
        var verdict = ResolveVerdict(poses);
        var failureReason = ResolveFailureReason(admission, poses, verdict);
        var sourceNs = selectedPose?.SourceTimestampNs ??
            checked((long)Math.Min(admission.SourceTimeMaxNs, (ulong)long.MaxValue));
        var estimatedAtNs = selectedPose?.EstimatedAtNs ?? 0L;
        var confidence = selectedPose?.Confidence ?? 0.0;
        var latencyMs = selectedPose?.LatencyMilliseconds ?? 0.0;

        return new MimirMoveProofSurfaceDocument(
            ProofId: $"{poseFrame.ProducerPeerId}:move-proof:{SequenceSuffix(poseFrame.FrameId)}",
            MuninnEvidenceFrameId: admission.FrameId,
            MimirEvidenceFrameId: string.IsNullOrWhiteSpace(mimirEvidenceFrameId)
                ? $"mimir:starfire:move-evidence:{SequenceSuffix(admission.FrameId)}"
                : mimirEvidenceFrameId,
            MimirPoseFrameId: poseFrame.FrameId,
            FensalirFrameId: fensalirFrameId,
            ProducerPeerId: poseFrame.ProducerPeerId,
            FusionAuthorityId: selectedPose?.FusionAuthorityId ?? "mimir.runtime.move-fusion",
            TrackingSpaceId: poseFrame.TrackingSpaceId,
            CalibrationId: poseFrame.CalibrationId,
            SourceTimestampNs: sourceNs,
            MuninnPublishedAtNs: admission.PublishedAtNs,
            MimirReadAtNs: admission.MimirReadAtNs,
            MimirAdmittedAtNs: admission.ArrivalMaxNs,
            FusionEstimatedAtNs: estimatedAtNs,
            FensalirPresentedAtNs: fensalirPresentedAtNs,
            ReservoirEdgeNs: admission.ReservoirEdgeNs,
            ReservoirWindowStartNs: admission.ReservoirWindowStartNs,
            OpticalMarkerCount: admission.OpticalMarkerCount,
            ControllerStateCount: admission.ControllerStateCount,
            PoseCount: poses.Length,
            PoseConfidence: Math.Clamp(confidence, 0.0, 1.0),
            LatencyMilliseconds: Math.Max(0.0, latencyMs),
            Verdict: verdict,
            FailureReason: failureReason,
            Poses: poses);
    }

    public static AquariumSplineFrame BuildFensalirProbeFrame(MimirMoveProofSurfaceDocument surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Poses.Length == 0)
        {
            return BuildFailureProbe(surface);
        }

        var splines = new List<AquariumSpline3D>();
        foreach (var pose in surface.Poses)
        {
            var center = ToVector(pose.PositionMeters);
            var confidence = (float)Math.Clamp(pose.Confidence, 0.0, 1.0);
            var color = surface.IsFullPose
                ? new Vector4(0.15f, 1.35f, 0.92f, 0.92f)
                : new Vector4(1.35f, 0.78f, 0.18f, 0.86f);
            var radius = MathF.Max(0.03f, 0.08f * confidence);
            splines.Add(new AquariumSpline3D(
                $"move-proof-{SanitizeId(pose.PoseId)}-x",
                [
                    new(center + new Vector3(-radius, 0.0f, 0.0f), color),
                    new(center + new Vector3(radius, 0.0f, 0.0f), color)
                ],
                new AquariumSplineStyle(0.012f, 2.0f, 0.94f, 0.7f, 0.10f),
                CatmullRomSubdivisions: 1));
            splines.Add(new AquariumSpline3D(
                $"move-proof-{SanitizeId(pose.PoseId)}-y",
                [
                    new(center + new Vector3(0.0f, -radius, 0.0f), color),
                    new(center + new Vector3(0.0f, radius, 0.0f), color)
                ],
                new AquariumSplineStyle(0.012f, 2.0f, 0.94f, 0.7f, 0.10f),
                CatmullRomSubdivisions: 1));
            splines.Add(new AquariumSpline3D(
                $"move-proof-{SanitizeId(pose.PoseId)}-z",
                [
                    new(center + new Vector3(0.0f, 0.0f, -radius), color),
                    new(center + new Vector3(0.0f, 0.0f, radius), color)
                ],
                new AquariumSplineStyle(0.012f, 2.0f, 0.94f, 0.7f, 0.10f),
                CatmullRomSubdivisions: 1));
        }

        return new AquariumSplineFrame { Splines = splines };
    }

    private static AquariumSplineFrame BuildFailureProbe(MimirMoveProofSurfaceDocument surface)
    {
        var color = new Vector4(1.2f, 0.2f, 0.12f, 0.85f);
        var z = surface.OpticalMarkerCount > 0 ? 0.35f : 0.0f;
        return new AquariumSplineFrame
        {
            Splines =
            [
                new AquariumSpline3D(
                    $"move-proof-{SanitizeId(surface.ProofId)}-missing-pose",
                    [
                        new(new Vector3(-0.16f, 0.0f, z), color),
                        new(new Vector3(0.16f, 0.0f, z), color),
                        new(new Vector3(0.0f, 0.16f, z), color),
                        new(new Vector3(-0.16f, 0.0f, z), color)
                    ],
                    new AquariumSplineStyle(0.01f, 1.2f, 0.85f, 0.8f, 0.12f),
                    CatmullRomSubdivisions: 1)
            ]
        };
    }

    private static MimirMoveProofVerdict ResolveVerdict(IReadOnlyList<MimirMoveControllerPoseDocument> poses)
    {
        if (poses.Count == 0)
        {
            return MimirMoveProofVerdict.NoPose;
        }

        return poses.Any(pose => pose.EvidenceKinds.Contains("optical-marker:triangulated", StringComparer.Ordinal))
            ? MimirMoveProofVerdict.FullPose
            : MimirMoveProofVerdict.SingleRayFallback;
    }

    private static string ResolveFailureReason(
        MimirMuninnMoveEvidenceAdmission admission,
        IReadOnlyList<MimirMoveControllerPoseDocument> poses,
        MimirMoveProofVerdict verdict)
    {
        if (verdict == MimirMoveProofVerdict.FullPose)
        {
            return "";
        }

        if (admission.OpticalMarkerCount == 0)
        {
            return "missing optical marker candidates";
        }

        if (admission.ControllerStateCount == 0)
        {
            return "missing controller state";
        }

        if (poses.Count == 0)
        {
            return "Mimir fusion produced no calibrated pose";
        }

        return "pose is single-ray fallback, not full calibrated optical fusion";
    }

    private static string SequenceSuffix(string frameId)
    {
        var separator = frameId.LastIndexOf(':');
        return separator >= 0 && separator < frameId.Length - 1
            ? frameId[(separator + 1)..]
            : frameId;
    }

    private static Vector3 ToVector(MimirVector3Snapshot value) =>
        new((float)value.X, (float)value.Y, (float)value.Z);

    private static string SanitizeId(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return new string(chars);
    }
}
