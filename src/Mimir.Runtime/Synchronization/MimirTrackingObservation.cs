using MessagePack;
using GameCult.Caching;

namespace Mimir.Runtime.Synchronization;

public enum MimirTrackingObservationKind
{
    Unknown,
    PsMoveController,
    Hand,
    Headset,
    Tool,
}

[MessagePackObject]
public readonly record struct MimirVector3Snapshot(
    [property: Key(0)] double X,
    [property: Key(1)] double Y,
    [property: Key(2)] double Z);

[MessagePackObject]
public readonly record struct MimirQuaternionSnapshot(
    [property: Key(0)] double X,
    [property: Key(1)] double Y,
    [property: Key(2)] double Z,
    [property: Key(3)] double W);

[MessagePackObject]
public sealed record MimirTrackingButtonSnapshot(
    [property: Key(0)] string Id,
    [property: Key(1)] bool Pressed,
    [property: Key(2)] double Value);

[MessagePackObject]
[CultDocument("mimir.move_tracking_observation", "mimir.move_tracking_observation.v1")]
public sealed record MimirTrackingObservation(
    [property: Key(0)]
    [property: CultName]
    string ObservationId,
    [property: Key(1)] string StreamId,
    [property: Key(2)] string DeviceId,
    [property: Key(3)] MimirTrackingObservationKind Kind,
    [property: Key(4)] string ProducerId,
    [property: Key(5)] string HostId,
    [property: Key(6)] string DiscoveryProviderId,
    [property: Key(7)] string TrackingSpaceId,
    [property: Key(8)] string CalibrationId,
    [property: Key(9)] long SourceTimestampNs,
    [property: Key(10)] long ArrivalTimestampNs,
    [property: Key(11)] ulong Sequence,
    [property: Key(12)] MimirVector3Snapshot PositionMeters,
    [property: Key(13)] MimirQuaternionSnapshot Orientation,
    [property: Key(14)] MimirVector3Snapshot LinearVelocityMetersPerSecond,
    [property: Key(15)] MimirVector3Snapshot AngularVelocityRadiansPerSecond,
    [property: Key(16)] double Confidence,
    [property: Key(17)] double LatencyMilliseconds,
    [property: Key(18)] double Battery01,
    [property: Key(19)] MimirTrackingButtonSnapshot[] Buttons)
{
    public static MimirTrackingObservation PsMove(
        string streamId,
        string deviceId,
        ulong sequence,
        long sourceTimestampNs,
        long arrivalTimestampNs,
        MimirVector3Snapshot positionMeters,
        MimirQuaternionSnapshot orientation,
        MimirVector3Snapshot linearVelocityMetersPerSecond,
        MimirVector3Snapshot angularVelocityRadiansPerSecond,
        double confidence,
        string calibrationId,
        string trackingSpaceId = "nightwing-move-space",
        string producerId = "muninn:nightwing:move",
        string hostId = "nightwing",
        string discoveryProviderId = "odin",
        double latencyMilliseconds = 0.0,
        double battery01 = double.NaN,
        MimirTrackingButtonSnapshot[]? buttons = null) =>
        new(
            $"{streamId}:{sequence}",
            streamId,
            deviceId,
            MimirTrackingObservationKind.PsMoveController,
            producerId,
            hostId,
            discoveryProviderId,
            trackingSpaceId,
            calibrationId,
            sourceTimestampNs,
            arrivalTimestampNs,
            sequence,
            positionMeters,
            orientation,
            linearVelocityMetersPerSecond,
            angularVelocityRadiansPerSecond,
            Math.Clamp(confidence, 0.0, 1.0),
            Math.Max(0.0, latencyMilliseconds),
            double.IsFinite(battery01) ? Math.Clamp(battery01, 0.0, 1.0) : double.NaN,
            buttons ?? []);
}
