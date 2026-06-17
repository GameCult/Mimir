using GameCult.Caching;
using GameCult.Mesh;
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
    [property: Key(11)] string ObservedAt,
    [property: Key(12)] string SourcePath = "");

[CultDocument("muninn.move_identity", "muninn.move_identity.v1")]
[MessagePackObject]
public sealed record MuninnMoveIdentityDocument(
    [property: Key(0)] string IdentityId,
    [property: Key(1)] string HostId,
    [property: Key(2)] string MoveId,
    [property: Key(3)] string SourcePath,
    [property: Key(4)] string BluetoothHostAddress,
    [property: Key(5)] string State,
    [property: Key(6)] string Detail,
    [property: Key(7)] string ObservedAt);

public sealed record MimirMuninnMoveIdentitySnapshot(
    string IdentityId,
    string HostId,
    string MoveId,
    string SourcePath,
    string BluetoothHostAddress,
    string State,
    string Detail,
    ulong ObservedAtNs,
    ulong ControllerIdHash);

public sealed record MimirMuninnMoveRosterEntry(
    string MoveId,
    ulong ControllerIdHash,
    string LatestHostId,
    string LatestState,
    string StateSummary,
    string PickupReadiness,
    bool IsBluetoothPickupReady,
    string BluetoothHostAddress,
    bool HasUsbWitness,
    string[] UsbHostIds,
    string[] BluetoothPickupHostIds,
    string[] UsbBlockingHostIds,
    string[] SourcePaths,
    ulong ObservedAtNs);

[MessagePackObject]
public sealed record MimirMuninnMoveEvidenceStreamFrame(
    [property: Key(0)] string FrameId,
    [property: Key(1)] string ProducerPeerId,
    [property: Key(2)] long PublishedAtNs,
    [property: Key(3)] MuninnMoveMarkerCandidateDocument[] MarkerCandidates,
    [property: Key(4)] MuninnMoveControllerStateDocument[] ControllerStates);

public static class MimirMuninnMoveEvidenceAdapter
{
    private const ulong LiveIdentityWindowNs = 60_000_000_000UL;

    public const string StreamMetadataSchemaId = "mimir.muninn_move_evidence_stream_frame.v1";

    public static CultMeshStreamDescriptor CreateStreamDescriptor(
        string streamId,
        string verseId,
        string ownerPeerId,
        string clockDomainId) =>
        new(
            streamId,
            verseId,
            ownerPeerId,
            CultMeshStreamKind.Bytes,
            new CultMeshStreamClock(clockDomainId, sourceId: streamId, confidence: 1.0, evidenceKind: "muninn-move-evidence"),
            [CultMeshStreamBodyTransport.SharedMemory, CultMeshStreamBodyTransport.CultCachePage],
            label: "Muninn Move evidence",
            requiredAccess: CultMeshStreamAccess.Read,
            maxInFlightFrames: 4,
            metadataSchemaId: StreamMetadataSchemaId);

    public static byte[] SerializeStreamFrame(
        MimirMuninnMoveEvidenceStreamFrame frame) =>
        MessagePackSerializer.Serialize(frame);

    public static MimirMuninnMoveEvidenceStreamFrame DeserializeStreamFrame(
        ReadOnlyMemory<byte> bytes) =>
        MessagePackSerializer.Deserialize<MimirMuninnMoveEvidenceStreamFrame>(bytes);

    public static bool TryAdmitLatestCultMeshFrame(
        CultMeshSharedMemoryFrameRing ring,
        MimirNativeReservoirRuntime runtime,
        string producerSourceId,
        string calibrationId,
        string trackingSpaceId,
        out MimirNativeSampleHandle handle,
        out int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(runtime);
        handle = default;
        sampleCount = 0;
        if (!ring.TryAcquireLatestRead(out var lease))
        {
            return false;
        }

        using (lease)
        {
            var frame = DeserializeStreamFrame(lease.Memory[..lease.Handle.ByteLength]);
            var samples = BuildNativeSamples(frame.MarkerCandidates, frame.ControllerStates);
            sampleCount = samples.Count;
            if (sampleCount == 0)
            {
                return false;
            }

            handle = runtime.AdmitMoveEvidence(producerSourceId, samples, calibrationId, trackingSpaceId);
            return true;
        }
    }

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

    public static IReadOnlyList<MimirMuninnMoveIdentitySnapshot> BuildIdentitySnapshots(
        IEnumerable<MuninnMoveIdentityDocument> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return identities
            .Select(ToIdentitySnapshot)
            .OrderBy(identity => identity.MoveId, StringComparer.Ordinal)
            .ThenByDescending(identity => identity.ObservedAtNs)
            .ToArray();
    }

    public static IReadOnlyList<MimirMuninnMoveRosterEntry> BuildIdentityRoster(
        IEnumerable<MuninnMoveIdentityDocument> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return BuildIdentitySnapshots(identities)
            .GroupBy(identity => identity.MoveId, StringComparer.Ordinal)
            .Select(ToRosterEntry)
            .OrderBy(entry => entry.MoveId, StringComparer.Ordinal)
            .ToArray();
    }

    public static MimirMuninnMoveIdentitySnapshot ToIdentitySnapshot(MuninnMoveIdentityDocument identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new MimirMuninnMoveIdentitySnapshot(
            IdentityId: identity.IdentityId,
            HostId: identity.HostId,
            MoveId: identity.MoveId,
            SourcePath: identity.SourcePath,
            BluetoothHostAddress: identity.BluetoothHostAddress,
            State: identity.State,
            Detail: identity.Detail,
            ObservedAtNs: ObservedAtToUnixNs(identity.ObservedAt),
            ControllerIdHash: Fnva64($"{identity.HostId}:{identity.MoveId}"));
    }

    private static MimirMuninnMoveRosterEntry ToRosterEntry(
        IGrouping<string, MimirMuninnMoveIdentitySnapshot> group)
    {
        var orderedSnapshots = group
            .OrderByDescending(identity => identity.ObservedAtNs)
            .ThenBy(identity => identity.HostId, StringComparer.Ordinal)
            .ThenBy(identity => identity.SourcePath, StringComparer.Ordinal)
            .ToArray();
        var latest = orderedSnapshots[0];
        var snapshots = orderedSnapshots
            .Where(identity => IsLiveIdentitySnapshot(identity, latest.ObservedAtNs))
            .ToArray();
        if (snapshots.Length == 0)
        {
            snapshots = [latest];
        }
        var usbHostIds = snapshots
            .Where(IsUsbVisible)
            .Select(identity => identity.HostId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var pickupHostIds = snapshots
            .Where(IsBluetoothPickupState)
            .Select(identity => identity.HostId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var waitingPickupHostIds = snapshots
            .Where(identity => identity.State.Equals("bluetooth-waiting", StringComparison.OrdinalIgnoreCase))
            .Select(identity => identity.HostId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var hasConnectedBluetooth = snapshots.Any(identity =>
            identity.State.Equals("bluetooth-connected", StringComparison.OrdinalIgnoreCase));
        var hasUnreachableBluetooth = snapshots.Any(identity =>
            identity.State.Equals("bluetooth-unreachable", StringComparison.OrdinalIgnoreCase));
        var pickupReadiness = ResolvePickupReadiness(
            usbHostIds,
            waitingPickupHostIds,
            hasConnectedBluetooth,
            hasUnreachableBluetooth);
        var sourcePaths = snapshots
            .Select(identity => identity.SourcePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new MimirMuninnMoveRosterEntry(
            MoveId: group.Key,
            ControllerIdHash: Fnva64($"move:{group.Key}"),
            LatestHostId: latest.HostId,
            LatestState: latest.State,
            StateSummary: string.Join("+", snapshots.Select(identity => identity.State)
                .Where(state => !string.IsNullOrWhiteSpace(state))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(StateRank)
                .ThenBy(state => state, StringComparer.Ordinal)),
            PickupReadiness: pickupReadiness,
            IsBluetoothPickupReady: pickupReadiness == "pickup-ready",
            BluetoothHostAddress: snapshots
                .Select(identity => identity.BluetoothHostAddress)
                .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address)) ?? string.Empty,
            HasUsbWitness: usbHostIds.Length > 0,
            UsbHostIds: usbHostIds,
            BluetoothPickupHostIds: pickupHostIds,
            UsbBlockingHostIds: usbHostIds.Length > 0 ? usbHostIds : [],
            SourcePaths: sourcePaths,
            ObservedAtNs: latest.ObservedAtNs);
    }

    private static string ResolvePickupReadiness(
        string[] usbHostIds,
        string[] waitingPickupHostIds,
        bool hasConnectedBluetooth,
        bool hasUnreachableBluetooth)
    {
        if (usbHostIds.Length > 0)
        {
            return "usb-owned";
        }

        if (hasConnectedBluetooth)
        {
            return "bluetooth-connected";
        }

        if (hasUnreachableBluetooth)
        {
            return "bluetooth-unreachable";
        }

        if (waitingPickupHostIds.Length > 0)
        {
            return "pickup-ready";
        }

        return "unknown";
    }

    private static bool IsUsbVisible(MimirMuninnMoveIdentitySnapshot identity) =>
        identity.State.Equals("usb-visible", StringComparison.OrdinalIgnoreCase);

    private static bool IsLiveIdentitySnapshot(MimirMuninnMoveIdentitySnapshot identity, ulong latestObservedAtNs) =>
        identity.ObservedAtNs <= 0 ||
        latestObservedAtNs <= 0 ||
        identity.ObservedAtNs > latestObservedAtNs ||
        latestObservedAtNs - identity.ObservedAtNs <= LiveIdentityWindowNs;

    private static bool IsBluetoothPickupState(MimirMuninnMoveIdentitySnapshot identity) =>
        identity.State.Equals("bluetooth-waiting", StringComparison.OrdinalIgnoreCase) ||
        identity.State.Equals("bluetooth-connected", StringComparison.OrdinalIgnoreCase);

    private static int StateRank(string state) =>
        state.ToLowerInvariant() switch
        {
            "usb-visible" => 0,
            "bluetooth-connected" => 1,
            "bluetooth-unreachable" => 2,
            "bluetooth-waiting" => 3,
            "bluetooth-known" => 4,
            _ => 9
        };

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
        const string unixPrefix = "unix-";
        if (observedAt.StartsWith(unixPrefix, StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(observedAt[unixPrefix.Length..], out var unixSeconds))
        {
            return checked(unixSeconds * 1_000_000_000UL);
        }

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
