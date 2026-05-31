using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var port = ParseInt(args, "--port", 8795);
using var server = new EveDashboardServer(port);
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

internal sealed class EveDashboardServer(int port) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TcpListener listener = new(IPAddress.Any, port);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Guid, DashboardSocket> clients = new();
    private readonly DashboardState state = DashboardState.CreateDefault();

    public async Task RunAsync()
    {
        listener.Start();
        Console.WriteLine($"Mimir Eve dashboard listening on ws://0.0.0.0:{port}/eve/dashboard");
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
            var health = JsonSerializer.Serialize(new { ok = true, clients = clients.Count, state.Version }, JsonOptions);
            await WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(health)).ConfigureAwait(false);
            return;
        }

        if (request.Path != "/eve/dashboard" || !request.Headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
            return;
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

        if (command == null || string.IsNullOrWhiteSpace(command.NodeId))
        {
            return false;
        }

        var node = state.Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, command.NodeId, StringComparison.Ordinal));
        if (node == null)
        {
            return false;
        }

        switch ((command.Type ?? "").Trim().ToLowerInvariant())
        {
            case "select":
                state.SelectedNodeId = node.Id;
                break;
            case "move":
                state.SelectedNodeId = node.Id;
                node.X = Clamp(command.X ?? node.X, -1.0, 1.0);
                node.Y = Clamp(command.Y ?? node.Y, -1.0, 1.0);
                break;
            case "scale":
                state.SelectedNodeId = node.Id;
                node.Scale = Clamp(command.Scale ?? node.Scale, 0.25, 3.0);
                break;
            case "rotate":
                state.SelectedNodeId = node.Id;
                node.Rotation = command.Rotation ?? node.Rotation;
                break;
            case "toggle-visibility":
                state.SelectedNodeId = node.Id;
                node.Visible = command.Visible ?? !node.Visible;
                break;
            case "reset-transform":
                state.SelectedNodeId = node.Id;
                node.X = node.DefaultX;
                node.Y = node.DefaultY;
                node.Rotation = 0.0;
                node.Scale = 1.0;
                break;
            default:
                return false;
        }

        state.Version++;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        Console.WriteLine($"EVE dashboard command: {text}");
        Console.Out.Flush();
        return true;
    }

    private async Task SendStateAsync(DashboardSocket socket)
    {
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

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

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

internal sealed record HttpRequest(string Path, IReadOnlyDictionary<string, string> Headers);

internal sealed record WebSocketFrame(int Opcode, byte[] Payload);

internal sealed record DashboardSocket(TcpClient Client, NetworkStream Stream);

internal sealed class DashboardState
{
    public string Type { get; set; } = "dashboard-state";

    public long Version { get; set; } = 1;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string SelectedNodeId { get; set; } = "program";

    public string LutPreset { get; set; } = "neutral";

    public List<DashboardNode> Nodes { get; set; } = [];

    public static DashboardState CreateDefault()
    {
        return new DashboardState
        {
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
}

internal sealed class DashboardCommand
{
    public string Type { get; set; } = "";

    public string NodeId { get; set; } = "";

    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Rotation { get; set; }

    public double? Scale { get; set; }

    public bool? Visible { get; set; }
}
