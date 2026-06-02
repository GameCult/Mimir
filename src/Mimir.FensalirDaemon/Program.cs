using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using Mimir.Runtime.Synchronization;

var options = FensalirDaemonOptions.Parse(args);
Directory.CreateDirectory(Path.GetDirectoryName(options.CultCachePath) ?? ".");
await using var state = await FensalirDaemonState.OpenAsync(options).ConfigureAwait(false);
using var server = new FensalirDaemonEveServer(options, state);

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

Console.Error.WriteLine($"Mimir Fensalir daemon drinking Well from {options.WellLogPath}");
Console.Error.WriteLine($"Mimir Fensalir daemon state cache {options.CultCachePath}");
Console.Error.WriteLine($"Mimir Fensalir daemon speaking Eve/CultMesh on ws://0.0.0.0:{options.Port}/eve/deck");

var serveTask = server.RunAsync(stopping.Token);
await state.RunAsync(stopping.Token).ConfigureAwait(false);
await serveTask.ConfigureAwait(false);
return 0;

internal sealed record FensalirDaemonOptions(
    string DaemonId,
    string VerseId,
    string WellLogPath,
    string CultCachePath,
    int Port,
    int PollMs,
    int StatusSeconds,
    double GpuBudget,
    double CpuBudget,
    bool FromStart,
    bool Once)
{
    public static FensalirDaemonOptions Parse(IReadOnlyList<string> args)
    {
        var runtimeRoot = Path.Combine(RepoRoot(), "artifacts", "runtime");
        var defaultCache = Path.Combine(RepoRoot(), "state", "fensalir-daemon.ccmp");
        return new FensalirDaemonOptions(
            ParseString(args, "--daemon-id", "mimir-fensalir-daemon"),
            ParseString(args, "--verse-id", "mimir.local"),
            ParseString(args, "--well-log", FindLatestWellLog(runtimeRoot)),
            ParseString(args, "--cultcache", defaultCache),
            Math.Clamp(ParseInt(args, "--port", 8799), 1024, 65535),
            Math.Max(10, ParseInt(args, "--poll-ms", 100)),
            Math.Max(1, ParseInt(args, "--status-seconds", 5)),
            Math.Clamp(ParseDouble(args, "--gpu-budget", 0.50), 0.0, 1.0),
            Math.Clamp(ParseDouble(args, "--cpu-budget", 0.25), 0.0, 1.0),
            ParseBool(args, "--from-start", false),
            ParseBool(args, "--once", false));
    }

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(current.FullName, "Mimir.slnx")))
            {
                return current.FullName;
            }

            if (current.Parent == null)
            {
                break;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string FindLatestWellLog(string runtimeRoot)
    {
        if (!Directory.Exists(runtimeRoot))
        {
            return "";
        }

        return Directory.EnumerateFiles(runtimeRoot, "*.log", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(runtimeRoot, "*.jsonl", SearchOption.AllDirectories))
            .Select(static path => new FileInfo(path))
            .Where(static info => info.Length > 0)
            .OrderByDescending(static info => info.LastWriteTimeUtc)
            .Take(128)
            .Select(static info => info.FullName)
            .FirstOrDefault(ContainsWellDocument) ?? "";
    }

    private static bool ContainsWellDocument(string path)
    {
        try
        {
            return DaemonUtil.TailTextFile(path, 128 * 1024).Contains("mimir.well_", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string ParseString(IReadOnlyList<string> args, string name, string fallback)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                return args[index + 1];
            }

            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[index][(name.Length + 1)..];
            }
        }

        return fallback;
    }

    private static int ParseInt(IReadOnlyList<string> args, string name, int fallback) =>
        int.TryParse(ParseString(args, name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double ParseDouble(IReadOnlyList<string> args, string name, double fallback) =>
        double.TryParse(ParseString(args, name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool ParseBool(IReadOnlyList<string> args, string name, bool fallback)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return true;
            }

            return bool.TryParse(args[index + 1], out var value) ? value : fallback;
        }

        return fallback;
    }
}

internal sealed class FensalirDaemonState : IAsyncDisposable
{
    private readonly FensalirDaemonOptions options;
    private readonly CultCache cache;
    private readonly CultRecordHandle<MimirFensalirDaemonStateDocument> handle;
    private readonly WellLogTailState tail;
    private readonly object gate = new();
    private MimirFensalirDaemonStateDocument document;
    private DateTimeOffset lastStatus = DateTimeOffset.MinValue;

    private FensalirDaemonState(
        FensalirDaemonOptions options,
        CultCache cache,
        CultRecordHandle<MimirFensalirDaemonStateDocument> handle,
        MimirFensalirDaemonStateDocument document)
    {
        this.options = options;
        this.cache = cache;
        this.handle = handle;
        this.document = document;
        tail = new WellLogTailState(options.WellLogPath, options.FromStart);
    }

    public MimirFensalirDaemonStateDocument Document
    {
        get
        {
            lock (gate)
            {
                return document;
            }
        }
    }

    public static async Task<FensalirDaemonState> OpenAsync(FensalirDaemonOptions options)
    {
        var cache = await CultCacheMessagePack.OpenAsync(options.CultCachePath, new CultCacheOpenOptions
        {
            UseDirectoryStore = true,
            FlushOnDispose = true,
            StoreFlushOnDispose = true,
        }).ConfigureAwait(false);
        var key = new CultRecordKey(options.DaemonId);
        var existing = cache.Get<MimirFensalirDaemonStateDocument>(key);
        var document = existing ?? BuildInitialDocument(options);
        var handle = await cache.UpsertAsync(document, new CultRecordHandle<MimirFensalirDaemonStateDocument>(key)).ConfigureAwait(false);
        await cache.FlushAsync().ConfigureAwait(false);
        return new FensalirDaemonState(options, cache, handle, document);
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var touched = false;
            foreach (var json in tail.ReadNewDocuments())
            {
                using var _ = json;
                if (TryDrink(json.RootElement, out var next))
                {
                    lock (gate)
                    {
                        document = next;
                    }

                    touched = true;
                }
            }

            if (touched)
            {
                await cache.UpsertAsync(document, handle).ConfigureAwait(false);
                await cache.FlushAsync(soft: true).ConfigureAwait(false);
                if (options.Once)
                {
                    return;
                }
            }

            if (DateTimeOffset.UtcNow - lastStatus >= TimeSpan.FromSeconds(options.StatusSeconds))
            {
                lastStatus = DateTimeOffset.UtcNow;
                var state = Document;
                Console.Error.WriteLine(
                    $"mimir-fensalir-daemon status={state.Status} wellSeq={state.LastWellSequence} captureSeq={state.LastCaptureSequence} slices={state.ReadySlices}/{state.TotalSlices} offset={tail.Position}");
            }

            try
            {
                await Task.Delay(options.PollMs, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        cache.Dispose();
        return ValueTask.CompletedTask;
    }

    private bool TryDrink(JsonElement root, out MimirFensalirDaemonStateDocument next)
    {
        var kind = DaemonUtil.JsonText(root, "document") ?? "";
        var current = Document;
        next = current;
        if (kind == "mimir.well_snapshot.v1")
        {
            var buffers = DaemonUtil.JsonArray(DaemonUtil.JsonGet(root, "buffers")).ToArray();
            var frame = DaemonUtil.JsonGet(root, "synchronizedFrame");
            var slices = DaemonUtil.JsonArray(DaemonUtil.JsonGet(frame, "slices")).ToArray();
            var streamPressure = DaemonUtil.JsonGet(root, "streamPressure");
            var poll = DaemonUtil.JsonGet(streamPressure, "poll");
            var publish = DaemonUtil.JsonGet(streamPressure, "publish");
            var capture = DaemonUtil.JsonGet(root, "capture");
            var storage = DaemonUtil.JsonGet(capture, "storage");
            next = current with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                Status = "drinking-well",
                WellSource = string.IsNullOrWhiteSpace(options.WellLogPath) ? "missing" : options.WellLogPath,
                LastWellSequence = (long)DaemonUtil.JsonNumber(root, "sequence"),
                AudioSources = buffers.Count(static item => string.Equals(DaemonUtil.JsonText(item, "kind", "Kind"), "Audio", StringComparison.OrdinalIgnoreCase)),
                VideoSources = buffers.Count(static item => string.Equals(DaemonUtil.JsonText(item, "kind", "Kind"), "Video", StringComparison.OrdinalIgnoreCase)),
                ReadySlices = slices.Count(static slice => string.Equals(DaemonUtil.JsonText(slice, "status", "Status"), "Ready", StringComparison.OrdinalIgnoreCase)),
                TotalSlices = slices.Length,
                PresentationDelayMs = DaemonUtil.JsonNumber(frame, "presentationDelayMs", "PresentationDelayMs"),
                PollAverageMs = DaemonUtil.JsonNumber(poll, "averageMilliseconds"),
                PublishAverageMs = DaemonUtil.JsonNumber(publish, "averageMilliseconds"),
                Notes = [
                    $"capture-body-transport={DaemonUtil.JsonText(storage, "bodyTransport") ?? "unknown"}",
                    $"inline-bodies={DaemonUtil.JsonText(storage, "inlineBodies") ?? "unknown"}",
                    "reservoir-kernels=pending-owner-installed"
                ],
            };
            return true;
        }

        if (kind == "mimir.well_capture_page.v1")
        {
            next = current with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                Status = "drinking-capture-pages",
                WellSource = string.IsNullOrWhiteSpace(options.WellLogPath) ? "missing" : options.WellLogPath,
                LastCaptureSequence = (long)DaemonUtil.JsonNumber(root, "captureSequence"),
            };
            return true;
        }

        if (kind == "mimir.well_stream_pressure.v1")
        {
            var poll = DaemonUtil.JsonGet(root, "poll");
            var publish = DaemonUtil.JsonGet(root, "publish");
            next = current with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                Status = "observing-stream-pressure",
                PollAverageMs = DaemonUtil.JsonNumber(poll, "averageMilliseconds"),
                PublishAverageMs = DaemonUtil.JsonNumber(publish, "averageMilliseconds"),
            };
            return true;
        }

        return false;
    }

    private static MimirFensalirDaemonStateDocument BuildInitialDocument(FensalirDaemonOptions options) =>
        new(
            options.DaemonId,
            DateTimeOffset.UtcNow.ToString("O"),
            options.VerseId,
            string.IsNullOrWhiteSpace(options.WellLogPath) ? "waiting-for-well" : "booting",
            string.IsNullOrWhiteSpace(options.WellLogPath) ? "missing" : options.WellLogPath,
            -1,
            -1,
            0,
            0,
            0,
            0,
            0.0,
            0.0,
            0.0,
            options.GpuBudget,
            options.CpuBudget,
            "surface-owner-installed",
            ["cultcache-state", "cultnet-websocket", "cultmesh-eve-dashboard", "well-tail"],
            ["reservoir kernels pending"]);
}

internal sealed class FensalirDaemonEveServer(FensalirDaemonOptions options, FensalirDaemonState state) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TcpListener listener = new(IPAddress.Any, options.Port);
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
        var request = await DaemonUtil.ReadHttpRequestAsync(stream).ConfigureAwait(false);
        if (request.Path == "/health")
        {
            var health = JsonSerializer.Serialize(new
            {
                ok = true,
                providerId = options.DaemonId,
                verse = options.VerseId,
                status = state.Document.Status,
                cultCache = options.CultCachePath,
                cultMeshDocument = "mimir.fensalir_daemon_state",
            }, JsonOptions);
            await DaemonUtil.WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(health)).ConfigureAwait(false);
            return;
        }

        if (request.Path == "/eve/deck/manifest")
        {
            await DaemonUtil.WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Manifest(), JsonOptions))).ConfigureAwait(false);
            return;
        }

        if (!IsDashboardPath(request.Path, out var binary) || !request.Headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await DaemonUtil.WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
            return;
        }

        await DaemonUtil.WriteWebSocketHandshakeAsync(stream, key).ConfigureAwait(false);
        while (!token.IsCancellationRequested)
        {
            await SendStateAsync(stream, binary).ConfigureAwait(false);
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
    }

    private async Task SendStateAsync(NetworkStream stream, bool binary)
    {
        var document = state.Document;
        var dashboard = BuildDashboard(document);
        if (binary)
        {
            var cultMesh = ToCultMesh(dashboard);
            await DaemonUtil.SendBinaryFrameAsync(stream, MessagePackSerializer.Serialize(cultMesh)).ConfigureAwait(false);
            return;
        }

        await DaemonUtil.SendTextFrameAsync(stream, JsonSerializer.Serialize(dashboard, JsonOptions)).ConfigureAwait(false);
    }

    private DashboardState BuildDashboard(MimirFensalirDaemonStateDocument doc)
    {
        var readiness = doc.TotalSlices <= 0 ? 0.0 : doc.ReadySlices / (double)doc.TotalSlices;
        var pressure = Math.Clamp(Math.Max(doc.PollAverageMs / 16.0, doc.PublishAverageMs / 16.0), 0.0, 1.0);
        var nodes = new List<DashboardNode>
        {
            new("daemon", $"Fensalir Daemon\n{doc.Status}", "daemon", 0.0, -0.42, 0.70, 0.18, doc.Status),
            new("well", $"Well Drink\nseq {doc.LastWellSequence}", "well", -0.38, -0.08, 0.32, 0.18, doc.LastWellSequence >= 0 ? "live" : "waiting"),
            new("capture", $"Capture Pages\nseq {doc.LastCaptureSequence}", "cultcache", 0.0, -0.08, 0.32, 0.18, doc.LastCaptureSequence >= 0 ? "paged" : "waiting"),
            new("budget", $"Budget\nGPU {doc.GpuBudget:0.00} CPU {doc.CpuBudget:0.00}", "budget", 0.38, -0.08, 0.32, 0.18, "configured"),
        };
        return new DashboardState
        {
            ProviderId = options.DaemonId,
            Title = "Mimir Fensalir Daemon",
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAt = DateTimeOffset.UtcNow,
            SelectedNodeId = "daemon",
            LutPreset = "terminal",
            Nodes = nodes,
            Surface = new DashboardSurface
            {
                Id = "mimir.fensalir-daemon.surface",
                Title = "Fensalir Daemon",
                Root = Ui.Container("daemon-root", "dashboard", new UiLayout { Direction = "vertical", Gap = 6, Padding = 6, Overflow = "scroll", MinWidth = 96, MinHeight = 40, PreferredWidth = 150, PreferredHeight = 92, Density = "continuous" },
                [
                    Ui.Row("daemon-row-top",
                    [
                        Ui.Pane("daemon-pane", "Daemon",
                        [
                            Ui.Text("daemon-status", $"{DaemonUtil.Pulse()} {doc.Status}", "strong", Bind("/status")),
                            Ui.Text("daemon-owner", $"verse {doc.VerseId}\nstate {options.CultCachePath}", "caption", Bind("/verseId")),
                            Ui.Bar("daemon-readiness", "ready slices", readiness, DaemonUtil.Tone(readiness), $"{doc.ReadySlices}/{doc.TotalSlices}"),
                        ]),
                        Ui.Pane("well-pane", "Well",
                        [
                            Ui.Text("well-source", doc.WellSource, "caption", Bind("/wellSource")),
                            Ui.Text("well-seq", $"well seq {doc.LastWellSequence} capture seq {doc.LastCaptureSequence}", "mono", Bind("/lastWellSequence")),
                            Ui.Text("well-counts", $"audio {doc.AudioSources} video {doc.VideoSources} delay {doc.PresentationDelayMs:0.0}ms", "caption", Bind("/presentationDelayMs")),
                        ]),
                    ]),
                    Ui.Row("daemon-row-bottom",
                    [
                        Ui.Pane("budget-pane", "Budgets",
                        [
                            Ui.Bar("gpu-budget", "GPU share", doc.GpuBudget, "cool", $"{doc.GpuBudget:0.00}"),
                            Ui.Bar("cpu-budget", "CPU share", doc.CpuBudget, "warm", $"{doc.CpuBudget:0.00}"),
                            Ui.Text("worker-mode", doc.WorkerMode, "caption", Bind("/workerMode")),
                        ]),
                        Ui.Pane("pressure-pane", "Pressure",
                        [
                            Ui.Bar("pressure-bar", "publish/poll pressure", pressure, DaemonUtil.Tone(1.0 - pressure), $"{Math.Max(doc.PollAverageMs, doc.PublishAverageMs):0.000}ms"),
                            Ui.Text("pressure-detail", $"poll {doc.PollAverageMs:0.000}ms publish {doc.PublishAverageMs:0.000}ms", "caption", Bind("/pollAverageMs")),
                            Ui.Text("notes", string.Join("\n", doc.Notes.Take(4)), "caption", Bind("/notes")),
                        ]),
                    ]),
                ]),
            },
        };
    }

    private UiBinding Bind(string path) => new()
    {
        DocumentSchema = "mimir.fensalir_daemon_state.v1",
        DocumentId = options.DaemonId,
        Path = path,
        ValueKind = "state",
        Access = "read",
        Authority = "Mimir.FensalirDaemon",
    };

    private static MimirEveDashboardStateDocument ToCultMesh(DashboardState state) =>
        new(
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
                null,
                null,
                null)).ToArray(),
            ToCultMeshSurface(state.Surface));

    private static MimirEveDashboardSurfaceSnapshot ToCultMeshSurface(DashboardSurface surface) =>
        new(
            surface.Schema,
            surface.Id,
            surface.Title,
            ToCultMeshElement(surface.Root),
            []);

    private static MimirEveDashboardUiElementSnapshot ToCultMeshElement(UiElement element) =>
        new(
            element.Id,
            element.Kind,
            element.Role,
            element.Text,
            null,
            null,
            null,
            element.CommandId,
            element.Layout == null ? null : new MimirEveDashboardUiLayoutSnapshot(
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
            null,
            element.Metric == null ? null : new MimirEveDashboardUiMetricSnapshot(element.Metric.Label, element.Metric.Value, element.Metric.Tone),
            element.Children.Select(ToCultMeshElement).ToArray(),
            element.Binding == null ? null : new MimirEveDashboardUiBindingSnapshot(
                element.Binding.DocumentSchema,
                element.Binding.DocumentId,
                element.Binding.Path,
                element.Binding.ValueKind,
                element.Binding.Access,
                element.Binding.Authority,
                element.Binding.CommandId));

    private DashboardProviderManifest Manifest() =>
        new(
            options.DaemonId,
            "Mimir Fensalir Daemon",
            "Well-drinking Fensalir reservoir owner: CultCache state, CultNet/WebSocket transport, CultMesh/Eve surface.",
            "1",
            $"/eve/deck/{options.DaemonId}",
            ["cultcache-state", "cultnet-websocket", "cultmesh-eve-dashboard", "well-tail", "reservoir-owner"],
            UsesCultMesh: true,
            Transport: "CultCache .ccmp + Eve/CultMesh MessagePack WebSocket provider");

    private static bool IsDashboardPath(string path, out bool binary)
    {
        binary = path == "/eve/deck/cultmesh";
        return path is "/eve/deck" or "/eve/dashboard" or "/eve/deck/cultmesh";
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Dispose();
        stopping.Dispose();
    }
}

internal sealed class WellLogTailState
{
    private readonly string path;
    private long position;

    public WellLogTailState(string path, bool fromStart)
    {
        this.path = path;
        position = !fromStart && File.Exists(path) ? Math.Max(0, new FileInfo(path).Length - 1024 * 1024) : 0;
    }

    public long Position => position;

    public IEnumerable<JsonDocument> ReadNewDocuments()
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            yield break;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (position > stream.Length)
        {
            position = 0;
        }

        stream.Seek(position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument? parsed = null;
            try
            {
                parsed = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
            }

            if (parsed != null)
            {
                yield return parsed;
            }
        }

        position = stream.Position;
    }
}

internal sealed record HttpRequest(string Path, Dictionary<string, string> Headers);

internal static class Ui
{
    public static UiElement Container(string id, string kind, UiLayout layout, IReadOnlyList<UiElement> children) =>
        new() { Id = id, Kind = kind, Layout = layout, Children = children };

    public static UiElement Row(string id, IReadOnlyList<UiElement> children) =>
        Container(id, "row", new UiLayout { Direction = "horizontal", Gap = 8 }, children);

    public static UiElement Pane(string id, string title, IReadOnlyList<UiElement> children) =>
        new()
        {
            Id = id,
            Kind = "pane",
            Text = title,
            Layout = new UiLayout { Direction = "vertical", Gap = 8, Padding = 10, Grow = 1 },
            Children = children,
        };

    public static UiElement Text(string id, string text, string role, UiBinding? binding = null) =>
        new() { Id = id, Kind = "text", Role = role, Text = text, Binding = binding };

    public static UiElement Bar(string id, string label, double value, string tone, string display) =>
        new()
        {
            Id = id,
            Kind = "metric",
            Text = display,
            Metric = new UiMetric(label, Math.Clamp(value, 0.0, 1.0), tone),
        };
}

internal sealed class DashboardState
{
    public string Type { get; set; } = "dashboard-state";
    public string Schema { get; set; } = "mimir.eve_dashboard_state.v1";
    public string ProviderId { get; set; } = "";
    public string Title { get; set; } = "";
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string SelectedNodeId { get; set; } = "";
    public string LutPreset { get; set; } = "terminal";
    public List<DashboardNode> Nodes { get; set; } = [];
    public DashboardSurface Surface { get; set; } = new();
}

internal sealed class DashboardSurface
{
    public string Schema { get; set; } = "cultmesh.eve_surface.v0";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public UiElement Root { get; set; } = new();
}

internal sealed class DashboardNode(
    string id,
    string label,
    string kind,
    double x,
    double y,
    double width,
    double height,
    string health)
{
    public string Id { get; set; } = id;
    public string Label { get; set; } = label;
    public string Kind { get; set; } = kind;
    public bool Visible { get; set; } = true;
    public double X { get; set; } = x;
    public double Y { get; set; } = y;
    public double Z { get; set; }
    public double Rotation { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Width { get; set; } = width;
    public double Height { get; set; } = height;
    public string Health { get; set; } = health;
}

internal sealed class UiElement
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Role { get; set; }
    public string? Text { get; set; }
    public string? CommandId { get; set; }
    public UiBinding? Binding { get; set; }
    public UiLayout? Layout { get; set; }
    public UiMetric? Metric { get; set; }
    public IReadOnlyList<UiElement> Children { get; set; } = [];
}

internal sealed class UiLayout
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

internal sealed class UiBinding
{
    public string DocumentSchema { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string Path { get; set; } = "";
    public string ValueKind { get; set; } = "";
    public string Access { get; set; } = "read";
    public string Authority { get; set; } = "";
    public string? CommandId { get; set; }
}

internal sealed record UiMetric(string Label, double Value, string Tone);

internal sealed record DashboardProviderManifest(
    string Id,
    string Title,
    string Description,
    string Version,
    string Endpoint,
    string[] Capabilities,
    bool UsesCultMesh,
    string Transport);

internal static class DaemonUtil
{
    public static string Tone(double value) =>
        value >= 0.70 ? "cool" : value >= 0.25 ? "warm" : "danger";

    public static string Pulse() =>
        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 250 % 4) switch
        {
            0 => "|",
            1 => "/",
            2 => "-",
            _ => "\\",
        };

    public static JsonElement JsonGet(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value;
            }
        }

        return default;
    }

    public static IReadOnlyList<JsonElement> JsonArray(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToArray() : [];

    public static string? JsonText(JsonElement element, params string[] names)
    {
        var value = names.Length == 0 ? element : JsonGet(element, names);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static double JsonNumber(JsonElement element, params string[] names)
    {
        var value = names.Length == 0 ? element : JsonGet(element, names);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0.0;
    }

    public static string TailTextFile(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var start = Math.Max(0, length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static async Task<HttpRequest> ReadHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(buffer, 0, count);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var path = "/";
        var first = lines.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(first))
        {
            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                path = parts[1];
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return new HttpRequest(path, headers);
    }

    public static async Task WriteHttpResponseAsync(NetworkStream stream, string status, string contentType, byte[] body)
    {
        var header = Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    public static async Task WriteWebSocketHandshakeAsync(NetworkStream stream, string key)
    {
        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
        var accept = Convert.ToBase64String(hash);
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response).ConfigureAwait(false);
    }

    public static async Task SendTextFrameAsync(NetworkStream stream, string text) =>
        await SendFrameAsync(stream, 0x1, Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);

    public static async Task SendBinaryFrameAsync(NetworkStream stream, byte[] payload) =>
        await SendFrameAsync(stream, 0x2, payload).ConfigureAwait(false);

    private static async Task SendFrameAsync(NetworkStream stream, int opcode, byte[] payload)
    {
        var header = new List<byte> { (byte)(0x80 | opcode) };
        if (payload.Length < 126)
        {
            header.Add((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header.Add(126);
            header.Add((byte)(payload.Length >> 8));
            header.Add((byte)(payload.Length & 0xff));
        }
        else
        {
            header.Add(127);
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                header.Add((byte)((ulong)payload.Length >> shift));
            }
        }

        await stream.WriteAsync(header.ToArray()).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
    }
}
