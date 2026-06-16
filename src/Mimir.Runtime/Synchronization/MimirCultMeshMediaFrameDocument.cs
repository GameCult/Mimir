using GameCult.Caching;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

[CultDocument("mimir.cultmesh_media_frame", "mimir.cultmesh_media_frame.v1")]
[MessagePackObject]
public sealed record MimirCultMeshMediaFrameDocument(
    [property: Key(0)]
    [property: CultName]
    string FrameId,
    [property: Key(1)] string StreamId,
    [property: Key(2)] long Sequence,
    [property: Key(3)] string ProducedAtUtc,
    [property: Key(4)] long TimestampNanoseconds,
    [property: Key(5)] string PayloadKind,
    [property: Key(6)] string Container,
    [property: Key(7)] string VideoCodec,
    [property: Key(8)] string AudioCodec,
    [property: Key(9)] int PayloadBytes,
    [property: Key(10)] byte[] Payload,
    [property: Key(11)] string ProducerNode,
    [property: Key(12)] string ClockDomainId,
    [property: Key(13)] string[] Tags);
