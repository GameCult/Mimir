using System.Buffers.Binary;
using GameCult.Caching;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

[CultDocument("mimir.face_tracking_observation", "mimir.face_tracking_observation.v1")]
[MessagePackObject]
public sealed record MimirFaceTrackingObservation(
    [property: Key(0), CultName] string ObservationId,
    [property: Key(1)] string StreamId,
    [property: Key(2)] string DeviceId,
    [property: Key(3)] string SubjectName,
    [property: Key(4)] string ProducerId,
    [property: Key(5)] string HostId,
    [property: Key(6)] ulong Sequence,
    [property: Key(7)] long SourceFrame,
    [property: Key(8)] int SourceSubFrame,
    [property: Key(9)] int FrameRateNumerator,
    [property: Key(10)] int FrameRateDenominator,
    [property: Key(11)] long ArrivalTimestampNs,
    [property: Key(12)] string SourceClockDomainId,
    [property: Key(13)] string ArrivalClockDomainId,
    [property: Key(14)] ulong SourceEpoch,
    [property: Key(15)] string[] ChannelNames,
    [property: Key(16)] float[] ChannelValues,
    [property: Key(17)] string SourceProtocol);

public sealed record MimirLiveLinkFacePacket(
    byte Version, string DeviceId, string SubjectName, uint Frame, uint SubFrame,
    uint FrameRateNumerator, uint FrameRateDenominator, float[] Channels);

public static class MimirLiveLinkFaceDecoder
{
    // Epic AppleARKitLiveLinkSource v6 wire layout, independently mirrored by
    // https://github.com/Jules-NC/GodotARKit/blob/main/arkit_packet.gd
    public const byte SupportedVersion = 6;
    public const int ChannelCount = 61;

    public static readonly string[] ChannelNames =
    [
        "EyeBlinkLeft", "EyeLookDownLeft", "EyeLookInLeft", "EyeLookOutLeft", "EyeLookUpLeft",
        "EyeSquintLeft", "EyeWideLeft", "EyeBlinkRight", "EyeLookDownRight", "EyeLookInRight",
        "EyeLookOutRight", "EyeLookUpRight", "EyeSquintRight", "EyeWideRight", "JawForward",
        "JawRight", "JawLeft", "JawOpen", "MouthClose", "MouthFunnel", "MouthPucker",
        "MouthRight", "MouthLeft", "MouthSmileLeft", "MouthSmileRight", "MouthFrownLeft",
        "MouthFrownRight", "MouthDimpleLeft", "MouthDimpleRight", "MouthStretchLeft",
        "MouthStretchRight", "MouthRollLower", "MouthRollUpper", "MouthShrugLower",
        "MouthShrugUpper", "MouthPressLeft", "MouthPressRight", "MouthLowerDownLeft",
        "MouthLowerDownRight", "MouthUpperUpLeft", "MouthUpperUpRight", "BrowDownLeft",
        "BrowDownRight", "BrowInnerUp", "BrowOuterUpLeft", "BrowOuterUpRight", "CheekPuff",
        "CheekSquintLeft", "CheekSquintRight", "NoseSneerLeft", "NoseSneerRight", "TongueOut",
        "HeadYaw", "HeadPitch", "HeadRoll", "LeftEyeYaw", "LeftEyePitch", "LeftEyeRoll",
        "RightEyeYaw", "RightEyePitch", "RightEyeRoll"
    ];

    public static bool TryDecode(ReadOnlySpan<byte> payload, out MimirLiveLinkFacePacket? packet, out string error)
    {
        packet = null;
        error = string.Empty;
        var reader = new BigEndianReader(payload);
        if (!reader.TryByte(out var version) || version != SupportedVersion)
        {
            error = "Live Link Face packet version is absent or unsupported; expected v6.";
            return false;
        }
        if (!reader.TryString(out var deviceId) || !reader.TryString(out var subjectName) ||
            !reader.TryUInt32(out var frame) || !reader.TryUInt32(out var subFrame) ||
            !reader.TryUInt32(out var fps) || !reader.TryUInt32(out var denominator) ||
            !reader.TryByte(out var count) || count != ChannelCount)
        {
            error = "Live Link Face v6 header is truncated or does not declare 61 channels.";
            return false;
        }
        var channels = new float[ChannelCount];
        for (var index = 0; index < channels.Length; index++)
        {
            if (!reader.TrySingle(out channels[index]) || !float.IsFinite(channels[index]))
            {
                error = $"Live Link Face channel {index} is truncated or non-finite.";
                return false;
            }
        }
        if (reader.Remaining != 0 || string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(subjectName) || denominator == 0)
        {
            error = "Live Link Face packet has trailing bytes or invalid identity/timecode.";
            return false;
        }
        packet = new(version, deviceId, subjectName, frame, subFrame, fps, denominator, channels);
        return true;
    }

    private ref struct BigEndianReader
    {
        private readonly ReadOnlySpan<byte> bytes;
        private int offset;
        public BigEndianReader(ReadOnlySpan<byte> bytes) { this.bytes = bytes; offset = 0; }
        public int Remaining => bytes.Length - offset;
        public bool TryByte(out byte value) { value = 0; if (Remaining < 1) return false; value = bytes[offset++]; return true; }
        public bool TryUInt32(out uint value) { value = 0; if (Remaining < 4) return false; value = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]); offset += 4; return true; }
        public bool TrySingle(out float value) { value = 0; if (!TryUInt32(out var bits)) return false; value = BitConverter.Int32BitsToSingle(unchecked((int)bits)); return true; }
        public bool TryString(out string value)
        {
            value = string.Empty;
            if (!TryUInt32(out var length) || length == 0 || length > 1024 || Remaining < length) return false;
            value = System.Text.Encoding.UTF8.GetString(bytes.Slice(offset, checked((int)length)));
            offset += checked((int)length);
            return true;
        }
    }
}

public sealed class MimirFaceObservationLedger(string streamId, string hostId = "starfire", string producerId = "mimir.runtime.face-receiver")
{
    private ulong sequence;
    private ulong sourceEpoch;
    private uint? lastSourceFrame;
    private long? lastArrivalTimestampNs;
    private string? boundIdentity;

    public bool TryAdmit(MimirLiveLinkFacePacket packet, long arrivalTimestampNs, out MimirFaceTrackingObservation? observation, out string error)
    {
        ArgumentNullException.ThrowIfNull(packet);
        observation = null;
        error = string.Empty;
        var identity = $"{packet.DeviceId}\n{packet.SubjectName}";
        if (boundIdentity is null) boundIdentity = identity;
        if (!string.Equals(boundIdentity, identity, StringComparison.Ordinal))
        {
            error = "Face stream identity changed; one ledger admits exactly one configured device and subject.";
            return false;
        }
        if (lastSourceFrame is { } prior && packet.Frame <= prior)
        {
            var restartGap = lastArrivalTimestampNs is { } lastArrival && arrivalTimestampNs - lastArrival >= 1_000_000_000L;
            if (!restartGap)
            {
                error = $"Duplicate or stale face frame {packet.Frame} arrived after {prior}.";
                return false;
            }
            sourceEpoch++;
        }
        lastSourceFrame = packet.Frame;
        lastArrivalTimestampNs = arrivalTimestampNs;
        var admittedSequence = sequence++;
        var values = packet.Channels.ToArray();
        for (var index = 0; index < 52; index++) values[index] = Math.Clamp(values[index], 0f, 1f);
        observation = new($"{streamId}:{admittedSequence:D20}", streamId, packet.DeviceId, packet.SubjectName,
            producerId, hostId, admittedSequence, packet.Frame, checked((int)packet.SubFrame),
            checked((int)packet.FrameRateNumerator), checked((int)packet.FrameRateDenominator), arrivalTimestampNs,
            "iphone-livelink-timecode", "starfire-monotonic", sourceEpoch,
            MimirLiveLinkFaceDecoder.ChannelNames.ToArray(), values,
            "epic.livelinkface.applearkit.udp.v6");
        return true;
    }
}
