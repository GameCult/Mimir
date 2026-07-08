using GameCult.Mesh;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirMoveProofRuntimeDriverOptions(
    string MimirEvidenceSourceId,
    string MimirEvidenceFramePrefix,
    string MimirPoseFramePrefix,
    string MimirPoseProducerPeerId,
    string FensalirFramePrefix,
    string FusionAuthorityId = "mimir.runtime.move-fusion",
    string ConsumerContract = "fensalir.move-controller-input");

public sealed class MimirMoveProofRuntimeDriver
{
    private readonly CultMeshSharedMemoryFrameRing ring;
    private readonly MimirNativeReservoirRuntime reservoir;
    private readonly MimirMoveFusionRigCalibration calibration;
    private readonly MimirMoveProofRuntimeDriverOptions options;
    private string lastFrameId = "";

    public MimirMoveProofRuntimeDriver(
        CultMeshSharedMemoryFrameRing ring,
        MimirNativeReservoirRuntime reservoir,
        MimirMoveFusionRigCalibration calibration,
        MimirMoveProofRuntimeDriverOptions options)
    {
        this.ring = ring ?? throw new ArgumentNullException(nameof(ring));
        this.reservoir = reservoir ?? throw new ArgumentNullException(nameof(reservoir));
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MimirEvidenceSourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MimirEvidenceFramePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MimirPoseFramePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MimirPoseProducerPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FensalirFramePrefix);
    }

    public MimirMoveProofPipelineResult? LastResult { get; private set; }

    public bool TryBuildLatest(ulong fensalirPresentedAtNs, out MimirMoveProofPipelineResult? result)
    {
        result = null;
        if (!ring.TryAcquireLatestRead(out var lease))
        {
            return false;
        }

        using (lease)
        {
            var frame = MimirMuninnMoveEvidenceAdapter.DeserializeStreamFrame(lease.Memory[..lease.Handle.ByteLength]);
            if (string.Equals(frame.FrameId, lastFrameId, StringComparison.Ordinal))
            {
                return false;
            }

            var sequence = SequenceSuffix(frame.FrameId);
            var pipelineOptions = new MimirMoveProofPipelineOptions(
                MimirEvidenceSourceId: options.MimirEvidenceSourceId,
                MimirEvidenceFrameId: $"{options.MimirEvidenceFramePrefix}:{sequence}",
                MimirPoseFrameId: $"{options.MimirPoseFramePrefix}:{sequence}",
                MimirPoseProducerPeerId: options.MimirPoseProducerPeerId,
                FensalirFrameId: $"{options.FensalirFramePrefix}:{sequence}",
                FensalirPresentedAtNs: fensalirPresentedAtNs,
                FusionAuthorityId: options.FusionAuthorityId,
                ConsumerContract: options.ConsumerContract);

            if (!MimirMoveProofPipeline.TryBuild(frame, reservoir, calibration, pipelineOptions, out result))
            {
                return false;
            }

            lastFrameId = frame.FrameId;
            LastResult = result;
            return true;
        }
    }

    public string Describe()
    {
        var last = LastResult?.ProofSurface;
        return last is null
            ? $"{options.MimirEvidenceSourceId} waiting"
            : $"{last.ProofId} verdict={last.Verdict} chain={last.MuninnEvidenceFrameId}->{last.MimirEvidenceFrameId}->{last.MimirPoseFrameId}->{last.FensalirFrameId}";
    }

    private static string SequenceSuffix(string frameId)
    {
        var separator = frameId.LastIndexOf(':');
        return separator >= 0 && separator < frameId.Length - 1
            ? frameId[(separator + 1)..]
            : frameId;
    }
}
