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

[CultDocument("mimir.move_proof_evidence_frame_snapshot", "mimir.move_proof_evidence_frame_snapshot.v1")]
[MessagePackObject]
public sealed record MimirMoveProofEvidenceFrameSnapshotDocument(
    [property: Key(0)]
    [property: CultName]
    string SnapshotId,
    [property: Key(1)] string EvidenceStreamId,
    [property: Key(2)] string FrameId,
    [property: Key(3)] string ProducerPeerId,
    [property: Key(4)] long PublishedAtNs,
    [property: Key(5)] ulong CapturedAtNs,
    [property: Key(6)] byte[] Payload);

public static class MimirMoveProofEvidenceFrameSnapshot
{
    public static MimirMoveProofEvidenceFrameSnapshotDocument Create(
        MimirMuninnMoveEvidenceStreamFrame frame,
        byte[] payload,
        ulong capturedAtNs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
        {
            throw new ArgumentException("Snapshot payload must not be empty.", nameof(payload));
        }

        return new MimirMoveProofEvidenceFrameSnapshotDocument(
            SnapshotId: $"{frame.FrameId}:snapshot",
            EvidenceStreamId: StreamIdFromFrameId(frame.FrameId),
            FrameId: frame.FrameId,
            ProducerPeerId: frame.ProducerPeerId,
            PublishedAtNs: frame.PublishedAtNs,
            CapturedAtNs: capturedAtNs,
            Payload: payload);
    }

    public static MimirMoveProofEvidenceFrameSnapshotDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return MessagePackSerializer.Deserialize<MimirMoveProofEvidenceFrameSnapshotDocument>(File.ReadAllBytes(path));
    }

    public static void Save(string path, MimirMoveProofEvidenceFrameSnapshotDocument snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, MessagePackSerializer.Serialize(snapshot));
    }

    private static string StreamIdFromFrameId(string frameId)
    {
        var separator = frameId.LastIndexOf(':');
        return separator > 0 ? frameId[..separator] : frameId;
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

public sealed class MimirConfiguredMoveProofEvidenceRingProvider : IMimirMoveProofEvidenceRingProvider
{
    public static MimirConfiguredMoveProofEvidenceRingProvider Instance { get; } = new();

    private MimirConfiguredMoveProofEvidenceRingProvider()
    {
    }

    public bool TryOpenEvidenceRing(
        MimirMoveProofRuntimeConfiguration configuration,
        out MimirMoveProofEvidenceRingLease? lease,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.EvidenceSnapshotPath))
        {
            return MimirUnavailableMoveProofEvidenceRingProvider.Instance.TryOpenEvidenceRing(
                configuration,
                out lease,
                out diagnostic);
        }

        var snapshotPath = Path.GetFullPath(configuration.EvidenceSnapshotPath);
        if (!File.Exists(snapshotPath))
        {
            lease = null;
            diagnostic = $"Move proof evidence snapshot not found: {snapshotPath}";
            return false;
        }

        var snapshot = MimirMoveProofEvidenceFrameSnapshot.Load(snapshotPath);
        if (!string.Equals(snapshot.EvidenceStreamId, configuration.EvidenceStreamId, StringComparison.Ordinal))
        {
            lease = null;
            diagnostic = $"Move proof evidence snapshot stream '{snapshot.EvidenceStreamId}' does not match configured evidence stream '{configuration.EvidenceStreamId}'.";
            return false;
        }

        var decoded = MimirMuninnMoveEvidenceAdapter.DeserializeStreamFrame(snapshot.Payload);
        if (!string.Equals(decoded.FrameId, snapshot.FrameId, StringComparison.Ordinal) ||
            !string.Equals(decoded.ProducerPeerId, snapshot.ProducerPeerId, StringComparison.Ordinal) ||
            decoded.PublishedAtNs != snapshot.PublishedAtNs)
        {
            lease = null;
            diagnostic = "Move proof evidence snapshot metadata does not match its encoded Muninn frame payload.";
            return false;
        }

        var ring = new CultMeshSharedMemoryFrameRing(
            configuration.EvidenceStreamId,
            slotCount: 1,
            slotByteLength: snapshot.Payload.Length);
        if (!ring.TryPublishCopy(snapshot.Payload, snapshot.PublishedAtNs, durationNs: 0, out _))
        {
            ring.Dispose();
            lease = null;
            diagnostic = $"Move proof evidence snapshot could not be published into fallback ring: {snapshotPath}";
            return false;
        }

        lease = new MimirMoveProofEvidenceRingLease(ring, ownsRing: true);
        diagnostic = $"one-copy evidence snapshot supplied from {snapshotPath}; this is not a live cross-process CultMesh ring";
        return true;
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
