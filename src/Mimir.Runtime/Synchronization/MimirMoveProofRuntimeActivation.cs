using GameCult.Mesh;

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
    string Diagnostic);

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
