using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameCult.Mesh;
using GameCult.Networking;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirDiscoveredCultMeshInputStream(
    string ProviderId,
    string StreamId,
    string Schema,
    string Address,
    string Channel,
    uint ConnectionId);

public sealed class MimirOdinMoveProofEvidenceRingProvider : IMimirMoveProofEvidenceRingProvider
{
    private const uint OdinSnapshotConnectionId = 0x0d1d0002;
    public static MimirOdinMoveProofEvidenceRingProvider Instance { get; } = new();

    private MimirOdinMoveProofEvidenceRingProvider() { }

    public bool TryOpenEvidenceRing(
        MimirMoveProofRuntimeConfiguration configuration,
        out MimirMoveProofEvidenceRingLease? lease,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lease = null;
        MimirMoveEvidenceRudpPump? pump = null;
        CultMeshSharedMemoryFrameRing? ring = null;
        try
        {
            var stream = Discover(configuration.OdinCultMeshUri, configuration.MuninnProviderId, configuration.EvidenceStreamId);
            ring = new CultMeshSharedMemoryFrameRing(configuration.EvidenceStreamId, slotCount: 4, slotByteLength: 256 * 1024);
            pump = new MimirMoveEvidenceRudpPump(
                stream,
                ring,
                () => Discover(configuration.OdinCultMeshUri, configuration.MuninnProviderId, configuration.EvidenceStreamId));
            pump.Start();
            lease = new MimirMoveProofEvidenceRingLease(ring, ownsRing: true, transportOwner: pump);
            diagnostic = $"Odin discovered {stream.ProviderId}/{stream.StreamId}; lowering {stream.Address}/{stream.Channel} into local CultMesh ring";
            return true;
        }
        catch (Exception error)
        {
            pump?.Dispose();
            ring?.Dispose();
            diagnostic = error.Message;
            return false;
        }
    }

    public static MimirDiscoveredCultMeshInputStream Discover(string odinCultMeshUri, string providerId, string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(odinCultMeshUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        var endpoint = CultMesh.ResolveRudpEndpoint(odinCultMeshUri);
        using var transport = CultMesh.CreateRudpClient("mimir-odin-discovery", OdinSnapshotConnectionId, endpoint);
        if (!transport.ConnectAndWait([], TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)))
        {
            throw new TimeoutException($"Timed out connecting to Odin at {odinCultMeshUri}.");
        }

        var messageId = $"mimir-muninn-discovery:{Guid.NewGuid():N}";
        transport.SendSchemaMessage(new CultNetSnapshotRequestMessage
        {
            MessageId = messageId,
            SchemaIds = ["gamecult.eve.surface_state.v1"],
            RecordKeys = ["surface:gamecult.network.status"]
        });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var message = transport.ReceiveSchemaMessage(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(10));
            if (message is CultNetErrorMessage error)
            {
                throw new InvalidOperationException(error.Error);
            }
            if (message is CultNetSnapshotResponseRawMessage response &&
                string.Equals(response.MessageId, messageId, StringComparison.Ordinal))
            {
                return SelectStream(response, providerId, streamId);
            }
        }
        throw new TimeoutException($"Timed out waiting for Odin provider snapshot from {odinCultMeshUri}.");
    }

    public static MimirDiscoveredCultMeshInputStream SelectStream(
        CultNetSnapshotResponseRawMessage response,
        string providerId,
        string streamId)
    {
        var document = response.Documents.FirstOrDefault(candidate =>
            string.Equals(candidate.SchemaId, "gamecult.eve.surface_state.v1", StringComparison.Ordinal) &&
            string.Equals(candidate.RecordKey, "surface:gamecult.network.status", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Odin snapshot omitted surface:gamecult.network.status.");
        using var json = JsonDocument.Parse(MessagePackSerializer.ConvertToJson(document.Payload));
        if (!json.RootElement.TryGetProperty("providerCatalog", out var providers))
        {
            throw new InvalidOperationException("Odin network status omitted providerCatalog.");
        }
        foreach (var provider in providers.EnumerateArray())
        {
            if (!string.Equals(Text(provider, "id"), providerId, StringComparison.Ordinal) ||
                !provider.TryGetProperty("inputStreams", out var streams))
            {
                continue;
            }
            foreach (var stream in streams.EnumerateArray())
            {
                if (!string.Equals(Text(stream, "streamId"), streamId, StringComparison.Ordinal) ||
                    !string.Equals(Text(stream, "schema"), MimirMuninnMoveEvidenceAdapter.StreamMetadataSchemaId, StringComparison.Ordinal) ||
                    !string.Equals(Text(stream, "transport"), "cultnet.transport.rudp.v0", StringComparison.Ordinal))
                {
                    continue;
                }
                return new MimirDiscoveredCultMeshInputStream(
                    providerId,
                    streamId,
                    Text(stream, "schema"),
                    Text(stream, "address"),
                    Text(stream, "channel"),
                    stream.GetProperty("connectionId").GetUInt32());
            }
            throw new InvalidOperationException($"Odin provider '{providerId}' has no compatible input stream '{streamId}'.");
        }
        throw new InvalidOperationException($"Odin has no provider '{providerId}'.");
    }

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.GetString() ?? "" : "";
}

internal sealed class MimirMoveEvidenceRudpPump : IDisposable
{
    private static readonly TimeSpan SourceStaleAfter = TimeSpan.FromSeconds(2);
    private readonly MimirDiscoveredCultMeshInputStream initialStream;
    private readonly CultMeshSharedMemoryFrameRing ring;
    private readonly Func<MimirDiscoveredCultMeshInputStream> rediscover;
    private readonly CancellationTokenSource stopping = new();
    private readonly ManualResetEventSlim started = new(false);
    private Thread? thread;
    private Exception? startupError;

    public MimirMoveEvidenceRudpPump(
        MimirDiscoveredCultMeshInputStream initialStream,
        CultMeshSharedMemoryFrameRing ring,
        Func<MimirDiscoveredCultMeshInputStream> rediscover)
    {
        this.initialStream = initialStream;
        this.ring = ring;
        this.rediscover = rediscover;
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "mimir-muninn-move-evidence" };
        thread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(6)))
        {
            throw new TimeoutException($"Timed out starting Move evidence receiver for {initialStream.Address}.");
        }
        if (startupError is not null)
        {
            throw new InvalidOperationException(
                $"Could not connect Odin-discovered Move evidence stream {initialStream.StreamId} at {initialStream.Address}: {startupError.Message}",
                startupError);
        }
    }

    private void Run()
    {
        var stream = initialStream;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                ReceiveSession(stream);
            }
            catch (Exception error) when (!stopping.IsCancellationRequested)
            {
                if (!started.IsSet)
                {
                    startupError = error;
                    started.Set();
                    return;
                }

                Console.Error.WriteLine(
                    $"Mimir Move evidence receiver reconnecting stream={stream.StreamId} address={stream.Address}: {error.Message}");
                if (stopping.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250)))
                {
                    return;
                }
                try
                {
                    stream = rediscover();
                }
                catch (Exception discoveryError)
                {
                    Console.Error.WriteLine(
                        $"Mimir Move evidence rediscovery failed provider={stream.ProviderId} stream={stream.StreamId}: {discoveryError.Message}");
                }
            }
        }
    }

    private void ReceiveSession(MimirDiscoveredCultMeshInputStream stream)
    {
        using var transport = CultMesh.CreateRudpClient(
            "mimir-move-evidence-consumer",
            stream.ConnectionId,
            CultMesh.ResolveRudpEndpoint($"rudp://{stream.Address}"));
        if (!transport.ConnectAndWait([], TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5)))
        {
            throw new TimeoutException($"Timed out connecting to {stream.Address}.");
        }
        transport.Send("hid.subscribe", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { streamId = stream.StreamId })));
        started.Set();
        var lastFrameAt = DateTime.UtcNow;
        while (!stopping.IsCancellationRequested)
        {
            transport.PollResends();
            var frame = transport.ReceiveOnce();
            if (frame is null)
            {
                if (DateTime.UtcNow - lastFrameAt >= SourceStaleAfter)
                {
                    throw new TimeoutException(
                        $"No Move evidence frame arrived for {SourceStaleAfter.TotalSeconds:0.#} seconds.");
                }
                Thread.Sleep(2);
                continue;
            }
            if (!string.Equals(frame.ChannelId, stream.Channel, StringComparison.Ordinal))
            {
                continue;
            }
            var decoded = MimirMuninnMoveEvidenceAdapter.DeserializeStreamFrame(frame.Payload);
            if (!string.Equals(decoded.FrameId, stream.StreamId, StringComparison.Ordinal) &&
                !decoded.FrameId.StartsWith($"{stream.StreamId}:", StringComparison.Ordinal))
            {
                continue;
            }
            lastFrameAt = DateTime.UtcNow;
            ring.TryPublishCopy(frame.Payload, decoded.PublishedAtNs, durationNs: 0, out _);
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        thread?.Join(TimeSpan.FromSeconds(2));
        started.Dispose();
        stopping.Dispose();
    }
}
