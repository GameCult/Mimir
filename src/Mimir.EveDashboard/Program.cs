using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var port = ParseInt(args, "--port", 8795);
var providers = EveDashboardProviderCatalog.Create(ParseProviderSpecs(args));
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
        while (!stopping.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
            _ = Task.Run(() => HandleAsync(client));
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

        if (!IsDashboardPath(request.Path, out var requestedProviderId) ||
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
        var socket = new DashboardSocket(tcpClient, stream);
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

    private static bool IsDashboardPath(string path, out string providerId)
    {
        providerId = "";
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
        await SendTextFrameAsync(socket.Stream, JsonSerializer.Serialize(state, JsonOptions)).ConfigureAwait(false);
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

    public static EveDashboardProviderCatalog Create(IReadOnlyList<string> remoteSpecs)
    {
        var providers = new List<IDashboardProvider>();
        var remoteProviders = remoteSpecs.Select(RemoteDashboardProvider.FromSpec).OfType<RemoteDashboardProvider>().ToArray();
        providers.Add(new DashboardBrokerProvider(remoteProviders));
        providers.Add(new MimirStreamLayoutProvider());
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
            new("provider-mimir", "Mimir Stream Layout", "dashboard-provider", -0.42, -0.34, 0.34, 0.22, "local")
            {
                ProviderId = MimirStreamLayoutProvider.ProviderId,
                Command = "open-provider",
            },
            new("provider-streampixels", "StreamPixels Edge", "dashboard-provider", 0.38, -0.34, 0.34, 0.22, "ssh ready")
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
                new DashboardNode("program", "Program Output", "camera", -0.05, -0.05, 0.62, 0.34, "live"),
                new DashboardNode("raven-display", "Raven Display", "screen", -0.58, -0.50, 0.34, 0.20, "waiting"),
                new DashboardNode("eve-camera", "Eve Camera", "camera", 0.46, -0.48, 0.30, 0.22, "armed"),
                new DashboardNode("kiyo-pro", "Kiyo Pro", "camera", -0.42, 0.42, 0.24, 0.18, "live"),
                new DashboardNode("leap-field", "Leap Field", "sensor", 0.38, 0.40, 0.28, 0.24, "calibrating"),
            ],
        };
}

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

internal sealed record DashboardSocket(TcpClient Client, NetworkStream Stream);

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
