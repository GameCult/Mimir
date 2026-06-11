using GameCult.Caching;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

[CultDocument("muninn.move_marker_candidate", "muninn.move_marker_candidate.v1")]
[MessagePackObject]
public sealed record MuninnMoveMarkerCandidateDocument(
    [property: Key(0)] string StreamId,
    [property: Key(1)] string HostId,
    [property: Key(2)] string CameraId,
    [property: Key(3)] ulong FrameSequence,
    [property: Key(4)] ulong SourceIdHash,
    [property: Key(5)] uint TileX,
    [property: Key(6)] uint TileY,
    [property: Key(7)] float CenterXPx,
    [property: Key(8)] float CenterYPx,
    [property: Key(9)] float RadiusPx,
    [property: Key(10)] uint AreaPx,
    [property: Key(11)] float MeanLuma,
    [property: Key(12)] uint PeakLuma,
    [property: Key(13)] float Score,
    [property: Key(14)] string ObservedAt);

[CultDocument("muninn.move_controller_state", "muninn.move_controller_state.v1")]
[MessagePackObject]
public sealed record MuninnMoveControllerStateDocument(
    [property: Key(0)] string StreamId,
    [property: Key(1)] string HostId,
    [property: Key(2)] string MoveId,
    [property: Key(3)] ulong Sequence,
    [property: Key(4)] long SourceTimestampNs,
    [property: Key(5)] float[] AccelerometerXyz,
    [property: Key(6)] float[] GyroscopeXyz,
    [property: Key(7)] float[] MagnetometerXyz,
    [property: Key(8)] float TriggerValue,
    [property: Key(9)] string[] Buttons,
    [property: Key(10)] float Battery01,
    [property: Key(11)] string ObservedAt);

public static class MimirMuninnMoveEvidenceAdapter
{
    public static IReadOnlyList<MimirNativeMoveEvidenceSample> BuildNativeSamples(
        IEnumerable<MuninnMoveMarkerCandidateDocument> markerCandidates,
        IEnumerable<MuninnMoveControllerStateDocument> controllerStates)
    {
        ArgumentNullException.ThrowIfNull(markerCandidates);
        ArgumentNullException.ThrowIfNull(controllerStates);
        var samples = new List<MimirNativeMoveEvidenceSample>();
        samples.AddRange(markerCandidates.Select(ToNativeSample));
        samples.AddRange(controllerStates.Select(ToNativeSample));
        return samples
            .OrderBy(sample => sample.SourceTimestampNs)
            .ThenBy(sample => sample.ArrivalNs)
            .ThenBy(sample => sample.Sequence)
            .ToArray();
    }

    public static MimirNativeMoveEvidenceSample ToNativeSample(MuninnMoveMarkerCandidateDocument marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var observedNs = ObservedAtToUnixNs(marker.ObservedAt);
        var timestampNs = observedNs == 0
            ? checked((ulong)marker.FrameSequence)
            : observedNs;
        var witnessHash = marker.SourceIdHash != 0
            ? marker.SourceIdHash
            : Fnva64($"{marker.HostId}:{marker.CameraId}:{marker.StreamId}");

        return new MimirNativeMoveEvidenceSample(
            WitnessIdHash: witnessHash,
            ControllerIdHash: 0,
            SourceTimestampNs: timestampNs,
            ArrivalNs: timestampNs,
            Sequence: marker.FrameSequence,
            EvidenceKind: (uint)MimirNativeMoveEvidenceKind.OpticalMarker,
            Flags: 0,
            ImageX: marker.CenterXPx,
            ImageY: marker.CenterYPx,
            RadiusPx: marker.RadiusPx,
            Confidence: Math.Clamp(marker.Score, 0.0f, 1.0f),
            AccelX: 0.0f,
            AccelY: 0.0f,
            AccelZ: 0.0f,
            GyroX: 0.0f,
            GyroY: 0.0f,
            GyroZ: 0.0f,
            Trigger: 0.0f,
            ButtonsMask: 0,
            Reserved: 0,
            Battery01: float.NaN,
            Reserved1: marker.TileX,
            Reserved2: marker.TileY);
    }

    public static MimirNativeMoveEvidenceSample ToNativeSample(MuninnMoveControllerStateDocument controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var observedNs = ObservedAtToUnixNs(controller.ObservedAt);
        var sourceNs = controller.SourceTimestampNs > 0
            ? checked((ulong)controller.SourceTimestampNs)
            : observedNs;
        var vector = ExtractVectors(controller);

        return new MimirNativeMoveEvidenceSample(
            WitnessIdHash: Fnva64(controller.StreamId),
            ControllerIdHash: Fnva64($"{controller.HostId}:{controller.MoveId}"),
            SourceTimestampNs: sourceNs,
            ArrivalNs: observedNs == 0 ? sourceNs : observedNs,
            Sequence: controller.Sequence,
            EvidenceKind: (uint)MimirNativeMoveEvidenceKind.ControllerState,
            Flags: 0,
            ImageX: float.NaN,
            ImageY: float.NaN,
            RadiusPx: float.NaN,
            Confidence: 1.0f,
            AccelX: vector.AccelX,
            AccelY: vector.AccelY,
            AccelZ: vector.AccelZ,
            GyroX: vector.GyroX,
            GyroY: vector.GyroY,
            GyroZ: vector.GyroZ,
            Trigger: Math.Clamp(controller.TriggerValue, 0.0f, 1.0f),
            ButtonsMask: ButtonMask(controller.Buttons),
            Reserved: 0,
            Battery01: float.IsFinite(controller.Battery01)
                ? Math.Clamp(controller.Battery01, 0.0f, 1.0f)
                : float.NaN,
            Reserved1: 0,
            Reserved2: 0);
    }

    public static ulong Fnva64(string value)
    {
        var hash = 14_695_981_039_346_656_037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 1_099_511_628_211UL;
        }

        return hash == 0 ? 1 : hash;
    }

    private static ulong ObservedAtToUnixNs(string observedAt)
    {
        if (!DateTimeOffset.TryParse(observedAt, out var parsed))
        {
            return 0;
        }

        var secondsNs = checked((ulong)parsed.ToUnixTimeSeconds() * 1_000_000_000UL);
        var tickRemainderNs = checked((ulong)(parsed.Ticks % TimeSpan.TicksPerSecond) * 100UL);
        return checked(secondsNs + tickRemainderNs);
    }

    private static (float AccelX, float AccelY, float AccelZ, float GyroX, float GyroY, float GyroZ) ExtractVectors(
        MuninnMoveControllerStateDocument controller)
    {
        var accel = controller.AccelerometerXyz ?? [];
        var gyro = controller.GyroscopeXyz ?? [];
        return (
            Component(accel, 0),
            Component(accel, 1),
            Component(accel, 2),
            Component(gyro, 0),
            Component(gyro, 1),
            Component(gyro, 2));
    }

    private static float Component(IReadOnlyList<float> values, int index) =>
        index < values.Count && float.IsFinite(values[index]) ? values[index] : 0.0f;

    private static uint ButtonMask(IEnumerable<string>? buttons)
    {
        var mask = 0u;
        foreach (var button in buttons ?? [])
        {
            var bit = button.ToLowerInvariant() switch
            {
                "select" => 0,
                "l3" => 1,
                "r3" => 2,
                "start" => 3,
                "up" => 4,
                "right" => 5,
                "down" => 6,
                "left" => 7,
                "l2" => 8,
                "r2" => 9,
                "l1" => 10,
                "r1" => 11,
                "triangle" => 12,
                "circle" => 13,
                "cross" => 14,
                "square" => 15,
                "ps" => 16,
                "move" => 17,
                "trigger" => 18,
                _ => -1
            };
            if (bit >= 0)
            {
                mask |= 1u << bit;
            }
        }

        return mask;
    }
}
