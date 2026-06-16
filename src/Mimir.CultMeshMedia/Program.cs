using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Mesh;
using GameCult.Networking;
using Mimir.Runtime.Synchronization;

const string SchemaName = "mimir.cultmesh_media_frame";
const string DefaultStreamId = "raven-primary-av";
const uint DefaultConnectionId = 0x4d4d_0101;
const int DefaultPort = 3075;
const int DefaultMaxFragmentBytes = 1200;
const int DefaultMaxPendingReliablePackets = 8192;

var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;
var options = CliOptions.Parse(args.Skip(1));

return command switch
{
    "relay" => await RunRelayAsync(options),
    "send" => await RunSenderAsync(options),
    "recv" or "receive" => await RunReceiverAsync(options),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("""
Usage:
  Mimir.CultMeshMedia relay --cache <path> [--bind 0.0.0.0:3075]
  Mimir.CultMeshMedia send --host <rudp-host> [--port 3075] [--stream raven-primary-av] [--chunk-bytes 16384] [--slots 96]
  Mimir.CultMeshMedia recv --host <rudp-host> [--port 3075] [--stream raven-primary-av] [--udp 127.0.0.1:5200]

send reads an MPEG-TS byte stream from stdin and publishes rolling CultMesh media-frame documents over CultNet RUDP.
recv subscribes to those RUDP document changes and writes ordered payload bytes to a local UDP MPEG-TS endpoint for OBS.
""");
    return 2;
}

static async Task<int> RunRelayAsync(CliOptions options)
{
    var cachePath = options.Get("cache") ?? Path.Combine(Environment.CurrentDirectory, "mimir-cultmesh-media.cc");
    var bind = ParseEndpoint(options.Get("bind") ?? $"0.0.0.0:{options.GetInt("port", DefaultPort)}");
    var registry = CreateDocumentRegistry();
    var schemaId = ResolveMediaSchemaId(registry);
    using var cache = await CultCacheMessagePack.OpenAsync(cachePath).ConfigureAwait(false);
    using var relay = new RudpMediaRelay(
        bind,
        cache,
        registry,
        schemaId,
        options.GetUInt("connection-id", DefaultConnectionId),
        options.GetInt("max-fragment-bytes", DefaultMaxFragmentBytes),
        options.GetInt("max-pending-reliable", DefaultMaxPendingReliablePackets));

    Console.WriteLine($"CultMesh media relay listening on rudp://{bind.Address}:{bind.Port}; cache={Path.GetFullPath(cachePath)}");
    await relay.RunAsync().ConfigureAwait(false);
    await cache.FlushAsync().ConfigureAwait(false);
    return 0;
}

static async Task<int> RunSenderAsync(CliOptions options)
{
    var endpoint = ResolveRudpEndpoint(options);
    var streamId = options.Get("stream") ?? DefaultStreamId;
    var chunkBytes = Math.Clamp(options.GetInt("chunk-bytes", 16 * 1024), 1316, 60 * 1024);
    var slots = Math.Clamp(options.GetInt("slots", 96), 8, 4096);
    var producer = options.Get("producer") ?? Environment.MachineName.ToLowerInvariant();
    var registry = CreateDocumentRegistry();

    using var transport = CreateRudpClient(
        options.Get("runtime") ?? $"{producer}-mimir-media-sender",
        endpoint,
        options);
    ConnectOrThrow(transport, endpoint.Uri);

    Console.Error.WriteLine($"Sending stdin MPEG-TS to {endpoint.Uri}/{streamId}; chunkBytes={chunkBytes}; slots={slots}");

    var input = Console.OpenStandardInput();
    var buffer = new byte[chunkBytes];
    long sequence = 0;
    var started = Stopwatch.StartNew();

    while (true)
    {
        var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
        if (read <= 0)
        {
            break;
        }

        var payload = buffer.AsSpan(0, read).ToArray();
        var document = new MimirCultMeshMediaFrameDocument(
            FrameId: $"{streamId}:{sequence:D20}",
            StreamId: streamId,
            Sequence: sequence,
            ProducedAtUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            TimestampNanoseconds: started.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency,
            PayloadKind: "mpegts-chunk",
            Container: "mpegts",
            VideoCodec: options.Get("video-codec") ?? "h264",
            AudioCodec: options.Get("audio-codec") ?? "aac",
            PayloadBytes: payload.Length,
            Payload: payload,
            ProducerNode: producer,
            ClockDomainId: options.Get("clock-domain") ?? "raven-monotonic",
            Tags: ["raven", "program", "obs", "cultmesh-media", "cultnet.transport.rudp.v0"]);

        var handle = new CultRecordHandle<MimirCultMeshMediaFrameDocument>(
            new CultRecordKey($"{streamId}:slot:{sequence % slots:D4}"));
        var message = registry.CreateRawDocumentPutMessage(
            Guid.NewGuid().ToString("N"),
            handle,
            document,
            new CultNetDocumentMessageOptions
            {
                SourceRuntimeId = producer,
                SourceRole = "mimir-cultmesh-media-sender",
                Tags = ["mimir", "media", streamId, "cultnet.transport.rudp.v0"]
            });

        transport.SendSchema(CultNetSchemaMessageSerialization.Serialize(message));
        PumpClient(transport, TimeSpan.FromMilliseconds(5));
        sequence++;
        if (sequence % 120 == 0)
        {
            Console.Error.WriteLine($"sent sequence={sequence - 1}");
        }
    }

    Console.Error.WriteLine($"sent chunks={sequence}; draining");
    PumpClient(transport, TimeSpan.FromMilliseconds(options.GetInt("drain-ms", 1000)));
    return 0;
}

static async Task<int> RunReceiverAsync(CliOptions options)
{
    var endpoint = ResolveRudpEndpoint(options);
    var streamId = options.Get("stream") ?? DefaultStreamId;
    var udp = options.Get("udp") ?? "127.0.0.1:5200";
    var udpEndpoint = ParseEndpoint(udp);
    var registry = CreateDocumentRegistry();
    var schemaId = ResolveMediaSchemaId(registry);
    var lastSequence = -1L;
    long frames = 0;
    long bytes = 0;
    var loggedFirstFrame = false;

    using var socket = new UdpClient();
    using var transport = CreateRudpClient(
        options.Get("runtime") ?? $"{Environment.MachineName.ToLowerInvariant()}-mimir-media-receiver",
        endpoint,
        options);
    ConnectOrThrow(transport, endpoint.Uri);
    transport.SendSchema(CultNetSchemaMessageSerialization.Serialize(new CultNetDatabaseSubscribeMessage
    {
        MessageId = Guid.NewGuid().ToString("N"),
        SubscriptionId = $"mimir-media-{streamId}",
        SchemaIds = [schemaId],
        IncludeSnapshot = false
    }));

    Console.WriteLine($"Receiving {endpoint.Uri}/{streamId}; writing MPEG-TS UDP to udp://{udp}");
    while (true)
    {
        transport.PollResends();
        if (transport.ReceiveOnce() is not { } frame)
        {
            await Task.Delay(2).ConfigureAwait(false);
            continue;
        }

        if (!string.Equals(frame.ChannelId, "schema", StringComparison.Ordinal))
        {
            continue;
        }

        if (CultNetSchemaMessageSerialization.Deserialize(frame.Payload) is not CultNetDatabaseChangeRawMessage change ||
            change.Document is not { } rawDocument ||
            !string.Equals(rawDocument.SchemaId, schemaId, StringComparison.Ordinal))
        {
            continue;
        }

        var document = (MimirCultMeshMediaFrameDocument)registry
            .GetBySchemaId(schemaId)!
            .PayloadDeserializer(rawDocument.Payload);

        if (!string.Equals(document.StreamId, streamId, StringComparison.Ordinal) ||
            document.Sequence <= lastSequence)
        {
            continue;
        }

        lastSequence = document.Sequence;
        socket.Send(document.Payload, document.Payload.Length, udpEndpoint);
        frames++;
        bytes += document.Payload.Length;

        if (!loggedFirstFrame)
        {
            loggedFirstFrame = true;
            Console.WriteLine($"received first sequence={document.Sequence} bytes={document.PayloadBytes}");
        }

        if (frames % 120 == 0)
        {
            Console.WriteLine($"received sequence={document.Sequence} frames={frames} bytes={bytes}");
        }
    }
}

static CultMeshRudpEndpoint ResolveRudpEndpoint(CliOptions options)
{
    if (options.Get("endpoint") is { } endpoint)
    {
        return CultMesh.ParseRudpEndpoint(endpoint);
    }

    var host = options.Get("host") ?? "10.77.0.1";
    var port = options.GetInt("port", DefaultPort);
    return CultMesh.ParseRudpEndpoint($"rudp://{host}:{port}");
}

static CultNetRudpSocketTransportConnection CreateRudpClient(
    string runtimeId,
    CultMeshRudpEndpoint endpoint,
    CliOptions options)
{
    return CultMesh.CreateRudpClient(
        runtimeId,
        options.GetUInt("connection-id", DefaultConnectionId),
        endpoint,
        new CultMeshRudpSocketOptions
        {
            BindHost = options.Get("bind-host") ?? "0.0.0.0",
            BindPort = options.GetInt("bind-port", 0),
            ResendDelayMs = options.GetInt("resend-ms", 100),
            MaxFragmentBytes = options.GetInt("max-fragment-bytes", DefaultMaxFragmentBytes),
            MaxPendingReliablePackets = options.GetInt("max-pending-reliable", DefaultMaxPendingReliablePackets)
        });
}

static void ConnectOrThrow(CultNetRudpSocketTransportConnection transport, string endpoint)
{
    if (!transport.ConnectAndWait(Array.Empty<byte>(), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(5)))
    {
        throw new TimeoutException($"timed out connecting RUDP media transport to {endpoint}");
    }
}

static void PumpClient(CultNetRudpSocketTransportConnection transport, TimeSpan duration)
{
    var until = Stopwatch.StartNew();
    while (until.Elapsed < duration)
    {
        transport.PollResends();
        transport.ReceiveOnce();
        Thread.Sleep(1);
    }
}

static string ResolveMediaSchemaId(CultNetDocumentRegistry registry)
{
    var binding = registry.GetByDocumentType(typeof(MimirCultMeshMediaFrameDocument)) ??
        throw new InvalidOperationException($"CultNet document binding missing for {SchemaName}.");
    return binding.SchemaId;
}

static CultNetDocumentRegistry CreateDocumentRegistry()
{
    var documents = CultDocumentRegistry.Shared;
    documents.GetRequired<MimirCultMeshMediaFrameDocument>();
    return new CultNetDocumentRegistry(documents)
        .Register(CultNetDocumentBinding.ForDocument<MimirCultMeshMediaFrameDocument>(documents));
}

static IPEndPoint ParseEndpoint(string value)
{
    var split = value.LastIndexOf(':');
    if (split <= 0 || split == value.Length - 1)
    {
        throw new ArgumentException($"Endpoint must be host:port, not '{value}'.");
    }

    var host = value[..split];
    var port = int.Parse(value[(split + 1)..], CultureInfo.InvariantCulture);
    var addresses = Dns.GetHostAddresses(host);
    return new IPEndPoint(addresses.First(address => address.AddressFamily == AddressFamily.InterNetwork), port);
}

sealed class RudpMediaRelay : IDisposable
{
    private readonly Socket socket;
    private readonly CultCache cache;
    private readonly CultNetDocumentRegistry registry;
    private readonly string schemaId;
    private readonly uint connectionId;
    private readonly int maxFragmentBytes;
    private readonly int maxPendingReliablePackets;
    private readonly Dictionary<string, RelayPeer> peers = new(StringComparer.Ordinal);

    public RudpMediaRelay(
        IPEndPoint bind,
        CultCache cache,
        CultNetDocumentRegistry registry,
        string schemaId,
        uint connectionId,
        int maxFragmentBytes,
        int maxPendingReliablePackets)
    {
        this.cache = cache;
        this.registry = registry;
        this.schemaId = schemaId;
        this.connectionId = connectionId;
        this.maxFragmentBytes = maxFragmentBytes;
        this.maxPendingReliablePackets = maxPendingReliablePackets;
        socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(bind);
        socket.Blocking = false;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            ReceiveAvailable();
            PumpReliableResends();
            await Task.Delay(2).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        socket.Dispose();
    }

    private void ReceiveAvailable()
    {
        while (socket.Poll(0, SelectMode.SelectRead))
        {
            var buffer = new byte[65535];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                received = socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
            {
                return;
            }

            var wire = buffer.AsSpan(0, received).ToArray();
            var packet = CultNetRudpPacketCodec.Decode(wire);
            if (packet.ConnectionId != connectionId)
            {
                continue;
            }

            var peer = GetPeer((IPEndPoint)remote);
            if (packet.PacketType == CultNetRudpPacketType.Connect)
            {
                SendPacket(peer, peer.Session.AcceptConnect(packet, NowMs()));
                continue;
            }

            var result = peer.Session.Receive(packet, NowMs());
            if (result.Reply != null)
            {
                SendPacket(peer, result.Reply);
            }

            if (result.Disconnected)
            {
                peers.Remove(peer.Key);
                continue;
            }

            foreach (var frame in result.Delivered.Where(static frame => string.Equals(frame.ChannelId, "schema", StringComparison.Ordinal)))
            {
                HandleSchemaFrameAsync(peer, frame.Payload).GetAwaiter().GetResult();
            }

            if (packet.PacketType == CultNetRudpPacketType.Accept || result.Delivered.Count > 0)
            {
                SendPacket(peer, peer.Session.CreateAck());
            }
        }
    }

    private async Task HandleSchemaFrameAsync(RelayPeer peer, byte[] payload)
    {
        var message = CultNetSchemaMessageSerialization.Deserialize(payload);
        switch (message)
        {
            case CultNetDocumentPutRawMessage put:
                await ApplyPutAsync(put).ConfigureAwait(false);
                break;
            case CultNetDatabaseSubscribeMessage subscribe:
                peer.Subscriptions[subscribe.SubscriptionId] = subscribe;
                Console.WriteLine($"relay RUDP subscribe peer={peer.Key} subscription={subscribe.SubscriptionId} schemas={string.Join(",", subscribe.SchemaIds ?? [])}");
                break;
            case CultNetDatabaseUnsubscribeMessage unsubscribe:
                peer.Subscriptions.Remove(unsubscribe.SubscriptionId);
                break;
        }
    }

    private async Task ApplyPutAsync(CultNetDocumentPutRawMessage put)
    {
        if (put.Document == null ||
            !string.Equals(put.Document.SchemaId, schemaId, StringComparison.Ordinal))
        {
            return;
        }

        await registry.ApplyRawDocumentPutMessageAsync(cache, put).ConfigureAwait(false);
        await cache.FlushAsync(soft: true).ConfigureAwait(false);
        var change = new CultNetDatabaseChangeRawMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ChangeKind = "updated",
            Document = put.Document
        };

        var frame = CultNetSchemaMessageSerialization.Serialize(change);
        foreach (var peer in peers.Values)
        {
            foreach (var subscription in peer.Subscriptions.Values)
            {
                if (!Matches(subscription, put.Document.SchemaId, put.Document.RecordKey))
                {
                    continue;
                }

                change.SubscriptionId = subscription.SubscriptionId;
                SendSchema(peer, CultNetSchemaMessageSerialization.Serialize(change));
                if (!peer.HasForwardedFrame)
                {
                    peer.HasForwardedFrame = true;
                    Console.WriteLine($"relay RUDP forward peer={peer.Key} subscription={subscription.SubscriptionId} schema={put.Document.SchemaId}");
                }
                ReceiveAvailable();
            }
        }

        var document = (MimirCultMeshMediaFrameDocument)registry
            .GetBySchemaId(schemaId)!
            .PayloadDeserializer(put.Document.Payload);
        if (document.Sequence % 120 == 0)
        {
            Console.WriteLine($"relay RUDP put stream={document.StreamId} sequence={document.Sequence} bytes={document.PayloadBytes}");
        }
    }

    private static bool Matches(CultNetDatabaseSubscribeMessage request, string schemaId, string recordKey)
    {
        var schemaMatches = request.SchemaIds == null ||
            request.SchemaIds.Length == 0 ||
            request.SchemaIds.Contains(schemaId, StringComparer.Ordinal);
        var keyMatches = request.RecordKeys == null ||
            request.RecordKeys.Length == 0 ||
            request.RecordKeys.Contains(recordKey, StringComparer.Ordinal);
        return schemaMatches && keyMatches;
    }

    private RelayPeer GetPeer(IPEndPoint endpoint)
    {
        var key = endpoint.ToString();
        if (peers.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var peer = new RelayPeer(
            key,
            endpoint,
            new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = connectionId,
                InitialSequence = (uint)(peers.Count + 1),
                ResendDelayMs = 100,
                MaxPendingReliablePackets = maxPendingReliablePackets
            }));
        peers[key] = peer;
        return peer;
    }

    private void SendSchema(RelayPeer peer, byte[] payload)
    {
        foreach (var packet in peer.Session.SendMany(
                     "schema",
                     payload,
                     new CultNetRudpSendOptions { Reliable = true, Ordered = true, NowMs = NowMs() },
                     maxFragmentBytes))
        {
            SendPacket(peer, packet);
        }
    }

    private void SendPacket(RelayPeer peer, CultNetRudpPacket packet)
    {
        var wire = CultNetRudpPacketCodec.Encode(packet);
        socket.SendTo(wire, peer.Endpoint);
    }

    private void PumpReliableResends()
    {
        foreach (var peer in peers.Values)
        {
            foreach (var packet in peer.Session.DueResends(NowMs()))
            {
                SendPacket(peer, packet);
            }
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

sealed record RelayPeer(
    string Key,
    IPEndPoint Endpoint,
    CultNetRudpSession Session)
{
    public Dictionary<string, CultNetDatabaseSubscribeMessage> Subscriptions { get; } = new(StringComparer.Ordinal);
    public bool HasForwardedFrame { get; set; }
}

sealed class CliOptions
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(IEnumerable<string> args)
    {
        var parsed = new CliOptions();
        var pendingKey = string.Empty;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                pendingKey = arg[2..];
                parsed.values[pendingKey] = "true";
                continue;
            }

            if (!string.IsNullOrWhiteSpace(pendingKey))
            {
                parsed.values[pendingKey] = arg;
                pendingKey = string.Empty;
            }
        }

        return parsed;
    }

    public string? Get(string key) => values.TryGetValue(key, out var value) ? value : null;

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public uint GetUInt(string key, uint fallback) =>
        uint.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
