using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameCult.Caching;
using GameCult.Mesh;
using GameCult.Networking;
using MessagePack;
using Mimir.Runtime.Synchronization;

var options = RavenDaemonOptions.Parse(args);
Directory.CreateDirectory(options.LogRoot);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.CultMeshCachePath)) ?? ".");

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

var command = RavenMuxCommand.Build(options);
await using var state = await RavenCaptureMuxState.OpenAsync(options, command.CommandLine).ConfigureAwait(false);
using var server = new RavenDaemonEveServer(options, state);

if (options.DryRun)
{
    await state.PublishAsync(state.Document with
    {
        Status = "dry-run",
        UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        CommandLine = command.CommandLine,
        Notes =
        [
            "Dry run only; no capture process was started.",
            "FFmpeg owns display capture, Realtek loopback capture, mux, encode, and SRT transport.",
            "Mimir.RavenDaemon owns supervision and CultMesh/Eve status only.",
        ],
    }, stopping.Token).ConfigureAwait(false);
    Console.WriteLine(command.CommandLine);
    return;
}

Console.Error.WriteLine($"Mimir Raven daemon state cache {options.CultMeshCachePath}");
Console.Error.WriteLine($"Mimir Raven daemon speaking Eve/CultMesh on ws://0.0.0.0:{options.EvePort}/eve/deck");
Console.Error.WriteLine($"Mimir Raven mux target {command.Endpoint}");

var serverTask = server.RunAsync(stopping.Token);
var supervisorTask = RavenMuxSupervisor.RunAsync(options, command, state, stopping.Token);
await Task.WhenAll(serverTask, supervisorTask).ConfigureAwait(false);

internal sealed record RavenDaemonOptions(
    string DaemonId,
    string HostId,
    string TargetHost,
    int Port,
    int EvePort,
    string CultMeshCachePath,
    string LogRoot,
    int Width,
    int Height,
    int Framerate,
    int AudioSampleRate,
    int AudioChannels,
    string VideoCapture,
    int DdagrabOutputIndex,
    string Transport,
    string SrtMode,
    string FfmpegPath,
    string Source,
    string VideoBitrate,
    string AudioBitrate,
    string WasapiLoopbackPath,
    string AudioDevice,
    bool StartCultNetServer,
    bool Once,
    bool DryRun)
{
    public static RavenDaemonOptions Parse(string[] args)
    {
        return new RavenDaemonOptions(
            DaemonId: Text(args, "--daemon-id", "mimir.raven.capture-mux"),
            HostId: Text(args, "--host-id", "raven"),
            TargetHost: Text(args, "--target-host", "10.77.0.2"),
            Port: Int(args, "--port", 5200),
            EvePort: Int(args, "--eve-port", 8801),
            CultMeshCachePath: Text(args, "--cultmesh-cache", @"C:\Meta\Mimir\state\raven-capture-mux.ccmp"),
            LogRoot: Text(args, "--log-root", @"C:\Meta\Mimir\logs"),
            Width: Int(args, "--width", 1920),
            Height: Int(args, "--height", 1080),
            Framerate: Int(args, "--framerate", 30),
            AudioSampleRate: Int(args, "--audio-sample-rate", 48000),
            AudioChannels: Int(args, "--audio-channels", 2),
            VideoCapture: Choice(args, "--video-capture", "ddagrab", ["ddagrab", "gdigrab"]),
            DdagrabOutputIndex: Int(args, "--ddagrab-output-index", 0),
            Transport: Choice(args, "--transport", "srt", ["srt", "tcp-listener"]),
            SrtMode: Choice(args, "--srt-mode", "caller", ["caller", "listener"]),
            FfmpegPath: Text(args, "--ffmpeg", "ffmpeg"),
            Source: Text(args, "--source", "desktop"),
            VideoBitrate: Text(args, "--video-bitrate", "12000k"),
            AudioBitrate: Text(args, "--audio-bitrate", "192k"),
            WasapiLoopbackPath: Text(args, "--wasapi-loopback", ""),
            AudioDevice: Text(args, "--audio-device", "Realtek"),
            StartCultNetServer: !Flag(args, "--no-cultnet-server"),
            Once: Flag(args, "--once"),
            DryRun: Flag(args, "--dry-run"));
    }

    private static bool Flag(string[] args, string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static string Text(string[] args, string name, string fallback)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    private static int Int(string[] args, string name, int fallback) =>
        int.TryParse(Text(args, name, ""), out var value) ? value : fallback;

    private static string Choice(string[] args, string name, string fallback, string[] allowed)
    {
        var value = Text(args, name, fallback);
        if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(name, value, $"Expected one of: {string.Join(", ", allowed)}");
        }

        return value.ToLowerInvariant();
    }
}

internal sealed record RavenMuxCommand(string CommandLine, string Endpoint, string StdoutLog, string StderrLog)
{
    public static RavenMuxCommand Build(RavenDaemonOptions options)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var stdoutLog = Path.Combine(options.LogRoot, $"raven-av-srt-{timestamp}.out.log");
        var stderrLog = Path.Combine(options.LogRoot, $"raven-av-srt-{timestamp}.err.log");
        var endpoint = options.Transport == "tcp-listener"
            ? $"tcp://{options.TargetHost}:{options.Port}?listen=1"
            : $"srt://{options.TargetHost}:{options.Port}?mode={options.SrtMode}&latency=120000&timeout=30000000";
        var repoRoot = FindRepoRoot();
        var loopback = ResolveLoopbackCommand(options, repoRoot);
        var ffmpeg = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "warning",
        };

        if (options.VideoCapture == "ddagrab")
        {
            ffmpeg.AddRange([
                "-thread_queue_size",
                "1024",
                "-f",
                "lavfi",
                "-i",
                $"ddagrab=framerate={options.Framerate}:output_idx={options.DdagrabOutputIndex}:draw_mouse=1",
            ]);
        }
        else
        {
            ffmpeg.AddRange([
                "-thread_queue_size",
                "1024",
                "-f",
                "gdigrab",
                "-framerate",
                options.Framerate.ToString(),
                "-video_size",
                $"{options.Width}x{options.Height}",
                "-i",
                options.Source,
            ]);
        }

        var gop = Math.Max(1, options.Framerate * 2);
        ffmpeg.AddRange([
            "-thread_queue_size",
            "1024",
            "-f",
            "f32le",
            "-ar",
            options.AudioSampleRate.ToString(),
            "-ac",
            options.AudioChannels.ToString(),
            "-i",
            "pipe:0",
            "-map",
            "0:v:0",
            "-map",
            "1:a:0",
            "-c:v",
            "h264_nvenc",
            "-preset",
            "p4",
            "-tune",
            "ll",
            "-b:v",
            options.VideoBitrate,
            "-maxrate",
            options.VideoBitrate,
            "-bufsize",
            "24000k",
            "-g",
            gop.ToString(),
        ]);
        if (options.VideoCapture == "gdigrab")
        {
            ffmpeg.AddRange(["-pix_fmt", "yuv420p"]);
        }

        ffmpeg.AddRange([
            "-c:a",
            "aac",
            "-b:a",
            options.AudioBitrate,
            "-ar",
            options.AudioSampleRate.ToString(),
            "-ac",
            options.AudioChannels.ToString(),
            "-f",
            "mpegts",
            endpoint,
        ]);

        var command = loopback + " | " + Quote(options.FfmpegPath) + " " + string.Join(" ", ffmpeg.Select(Quote));
        return new RavenMuxCommand(command, endpoint, stdoutLog, stderrLog);
    }

    private static string ResolveLoopbackCommand(RavenDaemonOptions options, string repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(options.WasapiLoopbackPath))
        {
            return LoopbackExeCommand(options.WasapiLoopbackPath, options);
        }

        var exe = Path.Combine(repoRoot, "tools", "Mimir.WasapiLoopback", "Mimir.WasapiLoopback.exe");
        if (File.Exists(exe))
        {
            return LoopbackExeCommand(exe, options);
        }

        var script = Path.Combine(repoRoot, "scripts", "wasapi-loopback-capture.ps1");
        return string.Join(" ", [
            "powershell.exe",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            Quote(script),
            "-Output",
            Quote("stdout"),
            "-SampleRate",
            options.AudioSampleRate.ToString(),
            "-Channels",
            options.AudioChannels.ToString(),
        ]);
    }

    private static string LoopbackExeCommand(string exe, RavenDaemonOptions options)
    {
        var parts = new List<string>
        {
            Quote(exe),
            "--output",
            "stdout",
            "--sample-rate",
            options.AudioSampleRate.ToString(),
            "--channels",
            options.AudioChannels.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(options.AudioDevice))
        {
            parts.Add("--device");
            parts.Add(Quote(options.AudioDevice));
        }

        return string.Join(" ", parts);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mimir.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

internal sealed class RavenCaptureMuxState : IAsyncDisposable
{
    private readonly CultMeshNode node;
    private readonly CultRecordKey key;
    private readonly SemaphoreSlim gate = new(1, 1);

    private RavenCaptureMuxState(CultMeshNode node, CultRecordKey key, MimirRavenCaptureMuxStateDocument document)
    {
        this.node = node;
        this.key = key;
        Document = document;
    }

    public MimirRavenCaptureMuxStateDocument Document { get; private set; }

    public static async Task<RavenCaptureMuxState> OpenAsync(RavenDaemonOptions options, string commandLine)
    {
        var registry = new CultNetDocumentRegistry()
            .Register(CultNetDocumentBinding.ForDocument<MimirRavenCaptureMuxStateDocument>())
            .Register(CultNetDocumentBinding.ForDocument<MimirEveDashboardStateDocument>());
        var node = await CultMesh.CreateNodeAsync(options.CultMeshCachePath, new CultMeshNodeOptions
        {
            StartServer = options.StartCultNetServer,
            DatabaseOptions = new CultNetDatabaseOptions
            {
                RuntimeId = options.DaemonId,
                DocumentRegistry = registry,
            },
        }).ConfigureAwait(false);
        var key = new CultRecordKey(options.DaemonId);
        var document = InitialDocument(options, commandLine);
        var state = new RavenCaptureMuxState(node, key, document);
        await state.PublishAsync(document, CancellationToken.None).ConfigureAwait(false);
        return state;
    }

    public async Task PublishAsync(MimirRavenCaptureMuxStateDocument document, CancellationToken stopping)
    {
        await gate.WaitAsync(stopping).ConfigureAwait(false);
        try
        {
            Document = document;
            await node.Database.PutAsync(key, document).ConfigureAwait(false);
            await node.Database.PutAsync(new CultRecordKey(optionsKey(document.DaemonId, "dashboard")), RavenDaemonEveServer.ToCultMeshState(document)).ConfigureAwait(false);
            await node.FlushAsync(soft: true).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        static string optionsKey(string daemonId, string suffix) => daemonId + "." + suffix;
    }

    public async ValueTask DisposeAsync()
    {
        gate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
        node.Dispose();
    }

    private static MimirRavenCaptureMuxStateDocument InitialDocument(RavenDaemonOptions options, string commandLine) =>
        new(
            options.DaemonId,
            DateTimeOffset.UtcNow.ToString("O"),
            options.HostId,
            "starting",
            $"{options.Transport}://{options.TargetHost}:{options.Port}",
            options.Transport,
            "raven-display",
            "raven-realtk-loopback",
            options.VideoCapture,
            options.Width,
            options.Height,
            options.Framerate,
            options.AudioSampleRate,
            options.AudioChannels,
            null,
            null,
            0,
            null,
            "",
            "",
            commandLine,
            [
                "display-capture",
                "wasapi-render-loopback",
                "ffmpeg-mux",
                "srt-mpegts",
                "cultmesh-eve-status",
            ],
            [
                "Raven daemon supervises the capture process and publishes typed state.",
                "FFmpeg owns media capture, mux, encode, and transport.",
            ]);
}

internal static class RavenMuxSupervisor
{
    public static async Task RunAsync(RavenDaemonOptions options, RavenMuxCommand command, RavenCaptureMuxState state, CancellationToken stopping)
    {
        var restartCount = state.Document.RestartCount;
        while (!stopping.IsCancellationRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(command.StdoutLog) ?? ".");
            await using var stdout = new StreamWriter(new FileStream(command.StdoutLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            await using var stderr = new StreamWriter(new FileStream(command.StderrLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            using var process = StartMuxProcess(command, stdout, stderr);
            var started = DateTimeOffset.UtcNow;
            await state.PublishAsync(state.Document with
            {
                Status = "running",
                UpdatedAtUtc = started.ToString("O"),
                FfmpegPid = process.Id,
                StartedAtUtc = started.ToString("O"),
                RestartCount = restartCount,
                LastExitCode = null,
                StdoutLog = command.StdoutLog,
                StderrLog = command.StderrLog,
                CommandLine = command.CommandLine,
            }, stopping).ConfigureAwait(false);

            while (!process.HasExited && !stopping.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stopping).ConfigureAwait(false);
                await state.PublishAsync(state.Document with
                {
                    UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                    Status = "running",
                    FfmpegPid = process.Id,
                }, stopping).ConfigureAwait(false);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var exitCode = process.ExitCode;
            await state.PublishAsync(state.Document with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                Status = stopping.IsCancellationRequested ? "stopped" : "exited",
                FfmpegPid = null,
                LastExitCode = exitCode,
            }, CancellationToken.None).ConfigureAwait(false);
            if (options.Once || stopping.IsCancellationRequested)
            {
                return;
            }

            restartCount++;
            await state.PublishAsync(state.Document with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                Status = "restarting",
                RestartCount = restartCount,
            }, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 + restartCount)), stopping).ConfigureAwait(false);
        }
    }

    private static Process StartMuxProcess(RavenMuxCommand command, StreamWriter stdout, StreamWriter stderr)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                ArgumentList = { "/d", "/s", "/c", command.CommandLine },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
            {
                lock (stdout)
                {
                    stdout.WriteLine(eventArgs.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
            {
                lock (stderr)
                {
                    stderr.WriteLine(eventArgs.Data);
                }
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }
}

internal sealed class RavenDaemonEveServer(RavenDaemonOptions options, RavenCaptureMuxState state) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TcpListener listener = new(IPAddress.Any, options.EvePort);
    private readonly CancellationTokenSource stopping = new();

    public async Task RunAsync(CancellationToken outerStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerStopping, stopping.Token);
        listener.Start();
        while (!linked.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client, linked.Token), linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        await using var stream = client.GetStream();
        var request = await HttpWire.ReadRequestAsync(stream).ConfigureAwait(false);
        if (request.Path == "/health")
        {
            var document = state.Document;
            var health = JsonSerializer.Serialize(new
            {
                ok = document.Status is "running" or "dry-run",
                providerId = options.DaemonId,
                host = document.HostId,
                status = document.Status,
                target = document.TargetEndpoint,
                video = document.VideoSourceId,
                audio = document.AudioSourceId,
                ffmpegPid = document.FfmpegPid,
                restartCount = document.RestartCount,
                cultCache = options.CultMeshCachePath,
                cultMeshDocument = "mimir.raven_capture_mux_state",
            }, JsonOptions);
            await HttpWire.WriteResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(health)).ConfigureAwait(false);
            return;
        }

        if (request.Path == "/eve/deck/manifest")
        {
            await HttpWire.WriteResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Manifest(), JsonOptions))).ConfigureAwait(false);
            return;
        }

        if (request.Path == "/eve/deck/providers")
        {
            await HttpWire.WriteResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { providers = new[] { Manifest() } }, JsonOptions))).ConfigureAwait(false);
            return;
        }

        if (!IsDashboardPath(request.Path, out var binary) || !request.Headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await HttpWire.WriteResponseAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
            return;
        }

        await HttpWire.WriteWebSocketHandshakeAsync(stream, key).ConfigureAwait(false);
        while (!token.IsCancellationRequested)
        {
            var dashboard = ToCultMeshState(state.Document);
            if (binary)
            {
                await HttpWire.SendBinaryFrameAsync(stream, MessagePackSerializer.Serialize(dashboard)).ConfigureAwait(false);
            }
            else
            {
                await HttpWire.SendTextFrameAsync(stream, JsonSerializer.Serialize(dashboard, JsonOptions)).ConfigureAwait(false);
            }

            await Task.Delay(1000, token).ConfigureAwait(false);
        }
    }

    public static MimirEveDashboardStateDocument ToCultMeshState(MimirRavenCaptureMuxStateDocument doc)
    {
        var statusTone = doc.Status == "running" ? "live" : doc.Status == "dry-run" ? "diagnostic" : "waiting";
        return new MimirEveDashboardStateDocument(
            doc.DaemonId + ".dashboard",
            "Mimir Raven Capture Mux",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            doc.UpdatedAtUtc,
            "raven-daemon",
            "terminal",
            [
                new("raven-daemon", $"Raven Daemon\n{doc.Status}", "daemon", true, 0.0, -0.36, 0.0, 0.0, 1.0, 0.54, 0.18, statusTone, doc.DaemonId, null, "/health"),
                new("raven-display", $"Display\n{doc.Width}x{doc.Height}@{doc.Framerate}", "video", true, -0.34, 0.02, 0.0, 0.0, 1.0, 0.42, 0.18, doc.VideoCapture, doc.DaemonId, null, null),
                new("raven-loopback", $"Realtek Loopback\n{doc.AudioChannels}ch {doc.AudioSampleRate}Hz", "audio", true, 0.34, 0.02, 0.0, 0.0, 1.0, 0.42, 0.18, "wasapi-loopback", doc.DaemonId, null, null),
                new("raven-target", $"Mux Target\n{doc.TargetEndpoint}", "transport", true, 0.0, 0.38, 0.0, 0.0, 1.0, 0.72, 0.18, doc.Transport, doc.DaemonId, null, null),
            ],
            new MimirEveDashboardSurfaceSnapshot(
                "mimir.eve_surface.v1",
                "mimir.raven-capture-mux.surface",
                "Raven Capture Mux",
                Pane("raven-root", "Raven Capture Mux",
                [
                    Text("raven-status", $"status {doc.Status}\nffmpeg pid {doc.FfmpegPid?.ToString() ?? "none"}\nrestarts {doc.RestartCount}"),
                    Text("raven-media", $"video {doc.VideoSourceId} {doc.VideoCapture} {doc.Width}x{doc.Height}@{doc.Framerate}\naudio {doc.AudioSourceId} {doc.AudioChannels}ch {doc.AudioSampleRate}Hz"),
                    Text("raven-target-text", $"target {doc.TargetEndpoint}\nstdout {doc.StdoutLog}\nstderr {doc.StderrLog}"),
                ]),
                []));
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
        stopping.Dispose();
    }

    private DashboardProviderManifest Manifest() =>
        new(
            options.DaemonId,
            "Mimir Raven Capture Mux",
            "Raven-local supervisor for display capture plus Realtek render loopback muxed through FFmpeg and exposed as typed CultMesh/Eve state.",
            "0.1.0",
            $"ws://raven:{options.EvePort}/eve/deck",
            ["health", "eve-deck", "cultmesh-state", "raven-display", "raven-realtk-loopback"],
            UsesCultMesh: true,
            Transport: "CultMesh typed state + Eve WebSocket; FFmpeg/SRT owns media transport.");

    private static bool IsDashboardPath(string path, out bool binary)
    {
        binary = path == "/eve/deck/cultmesh";
        return path == "/eve/deck" || binary;
    }

    private static MimirEveDashboardUiElementSnapshot Pane(string id, string text, MimirEveDashboardUiElementSnapshot[] children) =>
        new(id, "pane", null, text, null, null, null, null, new("column", null, null, 1.0, 1.0, 1.0, "clip", Density: "compact"), new("panel", "neutral"), null, children);

    private static MimirEveDashboardUiElementSnapshot Text(string id, string text) =>
        new(id, "text", null, text, null, null, null, null, new("column", null, null, 0.0, 0.0, 0.0, "clip", Density: "compact"), new("label", "neutral"), null, []);
}

internal sealed record DashboardProviderManifest(
    string Id,
    string Title,
    string Description,
    string Version,
    string Endpoint,
    string[] Capabilities,
    bool UsesCultMesh,
    string Transport);

internal static class HttpWire
{
    public static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var lines = text.Split(["\r\n"], StringSplitOptions.None);
        var first = lines.FirstOrDefault() ?? "";
        var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new HttpRequest(parts.Length >= 2 ? parts[1] : "/", headers);
    }

    public static async Task WriteResponseAsync(NetworkStream stream, string status, string contentType, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    public static async Task WriteWebSocketHandshakeAsync(NetworkStream stream, string key)
    {
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response).ConfigureAwait(false);
    }

    public static Task SendTextFrameAsync(NetworkStream stream, string text) =>
        SendFrameAsync(stream, 0x1, Encoding.UTF8.GetBytes(text));

    public static Task SendBinaryFrameAsync(NetworkStream stream, byte[] payload) =>
        SendFrameAsync(stream, 0x2, payload);

    private static async Task SendFrameAsync(NetworkStream stream, byte opcode, byte[] payload)
    {
        var header = new List<byte> { (byte)(0x80 | opcode) };
        if (payload.Length <= 125)
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
}

internal sealed record HttpRequest(string Path, IReadOnlyDictionary<string, string> Headers);
