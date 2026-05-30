using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var port = ParseInt(args, "--port", 8792);
var text = ParseString(args, "--text", "Eve, this is Mimir. If you can see this, blink in packets.");
using var server = new EveDialogueServer(port, text);
await server.RunAsync();

static string ParseString(IReadOnlyList<string> args, string name, string fallback)
{
    for (var index = 0; index < args.Count - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return fallback;
}

static int ParseInt(IReadOnlyList<string> args, string name, int fallback) =>
    int.TryParse(ParseString(args, name, ""), out var value) ? value : fallback;

internal sealed class EveDialogueServer(int port, string text) : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Any, port);
    private readonly CancellationTokenSource stopping = new();

    public async Task RunAsync()
    {
        listener.Start();
        Console.WriteLine($"Eve dialogue listening on ws://0.0.0.0:{port}/stream");
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
        Console.WriteLine($"EVE connected: {tcpClient.Client.RemoteEndPoint} {request.Path}");
        Console.Out.Flush();
        if (request.Path == "/health")
        {
            var health = JsonSerializer.Serialize(new { ok = true, text });
            await WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(health)).ConfigureAwait(false);
            return;
        }

        if (request.Path != "/stream" || !request.Headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
            return;
        }

        await WriteWebSocketHandshakeAsync(stream, key).ConfigureAwait(false);
        await SendTextFrameAsync(stream, JsonSerializer.Serialize(new
        {
            type = "config",
            codec = "dialogue",
            width = 1620,
            height = 2160,
            deviceScaleFactor = 2.0,
            fps = 1,
        })).ConfigureAwait(false);
        await SendTextFrameAsync(stream, JsonSerializer.Serialize(new
        {
            type = "dialogue",
            text,
            sentAt = DateTimeOffset.UtcNow,
        })).ConfigureAwait(false);

        while (!stopping.IsCancellationRequested)
        {
            var frame = await ReceiveFrameAsync(stream).ConfigureAwait(false);
            if (frame.Opcode == 0x8)
            {
                await WriteCloseFrameAsync(stream).ConfigureAwait(false);
                break;
            }

            if (frame.Opcode == 0x1)
            {
                Console.WriteLine($"EVE says: {Encoding.UTF8.GetString(frame.Payload)}");
                Console.Out.Flush();
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
            throw new InvalidDataException("Dialogue frame too large.");
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
            throw new InvalidDataException("Dialogue frame is too large.");
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
