using GameCult.Mesh;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

[MessagePackObject]
public sealed record MimirMoveControllerPoseStreamFrame(
    [property: Key(0)] string FrameId,
    [property: Key(1)] string ProducerPeerId,
    [property: Key(2)] long PublishedAtNs,
    [property: Key(3)] string TrackingSpaceId,
    [property: Key(4)] string CalibrationId,
    [property: Key(5)] MimirMoveControllerPoseDocument[] Poses);

public static class MimirMovePoseStream
{
    public const string StreamMetadataSchemaId = "mimir.move_controller_pose_stream_frame.v1";

    public static CultMeshStreamDescriptor CreateStreamDescriptor(
        string streamId,
        string verseId,
        string ownerPeerId,
        string clockDomainId,
        string trackingSpaceId,
        string calibrationId) =>
        new(
            streamId,
            verseId,
            ownerPeerId,
            CultMeshStreamKind.Bytes,
            new CultMeshStreamClock(clockDomainId, sourceId: streamId, confidence: 1.0, evidenceKind: "mimir-move-controller-pose"),
            [CultMeshStreamBodyTransport.SharedMemory, CultMeshStreamBodyTransport.CultCachePage],
            label: $"Mimir Move controller poses ({trackingSpaceId}, {calibrationId})",
            requiredAccess: CultMeshStreamAccess.Read,
            maxInFlightFrames: 4,
            metadataSchemaId: StreamMetadataSchemaId);

    public static MimirMoveControllerPoseStreamFrame CreateFrame(
        string frameId,
        string producerPeerId,
        long publishedAtNs,
        string trackingSpaceId,
        string calibrationId,
        IEnumerable<MimirMoveControllerPoseDocument> poses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackingSpaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationId);
        ArgumentNullException.ThrowIfNull(poses);

        return new MimirMoveControllerPoseStreamFrame(
            frameId,
            producerPeerId,
            publishedAtNs,
            trackingSpaceId,
            calibrationId,
            poses
                .Where(pose => string.Equals(pose.TrackingSpaceId, trackingSpaceId, StringComparison.Ordinal) &&
                    string.Equals(pose.CalibrationId, calibrationId, StringComparison.Ordinal))
                .OrderBy(pose => pose.SourceTimestampNs)
                .ThenBy(pose => pose.Sequence)
                .ToArray());
    }

    public static byte[] SerializeFrame(MimirMoveControllerPoseStreamFrame frame) =>
        MessagePackSerializer.Serialize(frame);

    public static MimirMoveControllerPoseStreamFrame DeserializeFrame(ReadOnlyMemory<byte> bytes) =>
        MessagePackSerializer.Deserialize<MimirMoveControllerPoseStreamFrame>(bytes);
}
