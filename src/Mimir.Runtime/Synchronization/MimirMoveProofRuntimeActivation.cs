using GameCult.Mesh;
using GameCult.Caching;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

public interface IMimirMoveProofEvidenceRingProvider
{
    bool TryOpenEvidenceRing(
        MimirMoveProofRuntimeConfiguration configuration,
        out MimirMoveProofEvidenceRingLease? lease,
        out string diagnostic);
}

public sealed class MimirMoveProofEvidenceRingLease : IDisposable
{
    private readonly bool ownsRing;

    public MimirMoveProofEvidenceRingLease(CultMeshSharedMemoryFrameRing ring, bool ownsRing = false)
    {
        Ring = ring ?? throw new ArgumentNullException(nameof(ring));
        this.ownsRing = ownsRing;
    }

    public CultMeshSharedMemoryFrameRing Ring { get; }

    public void Dispose()
    {
        if (ownsRing)
        {
            Ring.Dispose();
        }
    }
}

public sealed record MimirMoveProofRuntimeActivationStatus(
    string EvidenceStreamId,
    bool Active,
    string Diagnostic,
    string ProviderKind,
    bool DriverRegistered);

[CultDocument("mimir.move_proof_runtime_activation", "mimir.move_proof_runtime_activation.v1")]
[MessagePackObject]
public sealed record MimirMoveProofRuntimeActivationDocument(
    [property: Key(0)]
    [property: CultName]
    string ActivationId,
    [property: Key(1)] string EvidenceStreamId,
    [property: Key(2)] bool Active,
    [property: Key(3)] string Diagnostic,
    [property: Key(4)] string ProviderKind,
    [property: Key(5)] bool DriverRegistered,
    [property: Key(6)] string NativeReservoirPath,
    [property: Key(7)] string[] CalibratedCameraIds,
    [property: Key(8)] int CalibratedCameraCount,
    [property: Key(9)] string LatestProofId,
    [property: Key(10)] string LatestMuninnEvidenceFrameId,
    [property: Key(11)] string LatestMimirEvidenceFrameId,
    [property: Key(12)] string LatestMimirPoseFrameId,
    [property: Key(13)] string LatestFensalirFrameId,
    [property: Key(14)] MimirMoveProofVerdict LatestVerdict,
    [property: Key(15)] ulong ObservedAtNs);

public static class MimirMoveProofRuntimeActivation
{
    public static MimirMoveProofRuntimeActivationDocument CreateDocument(
        MimirMoveProofRuntimeConfiguration configuration,
        MimirMoveProofRuntimeActivationStatus? status,
        MimirMoveProofSurfaceDocument? latestProofSurface,
        ulong observedAtNs)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var active = status?.Active ?? false;
        var diagnostic = status?.Diagnostic ?? "not activated";
        var providerKind = status?.ProviderKind ?? "";
        var driverRegistered = status?.DriverRegistered ?? false;
        var matchingSurface = IsSurfaceFromEvidenceStream(latestProofSurface, configuration.EvidenceStreamId)
            ? latestProofSurface
            : null;

        return new MimirMoveProofRuntimeActivationDocument(
            ActivationId: $"{configuration.EvidenceStreamId}:activation",
            EvidenceStreamId: configuration.EvidenceStreamId,
            Active: active,
            Diagnostic: diagnostic,
            ProviderKind: providerKind,
            DriverRegistered: driverRegistered,
            NativeReservoirPath: configuration.NativeReservoirPath,
            CalibratedCameraIds: configuration.Calibration.Cameras
                .Select(camera => camera.CameraId)
                .Where(cameraId => !string.IsNullOrWhiteSpace(cameraId))
                .ToArray(),
            CalibratedCameraCount: configuration.Calibration.Cameras.Count,
            LatestProofId: matchingSurface?.ProofId ?? "",
            LatestMuninnEvidenceFrameId: matchingSurface?.MuninnEvidenceFrameId ?? "",
            LatestMimirEvidenceFrameId: matchingSurface?.MimirEvidenceFrameId ?? "",
            LatestMimirPoseFrameId: matchingSurface?.MimirPoseFrameId ?? "",
            LatestFensalirFrameId: matchingSurface?.FensalirFrameId ?? "",
            LatestVerdict: matchingSurface?.Verdict ?? MimirMoveProofVerdict.Unknown,
            ObservedAtNs: observedAtNs);
    }

    private static bool IsSurfaceFromEvidenceStream(
        MimirMoveProofSurfaceDocument? latestProofSurface,
        string evidenceStreamId)
    {
        if (latestProofSurface is null || string.IsNullOrWhiteSpace(evidenceStreamId))
        {
            return false;
        }

        return string.Equals(latestProofSurface.MuninnEvidenceFrameId, evidenceStreamId, StringComparison.Ordinal) ||
            latestProofSurface.MuninnEvidenceFrameId.StartsWith($"{evidenceStreamId}:", StringComparison.Ordinal);
    }
}

public sealed class MimirUnavailableMoveProofEvidenceRingProvider : IMimirMoveProofEvidenceRingProvider
{
    public static MimirUnavailableMoveProofEvidenceRingProvider Instance { get; } = new();

    private MimirUnavailableMoveProofEvidenceRingProvider()
    {
    }

    public bool TryOpenEvidenceRing(
        MimirMoveProofRuntimeConfiguration configuration,
        out MimirMoveProofEvidenceRingLease? lease,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lease = null;
        diagnostic = "No live CultMesh shared-memory ring opener is configured for this runtime; C# CultMesh rings are currently in-process only.";
        return false;
    }
}
