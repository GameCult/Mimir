using MessagePack;

namespace Mimir.Runtime.Synchronization;

[MessagePackObject]
public sealed record MoveVisibilityObservation(
    [property: Key(0)] string ProviderId,
    [property: Key(1)] string CameraId,
    [property: Key(2)] string MoveId,
    [property: Key(3)] string FrameId,
    [property: Key(4)] long PublishedAtNs,
    [property: Key(5)] float CenterXPx,
    [property: Key(6)] float CenterYPx,
    [property: Key(7)] float RadiusPx,
    [property: Key(8)] float Confidence);

[MessagePackObject]
public sealed record MoveCrossCameraCorrespondence(
    [property: Key(0)] string MoveId,
    [property: Key(1)] MoveVisibilityObservation First,
    [property: Key(2)] MoveVisibilityObservation Second,
    [property: Key(3)] long AbsoluteSkewNs);

[MessagePackObject]
public sealed record MoveVisibilityWindowReceipt(
    [property: Key(0)] string Schema,
    [property: Key(1)] long StartedAtNs,
    [property: Key(2)] long EndedAtNs,
    [property: Key(3)] string[] Providers,
    [property: Key(4)] MoveVisibilityObservation[] Observations,
    [property: Key(5)] MoveCrossCameraCorrespondence[] Correspondences);
