using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

var config = EveRelayConfig.Parse(args);
using var relay = await MimirEveRelay.StartAsync(config);
await relay.RunAsync();

internal sealed class MimirEveRelay : IDisposable
{
    private readonly EveRelayConfig config;
    private readonly D3D12SharedTextureReader textureReader;
    private readonly HardwareH264Encoder encoder;
    private readonly EveWebSocketServer server;
    private readonly CancellationTokenSource stopping = new();

    private MimirEveRelay(
        EveRelayConfig config,
        D3D12SharedTextureReader textureReader,
        HardwareH264Encoder encoder,
        EveWebSocketServer server)
    {
        this.config = config;
        this.textureReader = textureReader;
        this.encoder = encoder;
        this.server = server;
    }

    public static async Task<MimirEveRelay> StartAsync(EveRelayConfig config)
    {
        var textureReader = D3D12SharedTextureReader.Open(config.SharedTextureName, config.Width, config.Height);
        var server = new EveWebSocketServer(config, textureReader.Width, textureReader.Height);
        var encoder = HardwareH264Encoder.Start(config, textureReader.Width, textureReader.Height, server.BroadcastBinaryAsync);
        var relay = new MimirEveRelay(config, textureReader, encoder, server);
        await server.StartAsync();
        Console.WriteLine($"Mimir Eve relay listening: ws://{config.LanHost}:{config.Port}/stream");
        Console.WriteLine($"Source: {config.SharedTextureName} {textureReader.Width}x{textureReader.Height}");
        Console.WriteLine($"Codec: h264-annexb via {config.Encoder}");
        return relay;
    }

    public async Task RunAsync()
    {
        var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(config.Fps, 1, 120));
        var nextFrame = Stopwatch.GetTimestamp();
        var stopwatchFrequency = Stopwatch.Frequency;
        var frames = 0L;
        var lastReport = Stopwatch.GetTimestamp();

        while (!stopping.IsCancellationRequested)
        {
            var now = Stopwatch.GetTimestamp();
            if (now < nextFrame)
            {
                var delay = TimeSpan.FromSeconds((nextFrame - now) / (double)stopwatchFrequency);
                await Task.Delay(delay, stopping.Token).ConfigureAwait(false);
            }

            textureReader.CopyNextFrameTo(encoder.Input);
            await encoder.Input.FlushAsync(stopping.Token).ConfigureAwait(false);
            frames++;
            nextFrame += (long)(frameInterval.TotalSeconds * stopwatchFrequency);

            var reportNow = Stopwatch.GetTimestamp();
            if ((reportNow - lastReport) / (double)stopwatchFrequency >= 2.0)
            {
                Console.WriteLine($"Mimir Eve relay: frames={frames} clients={server.ClientCount} encodedBytes={encoder.EncodedBytes}");
                lastReport = reportNow;
            }
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        encoder.Dispose();
        server.Dispose();
        textureReader.Dispose();
        stopping.Dispose();
    }
}

internal sealed class HardwareH264Encoder : IDisposable
{
    private readonly Process process;
    private readonly Task outputTask;
    private readonly Task errorTask;
    private readonly H264AnnexBAccessUnitFramer framer = new();
    private long encodedBytes;

    private HardwareH264Encoder(Process process, Func<byte[], Task> broadcast)
    {
        this.process = process;
        Input = process.StandardInput.BaseStream;
        outputTask = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (!process.HasExited)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                foreach (var accessUnit in framer.Push(buffer.AsSpan(0, read).ToArray()))
                {
                    Interlocked.Add(ref encodedBytes, accessUnit.Length);
                    await broadcast(accessUnit).ConfigureAwait(false);
                }
            }
        });
        errorTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Console.Error.WriteLine($"ffmpeg: {line}");
                }
            }
        });
    }

    public Stream Input { get; }

    public long EncodedBytes => Interlocked.Read(ref encodedBytes);

    public static HardwareH264Encoder Start(
        EveRelayConfig config,
        int width,
        int height,
        Func<byte[], Task> broadcast)
    {
        var ffmpeg = ResolveFfmpeg(config.FfmpegPath);
        var arguments = BuildArguments(config, width, height);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.Start();
        return new HardwareH264Encoder(process, broadcast);
    }

    public void Dispose()
    {
        try
        {
            Input.Dispose();
        }
        catch
        {
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        Task.WaitAll([outputTask, errorTask], TimeSpan.FromSeconds(1));
        process.Dispose();
    }

    private static string BuildArguments(EveRelayConfig config, int width, int height)
    {
        var common = $"-hide_banner -loglevel warning -f rawvideo -pix_fmt bgra -s {width}x{height} -r {config.Fps} -i pipe:0 -an ";
        return config.Encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase)
            ? common + $"-c:v libx264 -preset ultrafast -tune zerolatency -x264-params keyint={config.Fps}:min-keyint={config.Fps}:scenecut=0 -pix_fmt yuv420p -f h264 pipe:1"
            : common + $"-c:v h264_nvenc -preset p1 -tune ull -rc constqp -qp {config.Quality} -g {config.Fps} -bf 0 -pix_fmt yuv420p -f h264 pipe:1";
    }

    private static string ResolveFfmpeg(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var candidate in new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
            @"C:\Users\Meta\scoop\shims\ffmpeg.exe",
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("ffmpeg.exe was not found. Install FFmpeg or pass --ffmpeg <path>.");
    }
}

internal sealed unsafe class D3D12SharedTextureReader : IDisposable
{
    private const int BytesPerPixel = 4;

    private readonly ID3D12Device device;
    private readonly ID3D12CommandQueue queue;
    private readonly ID3D12CommandAllocator allocator;
    private readonly ID3D12GraphicsCommandList commandList;
    private readonly ID3D12Fence fence;
    private readonly AutoResetEvent fenceEvent = new(false);
    private readonly ID3D12Resource texture;
    private readonly ID3D12Resource readback;
    private readonly int sourceRowBytes;
    private readonly int readbackRowPitch;
    private ulong fenceValue;

    private D3D12SharedTextureReader(
        ID3D12Device device,
        ID3D12CommandQueue queue,
        ID3D12CommandAllocator allocator,
        ID3D12GraphicsCommandList commandList,
        ID3D12Fence fence,
        ID3D12Resource texture,
        ID3D12Resource readback,
        int width,
        int height,
        int readbackRowPitch)
    {
        this.device = device;
        this.queue = queue;
        this.allocator = allocator;
        this.commandList = commandList;
        this.fence = fence;
        this.texture = texture;
        this.readback = readback;
        Width = width;
        Height = height;
        this.readbackRowPitch = readbackRowPitch;
        sourceRowBytes = checked(width * BytesPerPixel);
    }

    public int Width { get; }

    public int Height { get; }

    public static D3D12SharedTextureReader Open(string sharedTextureName, int configuredWidth, int configuredHeight)
    {
        var device = D3D12.D3D12CreateDevice<ID3D12Device>(IntPtr.Zero, FeatureLevel.Level_11_0);
        device.Name = "Mimir Eve Relay D3D12 Device";
        var queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        var allocator = device.CreateCommandAllocator(CommandListType.Direct);
        var commandList = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
        commandList.Close();
        var fence = device.CreateFence(0);
        var sharedHandle = device.OpenSharedHandleByName(sharedTextureName);
        var texture = device.OpenSharedHandle<ID3D12Resource>(sharedHandle);
        var description = texture.Description;
        var width = checked((int)description.Width);
        var height = checked((int)description.Height);
        var rowPitch = Align(checked(width * BytesPerPixel), D3D12.TextureDataPitchAlignment);
        var readback = device.CreateCommittedResource(
            HeapType.Readback,
            ResourceDescription.Buffer((ulong)(rowPitch * height)),
            ResourceStates.CopyDest,
            null);
        readback.Name = "Mimir Eve Relay Readback";
        return new D3D12SharedTextureReader(device, queue, allocator, commandList, fence, texture, readback, width, height, rowPitch);
    }

    public void CopyNextFrameTo(Stream destination)
    {
        allocator.Reset();
        commandList.Reset(allocator, null);
        var source = new TextureCopyLocation(texture, 0);
        var dest = new TextureCopyLocation(
            readback,
            new PlacedSubresourceFootPrint
            {
                Offset = 0,
                Footprint = new SubresourceFootPrint(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, (uint)readbackRowPitch),
            });
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            texture,
            ResourceStates.Common,
            ResourceStates.CopySource));
        commandList.CopyTextureRegion(dest, 0, 0, 0, source, null);
        commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            texture,
            ResourceStates.CopySource,
            ResourceStates.Common));
        commandList.Close();
        queue.ExecuteCommandList(commandList);
        WaitForGpu();

        var mapped = readback.Map<byte>(0);
        try
        {
            for (var row = 0; row < Height; row++)
            {
                destination.Write(new ReadOnlySpan<byte>(mapped + (row * readbackRowPitch), sourceRowBytes));
            }
        }
        finally
        {
            readback.Unmap(0, null);
        }
    }

    public void Dispose()
    {
        WaitForGpu();
        readback.Dispose();
        texture.Dispose();
        fence.Dispose();
        commandList.Dispose();
        allocator.Dispose();
        queue.Dispose();
        device.Dispose();
        fenceEvent.Dispose();
    }

    private void WaitForGpu()
    {
        var signalValue = ++fenceValue;
        queue.Signal(fence, signalValue);
        if (fence.CompletedValue < signalValue)
        {
            fence.SetEventOnCompletion(signalValue, fenceEvent.SafeWaitHandle.DangerousGetHandle());
            fenceEvent.WaitOne();
        }
    }

    private static int Align(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }
}

internal sealed class EveWebSocketServer : IDisposable
{
    private readonly EveRelayConfig config;
    private readonly int width;
    private readonly int height;
    private readonly TcpListener listener;
    private readonly ConcurrentDictionary<Guid, EveSocketClient> clients = new();
    private readonly CancellationTokenSource stopping = new();

    public EveWebSocketServer(EveRelayConfig config, int width, int height)
    {
        this.config = config;
        this.width = width;
        this.height = height;
        listener = new TcpListener(IPAddress.Any, config.Port);
    }

    public int ClientCount => clients.Count;

    public Task StartAsync()
    {
        listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    public async Task BroadcastBinaryAsync(byte[] payload)
    {
        foreach (var (id, client) in clients)
        {
            try
            {
                await client.SendBinaryAsync(payload).ConfigureAwait(false);
            }
            catch
            {
                clients.TryRemove(id, out _);
                client.Dispose();
            }
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
        foreach (var socket in clients.Values)
        {
            socket.Dispose();
        }

        stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            var tcpClient = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
            _ = Task.Run(() => HandleAsync(tcpClient));
        }
    }

    private async Task HandleAsync(TcpClient tcpClient)
    {
        await using var stream = tcpClient.GetStream();
        var request = await ReadHttpRequestAsync(stream).ConfigureAwait(false);
        if (request.Path == "/health")
        {
            var health = JsonSerializer.Serialize(new
            {
                ok = true,
                codec = "h264-annexb",
                width,
                height,
                config.DeviceScaleFactor,
                clients = clients.Count,
            });
            await WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(health)).ConfigureAwait(false);
            return;
        }

        if (request.Path != "/stream" || !request.Headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
            return;
        }

        await WriteWebSocketHandshakeAsync(stream, key).ConfigureAwait(false);
        var id = Guid.NewGuid();
        using var client = new EveSocketClient(tcpClient, stream);
        clients[id] = client;
        await SendConfigAsync(client).ConfigureAwait(false);
        await ReceivePointerLoopAsync(id, client).ConfigureAwait(false);
    }

    private Task SendConfigAsync(EveSocketClient socket)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "config",
            codec = "h264-annexb",
            width,
            height,
            config.DeviceScaleFactor,
            fps = config.Fps,
        });
        return socket.SendTextAsync(json);
    }

    private async Task ReceivePointerLoopAsync(Guid id, EveSocketClient socket)
    {
        try
        {
            while (true)
            {
                var message = await socket.ReceiveAsync().ConfigureAwait(false);
                if (message.Opcode == 0x8)
                {
                    break;
                }

                if (message.Opcode == 0x1)
                {
                    var json = Encoding.UTF8.GetString(message.Payload);
                    Console.WriteLine($"EVE input: {json}");
                }
            }
        }
        finally
        {
            clients.TryRemove(id, out _);
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
}

internal sealed class EveSocketClient : IDisposable
{
    private readonly TcpClient tcpClient;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public EveSocketClient(TcpClient tcpClient, NetworkStream stream)
    {
        this.tcpClient = tcpClient;
        this.stream = stream;
    }

    public Task SendTextAsync(string text)
    {
        return SendFrameAsync(0x1, Encoding.UTF8.GetBytes(text));
    }

    public Task SendBinaryAsync(byte[] payload)
    {
        return SendFrameAsync(0x2, payload);
    }

    public async Task<WebSocketFrame> ReceiveAsync()
    {
        var header0 = stream.ReadByte();
        var header1 = stream.ReadByte();
        if (header0 < 0 || header1 < 0)
        {
            throw new IOException("WebSocket closed.");
        }

        var opcode = header0 & 0x0f;
        var masked = (header1 & 0x80) != 0;
        ulong length = (ulong)(header1 & 0x7f);
        if (length == 126)
        {
            var ext = await ReadExactAsync(2).ConfigureAwait(false);
            length = (ulong)((ext[0] << 8) | ext[1]);
        }
        else if (length == 127)
        {
            var ext = await ReadExactAsync(8).ConfigureAwait(false);
            length = 0;
            foreach (var b in ext)
            {
                length = (length << 8) | b;
            }
        }

        var mask = masked ? await ReadExactAsync(4).ConfigureAwait(false) : [];
        var payload = await ReadExactAsync(checked((int)length)).ConfigureAwait(false);
        if (masked)
        {
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i & 3];
            }
        }

        return new WebSocketFrame(opcode, payload);
    }

    public void Dispose()
    {
        sendLock.Dispose();
        stream.Dispose();
        tcpClient.Dispose();
    }

    private async Task SendFrameAsync(byte opcode, byte[] payload)
    {
        await sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var header = new List<byte> { (byte)(0x80 | opcode) };
            if (payload.Length <= 125)
            {
                header.Add((byte)payload.Length);
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                header.Add(126);
                header.Add((byte)((payload.Length >> 8) & 0xff));
                header.Add((byte)(payload.Length & 0xff));
            }
            else
            {
                header.Add(127);
                var length = (ulong)payload.Length;
                for (var shift = 56; shift >= 0; shift -= 8)
                {
                    header.Add((byte)((length >> shift) & 0xff));
                }
            }

            await stream.WriteAsync(header.ToArray()).ConfigureAwait(false);
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task<byte[]> ReadExactAsync(int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset)).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("WebSocket closed.");
            }

            offset += read;
        }

        return buffer;
    }
}

internal sealed record HttpRequest(string Path, IReadOnlyDictionary<string, string> Headers);

internal sealed record WebSocketFrame(int Opcode, byte[] Payload);

internal sealed class H264AnnexBAccessUnitFramer
{
    private readonly List<byte> buffer = [];
    private readonly List<byte[]> currentAccessUnit = [];
    private bool currentHasSlice;

    public IEnumerable<byte[]> Push(byte[] chunk)
    {
        buffer.AddRange(chunk);
        while (TryPopNal(out var nal))
        {
            var nalType = nal.Length == 0 ? 0 : nal[0] & 0x1f;
            var isSlice = nalType is 1 or 5;
            var startsNewAccessUnit = nalType == 9 || (isSlice && currentHasSlice);
            if (startsNewAccessUnit && currentAccessUnit.Count > 0)
            {
                yield return BuildCurrentAccessUnit();
                currentAccessUnit.Clear();
                currentHasSlice = false;
            }

            currentAccessUnit.Add(nal);
            currentHasSlice |= isSlice;
        }
    }

    private byte[] BuildCurrentAccessUnit()
    {
        var byteCount = currentAccessUnit.Sum(nal => nal.Length + 4);
        var output = new byte[byteCount];
        var offset = 0;
        foreach (var nal in currentAccessUnit)
        {
            output[offset++] = 0;
            output[offset++] = 0;
            output[offset++] = 0;
            output[offset++] = 1;
            nal.CopyTo(output.AsSpan(offset));
            offset += nal.Length;
        }

        return output;
    }

    private bool TryPopNal(out byte[] nal)
    {
        nal = [];
        var firstStart = FindStartCode(buffer, 0, out var firstStartLength);
        if (firstStart < 0)
        {
            buffer.Clear();
            return false;
        }

        if (firstStart > 0)
        {
            buffer.RemoveRange(0, firstStart);
            firstStart = 0;
        }

        var payloadStart = firstStart + firstStartLength;
        var nextStart = FindStartCode(buffer, payloadStart, out _);
        if (nextStart < 0)
        {
            return false;
        }

        nal = buffer.GetRange(payloadStart, nextStart - payloadStart).ToArray();
        buffer.RemoveRange(0, nextStart);
        return nal.Length > 0;
    }

    private static int FindStartCode(List<byte> bytes, int offset, out int length)
    {
        for (var index = offset; index < bytes.Count - 3; index++)
        {
            if (bytes[index] == 0 && bytes[index + 1] == 0 && bytes[index + 2] == 1)
            {
                length = 3;
                return index;
            }

            if (index < bytes.Count - 4 && bytes[index] == 0 && bytes[index + 1] == 0 && bytes[index + 2] == 0 && bytes[index + 3] == 1)
            {
                length = 4;
                return index;
            }
        }

        length = 0;
        return -1;
    }
}

internal sealed record EveRelayConfig(
    string SharedTextureName,
    int Width,
    int Height,
    int Fps,
    int Port,
    string LanHost,
    double DeviceScaleFactor,
    string Encoder,
    int Quality,
    string? FfmpegPath)
{
    public static EveRelayConfig Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[index][2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            values[key] = value;
        }

        return new EveRelayConfig(
            StringValue(values, "shared-texture", "Global\\MimirFensalirProgramTexture"),
            IntValue(values, "width", 0),
            IntValue(values, "height", 0),
            IntValue(values, "fps", 30),
            IntValue(values, "port", 8792),
            StringValue(values, "lan-host", "192.168.1.66"),
            DoubleValue(values, "scale", 2.0),
            StringValue(values, "encoder", "h264_nvenc"),
            IntValue(values, "quality", 26),
            values.GetValueOrDefault("ffmpeg"));
    }

    private static string StringValue(IReadOnlyDictionary<string, string> values, string name, string fallback) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static int IntValue(IReadOnlyDictionary<string, string> values, string name, int fallback) =>
        values.TryGetValue(name, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static double DoubleValue(IReadOnlyDictionary<string, string> values, string name, double fallback) =>
        values.TryGetValue(name, out var value) && double.TryParse(value, out var parsed) ? parsed : fallback;
}
