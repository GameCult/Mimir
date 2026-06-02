using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using Mimir.Runtime.Synchronization;

var port = ParseInt(args, "--port", 8795);
var voidBotSwarmStatePath = ParseString(args, "--voidbot-swarm-state", @"E:\Projects\VoidBot\.voidbot\status\swarm-state.json");
var mimirTelemetryLogPath = ParseString(args, "--mimir-telemetry-log", "");
var mimirObservationLogPath = ParseString(args, "--mimir-observation-log", @"E:\Projects\Mimir\artifacts\runtime\periwinkle-cultmesh-sensors.out.log");
var providers = EveDashboardProviderCatalog.Create(ParseProviderSpecs(args), voidBotSwarmStatePath, mimirTelemetryLogPath, mimirObservationLogPath);
using var server = new EveDashboardServer(port, providers);
await server.RunAsync();

static int ParseInt(IReadOnlyList<string> args, string name, int fallback)
{
    for (var index = 0; index < args.Count - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[index + 1], out var value))
        {
            return value;
        }
    }

    return fallback;
}

static IReadOnlyList<string> ParseProviderSpecs(IReadOnlyList<string> args)
{
    var specs = new List<string>();
    for (var index = 0; index < args.Count - 1; index++)
    {
        if (string.Equals(args[index], "--provider", StringComparison.OrdinalIgnoreCase))
        {
            specs.Add(args[index + 1]);
        }
    }

    return specs;
}

static string ParseString(IReadOnlyList<string> args, string name, string fallback)
{
    for (var index = 0; index < args.Count - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return args[index + 1];
        }
    }

    return fallback;
}

internal sealed class EveDashboardServer(int port, EveDashboardProviderCatalog providers) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TcpListener listener = new(IPAddress.Any, port);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Guid, DashboardSocket> clients = new();
    private string activeProviderId = providers.DefaultProviderId;

    public async Task RunAsync()
    {
        listener.Start();
        Console.WriteLine($"Mimir Eve dashboard broker listening on ws://0.0.0.0:{port}/eve/deck");
        Console.WriteLine($"Compatibility endpoint remains ws://0.0.0.0:{port}/eve/dashboard");
        _ = Task.Run(BroadcastHeartbeatAsync);
        while (!stopping.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task BroadcastHeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stopping.IsCancellationRequested && await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
        {
            if (clients.IsEmpty)
            {
                continue;
            }

            await BroadcastStateAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(TcpClient tcpClient)
    {
        await using var stream = tcpClient.GetStream();
        var request = await ReadHttpRequestAsync(stream).ConfigureAwait(false);
        Console.WriteLine($"EVE dashboard connected: {tcpClient.Client.RemoteEndPoint} {request.Path}");
        Console.Out.Flush();

        if (request.Path == "/health")
        {
            var provider = providers.Get(activeProviderId);
            var health = JsonSerializer.Serialize(new
            {
                ok = true,
                clients = clients.Count,
                activeProviderId,
                providerVersion = provider.State.Version,
                providers = providers.Manifests.Count,
                transport = "eve-deck-ws",
                cultMeshDocument = "mimir.eve_dashboard_state",
            }, JsonOptions);
            await WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(health)).ConfigureAwait(false);
            return;
        }

        if (request.Path is "/eve/deck/manifest" or "/eve/dashboard/manifest")
        {
            var manifest = JsonSerializer.Serialize(providers.BrokerManifest, JsonOptions);
            await WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(manifest)).ConfigureAwait(false);
            return;
        }

        if (request.Path == "/eve/deck/providers")
        {
            var catalog = JsonSerializer.Serialize(new DashboardProviderCatalogDocument(providers.Manifests), JsonOptions);
            await WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(catalog)).ConfigureAwait(false);
            return;
        }

        if (!IsDashboardPath(request.Path, out var requestedProviderId, out var cultMeshBinary) ||
            !request.Headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(requestedProviderId) && providers.Contains(requestedProviderId))
        {
            activeProviderId = requestedProviderId;
        }

        await WriteWebSocketHandshakeAsync(stream, key).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var socket = new DashboardSocket(tcpClient, stream, cultMeshBinary);
        clients[id] = socket;
        await SendStateAsync(socket).ConfigureAwait(false);
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var frame = await ReceiveFrameAsync(stream).ConfigureAwait(false);
                if (frame.Opcode == 0x8)
                {
                    await WriteCloseFrameAsync(stream).ConfigureAwait(false);
                    break;
                }

                if (frame.Opcode == 0x2 && cultMeshBinary)
                {
                    if (ApplyCultMeshCommand(frame.Payload))
                    {
                        await BroadcastStateAsync().ConfigureAwait(false);
                    }

                    continue;
                }

                if (frame.Opcode != 0x1)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(frame.Payload);
                if (ApplyCommand(text))
                {
                    await BroadcastStateAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            clients.TryRemove(id, out _);
        }
    }

    private static bool IsDashboardPath(string path, out string providerId, out bool cultMeshBinary)
    {
        providerId = "";
        cultMeshBinary = false;
        if (path == "/eve/deck/cultmesh")
        {
            cultMeshBinary = true;
            return true;
        }

        const string cultMeshProviderPrefix = "/eve/deck/cultmesh/";
        if (path.StartsWith(cultMeshProviderPrefix, StringComparison.Ordinal))
        {
            providerId = Uri.UnescapeDataString(path[cultMeshProviderPrefix.Length..]);
            cultMeshBinary = true;
            return !string.IsNullOrWhiteSpace(providerId);
        }

        if (path == "/eve/dashboard" || path == "/eve/deck")
        {
            return true;
        }

        const string providerPrefix = "/eve/deck/";
        if (path.StartsWith(providerPrefix, StringComparison.Ordinal))
        {
            providerId = Uri.UnescapeDataString(path[providerPrefix.Length..]);
            return !string.IsNullOrWhiteSpace(providerId);
        }

        return false;
    }

    private bool ApplyCultMeshCommand(byte[] payload)
    {
        MimirEveDashboardCommandDocument? commandDocument;
        try
        {
            commandDocument = MessagePackSerializer.Deserialize<MimirEveDashboardCommandDocument>(payload);
        }
        catch
        {
            return false;
        }

        var command = new DashboardCommand
        {
            Type = commandDocument.Type,
            NodeId = commandDocument.NodeId,
            ProviderId = commandDocument.ProviderId,
            X = commandDocument.X,
            Y = commandDocument.Y,
            Rotation = commandDocument.Rotation,
            Scale = commandDocument.Scale,
            Visible = commandDocument.Visible,
        };

        return ApplyCommand(JsonSerializer.Serialize(command, JsonOptions));
    }

    private bool ApplyCommand(string text)
    {
        DashboardCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<DashboardCommand>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (command == null)
        {
            return false;
        }

        if (string.Equals(command.Type, "open-provider", StringComparison.OrdinalIgnoreCase))
        {
            var providerId = command.ProviderId;
            if (string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(command.NodeId))
            {
                providerId = providers.Get(activeProviderId).State.Nodes
                    .FirstOrDefault(node => string.Equals(node.Id, command.NodeId, StringComparison.Ordinal))
                    ?.ProviderId;
            }

            if (!string.IsNullOrWhiteSpace(providerId) && providers.Contains(providerId))
            {
                activeProviderId = providerId;
                Console.WriteLine($"EVE dashboard provider switched: {activeProviderId}");
                Console.Out.Flush();
                return true;
            }

            return false;
        }

        var provider = providers.Get(activeProviderId);
        if (provider.ApplyCommand(command))
        {
            Console.WriteLine($"EVE dashboard command({activeProviderId}): {text}");
            Console.Out.Flush();
            return true;
        }

        return false;
    }

    private async Task SendStateAsync(DashboardSocket socket)
    {
        var state = providers.Get(activeProviderId).State;
        if (socket.CultMeshBinary)
        {
            var document = new MimirEveDashboardStateDocument(
                state.ProviderId,
                state.Title,
                state.Version,
                state.UpdatedAt.ToString("O"),
                state.SelectedNodeId,
                state.LutPreset,
                state.Nodes.Select(static node => new MimirEveDashboardNodeSnapshot(
                    node.Id,
                    node.Label,
                    node.Kind,
                    node.Visible,
                    node.X,
                    node.Y,
                    node.Z,
                    node.Rotation,
                    node.Scale,
                    node.Width,
                    node.Height,
                    node.Health,
                    node.ProviderId,
                    node.Command,
                    node.Endpoint)).ToArray(),
                ToCultMeshSurface(state.Surface));
            await SendBinaryFrameAsync(socket.Stream, MessagePackSerializer.Serialize(document)).ConfigureAwait(false);
            return;
        }

        await SendTextFrameAsync(socket.Stream, JsonSerializer.Serialize(state, JsonOptions)).ConfigureAwait(false);
    }

    private static MimirEveDashboardSurfaceSnapshot? ToCultMeshSurface(DashboardSurface? surface)
    {
        if (surface == null)
        {
            return null;
        }

        return new MimirEveDashboardSurfaceSnapshot(
            surface.Schema,
            surface.Id,
            surface.Title,
            ToCultMeshElement(surface.Root),
            surface.Assets
                .Select(static asset => new MimirEveDashboardSurfaceAssetSnapshot(asset.Id, asset.Kind, asset.Uri))
                .ToArray());
    }

    private static MimirEveDashboardUiElementSnapshot ToCultMeshElement(DashboardUiElement element)
    {
        return new MimirEveDashboardUiElementSnapshot(
            element.Id,
            element.Kind,
            element.Role,
            element.Text,
            element.AssetRef,
            element.AssetUri,
            element.BindNodeId,
            element.CommandId,
                    element.Layout == null
                        ? null
                        : new MimirEveDashboardUiLayoutSnapshot(
                            element.Layout.Direction,
                            element.Layout.Width,
                            element.Layout.Height,
                            element.Layout.Grow,
                            element.Layout.Gap,
                            element.Layout.Padding,
                            element.Layout.Overflow,
                            element.Layout.MinWidth,
                            element.Layout.MinHeight,
                            element.Layout.PreferredWidth,
                            element.Layout.PreferredHeight,
                            element.Layout.Priority,
                            element.Layout.Density,
                            element.Layout.ViewportMode),
            element.Style == null
                ? null
                : new MimirEveDashboardUiStyleSnapshot(element.Style.Variant, element.Style.Tone),
            element.Metric == null
                ? null
                : new MimirEveDashboardUiMetricSnapshot(element.Metric.Label, element.Metric.Value, element.Metric.Tone),
            element.Children.Select(ToCultMeshElement).ToArray(),
            element.Binding == null
                ? null
                : new MimirEveDashboardUiBindingSnapshot(
                    element.Binding.DocumentSchema,
                    element.Binding.DocumentId,
                    element.Binding.Path,
                    element.Binding.ValueKind,
                    element.Binding.Access,
                    element.Binding.Authority,
                    element.Binding.CommandId));
    }

    private async Task BroadcastStateAsync()
    {
        foreach (var (id, socket) in clients)
        {
            try
            {
                await SendStateAsync(socket).ConfigureAwait(false);
            }
            catch
            {
                clients.TryRemove(id, out _);
            }
        }
    }

    private static async Task<HttpRequest> ReadHttpRequestAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var lastFour = new Queue<byte>(4);
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                throw new IOException("Client closed before HTTP headers completed.");
            }

            var b = (byte)value;
            bytes.Add(b);
            lastFour.Enqueue(b);
            if (lastFour.Count > 4)
            {
                lastFour.Dequeue();
            }

            if (lastFour.Count == 4 && lastFour.SequenceEqual(new byte[] { 13, 10, 13, 10 }))
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(bytes.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return new HttpRequest(first.Length > 1 ? first[1] : "/", headers);
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, string status, string contentType, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    private static async Task WriteWebSocketHandshakeAsync(NetworkStream stream, string key)
    {
        var acceptBytes = SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
        var accept = Convert.ToBase64String(acceptBytes);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response).ConfigureAwait(false);
    }

    private static async Task SendTextFrameAsync(NetworkStream stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var header = new List<byte> { 0x81 };
        if (payload.Length < 126)
        {
            header.Add((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header.Add(126);
            header.Add((byte)(payload.Length >> 8));
            header.Add((byte)payload.Length);
        }
        else
        {
            header.Add(127);
            var length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((long)payload.Length));
            header.AddRange(length);
        }

        await stream.WriteAsync(header.ToArray()).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
    }

    private static async Task SendBinaryFrameAsync(NetworkStream stream, byte[] payload)
    {
        var header = BuildFrameHeader(0x82, payload.Length);
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
    }

    private static byte[] BuildFrameHeader(byte opcode, int payloadLength)
    {
        var header = new List<byte> { opcode };
        if (payloadLength < 126)
        {
            header.Add((byte)payloadLength);
        }
        else if (payloadLength <= ushort.MaxValue)
        {
            header.Add(126);
            header.Add((byte)(payloadLength >> 8));
            header.Add((byte)payloadLength);
        }
        else
        {
            header.Add(127);
            var length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((long)payloadLength));
            header.AddRange(length);
        }

        return header.ToArray();
    }

    private static async Task<WebSocketFrame> ReceiveFrameAsync(NetworkStream stream)
    {
        var header = new byte[2];
        await ReadExactAsync(stream, header).ConfigureAwait(false);
        var opcode = header[0] & 0x0f;
        var masked = (header[1] & 0x80) != 0;
        var length = header[1] & 0x7f;
        if (length == 126)
        {
            var extended = new byte[2];
            await ReadExactAsync(stream, extended).ConfigureAwait(false);
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            await ReadExactAsync(stream, extended).ConfigureAwait(false);
            var longLength = 0L;
            foreach (var b in extended)
            {
                longLength = (longLength << 8) | b;
            }

            if (longLength > int.MaxValue)
            {
                throw new InvalidDataException("WebSocket frame is too large.");
            }

            length = (int)longLength;
        }

        var mask = new byte[4];
        if (masked)
        {
            await ReadExactAsync(stream, mask).ConfigureAwait(false);
        }

        var payload = new byte[length];
        await ReadExactAsync(stream, payload).ConfigureAwait(false);
        if (masked)
        {
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] ^= mask[index % 4];
            }
        }

        return new WebSocketFrame(opcode, payload);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset)).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("WebSocket closed.");
            }

            offset += read;
        }
    }

    private static Task WriteCloseFrameAsync(NetworkStream stream) =>
        stream.WriteAsync(new byte[] { 0x88, 0x00 }).AsTask();

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
        stopping.Dispose();
    }
}

internal sealed class EveDashboardProviderCatalog
{
    private readonly Dictionary<string, IDashboardProvider> providers;

    private EveDashboardProviderCatalog(IEnumerable<IDashboardProvider> providers)
    {
        this.providers = providers.ToDictionary(provider => provider.Manifest.Id, StringComparer.Ordinal);
        DefaultProviderId = DashboardBrokerProvider.ProviderId;
        Manifests = this.providers.Values.Select(provider => provider.Manifest).ToArray();
        BrokerManifest = new DashboardProviderManifest(
            "eve.dashboard.broker",
            "Eve Dashboard Broker",
            "Native retained dashboard router for LAN and SSH-tunneled control surfaces.",
            "1",
            "/eve/deck",
            ["scene2d", "provider-switching", "touch-commands", "cultmesh-state-documents"],
            UsesCultMesh: true,
            Transport: "WebSocket now; CultMesh typed state when available.");
    }

    public string DefaultProviderId { get; }

    public IReadOnlyList<DashboardProviderManifest> Manifests { get; }

    public DashboardProviderManifest BrokerManifest { get; }

    public static EveDashboardProviderCatalog Create(
        IReadOnlyList<string> remoteSpecs,
        string voidBotSwarmStatePath,
        string mimirTelemetryLogPath,
        string mimirObservationLogPath)
    {
        var providers = new List<IDashboardProvider>();
        var remoteProviders = remoteSpecs.Select(RemoteDashboardProvider.FromSpec).OfType<RemoteDashboardProvider>().ToArray();
        providers.Add(new DashboardBrokerProvider(remoteProviders));
        providers.Add(new MimirLiveStatsProvider(mimirTelemetryLogPath, mimirObservationLogPath));
        providers.Add(new MimirStreamLayoutProvider());
        providers.Add(new VoidBotSwarmProvider(voidBotSwarmStatePath));
        providers.Add(new YggdrasilStreamPixelsProvider());
        providers.AddRange(remoteProviders);
        return new EveDashboardProviderCatalog(providers);
    }

    public bool Contains(string providerId) => providers.ContainsKey(providerId);

    public IDashboardProvider Get(string providerId) =>
        providers.TryGetValue(providerId, out var provider) ? provider : providers[DefaultProviderId];
}

internal interface IDashboardProvider
{
    DashboardProviderManifest Manifest { get; }

    DashboardState State { get; }

    bool ApplyCommand(DashboardCommand command);
}

internal abstract class MutableDashboardProvider : IDashboardProvider
{
    protected MutableDashboardProvider(DashboardProviderManifest manifest, DashboardState state)
    {
        Manifest = manifest;
        State = state;
    }

    public DashboardProviderManifest Manifest { get; }

    public DashboardState State { get; }

    public virtual bool ApplyCommand(DashboardCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.NodeId))
        {
            return false;
        }

        var node = State.Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, command.NodeId, StringComparison.Ordinal));
        if (node == null)
        {
            return false;
        }

        switch ((command.Type ?? "").Trim().ToLowerInvariant())
        {
            case "select":
                State.SelectedNodeId = node.Id;
                break;
            case "move":
                State.SelectedNodeId = node.Id;
                node.X = Clamp(command.X ?? node.X, -1.0, 1.0);
                node.Y = Clamp(command.Y ?? node.Y, -1.0, 1.0);
                break;
            case "scale":
                State.SelectedNodeId = node.Id;
                node.Scale = Clamp(command.Scale ?? node.Scale, 0.25, 3.0);
                break;
            case "rotate":
                State.SelectedNodeId = node.Id;
                node.Rotation = command.Rotation ?? node.Rotation;
                break;
            case "toggle-visibility":
                State.SelectedNodeId = node.Id;
                node.Visible = command.Visible ?? !node.Visible;
                break;
            case "reset-transform":
                State.SelectedNodeId = node.Id;
                node.X = node.DefaultX;
                node.Y = node.DefaultY;
                node.Rotation = 0.0;
                node.Scale = 1.0;
                break;
            default:
                return false;
        }

        Touch();
        return true;
    }

    protected void Touch()
    {
        State.Version++;
        State.UpdatedAt = DateTimeOffset.UtcNow;
    }

    protected static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));
}

internal sealed class DashboardBrokerProvider : MutableDashboardProvider
{
    public const string ProviderId = "eve.dashboard.broker";

    public DashboardBrokerProvider(IReadOnlyList<RemoteDashboardProvider> remoteProviders)
        : base(
            new DashboardProviderManifest(
                ProviderId,
                "Dashboard Switchboard",
                "Provider picker rendered as native Eve panels.",
                "1",
                "/eve/deck",
                ["scene2d", "provider-switching"],
                UsesCultMesh: true,
                Transport: "broker-local"),
            CreateState(remoteProviders))
    {
    }

    private static DashboardState CreateState(IReadOnlyList<RemoteDashboardProvider> remoteProviders)
    {
        var nodes = new List<DashboardNode>
        {
            new("provider-mimir-live", "Mimir Live Stats", "dashboard-provider", -0.56, -0.34, 0.30, 0.22, "telemetry")
            {
                ProviderId = MimirLiveStatsProvider.ProviderId,
                Command = "open-provider",
            },
            new("provider-mimir", "Mimir Stream Layout", "dashboard-provider", -0.18, -0.34, 0.30, 0.22, "local")
            {
                ProviderId = MimirStreamLayoutProvider.ProviderId,
                Command = "open-provider",
            },
            new("provider-voidbot", "VoidBot Swarm", "dashboard-provider", 0.0, -0.34, 0.30, 0.22, "ctb")
            {
                ProviderId = VoidBotSwarmProvider.ProviderId,
                Command = "open-provider",
            },
            new("provider-streampixels", "StreamPixels Edge", "dashboard-provider", 0.56, -0.34, 0.30, 0.22, "ssh ready")
            {
                ProviderId = YggdrasilStreamPixelsProvider.ProviderId,
                Command = "open-provider",
            },
        };

        var y = 0.28;
        foreach (var provider in remoteProviders)
        {
            nodes.Add(new DashboardNode(
                $"provider-{provider.Manifest.Id}",
                provider.Manifest.Title,
                "dashboard-provider",
                0.0,
                y,
                0.40,
                0.18,
                "external")
            {
                ProviderId = provider.Manifest.Id,
                Command = "open-provider",
            });
            y = Math.Min(0.80, y + 0.22);
        }

        return new DashboardState
        {
            ProviderId = ProviderId,
            Title = "Eve Dashboard Switchboard",
            SelectedNodeId = nodes[0].Id,
            Nodes = nodes,
        };
    }
}

internal sealed class MimirLiveStatsProvider : IDashboardProvider
{
    public const string ProviderId = "mimir.live.stats";
    private const int TailBytes = 512 * 1024;
    private readonly string telemetryLogPath;
    private readonly string observationLogPath;
    private long version;

    public MimirLiveStatsProvider(string telemetryLogPath, string observationLogPath)
    {
        this.telemetryLogPath = telemetryLogPath;
        this.observationLogPath = observationLogPath;
        Manifest = new DashboardProviderManifest(
            ProviderId,
            "Mimir Live Stats",
            "Compact Eve/CultUI surface for live Mimir telemetry: RMS bars, sync confidence, source buffers, actuator state, and device observation streams.",
            "1",
            "/eve/deck/mimir.live.stats",
            ["live-telemetry", "compact-tui", "audio-rms", "sync-confidence", "observation-streams"],
            UsesCultMesh: true,
            Transport: "Mimir telemetry and observation ledgers projected as Eve/CultUI state.");
    }

    public DashboardProviderManifest Manifest { get; }

    public DashboardState State => BuildState();

    public bool ApplyCommand(DashboardCommand command) => false;

    private DashboardState BuildState()
    {
        var snapshot = MimirLiveStatsSnapshot.Load(telemetryLogPath, observationLogPath);
        var confidence = snapshot.SyncStates.Count == 0
            ? snapshot.SyncReports.Select(static report => report.Confidence)
                .Concat(snapshot.SyncDecodeAttempts.Select(static attempt => attempt.Confidence))
                .Concat(snapshot.Well?.AudioSyncStates.Select(static state => state.Confidence) ?? [])
                .DefaultIfEmpty(0.0)
                .Max()
            : snapshot.SyncStates.Average(static state => state.Confidence);
        var rms = snapshot.Spectra.Count == 0 ? 0.0 : snapshot.Spectra.Max(static spectrum => spectrum.Rms);
        var liveObservationCount = snapshot.ObservationStreams.Count(static stream => stream.State == "active");
        var status = snapshot.HasAnyData ? "live" : "waiting";
        var nodes = new List<DashboardNode>
        {
            new("mimir-live-root", $"Mimir Live Stats\n{snapshot.Summary}", "mimir-live-stats", 0.0, -0.50, 0.70, 0.20, status)
            {
                Detail = $"telemetry={snapshot.TelemetrySource} observations={snapshot.ObservationSource}",
            },
            new("mimir-tracking-confidence", $"Tracking Confidence\n{confidence:0.000}", "confidence", -0.44, -0.12, 0.28, 0.16, ToneFor(confidence))
            {
                Detail = $"{snapshot.SyncStates.Count} sync states / {snapshot.SyncReports.Count} reports / {snapshot.SyncDecodeAttempts.Count} decode attempts",
            },
            new("mimir-audio-rms", $"Audio RMS\n{rms:0.000000}", "audio-rms", 0.0, -0.12, 0.28, 0.16, ToneFor(NormalizeRms(rms)))
            {
                Detail = $"{snapshot.Spectra.Count} spectrum lanes; Well audio buffers {snapshot.Well?.AudioBuffers ?? 0}",
            },
            new("mimir-observation-streams", $"Device Streams\n{liveObservationCount}/{snapshot.ObservationStreams.Count} active", "observation-streams", 0.44, -0.12, 0.28, 0.16, liveObservationCount > 0 ? "active" : "waiting")
            {
                Detail = string.Join(", ", snapshot.ObservationStreams.Select(static stream => $"{stream.StreamId}:{stream.Kind}:{stream.State}")),
            },
        };

        return new DashboardState
        {
            ProviderId = ProviderId,
            Title = "Mimir Live Stats",
            Version = Interlocked.Increment(ref version),
            UpdatedAt = DateTimeOffset.UtcNow,
            SelectedNodeId = "mimir-live-root",
            LutPreset = "terminal",
            Nodes = nodes,
            Surface = BuildSurface(snapshot, confidence, rms, liveObservationCount),
        };
    }

    private static DashboardSurface BuildSurface(MimirLiveStatsSnapshot snapshot, double confidence, double rms, int liveObservationCount)
    {
        var pulse = Pulse();
        var children = new List<DashboardUiElement>
        {
            UiElement.Container(
                "mimir-live-row-overview",
                "row",
                new DashboardUiLayout { Direction = "horizontal", Gap = 8 },
                [
                    UiElement.Pane("mimir-live-overview", "Mimir Live Stats", [
                UiElement.Text("mimir-live-summary", $"{pulse} {snapshot.Summary}", "strong"),
                UiElement.Text("mimir-live-sources", $"telemetry: {snapshot.TelemetrySource}\nobservations: {snapshot.ObservationSource}", "caption"),
                StatBar("tracking-confidence", "tracking confidence", confidence, "cool"),
                StatBar("audio-rms", "max channel RMS", NormalizeRms(rms), "warm", $"{rms:0.000000}"),
                StatBar("observation-liveness", "device stream liveness", snapshot.ObservationStreams.Count == 0 ? 0.0 : liveObservationCount / (double)snapshot.ObservationStreams.Count, "cool"),
                    ]),
                    BuildWellPane(snapshot.Well),
                    BuildMoveMusicPane(snapshot.MoveMusic),
                ]),
            UiElement.Container(
                "mimir-live-row-runtime",
                "row",
                new DashboardUiLayout { Direction = "horizontal", Gap = 8 },
                [
                    BuildAudioPane(snapshot.Spectra, snapshot.Well),
                    BuildSyncPane(snapshot),
                    BuildVideoPane(snapshot),
                ]),
            UiElement.Container(
                "mimir-live-row-witness",
                "row",
                new DashboardUiLayout { Direction = "horizontal", Gap = 8 },
                [
                    BuildObservationPane(snapshot.ObservationStreams),
                    BuildActuatorPane(snapshot.ActuatorCommands),
                    BuildCalibrationPane(snapshot),
                ]),
        };

        return new DashboardSurface
        {
            Schema = "cultmesh.eve_surface.v0",
            Id = "mimir.live.stats.surface",
            Title = "Mimir Live Stats",
            Root = UiElement.Container(
                "mimir-live-stats-root",
                "dashboard",
                new DashboardUiLayout
                {
                    Direction = "vertical",
                    Gap = 6,
                    Padding = 6,
                    Overflow = "scroll",
                    Grow = 4,
                    MinWidth = 96,
                    MinHeight = 56,
                    PreferredWidth = 156,
                    PreferredHeight = 128,
                    Priority = -80,
                    Density = "continuous",
                    ViewportMode = "continuous-metrics",
                },
                children),
        };
    }

    private static DashboardUiElement BuildAudioPane(IReadOnlyList<MimirSpectrumStat> spectra, MimirWellStat? well)
    {
        var wellAudio = well?.Buffers
            .Where(static buffer => string.Equals(buffer.Kind, "Audio", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static buffer => buffer.SourceId, StringComparer.Ordinal)
            .Take(8)
            .Select(buffer => UiElement.Text(
                $"well-audio-{StableId(buffer.SourceId)}",
                $"{buffer.SourceId}: {buffer.Count} blocks edge {NsAge(buffer.EdgeNs)}",
                "caption"))
            .ToArray() ?? [];
        var items = spectra.Count == 0 && wellAudio.Length == 0
            ? [UiElement.Text("mimir-spectrum-empty", "no spectrum telemetry yet", "caption")]
            : spectra
                .OrderBy(static spectrum => spectrum.SourceId, StringComparer.Ordinal)
                .Take(8)
                .Select(spectrum => UiElement.Card(
                    $"spectrum-{StableId(spectrum.SourceId)}",
                    "audio-channel",
                    style: new DashboardUiStyle { Variant = "compact", Tone = ToneFor(NormalizeRms(spectrum.Rms)) },
                    children:
                    [
                        UiElement.Text($"spectrum-{StableId(spectrum.SourceId)}-label", $"{spectrum.SourceId} {Bar(NormalizeRms(spectrum.Rms), 18)}", "mono"),
                        UiElement.Text($"spectrum-{StableId(spectrum.SourceId)}-detail", $"rms {spectrum.Rms:0.000000} peak {spectrum.Peak:0.000000} floor {spectrum.NoiseFloorDb:0.0} dB", "caption"),
                        UiElement.Text($"spectrum-{StableId(spectrum.SourceId)}-peaks", spectrum.Peaks, "caption"),
                    ]))
                .Concat(wellAudio)
                .ToArray();

        return UiElement.Pane("mimir-audio-pane", "Audio", items);
    }

    private static DashboardUiElement BuildSyncPane(MimirLiveStatsSnapshot snapshot)
    {
        var children = new List<DashboardUiElement>();
        foreach (var state in snapshot.SyncStates.OrderBy(static state => state.SourceId, StringComparer.Ordinal).Take(8))
        {
            children.Add(UiElement.Card(
                $"sync-state-{StableId(state.SourceId)}",
                "sync-state",
                style: new DashboardUiStyle { Variant = "compact", Tone = ToneFor(state.Confidence) },
                children:
                [
                    UiElement.Text($"sync-state-{StableId(state.SourceId)}-head", $"{state.ReferenceSourceId}->{state.SourceId} {Bar(state.Confidence, 16)}", "mono"),
                    UiElement.Text($"sync-state-{StableId(state.SourceId)}-detail", $"delay {state.DelayUs:0.000} us / {state.DelayMs:0.000} ms  sro {state.SroPpm:0.000} ppm", "caption"),
                ]));
        }

        foreach (var report in snapshot.SyncReports.OrderBy(static report => report.SourceId, StringComparer.Ordinal).Take(6))
        {
            children.Add(UiElement.Text(
                $"sync-report-{StableId(report.SourceId)}",
                $"{report.Evidence} {report.ReferenceSourceId}->{report.SourceId} {report.DelayUs:0.000} us c={report.Confidence:0.000} events={report.Events}",
                "caption"));
        }

        foreach (var attempt in snapshot.SyncDecodeAttempts.OrderByDescending(static attempt => attempt.Confidence).ThenBy(static attempt => attempt.SourceId, StringComparer.Ordinal).Take(8))
        {
            children.Add(UiElement.Text(
                $"sync-decode-{StableId(attempt.ReferenceSourceId)}-{StableId(attempt.SourceId)}",
                $"decode {attempt.Status} {attempt.ReferenceSourceId}->{attempt.SourceId} c={attempt.Confidence:0.000} ref={attempt.ReferenceEnergy:0.000} cand={attempt.CandidateEnergy:0.000} matched={attempt.Matched}",
                "caption"));
        }

        if (children.Count == 0)
        {
            children.Add(UiElement.Text("mimir-sync-empty", "no sync state yet", "caption"));
        }

        return UiElement.Pane("mimir-sync-pane", "Sync Confidence", children);
    }

    private static DashboardUiElement BuildVideoPane(MimirLiveStatsSnapshot snapshot)
    {
        var children = new List<DashboardUiElement>();
        if (snapshot.Telemetry != null)
        {
            children.Add(UiElement.Text(
                "mimir-telemetry-line",
                $"sources {snapshot.Telemetry.Sources} poll {snapshot.Telemetry.LastPoll} ingested {snapshot.Telemetry.Ingested} analyze {snapshot.Telemetry.AnalyzeMs:0.0} ms mode {snapshot.Telemetry.AudioSync}",
                "mono"));
        }

        children.AddRange(snapshot.VideoBuffers
            .OrderBy(static video => video.SourceId, StringComparer.Ordinal)
            .Take(8)
            .Select(video => UiElement.Text(
                $"video-{StableId(video.SourceId)}",
                $"{video.SourceId}: {video.Count} frames {video.Latest}",
                "caption")));

        if (children.Count == 0)
        {
            children.Add(UiElement.Text("mimir-video-empty", "no video-buffer telemetry yet", "caption"));
        }

        return UiElement.Pane("mimir-video-pane", "Runtime Buffers", children);
    }

    private static DashboardUiElement BuildObservationPane(IReadOnlyList<MimirObservationStreamStat> streams)
    {
        var children = streams.Count == 0
            ? [UiElement.Text("mimir-observations-empty", "no device observation streams", "caption")]
            : streams
                .OrderBy(static stream => stream.DeviceId, StringComparer.Ordinal)
                .ThenBy(static stream => stream.StreamId, StringComparer.Ordinal)
                .Select(stream => UiElement.Card(
                    $"observation-{StableId(stream.StreamId)}-{StableId(stream.Kind)}",
                    "observation-stream",
                    style: new DashboardUiStyle { Variant = "compact", Tone = stream.State == "active" ? "cool" : "warm" },
                    children:
                    [
                        UiElement.Text($"observation-{StableId(stream.StreamId)}-head", $"{stream.StreamId} {stream.Kind} {stream.State}", "mono"),
                        UiElement.Text($"observation-{StableId(stream.StreamId)}-detail", $"{stream.Shape} seq {stream.Sequence} age {stream.AgeSeconds:0}s", "caption"),
                    ]))
                .ToArray();

        return UiElement.Pane("mimir-observation-pane", "Periwinkle / Eve Streams", children);
    }

    private static DashboardUiElement BuildActuatorPane(IReadOnlyList<MimirActuatorCommandStat> commands)
    {
        var children = commands.Count == 0
            ? [UiElement.Text("mimir-actuator-empty", "no actuator commands yet", "caption")]
            : commands
                .OrderBy(static command => command.SourceId, StringComparer.Ordinal)
                .Take(8)
                .Select(command => UiElement.Text(
                    $"actuator-{StableId(command.SourceId)}",
                    $"{command.SourceId}: delay {command.DelaySamples:0.000} samples ratio {command.Ratio:0.000000000} c={command.Confidence:0.000}",
                    "caption"))
                .ToArray();

        return UiElement.Pane("mimir-actuator-pane", "Alignment Actuator", children);
    }

    private static DashboardUiElement BuildMoveMusicPane(MimirMoveMusicStat? music)
    {
        if (music == null)
        {
            return UiElement.Pane("mimir-move-music-pane", "Move Score", [UiElement.Text("mimir-move-music-empty", "no Move score trace yet", "caption")]);
        }

        var children = new List<DashboardUiElement>
        {
            UiElement.Text("mimir-move-music-head", $"bpm {music.Bpm:0.0} c={music.BpmConfidence:0.00} {music.KeyName} {music.KeyMode} chord {music.ChordName}", "mono"),
            StatBar("mimir-move-loudness", "loudness gate", music.LoudnessGate, "warm", $"{music.LoudnessGate:0.000}"),
            UiElement.Text("mimir-move-score", $"score {Bar(music.ScoreEnvelopeMax, 18)} max env {music.ScoreEnvelopeMax:0.000} max rgb {music.MaxRgb}", "caption"),
            UiElement.Text("mimir-move-sources", string.Join(" | ", music.Sources.Take(4)), "caption"),
        };
        children.AddRange(music.Voices
            .Take(5)
            .Select(voice => UiElement.Text(
                $"mimir-move-voice-{StableId(voice.SourceId)}",
                $"{voice.SourceId}: {voice.NoteName} {voice.FrequencyHz:0.0}Hz c={voice.Confidence:0.000} {voice.Role}",
                "caption")));
        children.Add(UiElement.Text("mimir-move-piano-roll", music.PianoRoll, "mono"));
        children.AddRange(music.MoveTargets
            .Take(5)
            .Select(target => UiElement.Text(
                $"mimir-move-target-{target.MoveIndex}",
                $"move {target.MoveIndex}: {target.NoteName} {target.SourceId} priority {target.CalibrationPriority:0.000}",
                "caption")));

        return UiElement.Pane("mimir-move-music-pane", "Move Score", children);
    }

    private static DashboardUiElement BuildWellPane(MimirWellStat? well)
    {
        if (well == null)
        {
            return UiElement.Pane("mimir-well-pane", "Well", [UiElement.Text("mimir-well-empty", "no Well snapshot yet", "caption")]);
        }

        var children = new List<DashboardUiElement>
        {
            UiElement.Text("mimir-well-summary", $"seq {well.Sequence} {well.ElapsedSeconds:0}s ingested {well.IngestedSamples:0} sources {well.LiveSources}/{well.ConfiguredSources} source errors {well.SourceErrorCount}", "mono"),
            StatBar("mimir-well-completeness", "frame completeness", well.SynchronizedFrameComplete ? 1.0 : 0.0, well.SynchronizedFrameComplete ? "cool" : "danger"),
            UiElement.Text("mimir-well-frame", $"frame ready {well.ReadySlices}/{well.TotalSlices} delay {well.PresentationDelayMs:0}ms degraded {well.FrameDegradedKind} {well.FrameDegradedReason}", "caption"),
            UiElement.Text("mimir-well-latency", $"latency {well.LatencyCurrentMs:0}ms floor {well.LatencyFloorMs:0} ceiling {well.LatencyCeilingMs:0} overlap {well.LatencyRetainedOverlapMs:0}ms skew {well.LatencyEdgeSkewMs:0.0}ms {well.LatencyReason}", "caption"),
            UiElement.Text("mimir-well-pressure", $"poll {well.PollAverageMs:0.000}/{well.PollMaxMs:0.000}ms zero {well.ZeroPollIterations} publish {well.PublishAverageMs:0.000}/{well.PublishMaxMs:0.000}ms bytes {well.PublishedBytes:0}", "caption"),
            UiElement.Text("mimir-well-buffers", $"buffers {well.Buffers.Count} audio {well.AudioBuffers} video {well.VideoBuffers} capture bodies {well.CaptureInlineBodies}", "caption"),
            UiElement.Text("mimir-well-clock-maps", $"canonical clocks {well.CanonicalClockMaps.Count} max offset {well.MaxCanonicalClockOffsetMs:0.000}ms", "caption"),
            UiElement.Text("mimir-well-features", $"video features stable {well.FeatureStableTracks} c={well.FeatureMeanConfidence:0.000} motion {well.FeatureMeanMotionPixelsPerSecond:0.0}px/s faust signals {well.FeatureSignals.Count}", "caption"),
            UiElement.Text("mimir-well-probe", well.Probe == null ? "probe: none" : $"probe emit={well.Probe.ShouldEmit} sync={well.Probe.SyncConfidence:0.000} freq={well.Probe.FrequencyConfidence:0.000} {well.Probe.Reason}", "caption"),
        };
        children.AddRange(well.FeatureSignals
            .OrderByDescending(static signal => signal.Confidence)
            .ThenBy(static signal => signal.SourceId, StringComparer.Ordinal)
            .Take(5)
            .Select(signal => UiElement.Text(
                $"mimir-well-feature-{StableId(signal.SourceId)}",
                $"{signal.SourceId}: tracks {signal.StableTrackCount} c={signal.Confidence:0.000} motion {signal.MotionEnergy:0.000} centroid {signal.CentroidX:0.00},{signal.CentroidY:0.00}",
                "caption")));
        children.AddRange(well.ClockDomains
            .Take(6)
            .Select(domain => UiElement.Text(
                $"mimir-well-clock-{StableId(domain.ClockDomainId)}",
                $"{domain.ClockDomainId}: {domain.SourceCount} src overlap {domain.OverlapMs:0.000}ms offset {domain.OffsetMs:0.000}ms {(domain.IsReference ? "ref" : "")}",
                "caption")));
        children.AddRange(well.CanonicalClockMaps
            .OrderByDescending(static map => Math.Abs(map.OffsetMs))
            .ThenBy(static map => map.StreamKey, StringComparer.Ordinal)
            .Take(4)
            .Select(map => UiElement.Text(
                $"mimir-well-clock-map-{StableId(map.StreamKey)}",
                $"{map.StreamKey}: ingress offset {map.OffsetMs:0.000}ms samples {map.SampleCount}",
                "caption")));
        children.AddRange(well.VisualCalibration
            .Take(6)
            .Select(camera => UiElement.Text(
                $"mimir-well-cal-{StableId(camera.SourceId)}",
                $"{camera.SourceId}: score {camera.BestScore:0.000} leds {camera.BestDetectedLedCount} exp {camera.BestExposure:0} gain {camera.BestGain:0} {camera.State}",
                "caption")));
        return UiElement.Pane("mimir-well-pane", "Well", children);
    }

    private static DashboardUiElement BuildCalibrationPane(MimirLiveStatsSnapshot snapshot)
    {
        var leap = snapshot.Well?.Buffers.FirstOrDefault(static buffer => buffer.SourceId.Contains("leap", StringComparison.OrdinalIgnoreCase));
        var eyeStreams = snapshot.ObservationStreams
            .Where(static stream => stream.StreamId.Contains("eye", StringComparison.OrdinalIgnoreCase) || stream.StreamId.Contains("video2", StringComparison.OrdinalIgnoreCase) || stream.StreamId.Contains("video3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static stream => stream.StreamId, StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        var moveStreams = snapshot.ObservationStreams
            .Where(static stream => stream.Kind.Contains("move", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static stream => stream.StreamId, StringComparer.Ordinal)
            .Take(4)
            .ToArray();

        var children = new List<DashboardUiElement>
        {
            UiElement.Text("mimir-calibration-leap", leap == null ? "Leap: no Well buffer" : $"Leap: {leap.Count} frames {leap.PixelFormat} {leap.Width:0}x{leap.Height:0} edge {NsAge(leap.EdgeNs)}", "mono"),
            UiElement.Text("mimir-calibration-eyes", $"Eyes: {eyeStreams.Count(static stream => stream.State == "active")}/{eyeStreams.Length} active", "caption"),
            UiElement.Text("mimir-calibration-moves", $"Moves: {moveStreams.Count(static stream => stream.State == "active")}/{moveStreams.Length} active", "caption"),
        };
        children.AddRange(eyeStreams.Select(stream => UiElement.Text($"mimir-eye-{StableId(stream.StreamId)}", $"{stream.StreamId} {stream.Shape} age {stream.AgeSeconds:0}s", "caption")));
        children.AddRange(moveStreams.Select(stream => UiElement.Text($"mimir-move-{StableId(stream.StreamId)}", $"{stream.StreamId} {stream.Shape} age {stream.AgeSeconds:0}s", "caption")));
        return UiElement.Pane("mimir-calibration-pane", "Leap / Eyes / Moves", children);
    }

    private static DashboardUiElement StatBar(string id, string label, double value, string tone, string? displayValue = null) =>
        UiElement.Card(
            id,
            "stat-bar",
            style: new DashboardUiStyle { Variant = "compact", Tone = tone },
            children:
            [
                UiElement.Text($"{id}-label", $"{label}: {displayValue ?? value.ToString("0.000", CultureInfo.InvariantCulture)}", "caption"),
                UiElement.Text($"{id}-bar", Bar(value, 28), "mono"),
                UiElement.Metric($"{id}-metric", label, value, tone),
            ]);

    private static string Bar(double value, int width)
    {
        var clamped = Math.Clamp(value, 0.0, 1.0);
        var filled = (int)Math.Round(clamped * width);
        var pulseIndex = Math.Clamp((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 180 % Math.Max(1, width)), 0, Math.Max(0, width - 1));
        var builder = new StringBuilder(width);
        for (var index = 0; index < width; index++)
        {
            builder.Append(index < filled ? (index == pulseIndex ? '*' : '#') : '.');
        }

        return builder.ToString();
    }

    private static string Pulse() =>
        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 250 % 4) switch
        {
            0 => "|",
            1 => "/",
            2 => "-",
            _ => "\\",
        };

    private static double NormalizeRms(double rms) => Math.Clamp(rms / 0.08, 0.0, 1.0);

    private static string ToneFor(double value) =>
        value >= 0.70 ? "cool" : value >= 0.25 ? "warm" : "danger";

    private static string NsAge(double timestampNs)
    {
        if (timestampNs <= 0)
        {
            return "unknown";
        }

        var nowNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000.0;
        var seconds = Math.Max(0.0, (nowNs - timestampNs) / 1_000_000_000.0);
        return seconds < 1.0 ? $"{seconds * 1000.0:0}ms" : $"{seconds:0.0}s";
    }

    private static string StableId(string value) =>
        string.Join("-", value.ToLowerInvariant().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Replace(":", "-", StringComparison.Ordinal)
            .Replace(">", "-", StringComparison.Ordinal)
            .Replace(" ", "-", StringComparison.Ordinal);
}

internal sealed class MimirLiveStatsSnapshot
{
    public string TelemetrySource { get; init; } = "none";
    public string ObservationSource { get; init; } = "none";
    public MimirTelemetryStat? Telemetry { get; init; }
    public IReadOnlyList<MimirSpectrumStat> Spectra { get; init; } = [];
    public IReadOnlyList<MimirSyncStateStat> SyncStates { get; init; } = [];
    public IReadOnlyList<MimirSyncReportStat> SyncReports { get; init; } = [];
    public IReadOnlyList<MimirSyncDecodeAttemptStat> SyncDecodeAttempts { get; init; } = [];
    public IReadOnlyList<MimirVideoBufferStat> VideoBuffers { get; init; } = [];
    public IReadOnlyList<MimirActuatorCommandStat> ActuatorCommands { get; init; } = [];
    public IReadOnlyList<MimirObservationStreamStat> ObservationStreams { get; init; } = [];
    public MimirWellStat? Well { get; init; }
    public MimirMoveMusicStat? MoveMusic { get; init; }
    public bool HasAnyData => Telemetry != null || Well != null || MoveMusic != null || Spectra.Count > 0 || SyncStates.Count > 0 || VideoBuffers.Count > 0 || ObservationStreams.Count > 0;
    public string Summary => Well == null
        ? $"{Spectra.Count} audio lanes / {SyncStates.Count} sync states / {SyncDecodeAttempts.Count} decode attempts / {VideoBuffers.Count} video buffers / {ObservationStreams.Count} device streams"
        : $"Well {Well.LiveSources}/{Well.ConfiguredSources} sources / {Well.AudioBuffers} audio / {Well.VideoBuffers} video / {ObservationStreams.Count} device streams";

    public static MimirLiveStatsSnapshot Load(string telemetryLogPath, string observationLogPath)
    {
        var telemetrySource = ResolveTelemetryLog(telemetryLogPath);
        var telemetryLines = File.Exists(telemetrySource) ? TailTextFile(telemetrySource, 512 * 1024).Split("\n", StringSplitOptions.RemoveEmptyEntries) : [];
        var observationLines = File.Exists(observationLogPath) ? TailTextFile(observationLogPath, 512 * 1024).Split("\n", StringSplitOptions.RemoveEmptyEntries) : [];
        return new MimirLiveStatsSnapshot
        {
            TelemetrySource = string.IsNullOrWhiteSpace(telemetrySource) ? "none" : telemetrySource,
            ObservationSource = File.Exists(observationLogPath) ? observationLogPath : $"missing {observationLogPath}",
            Telemetry = telemetryLines.Select(ParseTelemetry).Where(static value => value != null).LastOrDefault(),
            Spectra = LatestBySource(telemetryLines.Select(ParseSpectrum).Where(static value => value != null)!),
            SyncStates = LatestBySource(telemetryLines.Select(ParseSyncState).Where(static value => value != null)!),
            SyncReports = LatestBySource(telemetryLines.Select(ParseSyncReport).Where(static value => value != null)!),
            SyncDecodeAttempts = LatestSyncDecodeAttempts(telemetryLines.Select(ParseSyncDecodeAttempt).Where(static value => value != null)!),
            VideoBuffers = LatestBySource(telemetryLines.Select(ParseVideoBuffer).Where(static value => value != null)!),
            ActuatorCommands = LatestBySource(telemetryLines.Select(ParseActuatorCommand).Where(static value => value != null)!),
            ObservationStreams = LatestObservationStreams(observationLines),
            Well = LatestWellSnapshot(telemetryLines.Concat(observationLines)),
            MoveMusic = LatestMoveMusicTrace(telemetrySource, telemetryLines),
        };
    }

    private static string ResolveTelemetryLog(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var runtimeDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "runtime");
        runtimeDir = Path.GetFullPath(runtimeDir);
        if (!Directory.Exists(runtimeDir))
        {
            runtimeDir = @"E:\Projects\Mimir\artifacts\runtime";
        }

        if (!Directory.Exists(runtimeDir))
        {
            return "";
        }

        foreach (var file in Directory.EnumerateFiles(runtimeDir, "*.out.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(32))
        {
            var text = TailTextFile(file, 128 * 1024);
            if (text.Contains("mimir-sync-telemetry", StringComparison.Ordinal) ||
                text.Contains("mimir-spectrum ", StringComparison.Ordinal) ||
                text.Contains("mimir-video-buffer ", StringComparison.Ordinal))
            {
                return file;
            }
        }

        return "";
    }

    private static IReadOnlyList<T> LatestBySource<T>(IEnumerable<T?> values)
        where T : class, IMimirSourceStat
    {
        var bySource = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value == null)
            {
                continue;
            }

            bySource[value.SourceId] = value;
        }

        return bySource.Values.OrderBy(static value => value.SourceId, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<MimirSyncDecodeAttemptStat> LatestSyncDecodeAttempts(IEnumerable<MimirSyncDecodeAttemptStat?> values)
    {
        var byRoute = new Dictionary<string, MimirSyncDecodeAttemptStat>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value == null)
            {
                continue;
            }

            byRoute[$"{value.ReferenceSourceId}->{value.SourceId}"] = value;
        }

        return byRoute.Values
            .OrderByDescending(static value => value.Confidence)
            .ThenBy(static value => value.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static MimirWellStat? LatestWellSnapshot(IEnumerable<string> lines)
    {
        MimirWellStat? latest = null;
        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (string.Equals(JsonStringAny(root, "document"), "mimir.well_snapshot.v1", StringComparison.Ordinal))
                {
                    latest = ParseWellSnapshot(root);
                }
            }
            catch (JsonException)
            {
            }
        }

        return latest;
    }

    private static MimirWellStat ParseWellSnapshot(JsonElement root)
    {
        var buffers = ArrayItems(TryGetAny(root, "buffers"))
            .Select(static buffer =>
            {
                var latest = TryGetAny(buffer, "latest");
                var video = TryGetAny(latest, "video");
                var audio = TryGetAny(latest, "audio");
                return new MimirWellBufferStat(
                    JsonStringAny(buffer, "SourceId", "sourceId") ?? "unknown",
                    JsonStringAny(buffer, "Kind", "kind") ?? "unknown",
                    (int)JsonNumberAny(buffer, "Count", "count"),
                    JsonNumberAny(buffer, "EdgeNs", "edgeNs"),
                    (int)JsonNumberAny(video, "Width", "width"),
                    (int)JsonNumberAny(video, "Height", "height"),
                    JsonStringAny(video, "PixelFormat", "pixelFormat") ?? "",
                    (int)JsonNumberAny(audio, "SampleRate", "sampleRate"),
                    (int)JsonNumberAny(audio, "Channels", "channels"));
            })
            .ToArray();
        var frame = TryGetAny(root, "synchronizedFrame");
        var slices = ArrayItems(TryGetAny(frame, "slices")).ToArray();
        var audioSync = TryGetAny(root, "audioSync");
        var states = ArrayItems(TryGetAny(audioSync, "states"))
            .Select(static state => new MimirWellAudioSyncStateStat(
                JsonStringAny(state, "SourceId", "sourceId") ?? "unknown",
                JsonStringAny(state, "ReferenceSourceId", "referenceSourceId") ?? "unknown",
                JsonNumberAny(state, "SmoothedDelaySamples", "smoothedDelaySamples"),
                JsonNumberAny(state, "SamplingRateOffsetPpm", "samplingRateOffsetPpm"),
                JsonNumberAny(state, "Confidence", "confidence")))
            .ToArray();
        var probeElement = TryGetAny(audioSync, "probe");
        var probe = probeElement.ValueKind == JsonValueKind.Object
            ? new MimirWellProbeStat(
                JsonBoolAny(probeElement, "ShouldEmit", "shouldEmit"),
                JsonNumberAny(probeElement, "AggregateSyncConfidence", "aggregateSyncConfidence"),
                JsonNumberAny(probeElement, "AggregateFrequencyResponseConfidence", "aggregateFrequencyResponseConfidence"),
                JsonStringAny(probeElement, "reason") ?? "unknown")
            : null;
        var streamPressure = TryGetAny(root, "streamPressure");
        var pollPressure = TryGetAny(streamPressure, "poll");
        var publishPressure = TryGetAny(streamPressure, "publish");
        var latency = TryGetAny(root, "latency");
        var featureSignals = TryGetAny(root, "featureSignals");
        var featureSignalStats = ArrayItems(TryGetAny(featureSignals, "signals"))
            .Select(static signal => new MimirWellFeatureSignalStat(
                JsonStringAny(signal, "SourceId", "sourceId") ?? "unknown",
                (int)JsonNumberAny(signal, "StableTrackCount", "stableTrackCount"),
                JsonNumberAny(signal, "Confidence", "confidence"),
                JsonNumberAny(signal, "MeanMotionPixelsPerSecond", "meanMotionPixelsPerSecond"),
                JsonNumberAny(signal, "MotionEnergy", "motionEnergy"),
                JsonNumberAny(signal, "NormalizedCentroidX", "normalizedCentroidX"),
                JsonNumberAny(signal, "NormalizedCentroidY", "normalizedCentroidY")))
            .ToArray();
        var canonicalClockMaps = ArrayItems(TryGetAny(root, "canonicalClockMaps"))
            .Select(static map => new MimirWellCanonicalClockMapStat(
                JsonStringAny(map, "StreamKey", "streamKey") ?? "unknown",
                JsonNumberAny(map, "offsetMs"),
                (long)JsonNumberAny(map, "SampleCount", "sampleCount")))
            .ToArray();
        var clockDomains = ArrayItems(TryGetAny(TryGetAny(root, "clockDomains"), "domains"))
            .Select(static domain => new MimirWellClockDomainStat(
                JsonStringAny(domain, "ClockDomainId", "clockDomainId") ?? "unknown",
                (int)JsonNumberAny(domain, "SourceCount", "sourceCount"),
                JsonNumberAny(domain, "OverlapNs", "overlapNs") / 1_000_000.0,
                JsonBoolAny(domain, "HasLocalOverlap", "hasLocalOverlap"),
                JsonBoolAny(domain, "IsReferenceDomain", "isReferenceDomain"),
                JsonNumberAny(domain, "ProvisionalOffsetToReferenceMs", "provisionalOffsetToReferenceMs")))
            .ToArray();
        var visualCalibration = ArrayItems(TryGetAny(TryGetAny(root, "visualCalibration"), "cameras"))
            .Select(static camera => new MimirWellVisualCalibrationStat(
                JsonStringAny(camera, "SourceId", "sourceId") ?? "unknown",
                JsonStringAny(camera, "State", "state") ?? "unknown",
                JsonNumberAny(camera, "BestScore", "bestScore"),
                (int)JsonNumberAny(camera, "BestDetectedLedCount", "bestDetectedLedCount"),
                JsonBoolAny(camera, "BestUsableForCalibration", "bestUsableForCalibration"),
                JsonNumberAny(camera, "BestExposure", "bestExposure"),
                JsonNumberAny(camera, "BestGain", "bestGain")))
            .ToArray();
        var captureStorage = TryGetAny(TryGetAny(root, "capture"), "storage");
        var publication = TryGetAny(root, "publication");
        var stems = ArrayItems(TryGetAny(publication, "stems")).ToArray();
        return new MimirWellStat(
            (long)JsonNumberAny(root, "sequence"),
            JsonNumberAny(root, "elapsedSeconds"),
            JsonNumberAny(root, "ingestedSamples"),
            (int)JsonNumberAny(root, "configuredSources"),
            (int)JsonNumberAny(root, "liveSources"),
            ArrayItems(TryGetAny(root, "sourceErrors")).Count(),
            JsonBoolAny(frame, "IsComplete", "isComplete"),
            JsonNumberAny(frame, "PresentationDelayMs", "presentationDelayMs"),
            JsonStringAny(frame, "degradedKind") ?? "",
            JsonStringAny(frame, "degradedReason") ?? "",
            slices.Count(static slice => string.Equals(JsonStringAny(slice, "Status", "status"), "Ready", StringComparison.OrdinalIgnoreCase)),
            slices.Length,
            JsonNumberAny(latency, "currentDelayMs"),
            JsonNumberAny(latency, "ceilingDelayMs"),
            JsonNumberAny(latency, "floorDelayMs"),
            JsonNumberAny(latency, "retainedOverlapMs"),
            JsonNumberAny(latency, "edgeSkewMs"),
            JsonStringAny(latency, "Reason", "reason") ?? "",
            JsonNumberAny(pollPressure, "averageMilliseconds"),
            JsonNumberAny(pollPressure, "maxMilliseconds"),
            (long)JsonNumberAny(pollPressure, "zeroPollIterations"),
            JsonNumberAny(publishPressure, "averageMilliseconds"),
            JsonNumberAny(publishPressure, "maxMilliseconds"),
            (long)JsonNumberAny(publishPressure, "bytes"),
            JsonStringAny(captureStorage, "bodyTransport") ?? "unknown",
            stems.Length,
            buffers,
            clockDomains,
            states,
            visualCalibration,
            JsonNumberAny(featureSignals, "MeanConfidence", "meanConfidence"),
            JsonNumberAny(featureSignals, "MeanMotionPixelsPerSecond", "meanMotionPixelsPerSecond"),
            (int)JsonNumberAny(featureSignals, "StableTrackCount", "stableTrackCount"),
            featureSignalStats,
            canonicalClockMaps,
            probe);
    }

    private static MimirMoveMusicStat? LatestMoveMusicTrace(string telemetrySource, IReadOnlyList<string> telemetryLines)
    {
        var configuredLine = telemetryLines
            .Reverse()
            .FirstOrDefault(static line => line.Contains("\"live_score\"", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("\"score_gesture_envelopes\"", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(configuredLine))
        {
            return ParseMoveMusicTrace(telemetrySource, configuredLine);
        }

        return LatestMoveMusicTraceFromRuntime();
    }

    private static MimirMoveMusicStat? LatestMoveMusicTraceFromRuntime()
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "artifacts", "runtime");
        if (!Directory.Exists(runtimeRoot))
        {
            runtimeRoot = @"E:\Projects\Mimir\artifacts\runtime";
        }

        if (!Directory.Exists(runtimeRoot))
        {
            return null;
        }

        var trace = Directory.EnumerateFiles(runtimeRoot, "online-sync.jsonl", SearchOption.AllDirectories)
            .Select(static path => new FileInfo(path))
            .Where(static info => info.DirectoryName?.Contains("move", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(static info => info.LastWriteTimeUtc)
            .FirstOrDefault();
        if (trace == null || trace.Length == 0)
        {
            return null;
        }

        foreach (var line in TailTextFile(trace.FullName, 4 * 1024 * 1024)
            .Split("\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse()
            .Where(static candidate => candidate.Contains("\"live_score\"", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("\"score_gesture_envelopes\"", StringComparison.OrdinalIgnoreCase)))
        {
            var parsed = ParseMoveMusicTrace(trace.FullName, line);
            if (parsed != null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static MimirMoveMusicStat? ParseMoveMusicTrace(string sourcePath, string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var maxRgb = 0;
            var moves = TryGetAny(root, "moves");
            if (moves.ValueKind == JsonValueKind.Object)
            {
                foreach (var move in moves.EnumerateObject())
                {
                    if (move.Value.ValueKind == JsonValueKind.Array)
                    {
                        maxRgb = Math.Max(maxRgb, move.Value.EnumerateArray().Select(JsonNumber).Select(static value => (int)value).DefaultIfEmpty(0).Max());
                    }
                }
            }

            var envelopes = ArrayItems(TryGetAny(root, "score_gesture_envelopes")).Select(JsonNumber).DefaultIfEmpty(0.0).ToArray();
            var liveScore = TryGetAny(root, "live_score");
            var voices = ArrayItems(TryGetAny(liveScore, "voices"))
                .Select(static voice => new MimirMoveMusicVoiceStat(
                    JsonStringAny(voice, "source") ?? "unknown",
                    JsonStringAny(voice, "role") ?? "voice",
                    JsonStringAny(voice, "note_name") ?? "?",
                    JsonNumberAny(voice, "frequency_hz"),
                    JsonNumberAny(voice, "midi"),
                    JsonNumberAny(voice, "confidence"),
                    JsonNumberAny(voice, "strength"),
                    JsonBoolAny(voice, "active")))
                .OrderByDescending(static voice => voice.Active)
                .ThenByDescending(static voice => voice.Confidence)
                .ToArray();
            var targets = ArrayItems(TryGetAny(liveScore, "move_targets"))
                .Select(static target => new MimirMoveMusicTargetStat(
                    (int)JsonNumberAny(target, "move_index"),
                    JsonStringAny(target, "source") ?? "unknown",
                    JsonStringAny(target, "note_name") ?? "?",
                    JsonNumberAny(target, "target_note"),
                    JsonNumberAny(target, "confidence"),
                    JsonNumberAny(target, "calibration_priority")))
                .OrderBy(static target => target.MoveIndex)
                .ToArray();
            var sources = ArrayItems(TryGetAny(root, "music_sources"))
                .Select(static source =>
                {
                    var name = JsonStringAny(source, "source") ?? "source";
                    var strength = JsonNumberAny(source, "score_strength", "onset", "hit", "loudness_gate");
                    var key = JsonStringAny(source, "key_name") ?? "";
                    var mode = JsonStringAny(source, "key_mode") ?? "";
                    var harmonic = string.IsNullOrWhiteSpace(key)
                        ? $"f0 {JsonNumberAny(source, "fundamental_hz"):0}"
                        : $"{key} {mode}".Trim();
                    return $"{name} {JsonNumberAny(source, "bpm"):0.0}/{JsonNumberAny(source, "bpm_confidence"):0.00} s={strength:0.00} {harmonic}";
                })
                .ToArray();
            return new MimirMoveMusicStat(
                sourcePath,
                JsonNumberAny(root, "bpm"),
                JsonNumberAny(root, "bpm_confidence"),
                JsonNumberAny(root, "loudness_gate"),
                JsonNumberAny(root, "loudness_percentile"),
                JsonStringAny(root, "key_name") ?? "?",
                JsonStringAny(root, "key_mode") ?? "?",
                JsonStringAny(root, "chord_name") ?? "?",
                envelopes.Max(),
                maxRgb,
                sources,
                voices,
                targets,
                BuildPianoRoll(voices, targets));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildPianoRoll(
        IReadOnlyList<MimirMoveMusicVoiceStat> voices,
        IReadOnlyList<MimirMoveMusicTargetStat> targets)
    {
        var marks = new char[36];
        Array.Fill(marks, '.');
        foreach (var voice in voices.Where(static voice => voice.Active))
        {
            var index = PianoIndex(voice.MidiNote);
            if (index >= 0)
            {
                marks[index] = voice.Confidence >= 0.65 ? '#' : '+';
            }
        }

        foreach (var target in targets)
        {
            var index = PianoIndex(target.TargetNote);
            if (index >= 0 && marks[index] == '.')
            {
                marks[index] = 'o';
            }
        }

        return $"C3 {new string(marks)} C6";
    }

    private static int PianoIndex(double midiNote)
    {
        if (midiNote <= 0.0)
        {
            return -1;
        }

        return (int)Math.Clamp(Math.Round(midiNote) - 48, 0, 35);
    }

    private static IReadOnlyList<MimirObservationStreamStat> LatestObservationStreams(IEnumerable<string> lines)
    {
        var byStream = new Dictionary<string, MimirObservationStreamStat>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = JsonString(root, "type");
                if (type is not ("cultmesh-observation" or "cultmesh-media-observation"))
                {
                    continue;
                }

                var deviceId = JsonString(root, "DeviceId") ?? "unknown";
                var streamId = JsonString(root, "StreamId") ?? "unknown";
                var kind = JsonString(root, "Kind") ?? "unknown";
                var latestAt = DateTimeOffset.TryParse(JsonString(root, "WallClockUtc"), out var parsed) ? parsed : DateTimeOffset.MinValue;
                var age = latestAt == DateTimeOffset.MinValue ? double.PositiveInfinity : Math.Max(0.0, (now - latestAt).TotalSeconds);
                var shape = ObservationShape(root);
                byStream[$"{deviceId}:{streamId}:{kind}"] = new MimirObservationStreamStat(
                    deviceId,
                    streamId,
                    kind,
                    JsonNumber(root, "Sequence"),
                    age <= 120.0 ? "active" : "stale",
                    age,
                    shape);
            }
            catch (JsonException)
            {
            }
        }

        return byStream.Values.ToArray();
    }

    private static string ObservationShape(JsonElement root)
    {
        var format = JsonString(root, "Format") ?? "";
        var width = JsonNumber(root, "Width");
        var height = JsonNumber(root, "Height");
        var sampleRate = JsonNumber(root, "SampleRate");
        var channels = JsonNumber(root, "Channels");
        var frames = JsonNumber(root, "FrameCount");
        var bytes = JsonNumber(root, "PayloadBytes");
        if (width > 0 && height > 0)
        {
            return $"{format} {width:0}x{height:0} {bytes:0} bytes";
        }

        if (sampleRate > 0)
        {
            return $"{format} {sampleRate:0} Hz x {Math.Max(1, channels):0} {frames:0} frames {bytes:0} bytes";
        }

        if (root.TryGetProperty("Values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            return string.Join(", ", values.EnumerateArray().Take(3).Select(static value => value.TryGetDouble(out var number) ? number.ToString("0.000", CultureInfo.InvariantCulture) : "?"));
        }

        return "observation";
    }

    private static MimirTelemetryStat? ParseTelemetry(string line)
    {
        if (!line.StartsWith("mimir-sync-telemetry ", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = Fields(line);
        return new MimirTelemetryStat(
            Number(fields, "t"),
            (int)Number(fields, "sources"),
            (int)Number(fields, "lastPoll"),
            Number(fields, "ingested"),
            Text(fields, "audioSync"),
            (int)Number(fields, "reports"),
            (int)Number(fields, "states"),
            Number(fields, "analyzeMs"));
    }

    private static MimirSpectrumStat? ParseSpectrum(string line)
    {
        if (!line.StartsWith("mimir-spectrum ", StringComparison.Ordinal))
        {
            return null;
        }

        var sourceId = TokenAfterPrefix(line, "mimir-spectrum ");
        var fields = Fields(line);
        return new MimirSpectrumStat(
            sourceId,
            Text(fields, "label"),
            (int)Number(fields, "rate"),
            (int)Number(fields, "fft"),
            Number(fields, "rms"),
            Number(fields, "peak"),
            Number(fields, "floorDb"),
            Text(fields, "peaks"));
    }

    private static MimirSyncStateStat? ParseSyncState(string line)
    {
        if (!line.StartsWith("mimir-sync-state ", StringComparison.Ordinal))
        {
            return null;
        }

        var route = TokenAfterPrefix(line, "mimir-sync-state ");
        var parts = route.Split("->", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        var fields = Fields(line);
        return new MimirSyncStateStat(
            parts[1],
            parts[0],
            Number(fields, "delaySamples"),
            Number(fields, "delayUs"),
            Number(fields, "delayMs"),
            Number(fields, "sroPpm"),
            Number(fields, "confidence"));
    }

    private static MimirSyncReportStat? ParseSyncReport(string line)
    {
        if (!line.StartsWith("mimir-sync-report ", StringComparison.Ordinal) &&
            !line.StartsWith("mimir-complex-contour-report ", StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = line.StartsWith("mimir-sync-report ", StringComparison.Ordinal) ? "mimir-sync-report " : "mimir-complex-contour-report ";
        var route = TokenAfterPrefix(line, prefix);
        var parts = route.Split("->", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        var fields = Fields(line);
        return new MimirSyncReportStat(
            parts[1],
            parts[0],
            Text(fields, "evidence", prefix.Contains("complex", StringComparison.Ordinal) ? "complex-contour" : "sync"),
            Number(fields, "delayUs"),
            Number(fields, "confidence"),
            (int)Number(fields, "timelineEvents") + (int)Number(fields, "directHits"));
    }

    private static MimirSyncDecodeAttemptStat? ParseSyncDecodeAttempt(string line)
    {
        if (!line.StartsWith("mimir-sync-decode ", StringComparison.Ordinal))
        {
            return null;
        }

        var route = TokenAfterPrefix(line, "mimir-sync-decode ");
        var parts = route.Split("->", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        var fields = Fields(line);
        return new MimirSyncDecodeAttemptStat(
            parts[1],
            parts[0],
            Text(fields, "status", "unknown"),
            (int)Number(fields, "compared"),
            (int)Number(fields, "rate"),
            Number(fields, "refEnergy"),
            Number(fields, "candEnergy"),
            (int)Number(fields, "matched"),
            Number(fields, "confidence"));
    }

    private static MimirVideoBufferStat? ParseVideoBuffer(string line)
    {
        if (!line.StartsWith("mimir-video-buffer ", StringComparison.Ordinal))
        {
            return null;
        }

        var sourceId = TokenAfterPrefix(line, "mimir-video-buffer ");
        var fields = Fields(line);
        var latestIndex = line.IndexOf(" latest=", StringComparison.Ordinal);
        return new MimirVideoBufferStat(
            sourceId,
            (int)Number(fields, "count"),
            latestIndex < 0 ? "" : line[(latestIndex + " latest=".Length)..]);
    }

    private static MimirActuatorCommandStat? ParseActuatorCommand(string line)
    {
        if (!line.StartsWith("mimir-audio-actuator-command ", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = Fields(line);
        var source = Text(fields, "source");
        return string.IsNullOrWhiteSpace(source)
            ? null
            : new MimirActuatorCommandStat(source, Number(fields, "delaySamples"), Number(fields, "ratio"), Number(fields, "confidence"));
    }

    private static Dictionary<string, string> Fields(string line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in Tokenize(line))
        {
            var equals = token.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            fields[token[..equals]] = token[(equals + 1)..].Trim('"');
        }

        return fields;
    }

    private static IEnumerable<string> Tokenize(string line)
    {
        var builder = new StringBuilder();
        var quoted = false;
        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
                builder.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                continue;
            }

            builder.Append(character);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string TokenAfterPrefix(string line, string prefix)
    {
        var rest = line[prefix.Length..];
        var space = rest.IndexOf(' ');
        return space < 0 ? rest : rest[..space];
    }

    private static string Text(IReadOnlyDictionary<string, string> fields, string key, string fallback = "") =>
        fields.TryGetValue(key, out var value) ? value : fallback;

    private static double Number(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0.0;

    private static string? JsonString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    private static double JsonNumber(JsonElement root, string property) =>
        root.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.Number && child.TryGetDouble(out var value) ? value : 0.0;

    private static JsonElement TryGetAny(JsonElement root, params string[] properties)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        foreach (var property in properties)
        {
            if (root.TryGetProperty(property, out var child))
            {
                return child;
            }
        }

        return default;
    }

    private static IEnumerable<JsonElement> ArrayItems(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in element.EnumerateArray())
        {
            yield return item;
        }
    }

    private static string? JsonStringAny(JsonElement root, params string[] properties)
    {
        var child = TryGetAny(root, properties);
        return child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    }

    private static double JsonNumberAny(JsonElement root, params string[] properties) => JsonNumber(TryGetAny(root, properties));

    private static double JsonNumber(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? value : 0.0;

    private static bool JsonBoolAny(JsonElement root, params string[] properties)
    {
        var child = TryGetAny(root, properties);
        return child.ValueKind == JsonValueKind.True || (child.ValueKind == JsonValueKind.Number && child.TryGetDouble(out var value) && value != 0.0);
    }

    private static string NsAge(double timestampNs)
    {
        if (timestampNs <= 0)
        {
            return "unknown";
        }

        var nowNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000.0;
        var seconds = Math.Max(0.0, (nowNs - timestampNs) / 1_000_000_000.0);
        return seconds < 1.0 ? $"{seconds * 1000.0:0}ms" : $"{seconds:0.0}s";
    }

    private static string TailTextFile(string filePath, int maxBytes)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length == 0)
        {
            return "";
        }

        var length = (int)Math.Min(maxBytes, info.Length);
        var buffer = new byte[length];
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(-length, SeekOrigin.End);
        _ = stream.Read(buffer, 0, length);
        return Encoding.UTF8.GetString(buffer);
    }
}

internal interface IMimirSourceStat
{
    string SourceId { get; }
}

internal sealed record MimirTelemetryStat(double RuntimeSeconds, int Sources, int LastPoll, double Ingested, string AudioSync, int Reports, int States, double AnalyzeMs);

internal sealed record MimirSpectrumStat(string SourceId, string Label, int SampleRate, int FftSize, double Rms, double Peak, double NoiseFloorDb, string Peaks) : IMimirSourceStat;

internal sealed record MimirSyncStateStat(string SourceId, string ReferenceSourceId, double DelaySamples, double DelayUs, double DelayMs, double SroPpm, double Confidence) : IMimirSourceStat;

internal sealed record MimirSyncReportStat(string SourceId, string ReferenceSourceId, string Evidence, double DelayUs, double Confidence, int Events) : IMimirSourceStat;

internal sealed record MimirSyncDecodeAttemptStat(string SourceId, string ReferenceSourceId, string Status, int Compared, int SampleRate, double ReferenceEnergy, double CandidateEnergy, int Matched, double Confidence) : IMimirSourceStat;

internal sealed record MimirVideoBufferStat(string SourceId, int Count, string Latest) : IMimirSourceStat;

internal sealed record MimirActuatorCommandStat(string SourceId, double DelaySamples, double Ratio, double Confidence) : IMimirSourceStat;

internal sealed record MimirObservationStreamStat(string DeviceId, string StreamId, string Kind, double Sequence, string State, double AgeSeconds, string Shape);

internal sealed record MimirWellStat(
    long Sequence,
    double ElapsedSeconds,
    double IngestedSamples,
    int ConfiguredSources,
    int LiveSources,
    int SourceErrorCount,
    bool SynchronizedFrameComplete,
    double PresentationDelayMs,
    string FrameDegradedKind,
    string FrameDegradedReason,
    int ReadySlices,
    int TotalSlices,
    double LatencyCurrentMs,
    double LatencyCeilingMs,
    double LatencyFloorMs,
    double LatencyRetainedOverlapMs,
    double LatencyEdgeSkewMs,
    string LatencyReason,
    double PollAverageMs,
    double PollMaxMs,
    long ZeroPollIterations,
    double PublishAverageMs,
    double PublishMaxMs,
    long PublishedBytes,
    string CaptureInlineBodies,
    int PublicationStemCount,
    IReadOnlyList<MimirWellBufferStat> Buffers,
    IReadOnlyList<MimirWellClockDomainStat> ClockDomains,
    IReadOnlyList<MimirWellAudioSyncStateStat> AudioSyncStates,
    IReadOnlyList<MimirWellVisualCalibrationStat> VisualCalibration,
    double FeatureMeanConfidence,
    double FeatureMeanMotionPixelsPerSecond,
    int FeatureStableTracks,
    IReadOnlyList<MimirWellFeatureSignalStat> FeatureSignals,
    IReadOnlyList<MimirWellCanonicalClockMapStat> CanonicalClockMaps,
    MimirWellProbeStat? Probe)
{
    public int AudioBuffers => Buffers.Count(static buffer => string.Equals(buffer.Kind, "Audio", StringComparison.OrdinalIgnoreCase));

    public int VideoBuffers => Buffers.Count(static buffer => string.Equals(buffer.Kind, "Video", StringComparison.OrdinalIgnoreCase));

    public double MaxCanonicalClockOffsetMs => CanonicalClockMaps.Count == 0
        ? 0.0
        : CanonicalClockMaps.Max(static map => Math.Abs(map.OffsetMs));
}

internal sealed record MimirWellBufferStat(
    string SourceId,
    string Kind,
    int Count,
    double EdgeNs,
    int Width,
    int Height,
    string PixelFormat,
    int SampleRate,
    int Channels);

internal sealed record MimirWellAudioSyncStateStat(string SourceId, string ReferenceSourceId, double DelaySamples, double SroPpm, double Confidence);

internal sealed record MimirWellClockDomainStat(string ClockDomainId, int SourceCount, double OverlapMs, bool HasLocalOverlap, bool IsReference, double OffsetMs);

internal sealed record MimirWellVisualCalibrationStat(string SourceId, string State, double BestScore, int BestDetectedLedCount, bool BestUsableForCalibration, double BestExposure, double BestGain);

internal sealed record MimirWellFeatureSignalStat(string SourceId, int StableTrackCount, double Confidence, double MeanMotionPixelsPerSecond, double MotionEnergy, double CentroidX, double CentroidY);

internal sealed record MimirWellCanonicalClockMapStat(string StreamKey, double OffsetMs, long SampleCount);

internal sealed record MimirWellProbeStat(bool ShouldEmit, double SyncConfidence, double FrequencyConfidence, string Reason);

internal sealed record MimirMoveMusicStat(
    string TracePath,
    double Bpm,
    double BpmConfidence,
    double LoudnessGate,
    double LoudnessPercentile,
    string KeyName,
    string KeyMode,
    string ChordName,
    double ScoreEnvelopeMax,
    int MaxRgb,
    IReadOnlyList<string> Sources,
    IReadOnlyList<MimirMoveMusicVoiceStat> Voices,
    IReadOnlyList<MimirMoveMusicTargetStat> MoveTargets,
    string PianoRoll);

internal sealed record MimirMoveMusicVoiceStat(string SourceId, string Role, string NoteName, double FrequencyHz, double MidiNote, double Confidence, double Strength, bool Active);

internal sealed record MimirMoveMusicTargetStat(int MoveIndex, string SourceId, string NoteName, double TargetNote, double Confidence, double CalibrationPriority);

internal sealed class MimirStreamLayoutProvider : MutableDashboardProvider
{
    public const string ProviderId = "mimir.stream.layout";

    public MimirStreamLayoutProvider()
        : base(
            new DashboardProviderManifest(
                ProviderId,
                "Mimir Stream Layout",
                "Native scene graph editor for program layout, source transforms, visibility, and future LUT controls.",
                "1",
                "/eve/deck/mimir.stream.layout",
                ["scene2d", "visibility", "transform", "lut-presets", "audio-mix"],
                UsesCultMesh: true,
                Transport: "local WebSocket; state document can mirror through CultMesh."),
            CreateState())
    {
    }

    private static DashboardState CreateState() =>
        new()
        {
            ProviderId = ProviderId,
            Title = "Mimir Stream Layout",
            Nodes =
            [
                new DashboardNode("program", "Program Output", "camera", -0.05, -0.05, 0.62, 0.34, "live") { Detail = "Final OBS-facing composition surface. Visibility, solo, opacity, layer order, audio stem selection, mute/solo/gain, and LUT preset controls belong here." },
                new DashboardNode("editor", "Mimir Editor", "editor", -0.48, 0.34, 0.40, 0.28, "live") { Detail = "Scene graph owner for editor camera, sensor-feed panels, SDF text placeholders, model placeholders, selected node, visibility, locks, transform reset, and grab/rotate/resize gizmo intent." },
                new DashboardNode("sync", "Mimir Sync", "timing", 0.48, 0.34, 0.36, 0.28, "live") { Detail = "Rolling buffer and synchronization surface: five-second window, stream count, source count, poll cadence, ingested samples, audio sync state, and spectrum cadence." },
                new DashboardNode("leap-field", "Leap Field", "sensor", 0.38, 0.02, 0.28, 0.20, "dense geometry") { Detail = "Leap packed stereo IR feeds Fensalir stereo depth, disparity SurfacePage, point-cloud Mesh socket, surface claims, and GPU sensor fusion." },
                new DashboardNode("ps3-eye-0", "PS3 Eye 0", "camera", -0.56, -0.34, 0.24, 0.18, "187 fps tracker") { Detail = "High-rate Bayer8 tracking witness. GPU feature extraction samples this texture through Fensalir GpuSensorFusion." },
                new DashboardNode("ps3-eye-1", "PS3 Eye 1", "camera", -0.26, -0.44, 0.24, 0.18, "187 fps tracker") { Detail = "Second high-rate Bayer8 tracking witness. Used for fast motion and online calibration constraints." },
                new DashboardNode("kiyo-pro", "Kiyo Pro", "camera", 0.18, -0.42, 0.24, 0.18, "YUY2 RGB") { Detail = "RGB/context witness. Fensalir decodes YUY2 in compute through the documented R8G8B8A8_UNORM SRV path." },
                new DashboardNode("kiyo-basic", "Kiyo Basic", "camera", 0.50, -0.34, 0.24, 0.18, "YUY2 RGB") { Detail = "Second RGB/context witness. Enters the same GpuSensorFusion path after YUY2 decode." },
                new DashboardNode("eve-camera", "Eve Camera", "camera", 0.0, -0.62, 0.30, 0.18, "armed") { Detail = "Portable sensor uplink target. EveCanvas owns capture on-device and sends frame events into Mimir when online." },
            ],
        };
}

internal sealed class VoidBotSwarmProvider : IDashboardProvider
{
    public const string ProviderId = "voidbot.swarm";

    private readonly string swarmStatePath;
    private DashboardState currentState;
    private DateTime lastWriteUtc = DateTime.MinValue;
    private string selectedIdentityId = "";
    private string selectedStatePath = "";

    public VoidBotSwarmProvider(string swarmStatePath)
    {
        this.swarmStatePath = swarmStatePath;
        Manifest = new DashboardProviderManifest(
            ProviderId,
            "VoidBot Swarm",
            "Native Eve tab for VoidBot agent status, CTB order, and selected Face state.",
            "1",
            "/eve/deck/voidbot.swarm",
            ["ctb", "agent-status", "state-tree", "cultmesh-snapshot"],
            UsesCultMesh: true,
            Transport: "Reads VoidBot swarm-state.json now; consumes voidbot.swarm_state_snapshot.v1 when CultMesh bridge is live.");
        currentState = BuildMissingState();
    }

    public DashboardProviderManifest Manifest { get; }

    public DashboardState State
    {
        get
        {
            RefreshIfNeeded(force: false);
            return currentState;
        }
    }

    public bool ApplyCommand(DashboardCommand command)
    {
        RefreshIfNeeded(force: false);
        var node = currentState.Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, command.NodeId, StringComparison.Ordinal));
        if (node == null)
        {
            return false;
        }

        currentState.SelectedNodeId = node.Id;
        if (!string.IsNullOrWhiteSpace(node.IdentityId))
        {
            selectedIdentityId = node.IdentityId;
            selectedStatePath = "";
        }

        if (!string.IsNullOrWhiteSpace(node.StatePath))
        {
            selectedStatePath = node.StatePath;
        }

        RefreshIfNeeded(force: true);
        currentState.SelectedNodeId = node.Id;
        return true;
    }

    private void RefreshIfNeeded(bool force)
    {
        try
        {
            var info = new FileInfo(swarmStatePath);
            if (!info.Exists)
            {
                currentState = BuildMissingState();
                return;
            }

            if (!force && info.LastWriteTimeUtc == lastWriteUtc)
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(info.FullName));
            currentState = BuildState(document.RootElement, info.LastWriteTimeUtc);
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch (Exception error)
        {
            currentState = BuildErrorState(error.Message);
        }
    }

    private DashboardState BuildState(JsonElement root, DateTime sourceWriteUtc)
    {
        var summary = TryGet(root, "summary");
        var controls = TryGet(root, "controls");
        var cultMesh = TryGet(root, "cultMesh");
        var orchestrator = TryGet(root, "orchestrator");
        var participants = ArrayItems(TryGet(root, "participants")).ToArray();
        var upcoming = ArrayItems(TryGet(root, "upcomingTurns")).ToArray();

        if (string.IsNullOrWhiteSpace(selectedIdentityId))
        {
            selectedIdentityId = StringValue(summary, "nextIdentityId")
                ?? StringValue(upcoming.FirstOrDefault(), "identityId")
                ?? StringValue(participants.FirstOrDefault(), "identityId")
                ?? "";
        }

        var selectedAgent = participants.FirstOrDefault(participant => string.Equals(StringValue(participant, "identityId"), selectedIdentityId, StringComparison.Ordinal));
        if (selectedAgent.ValueKind == JsonValueKind.Undefined)
        {
            selectedAgent = participants.FirstOrDefault();
            selectedIdentityId = StringValue(selectedAgent, "identityId") ?? "";
        }

        var nodes = new List<DashboardNode>();
        var paused = BoolValue(summary, "paused") == true;
        var stateLabel = StringValue(summary, "state") ?? "unknown";
        var generatedAt = StringValue(root, "generatedAt") ?? sourceWriteUtc.ToString("O");
        var cadence = NumberValue(controls, "cadenceMultiplier") ?? NumberValue(summary, "cadenceMultiplier") ?? 1;
        nodes.Add(new DashboardNode(
            "voidbot-summary",
            $"VoidBot Swarm\n{stateLabel}  next {StringValue(summary, "nextDisplayName") ?? "none"}",
            "swarm",
            -0.60,
            -0.54,
            0.34,
            0.22,
            paused ? "paused" : "running")
        {
            Detail = $"agents {NumberValue(summary, "participantCount") ?? 0:0}  ready {NumberValue(summary, "readyNowCount") ?? 0:0}  cadence x{cadence:0.##}\nmesh {StringValue(cultMesh, "writeStatus") ?? "missing"}  {generatedAt}",
        });

        AddCtbRail(nodes, upcoming);
        AddAgentCards(nodes, participants);
        AddSelectedAgent(nodes, selectedAgent);

        var selectedNodeId = string.IsNullOrWhiteSpace(currentState.SelectedNodeId) ||
            nodes.All(node => !string.Equals(node.Id, currentState.SelectedNodeId, StringComparison.Ordinal))
                ? "voidbot-summary"
                : currentState.SelectedNodeId;

        var state = new DashboardState
        {
            ProviderId = ProviderId,
            Title = "VoidBot Swarm",
            Version = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UpdatedAt = DateTimeOffset.UtcNow,
            SelectedNodeId = selectedNodeId,
            Nodes = nodes,
        };
        state.Surface = BuildVoidBotSurface(state, summary, cultMesh, controls, orchestrator, upcoming, participants, selectedAgent);
        return state;
    }

    private DashboardSurface BuildVoidBotSurface(
        DashboardState state,
        JsonElement summary,
        JsonElement cultMesh,
        JsonElement controls,
        JsonElement orchestrator,
        IReadOnlyList<JsonElement> upcoming,
        IReadOnlyList<JsonElement> participants,
        JsonElement selectedAgent)
    {
        var generatedAt = StringValue(summary, "generatedAt") ?? state.UpdatedAt.ToString("O");
        var cadence = NumberValue(controls, "cadenceMultiplier") ?? NumberValue(summary, "cadenceMultiplier") ?? 1;
        var leaves = FlattenLeaves(ArrayItems(TryGet(TryGet(selectedAgent, "faceState"), "tree")).ToArray()).Take(5).ToArray();
        var selectedLeaf = leaves.FirstOrDefault(leaf => string.Equals(leaf.Path, selectedStatePath, StringComparison.Ordinal)) ?? leaves.FirstOrDefault();

        return new DashboardSurface
        {
            Schema = "cultmesh.eve_surface.v0",
            Id = "voidbot.swarm.surface",
            Title = "VoidBot Swarm",
            Root = UiElement.Container(
                "voidbot-cockpit",
                "cockpit",
                new DashboardUiLayout
                {
                    Direction = "vertical",
                    Gap = 8,
                    Padding = 8,
                    Overflow = "scroll",
                    Grow = 5,
                    MinWidth = 112,
                    MinHeight = 30,
                    PreferredWidth = 156,
                    PreferredHeight = 58,
                    Priority = -30,
                    Density = "continuous",
                    ViewportMode = "continuous-ops",
                },
                [
                    BuildTurnRail(upcoming),
                    UiElement.Container(
                        "voidbot-ops-row",
                        "ops-row",
                        new DashboardUiLayout { Direction = "horizontal", Gap = 8, Grow = 1 },
                        [
                            BuildUpcomingFacesPane(upcoming),
                            BuildWatchdogPane(orchestrator),
                        ]),
                    UiElement.Container(
                        "voidbot-workspace",
                        "workspace",
                        new DashboardUiLayout { Direction = "horizontal", Gap = 8, Grow = 2 },
                        [
                            BuildSelectedFacePane(summary, cultMesh, selectedAgent, cadence, generatedAt),
                            BuildStateGraphPane(leaves),
                            BuildStateDetailPane(state, selectedLeaf),
                        ]),
                ]),
            Assets = participants
                .Select(participant => new DashboardSurfaceAsset(
                    $"avatar:{StringValue(participant, "identityId") ?? ""}",
                    "image",
                    StringValue(participant, "avatarUrl") ?? ""))
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Uri))
                .GroupBy(asset => asset.Id)
                .Select(group => group.First())
                .ToArray(),
        };
    }

    private DashboardUiElement BuildTurnRail(IReadOnlyList<JsonElement> upcoming)
    {
        var count = Math.Min(14, upcoming.Count);
        var cards = new List<DashboardUiElement>();
        for (var index = 0; index < count; index++)
        {
            var turn = upcoming[index];
            var identityId = StringValue(turn, "identityId") ?? "";
            var active = !string.IsNullOrWhiteSpace(StringValue(turn, "activeJobId"));
            var mentionCount = NumberValue(turn, "pendingMentionCount") ?? 0;
            cards.Add(UiElement.Card(
                $"ctb-card-{index}-{identityId}",
                "turn-card",
                bindNodeId: $"ctb-{index}-{identityId}",
                commandId: $"select-identity:{identityId}",
                style: new DashboardUiStyle { Variant = active ? "active-turn" : mentionCount > 0 ? "mention-turn" : "default" },
                children:
                [
                    UiElement.Avatar($"ctb-avatar-{identityId}", StringValue(turn, "avatarUrl"), StringValue(turn, "displayName") ?? identityId),
                    UiElement.Text($"ctb-label-{identityId}", $"{index + 1}. {StringValue(turn, "displayName") ?? identityId}\n{StringValue(turn, "repoName") ?? "repo"}", "strong"),
                    UiElement.Text($"ctb-health-{identityId}", active ? "active" : mentionCount > 0 ? "mention" : Minutes(NumberValue(turn, "nextTurnInMinutes")), "caption"),
                ]));
        }

        return UiElement.Container(
            "ctb-rail",
            "ctb-rail",
            new DashboardUiLayout { Direction = "horizontal", Gap = 8, Height = 112, Overflow = "scroll-x" },
            cards);
    }

    private DashboardUiElement BuildUpcomingFacesPane(IReadOnlyList<JsonElement> upcoming)
    {
        var rows = upcoming
            .Take(10)
            .Select((turn, index) =>
            {
                var identityId = StringValue(turn, "identityId") ?? $"turn-{index}";
                var active = !string.IsNullOrWhiteSpace(StringValue(turn, "activeJobId"));
                var mentionCount = NumberValue(turn, "pendingMentionCount") ?? 0;
                var rest = TryGet(turn, "restState");
                var napping = BoolValue(rest, "isNapping") == true;
                var state = active ? "active" : mentionCount > 0 ? "mention" : napping ? "nap" : Minutes(NumberValue(turn, "nextTurnInMinutes"));
                var heat = NumberValue(turn, "heat") ?? 0;
                var speed = NumberValue(turn, "effectiveSpeed") ?? 0;
                return UiElement.Text(
                    $"upcoming-face-{index}-{identityId}",
                    $"{index + 1,2}. {(StringValue(turn, "displayName") ?? identityId),-10} {state,-8} {StringValue(turn, "repoName") ?? "repo"}  spd {speed:0.###} heat {heat:0.##}",
                    "mono");
            })
            .ToArray();

        return UiElement.Pane(
            "upcoming-faces-pane",
            "Next Faces",
            rows.Length > 0
                ? rows
                : [UiElement.Text("upcoming-faces-empty", "no upcoming Face turns", "caption")]);
    }

    private DashboardUiElement BuildWatchdogPane(JsonElement orchestrator)
    {
        var state = StringValue(orchestrator, "state") ?? "unknown";
        var organs = ArrayItems(TryGet(orchestrator, "organs")).ToArray();
        var watchdog = organs.FirstOrDefault(organ => string.Equals(StringValue(organ, "id"), "voidbot-operations-watchdog", StringComparison.OrdinalIgnoreCase));
        var children = new List<DashboardUiElement>
        {
            UiElement.Text("watchdog-summary", $"orchestrator {state}  organs {organs.Length}", "mono"),
        };

        if (watchdog.ValueKind != JsonValueKind.Undefined)
        {
            children.Add(UiElement.Text(
                "watchdog-status",
                $"watchdog {StringValue(watchdog, "lastStatus") ?? "unknown"} exit {(NumberValue(watchdog, "lastExitCode") ?? 0):0}\nlast {ShortIso(StringValue(watchdog, "lastFinishedAt") ?? StringValue(watchdog, "lastStartedAt"))}",
                "strong"));
        }
        else
        {
            children.Add(UiElement.Text("watchdog-missing", "watchdog organ not present in snapshot", "caption"));
        }

        children.AddRange(organs
            .Where(static organ => organ.ValueKind != JsonValueKind.Undefined)
            .OrderBy(organ => string.Equals(StringValue(organ, "id"), "voidbot-operations-watchdog", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(organ => StringValue(organ, "id"))
            .Take(7)
            .Select(organ => UiElement.Text(
                $"watchdog-organ-{StableId(StringValue(organ, "id") ?? "organ")}",
                $"{StringValue(organ, "label") ?? StringValue(organ, "id") ?? "organ"}: {StringValue(organ, "lastStatus") ?? "unknown"}",
                "caption")));

        return UiElement.Pane("voidbot-watchdog-pane", "VoidBot Watchdog", children);
    }

    private DashboardUiElement BuildSelectedFacePane(JsonElement summary, JsonElement cultMesh, JsonElement selectedAgent, double cadence, string generatedAt)
    {
        var identityId = StringValue(selectedAgent, "identityId") ?? "";
        var faceState = TryGet(selectedAgent, "faceState");
        var counts = TryGet(faceState, "counts");
        var memory = NumberValue(counts, "memory") ?? 0;
        var description = StringValue(selectedAgent, "description") ?? "No Face description registered.";
        return UiElement.Pane(
            "selected-face-pane",
            "Controls / Selected Face",
            [
                UiElement.Text("voidbot-summary-text", $"VoidBot Swarm\n{StringValue(summary, "state") ?? "unknown"}  next {StringValue(summary, "nextDisplayName") ?? "none"}\nagents {NumberValue(summary, "participantCount") ?? 0:0}  ready {NumberValue(summary, "readyNowCount") ?? 0:0}  cadence x{cadence:0.##}\nmesh {StringValue(cultMesh, "writeStatus") ?? "missing"}  {generatedAt}", "mono"),
                UiElement.Card(
                    "selected-face-card",
                    "inspector-hero",
                    bindNodeId: "agent-detail",
                    commandId: $"select-identity:{identityId}",
                    children:
                    [
                        UiElement.Avatar("selected-face-avatar", StringValue(selectedAgent, "avatarUrl"), StringValue(selectedAgent, "displayName") ?? identityId),
                        UiElement.Text("selected-face-name", $"Selected Face\n{StringValue(selectedAgent, "displayName") ?? identityId}", "title"),
                    ]),
                UiElement.Metric("metric-turn", "Turn", 0.96, "cool"),
                UiElement.Metric("metric-memory", "Memory", Math.Min(1.0, memory * 0.07), "cool"),
                UiElement.Metric("metric-pressure", "Pressure", 0.42, "warm"),
                UiElement.Metric("metric-heat", "Heat", 0.34, "warm"),
                UiElement.Metric("metric-load", "Load", 0.99, "danger"),
                UiElement.Metric("metric-speed", "Speed", 0.88, "cool"),
                UiElement.Text("selected-face-detail", $"memory {memory:0} pressures {NumberValue(counts, "pressures") ?? 0:0} constraints {NumberValue(selectedAgent, "constraintCount") ?? 0:0}\n{Truncate(description, 220)}", "body"),
            ]);
    }

    private DashboardUiElement BuildStateGraphPane(IReadOnlyList<VoidBotStateLeaf> leaves)
    {
        return UiElement.Pane(
            "state-graph-pane",
            "State Graph",
            leaves.Select((leaf, index) => UiElement.Card(
                $"state-leaf-card-{index}",
                "state-leaf",
                bindNodeId: $"state-{index}",
                commandId: $"select-state:{leaf.Path}",
                style: new DashboardUiStyle { Variant = string.Equals(leaf.Path, selectedStatePath, StringComparison.Ordinal) ? "selected" : "default" },
                children:
                [
                    UiElement.Text($"state-leaf-label-{index}", leaf.Label, "strong"),
                    UiElement.Text($"state-leaf-preview-{index}", leaf.Preview, "caption"),
                ])).ToArray());
    }

    private static DashboardUiElement BuildStateDetailPane(DashboardState state, VoidBotStateLeaf? selectedLeaf)
    {
        return UiElement.Pane(
            "state-detail-pane",
            "State Detail",
            [
                UiElement.Text("state-detail-heading", selectedLeaf != null ? $"{selectedLeaf.Label}\n{selectedLeaf.Path}" : state.Title, "strong"),
                UiElement.Text("state-detail-body", selectedLeaf?.Detail ?? state.Nodes.FirstOrDefault(static node => node.Id == "voidbot-summary")?.Detail ?? "No state detail.", "mono"),
            ]);
    }

    private void AddCtbRail(List<DashboardNode> nodes, IReadOnlyList<JsonElement> upcoming)
    {
        var count = Math.Min(8, upcoming.Count);
        for (var index = 0; index < count; index++)
        {
            var turn = upcoming[index];
            var identityId = StringValue(turn, "identityId") ?? "";
            var active = !string.IsNullOrWhiteSpace(StringValue(turn, "activeJobId"));
            var mentionCount = NumberValue(turn, "pendingMentionCount") ?? 0;
            nodes.Add(new DashboardNode(
                $"ctb-{index}-{identityId}",
                $"{index + 1}. {StringValue(turn, "displayName") ?? identityId}\n{StringValue(turn, "repoName") ?? "repo"}",
                "ctb-turn",
                -0.82 + (index * 0.235),
                -0.82,
                0.21,
                0.11,
                active ? "active" : mentionCount > 0 ? "mention" : Minutes(NumberValue(turn, "nextTurnInMinutes")))
            {
                IdentityId = identityId,
                AvatarUrl = StringValue(turn, "avatarUrl"),
                Detail = $"speed {NumberValue(turn, "effectiveSpeed") ?? 0:0.###} heat {NumberValue(turn, "heat") ?? 0:0.###}",
            });
        }
    }

    private void AddAgentCards(List<DashboardNode> nodes, IReadOnlyList<JsonElement> participants)
    {
        var visible = participants
            .OrderBy(participant => NumberValue(participant, "nextTurnInMinutes") ?? 999999)
            .Take(10)
            .ToArray();

        for (var index = 0; index < visible.Length; index++)
        {
            var agent = visible[index];
            var identityId = StringValue(agent, "identityId") ?? $"agent-{index}";
            var column = index % 5;
            var row = index / 5;
            var active = !string.IsNullOrWhiteSpace(StringValue(agent, "activeJobId"));
            var selected = string.Equals(identityId, selectedIdentityId, StringComparison.Ordinal);
            nodes.Add(new DashboardNode(
                $"agent-{identityId}",
                $"{StringValue(agent, "displayName") ?? identityId}\n{StringValue(agent, "repoName") ?? "repo"}",
                "agent",
                -0.74 + (column * 0.37),
                -0.28 + (row * 0.22),
                0.31,
                0.17,
                selected ? "selected" : active ? "active" : Minutes(NumberValue(agent, "nextTurnInMinutes")))
            {
                IdentityId = identityId,
                AvatarUrl = StringValue(agent, "avatarUrl"),
                Detail = $"load {NumberValue(agent, "currentLoad") ?? 0:0.##}  heat {NumberValue(agent, "heat") ?? 0:0.##}",
            });
        }
    }

    private void AddSelectedAgent(List<DashboardNode> nodes, JsonElement agent)
    {
        if (agent.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        var identityId = StringValue(agent, "identityId") ?? "";
        var faceState = TryGet(agent, "faceState");
        var counts = TryGet(faceState, "counts");
        var description = StringValue(agent, "description") ?? "No Face description registered.";
        nodes.Add(new DashboardNode(
            "agent-detail",
            $"{StringValue(agent, "displayName") ?? identityId} State\n{StringValue(agent, "repoName") ?? "repo"}",
            "state-detail",
            -0.42,
            0.52,
            0.55,
            0.30,
            BoolValue(faceState, "readable") == true ? "readable" : "unreadable")
        {
            IdentityId = identityId,
            AvatarUrl = StringValue(agent, "avatarUrl"),
            Detail = $"memory {NumberValue(counts, "memory") ?? 0:0} pressures {NumberValue(counts, "pressures") ?? 0:0} constraints {NumberValue(agent, "constraintCount") ?? 0:0}\n{Truncate(description, 150)}",
        });

        var leaves = FlattenLeaves(ArrayItems(TryGet(faceState, "tree")).ToArray()).Take(5).ToArray();
        if (string.IsNullOrWhiteSpace(selectedStatePath))
        {
            selectedStatePath = leaves.FirstOrDefault()?.Path ?? "";
        }

        for (var index = 0; index < leaves.Length; index++)
        {
            var leaf = leaves[index];
            var selected = string.Equals(leaf.Path, selectedStatePath, StringComparison.Ordinal);
            nodes.Add(new DashboardNode(
                $"state-{index}",
                $"{leaf.Label}\n{leaf.Preview}",
                "state-leaf",
                0.34,
                0.24 + (index * 0.13),
                0.34,
                0.10,
                selected ? "open" : "leaf")
            {
                IdentityId = identityId,
                StatePath = leaf.Path,
                Detail = leaf.Detail,
            });
        }

        var selectedLeaf = leaves.FirstOrDefault(leaf => string.Equals(leaf.Path, selectedStatePath, StringComparison.Ordinal));
        if (selectedLeaf != null && !string.IsNullOrWhiteSpace(selectedLeaf.Path))
        {
            nodes.Add(new DashboardNode(
                "state-detail",
                $"{selectedLeaf.Label}\n{selectedLeaf.Path}",
                "state-text",
                0.38,
                0.74,
                0.38,
                0.18,
                "detail")
            {
                IdentityId = identityId,
                StatePath = selectedLeaf.Path,
                Detail = Truncate(selectedLeaf.Detail, 280),
            });
        }
    }

    private DashboardState BuildMissingState() => new()
    {
        ProviderId = ProviderId,
        Title = "VoidBot Swarm",
        SelectedNodeId = "voidbot-missing",
        Nodes =
        [
            new DashboardNode("voidbot-missing", "VoidBot Swarm\nsnapshot missing", "swarm", 0.0, -0.10, 0.52, 0.24, "missing")
            {
                Detail = swarmStatePath,
            },
        ],
    };

    private DashboardState BuildErrorState(string error) => new()
    {
        ProviderId = ProviderId,
        Title = "VoidBot Swarm",
        SelectedNodeId = "voidbot-error",
        Nodes =
        [
            new DashboardNode("voidbot-error", "VoidBot Swarm\nstate read failed", "swarm", 0.0, -0.10, 0.52, 0.24, "error")
            {
                Detail = error,
            },
        ],
    };

    private static IEnumerable<VoidBotStateLeaf> FlattenLeaves(IReadOnlyList<JsonElement> nodes)
    {
        foreach (var node in nodes)
        {
            if (StringValue(node, "kind") == "leaf")
            {
                yield return new VoidBotStateLeaf(
                    StringValue(node, "label") ?? "leaf",
                    StringValue(node, "path") ?? "",
                    StringValue(node, "preview") ?? "",
                    StringValue(node, "detail") ?? StringValue(node, "preview") ?? "");
            }

            foreach (var child in FlattenLeaves(ArrayItems(TryGet(node, "children")).ToArray()))
            {
                yield return child;
            }
        }
    }

    private static JsonElement TryGet(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var child))
        {
            return child;
        }

        return default;
    }

    private static IEnumerable<JsonElement> ArrayItems(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in element.EnumerateArray())
        {
            yield return item;
        }
    }

    private static string? StringValue(JsonElement element, string property) => StringValue(TryGet(element, property));

    private static string? StringValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static double? NumberValue(JsonElement element, string property) => NumberValue(TryGet(element, property));

    private static double? NumberValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? value : null;

    private static bool? BoolValue(JsonElement element, string property)
    {
        var child = TryGet(element, property);
        return child.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string Minutes(double? value)
    {
        if (value == null)
        {
            return "unknown";
        }

        return value <= 0 ? "ready" : $"{value:0.#}m";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "...";

    private static string StableId(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        return builder.ToString().Trim('-');
    }

    private static string ShortIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.ToLocalTime().ToString("MM-dd HH:mm")
            : value;
    }
}

internal sealed record VoidBotStateLeaf(string Label, string Path, string Preview, string Detail);

internal sealed class YggdrasilStreamPixelsProvider : MutableDashboardProvider
{
    public const string ProviderId = "yggdrasil.streampixels.edge";

    public YggdrasilStreamPixelsProvider()
        : base(
            new DashboardProviderManifest(
                ProviderId,
                "StreamPixels Edge",
                "Yggdrasil/StreamPixels live edge control surface reachable over HTTPS or SSH-forwarded localhost.",
                "1",
                "/eve/deck/yggdrasil.streampixels.edge",
                ["status", "health", "ssh-tunnel", "hls"],
                UsesCultMesh: false,
                Transport: "TCP/WebSocket over LAN or SSH tunnel."),
            new DashboardState
            {
                ProviderId = ProviderId,
                Title = "StreamPixels Edge",
                SelectedNodeId = "hls-origin",
                Nodes =
                [
                    new DashboardNode("hls-origin", "HLS Origin", "service", -0.34, -0.18, 0.34, 0.22, "streampixels"),
                    new DashboardNode("rtmp-ingest", "RTMP Ingest", "service", 0.34, -0.18, 0.34, 0.22, "localhost"),
                    new DashboardNode("ssh-tunnel", "Yggdrasil SSH Tunnel", "transport", 0.0, 0.34, 0.44, 0.18, "tcp only"),
                ],
            })
    {
    }
}

internal sealed class RemoteDashboardProvider : MutableDashboardProvider
{
    private RemoteDashboardProvider(DashboardProviderManifest manifest, string upstream)
        : base(
            manifest,
            new DashboardState
            {
                ProviderId = manifest.Id,
                Title = manifest.Title,
                SelectedNodeId = "remote-endpoint",
                Nodes =
                [
                    new DashboardNode("remote-endpoint", manifest.Title, "remote-dashboard", 0.0, -0.10, 0.46, 0.24, "registered")
                    {
                        Endpoint = upstream,
                    },
                    new DashboardNode("remote-transport", "Transport", "transport", 0.0, 0.34, 0.40, 0.18, "tcp/ws")
                    {
                        Endpoint = upstream,
                    },
                ],
            })
    {
    }

    public static RemoteDashboardProvider? FromSpec(string spec)
    {
        var parts = spec.Split('|', 3, StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]))
        {
            Console.Error.WriteLine($"Ignoring dashboard provider spec. Expected id|title|ws-url, got: {spec}");
            return null;
        }

        return new RemoteDashboardProvider(
            new DashboardProviderManifest(
                parts[0],
                parts[1],
                "External EveDeck dashboard provider registered at broker launch.",
                "1",
                parts[2],
                ["scene2d", "external-provider"],
                UsesCultMesh: false,
                Transport: "registered WebSocket endpoint"),
            parts[2]);
    }
}

internal sealed record HttpRequest(string Path, IReadOnlyDictionary<string, string> Headers);

internal sealed record WebSocketFrame(int Opcode, byte[] Payload);

internal sealed record DashboardSocket(TcpClient Client, NetworkStream Stream, bool CultMeshBinary);

internal sealed record DashboardProviderCatalogDocument(IReadOnlyList<DashboardProviderManifest> Providers);

internal sealed record DashboardProviderManifest(
    string Id,
    string Title,
    string Description,
    string Version,
    string Endpoint,
    string[] Capabilities,
    bool UsesCultMesh,
    string Transport);

internal sealed class DashboardState
{
    public string Type { get; set; } = "dashboard-state";

    public string Schema { get; set; } = "mimir.eve_dashboard_state.v1";

    public string ProviderId { get; set; } = MimirStreamLayoutProvider.ProviderId;

    public string Title { get; set; } = "Mimir Dashboard";

    public long Version { get; set; } = 1;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string SelectedNodeId { get; set; } = "program";

    public string LutPreset { get; set; } = "neutral";

    public List<DashboardNode> Nodes { get; set; } = [];

    public DashboardSurface? Surface { get; set; }
}

internal sealed class DashboardSurface
{
    public string Schema { get; set; } = "cultmesh.eve_surface.v0";

    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public DashboardUiElement Root { get; set; } = UiElement.Container("root", "root", new DashboardUiLayout(), []);

    public IReadOnlyList<DashboardSurfaceAsset> Assets { get; set; } = [];
}

internal sealed record DashboardSurfaceAsset(string Id, string Kind, string Uri);

internal sealed class DashboardUiBinding
{
    public string DocumentSchema { get; set; } = "";

    public string DocumentId { get; set; } = "";

    public string Path { get; set; } = "";

    public string ValueKind { get; set; } = "";

    public string Access { get; set; } = "read";

    public string Authority { get; set; } = "";

    public string? CommandId { get; set; }
}

internal sealed class DashboardUiElement
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    public string? Role { get; set; }

    public string? Text { get; set; }

    public string? AssetRef { get; set; }

    public string? AssetUri { get; set; }

    public string? BindNodeId { get; set; }

    public string? CommandId { get; set; }

    public DashboardUiBinding? Binding { get; set; }

    public DashboardUiLayout? Layout { get; set; }

    public DashboardUiStyle? Style { get; set; }

    public DashboardUiMetric? Metric { get; set; }

    public IReadOnlyList<DashboardUiElement> Children { get; set; } = [];
}

internal sealed class DashboardUiLayout
{
    public string Direction { get; set; } = "vertical";

    public double? Width { get; set; }

    public double? Height { get; set; }

    public double? Grow { get; set; }

    public double? Gap { get; set; }

    public double? Padding { get; set; }

    public string? Overflow { get; set; }

    public double? MinWidth { get; set; }

    public double? MinHeight { get; set; }

    public double? PreferredWidth { get; set; }

    public double? PreferredHeight { get; set; }

    public double? Priority { get; set; }

    public string? Density { get; set; }

    public string? ViewportMode { get; set; }
}

internal sealed class DashboardUiStyle
{
    public string Variant { get; set; } = "default";

    public string? Tone { get; set; }
}

internal sealed record DashboardUiMetric(string Label, double Value, string Tone);

internal static class UiElement
{
    public static DashboardUiElement Container(string id, string kind, DashboardUiLayout layout, IReadOnlyList<DashboardUiElement> children)
    {
        return new DashboardUiElement { Id = id, Kind = kind, Layout = layout, Children = children };
    }

    public static DashboardUiElement Pane(string id, string title, IReadOnlyList<DashboardUiElement> children)
    {
        return new DashboardUiElement
        {
            Id = id,
            Kind = "pane",
            Text = title,
            Layout = new DashboardUiLayout { Direction = "vertical", Gap = 10, Padding = 12, Grow = 1 },
            Children = children,
        };
    }

    public static DashboardUiElement Card(
        string id,
        string role,
        string? bindNodeId = null,
        string? commandId = null,
        DashboardUiStyle? style = null,
        IReadOnlyList<DashboardUiElement>? children = null)
    {
        return new DashboardUiElement
        {
            Id = id,
            Kind = "card",
            Role = role,
            BindNodeId = bindNodeId,
            CommandId = commandId,
            Style = style,
            Children = children ?? [],
        };
    }

    public static DashboardUiElement Avatar(string id, string? avatarUrl, string alt)
    {
        return new DashboardUiElement
        {
            Id = id,
            Kind = "avatar",
            Text = alt,
            AssetUri = avatarUrl,
            Layout = new DashboardUiLayout { Width = 42, Height = 42 },
        };
    }

    public static DashboardUiElement Text(string id, string text, string role)
    {
        return new DashboardUiElement { Id = id, Kind = "text", Role = role, Text = text };
    }

    public static DashboardUiElement Metric(string id, string label, double value, string tone)
    {
        return new DashboardUiElement
        {
            Id = id,
            Kind = "metric",
            Metric = new DashboardUiMetric(label, Math.Clamp(value, 0.0, 1.0), tone),
        };
    }
}

internal sealed class DashboardNode(
    string id,
    string label,
    string kind,
    double defaultX,
    double defaultY,
    double width,
    double height,
    string health)
{
    public string Id { get; set; } = id;

    public string Label { get; set; } = label;

    public string Kind { get; set; } = kind;

    public bool Visible { get; set; } = true;

    public double X { get; set; } = defaultX;

    public double Y { get; set; } = defaultY;

    public double DefaultX { get; set; } = defaultX;

    public double DefaultY { get; set; } = defaultY;

    public double Z { get; set; }

    public double Rotation { get; set; }

    public double Scale { get; set; } = 1.0;

    public double Width { get; set; } = width;

    public double Height { get; set; } = height;

    public string Health { get; set; } = health;

    public string? ProviderId { get; set; }

    public string? Command { get; set; }

    public string? Endpoint { get; set; }

    public string? IdentityId { get; set; }

    public string? AvatarUrl { get; set; }

    public string? StatePath { get; set; }

    public string? Detail { get; set; }
}

internal sealed class DashboardCommand
{
    public string Type { get; set; } = "";

    public string NodeId { get; set; } = "";

    public string? ProviderId { get; set; }

    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Rotation { get; set; }

    public double? Scale { get; set; }

    public bool? Visible { get; set; }
}
