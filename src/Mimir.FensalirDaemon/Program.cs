using System.Globalization;
using System.Diagnostics;
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
    int WorkerTickMs,
    int MaxReservoirQueue,
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
            Math.Max(1, ParseInt(args, "--worker-ms", 4)),
            Math.Max(1, ParseInt(args, "--max-reservoir-queue", 120)),
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
    private readonly CultRecordHandle<MimirFensalirReservoirWorkerStateDocument> workerHandle;
    private readonly CultRecordHandle<MimirFensalirReservoirPressureDocument> pressureHandle;
    private readonly WellLogTailState tail;
    private readonly ReservoirWorkerState reservoirWorker;
    private readonly object gate = new();
    private MimirFensalirDaemonStateDocument document;
    private MimirFensalirReservoirWorkerStateDocument workerDocument;
    private MimirFensalirReservoirPressureDocument pressureDocument;
    private DateTimeOffset lastStatus = DateTimeOffset.MinValue;

    private FensalirDaemonState(
        FensalirDaemonOptions options,
        CultCache cache,
        CultRecordHandle<MimirFensalirDaemonStateDocument> handle,
        MimirFensalirDaemonStateDocument document,
        CultRecordHandle<MimirFensalirReservoirWorkerStateDocument> workerHandle,
        MimirFensalirReservoirWorkerStateDocument workerDocument,
        CultRecordHandle<MimirFensalirReservoirPressureDocument> pressureHandle,
        MimirFensalirReservoirPressureDocument pressureDocument)
    {
        this.options = options;
        this.cache = cache;
        this.handle = handle;
        this.workerHandle = workerHandle;
        this.pressureHandle = pressureHandle;
        this.document = document;
        this.workerDocument = workerDocument;
        this.pressureDocument = pressureDocument;
        tail = new WellLogTailState(options.WellLogPath, options.FromStart);
        reservoirWorker = new ReservoirWorkerState(options);
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

    public MimirFensalirReservoirWorkerStateDocument WorkerDocument
    {
        get
        {
            lock (gate)
            {
                return workerDocument;
            }
        }
    }

    public MimirFensalirReservoirPressureDocument PressureDocument
    {
        get
        {
            lock (gate)
            {
                return pressureDocument;
            }
        }
    }

    public static async Task<FensalirDaemonState> OpenAsync(FensalirDaemonOptions options)
    {
        var cache = await CultCacheMessagePack.OpenAsync(options.CultCachePath, new CultCacheOpenOptions
        {
            UseDirectoryStore = true,
            FlushOnDispose = false,
            StoreFlushOnDispose = false,
        }).ConfigureAwait(false);
        var key = new CultRecordKey(options.DaemonId);
        var existing = cache.Get<MimirFensalirDaemonStateDocument>(key);
        var document = existing ?? BuildInitialDocument(options);
        var handle = await cache.UpsertAsync(document, new CultRecordHandle<MimirFensalirDaemonStateDocument>(key)).ConfigureAwait(false);
        var workerKey = new CultRecordKey(options.DaemonId + "-reservoir-worker");
        var existingWorker = cache.Get<MimirFensalirReservoirWorkerStateDocument>(workerKey);
        var workerDocument = existingWorker ?? BuildInitialWorkerDocument(options);
        var workerHandle = await cache.UpsertAsync(workerDocument, new CultRecordHandle<MimirFensalirReservoirWorkerStateDocument>(workerKey)).ConfigureAwait(false);
        var pressureKey = new CultRecordKey(options.DaemonId + "-reservoir-pressure");
        var existingPressure = cache.Get<MimirFensalirReservoirPressureDocument>(pressureKey);
        var pressureDocument = existingPressure ?? BuildInitialPressureDocument(options);
        var pressureHandle = await cache.UpsertAsync(pressureDocument, new CultRecordHandle<MimirFensalirReservoirPressureDocument>(pressureKey)).ConfigureAwait(false);
        await SafeFlushAsync(cache, soft: false).ConfigureAwait(false);
        return new FensalirDaemonState(options, cache, handle, document, workerHandle, workerDocument, pressureHandle, pressureDocument);
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

            if (reservoirWorker.Advance(DateTimeOffset.UtcNow, out var workerSample))
            {
                lock (gate)
                {
                    document = ApplyWorkerSample(document, workerSample);
                    workerDocument = BuildWorkerDocument(options, document, workerSample);
                    pressureDocument = BuildPressureDocument(options, document, workerSample);
                }

                touched = true;
            }

            if (touched)
            {
                await cache.UpsertAsync(document, handle).ConfigureAwait(false);
                await cache.UpsertAsync(workerDocument, workerHandle).ConfigureAwait(false);
                await cache.UpsertAsync(pressureDocument, pressureHandle).ConfigureAwait(false);
                await SafeFlushAsync(cache, soft: true).ConfigureAwait(false);
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
                    $"mimir-fensalir-daemon status={state.Status} wellSeq={state.LastWellSequence} captureSeq={state.LastCaptureSequence} slices={state.ReadySlices}/{state.TotalSlices} jobs={state.PendingReservoirJobs} active={state.ActiveReservoirWorkers} done={state.CompletedReservoirJobs} drop={state.DroppedReservoirJobs} offset={tail.Position}");
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
        try
        {
            cache.Dispose();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"mimir-fensalir-daemon state-cache-dispose-warning {ex.Message}");
        }

        return ValueTask.CompletedTask;
    }

    private static async Task SafeFlushAsync(CultCache cache, bool soft)
    {
        try
        {
            await cache.FlushAsync(soft).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"mimir-fensalir-daemon state-cache-flush-warning soft={soft} {ex.Message}");
        }
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
            var captureSequence = (long)DaemonUtil.JsonNumber(root, "captureSequence");
            reservoirWorker.Enqueue(BuildReservoirJob(root, current, captureSequence));
            next = current with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                Status = "drinking-capture-pages",
                WellSource = string.IsNullOrWhiteSpace(options.WellLogPath) ? "missing" : options.WellLogPath,
                LastCaptureSequence = captureSequence,
            };
            return true;
        }

        if (kind == "mimir.cultmesh_stream_frame.v1")
        {
            var captureSequence = (long)DaemonUtil.JsonNumber(root, "captureSequence");
            reservoirWorker.Enqueue(BuildStreamFrameReservoirJob(root, current, captureSequence));
            var stream = DaemonUtil.JsonGet(root, "stream");
            next = current with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                Status = "drinking-stream-frames",
                WellSource = string.IsNullOrWhiteSpace(options.WellLogPath) ? "missing" : options.WellLogPath,
                LastCaptureSequence = captureSequence,
                Notes = [
                    $"stream={DaemonUtil.JsonText(stream, "streamId") ?? "unknown"}",
                    $"kind={DaemonUtil.JsonText(stream, "kind") ?? "unknown"}",
                    "reservoir-worker=stream-frame-lane"
                ],
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
            ["cultcache-state", "cultnet-websocket", "cultmesh-eve-dashboard", "well-tail", "stream-frame-tail"],
            ["reservoir kernels pending"],
            0,
            0,
            0,
            0,
            0.0,
            0.0);

    private static MimirFensalirReservoirWorkerStateDocument BuildInitialWorkerDocument(FensalirDaemonOptions options) =>
        new(
            options.DaemonId + "-reservoir-worker",
            DateTimeOffset.UtcNow.ToString("O"),
            options.DaemonId,
            "surface-owner-installed",
            -1,
            "configuredComposite",
            0,
            0,
            [],
            options.GpuBudget,
            options.CpuBudget,
            0,
            0,
            0,
            0,
            0,
            0,
            "",
            0,
            string.IsNullOrWhiteSpace(options.WellLogPath) ? "waiting-for-well" : "booting",
            "no capture page accepted yet",
            0.0,
            0.0,
            0.0,
            0.0,
            0,
            0.0);

    private static MimirFensalirReservoirPressureDocument BuildInitialPressureDocument(FensalirDaemonOptions options) =>
        new(
            options.DaemonId + "-reservoir-pressure",
            DateTimeOffset.UtcNow.ToString("O"),
            options.DaemonId,
            0.0,
            0.0,
            0,
            0.0,
            0.0,
            0,
            0.0,
            0,
            0,
            0,
            0,
            "no capture pressure observed yet");

    private static MimirFensalirDaemonStateDocument ApplyWorkerSample(
        MimirFensalirDaemonStateDocument current,
        ReservoirWorkerSample sample)
    {
        var status = current.Status;
        if (sample.RunningJobs > 0)
        {
            status = "reservoir-worker-active";
        }
        else if (sample.QueuedJobs > 0)
        {
            status = "reservoir-worker-queued";
        }

        return current with
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Status = status,
            WorkerMode = "reservoir-worker-scheduled",
            PendingReservoirJobs = sample.QueuedJobs + sample.RunningJobs,
            ActiveReservoirWorkers = sample.RunningJobs,
            CompletedReservoirJobs = sample.CompletedJobs,
            DroppedReservoirJobs = sample.DroppedJobs,
            ReservoirQueueMs = sample.GpuQueueMs,
            ReservoirWorkerUtilization = sample.Utilization,
        };
    }

    private static MimirFensalirReservoirWorkerStateDocument BuildWorkerDocument(
        FensalirDaemonOptions options,
        MimirFensalirDaemonStateDocument current,
        ReservoirWorkerSample sample) =>
        new(
            options.DaemonId + "-reservoir-worker",
            DateTimeOffset.UtcNow.ToString("O"),
            options.DaemonId,
            "reservoir-worker-scheduled",
            sample.InputCaptureSequence,
            sample.SelectedCompositeVersion,
            sample.AcceptedSlices,
            sample.RejectedSlices,
            sample.RejectionReasons,
            options.GpuBudget,
            options.CpuBudget,
            sample.QueuedJobs,
            sample.RunningJobs,
            sample.CompletedJobs,
            sample.DroppedJobs,
            sample.OldestAcceptedCanonicalNs,
            sample.NewestAcceptedCanonicalNs,
            sample.OutputProgramSurfaceId,
            sample.OutputFenceValue,
            sample.Status,
            sample.PressureReason,
            current.PollAverageMs,
            current.PublishAverageMs,
            current.TotalSlices <= 0 ? 0.0 : current.ReadySlices / (double)current.TotalSlices,
            sample.TimingConfidenceMin,
            sample.MaxDistanceFromPresentationNs,
            sample.GpuQueueMs);

    private static MimirFensalirReservoirPressureDocument BuildPressureDocument(
        FensalirDaemonOptions options,
        MimirFensalirDaemonStateDocument current,
        ReservoirWorkerSample sample) =>
        new(
            options.DaemonId + "-reservoir-pressure",
            DateTimeOffset.UtcNow.ToString("O"),
            options.DaemonId,
            current.PollAverageMs,
            current.PublishAverageMs,
            sample.CapturePageBytes,
            current.TotalSlices <= 0 ? 0.0 : current.ReadySlices / (double)current.TotalSlices,
            sample.TimingConfidenceMin,
            sample.MaxDistanceFromPresentationNs,
            sample.GpuQueueMs,
            sample.ReservoirHistoryRowsUsed,
            sample.DroppedBecauseBodyMissing,
            sample.DroppedBecauseFenceUnavailable,
            sample.DroppedBecauseTimingDegraded,
            sample.PressureReason);

    private static ReservoirJob BuildReservoirJob(JsonElement root, MimirFensalirDaemonStateDocument current, long captureSequence)
    {
        var samples = DaemonUtil.JsonArray(DaemonUtil.JsonGet(root, "samples")).ToArray();
        var frame = DaemonUtil.JsonGet(root, "frame");
        var composite = DaemonUtil.JsonGet(root, "configuredComposite");
        var videoComposite = DaemonUtil.JsonArray(DaemonUtil.JsonGet(composite, "video")).ToArray();
        var rawBytes = Encoding.UTF8.GetByteCount(root.GetRawText());
        var accepted = 0;
        var rejected = 0;
        var droppedBecauseBodyMissing = 0;
        var droppedBecauseFenceUnavailable = 0;
        var droppedBecauseTimingDegraded = 0;
        var oldestNs = long.MaxValue;
        var newestNs = long.MinValue;
        var minConfidence = 1.0;
        var maxDistanceNs = 0L;
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            var slice = DaemonUtil.JsonGet(sample, "slice");
            var status = DaemonUtil.JsonText(slice, "status", "Status") ?? "";
            var confidence = DaemonUtil.JsonNumber(slice, "timingConfidence", "TimingConfidence");
            var distanceNs = Math.Abs((long)DaemonUtil.JsonNumber(slice, "distanceFromPresentationNs", "DistanceFromPresentationNs"));
            var startNs = (long)DaemonUtil.JsonNumber(slice, "canonicalStartNs", "CanonicalStartNs");
            var endNs = (long)DaemonUtil.JsonNumber(slice, "canonicalEndNs", "CanonicalEndNs");
            if (string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase) && (confidence >= 0.20 || distanceNs == 0))
            {
                accepted++;
                minConfidence = Math.Min(minConfidence, confidence);
                maxDistanceNs = Math.Max(maxDistanceNs, distanceNs);
                oldestNs = Math.Min(oldestNs, startNs);
                newestNs = Math.Max(newestNs, endNs);
            }
            else
            {
                rejected++;
                reasons.Add(string.IsNullOrWhiteSpace(status) ? "missing-status" : $"slice-{status.ToLowerInvariant()}");
                if (confidence < 0.20)
                {
                    reasons.Add("low-timing-confidence");
                    droppedBecauseTimingDegraded++;
                }
            }

            var body = DaemonUtil.JsonGet(sample, "body");
            var sampleMeta = DaemonUtil.JsonGet(sample, "sample");
            var video = DaemonUtil.JsonGet(sampleMeta, "video");
            var bodyStatus = DaemonUtil.JsonText(body, "status") ?? "";
            var producerFence = DaemonUtil.JsonNumber(video, "producerFenceValue", "ProducerFenceValue");
            if (string.Equals(bodyStatus, "empty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bodyStatus, "metadata-only", StringComparison.OrdinalIgnoreCase))
            {
                droppedBecauseBodyMissing++;
            }

            if (video.ValueKind == JsonValueKind.Object && producerFence <= 0.0)
            {
                droppedBecauseFenceUnavailable++;
            }
        }

        return new ReservoirJob(
            captureSequence,
            samples.Length,
            accepted,
            rejected,
            oldestNs == long.MaxValue ? 0 : oldestNs,
            newestNs == long.MinValue ? 0 : newestNs,
            minConfidence == 1.0 && accepted == 0 ? 0.0 : minConfidence,
            maxDistanceNs,
            DaemonUtil.JsonNumber(frame, "presentationDelayMs", "PresentationDelayMs", "PresentationDelayMilliseconds"),
            current.PollAverageMs,
            current.PublishAverageMs,
            videoComposite.Length == 0 ? "configuredComposite" : $"configuredComposite/video:{videoComposite.Length}",
            reasons.ToArray(),
            Math.Max(0, current.AudioSources),
            Math.Max(0, current.VideoSources),
            rawBytes,
            droppedBecauseBodyMissing,
            droppedBecauseFenceUnavailable,
            droppedBecauseTimingDegraded);
    }

    private static ReservoirJob BuildStreamFrameReservoirJob(JsonElement root, MimirFensalirDaemonStateDocument current, long captureSequence)
    {
        var slice = DaemonUtil.JsonGet(root, "slice");
        var stream = DaemonUtil.JsonGet(root, "stream");
        var frame = DaemonUtil.JsonGet(root, "frame");
        var sample = DaemonUtil.JsonGet(root, "sample");
        var body = DaemonUtil.JsonGet(root, "body");
        var bodyRef = DaemonUtil.JsonGet(root, "bodyRef");
        var kind = DaemonUtil.JsonText(stream, "kind") ?? DaemonUtil.JsonText(sample, "Kind", "kind") ?? "";
        var status = DaemonUtil.JsonText(slice, "status", "Status") ?? "";
        var confidence = DaemonUtil.JsonNumber(slice, "timingConfidence", "TimingConfidence");
        var distanceNs = Math.Abs((long)DaemonUtil.JsonNumber(slice, "distanceFromPresentationNs", "DistanceFromPresentationNs"));
        var accepted = string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase) && (confidence >= 0.20 || distanceNs == 0) ? 1 : 0;
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        if (accepted == 0)
        {
            reasons.Add(string.IsNullOrWhiteSpace(status) ? "missing-status" : $"slice-{status.ToLowerInvariant()}");
            if (confidence < 0.20)
            {
                reasons.Add("low-timing-confidence");
            }
        }

        var bodyStatus = DaemonUtil.JsonText(body, "status") ?? "";
        var hasBodyRef = bodyRef.ValueKind == JsonValueKind.Object;
        var transport = DaemonUtil.JsonText(frame, "bodyTransport") ?? bodyStatus;
        var droppedBecauseBodyMissing = 0;
        var droppedBecauseFenceUnavailable = 0;
        var droppedBecauseTimingDegraded = 0;
        if (accepted == 0 && confidence < 0.20)
        {
            droppedBecauseTimingDegraded++;
        }

        if (string.Equals(transport, "metadata-only", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bodyStatus, "empty", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("body-metadata-only");
            accepted = 0;
            droppedBecauseBodyMissing++;
        }

        var hasInlineBody = string.Equals(transport, "inline-bytes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bodyStatus, "inline-bytes", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(kind, "Video", StringComparison.OrdinalIgnoreCase) &&
            !hasInlineBody &&
            !hasBodyRef &&
            DaemonUtil.JsonNumber(frame, "producerFenceValue", "ProducerFenceValue") <= 0.0 &&
            string.IsNullOrWhiteSpace(DaemonUtil.JsonText(frame, "nativeHandleKind", "NativeHandleKind")))
        {
            reasons.Add("fence-unavailable");
            droppedBecauseFenceUnavailable++;
        }

        var videoSources = string.Equals(kind, "Video", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var audioSources = string.Equals(kind, "Audio", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return new ReservoirJob(
            captureSequence,
            1,
            accepted,
            accepted == 1 ? 0 : 1,
            (long)DaemonUtil.JsonNumber(slice, "canonicalStartNs", "CanonicalStartNs"),
            (long)DaemonUtil.JsonNumber(slice, "canonicalEndNs", "CanonicalEndNs"),
            confidence,
            distanceNs,
            0.0,
            current.PollAverageMs,
            current.PublishAverageMs,
            $"streamFrame/{DaemonUtil.JsonText(stream, "streamId") ?? "unknown"}:{(hasBodyRef ? "paged" : transport)}",
            reasons.ToArray(),
            audioSources,
            videoSources,
            Encoding.UTF8.GetByteCount(root.GetRawText()),
            droppedBecauseBodyMissing,
            droppedBecauseFenceUnavailable,
            droppedBecauseTimingDegraded);
    }
}

internal sealed class ReservoirWorkerState
{
    private const double BaseSliceCostMs = 4.0;
    private readonly FensalirDaemonOptions options;
    private readonly Queue<ReservoirJob> queue = new();
    private DateTimeOffset lastAdvanced = DateTimeOffset.UtcNow;
    private ReservoirJob? active;
    private double activeRemainingMs;
    private long completed;
    private long dropped;
    private long outputFenceValue;
    private ReservoirWorkerSample lastSample = ReservoirWorkerSample.Empty;

    public ReservoirWorkerState(FensalirDaemonOptions options)
    {
        this.options = options;
    }

    public void Enqueue(ReservoirJob job)
    {
        if (job.CaptureSequence < 0 || job.AcceptedSlices <= 0)
        {
            dropped++;
            lastSample = lastSample with
            {
                DroppedJobs = dropped,
                Status = "dropping-unusable-capture-page",
                PressureReason = job.RejectionReasons.Length == 0 ? "no accepted slices" : string.Join(",", job.RejectionReasons.Take(3)),
            };
            return;
        }

        while (queue.Count >= options.MaxReservoirQueue)
        {
            queue.Dequeue();
            dropped++;
        }

        queue.Enqueue(job);
    }

    public bool Advance(DateTimeOffset now, out ReservoirWorkerSample sample)
    {
        var elapsedMs = Math.Max(0.0, (now - lastAdvanced).TotalMilliseconds);
        if (elapsedMs < options.WorkerTickMs && active == null && queue.Count == 0)
        {
            sample = lastSample;
            return false;
        }

        lastAdvanced = now;
        var budget = WorkerBudget();
        var availableMs = Math.Max(options.WorkerTickMs, elapsedMs) * budget;
        var completedJob = active;
        while (availableMs > 0.0)
        {
            if (active == null)
            {
                if (!queue.TryDequeue(out var next))
                {
                    break;
                }

                active = next;
                activeRemainingMs = Cost(next);
            }

            var spent = Math.Min(activeRemainingMs, availableMs);
            activeRemainingMs -= spent;
            availableMs -= spent;
            if (activeRemainingMs <= 0.0001)
            {
                completedJob = active;
                active = null;
                activeRemainingMs = 0.0;
                completed++;
                outputFenceValue++;
            }
        }

        sample = BuildSample(completedJob);
        lastSample = sample;
        return true;
    }

    private ReservoirWorkerSample BuildSample(ReservoirJob? latest)
    {
        var job = active ?? latest ?? queue.LastOrDefault();
        var queueMs = (activeRemainingMs + queue.Sum(Cost)) / Math.Max(0.05, WorkerBudget());
        var running = active == null ? 0 : 1;
        var status = running > 0 ? "running" : queue.Count > 0 ? "queued" : completed > 0 ? "caught-up" : lastSample.Status;
        var reason = queue.Count >= options.MaxReservoirQueue ? "queue at ceiling" : "within configured worker budget";
        return new ReservoirWorkerSample(
            job?.CaptureSequence ?? lastSample.InputCaptureSequence,
            job?.SelectedCompositeVersion ?? lastSample.SelectedCompositeVersion,
            job?.AcceptedSlices ?? 0,
            job?.RejectedSlices ?? 0,
            job?.RejectionReasons ?? [],
            queue.Count,
            running,
            completed,
            dropped,
            job?.OldestAcceptedCanonicalNs ?? 0,
            job?.NewestAcceptedCanonicalNs ?? 0,
            "mimir.fensalir.program.surface.pending",
            outputFenceValue,
            status,
            reason,
            job?.TimingConfidenceMin ?? 0.0,
            job?.MaxDistanceFromPresentationNs ?? 0,
            queueMs,
            running > 0 ? WorkerBudget() : 0.0,
            job?.CapturePageBytes ?? 0,
            Math.Max(1, job?.AcceptedSlices ?? 0),
            job?.DroppedBecauseBodyMissing ?? 0,
            job?.DroppedBecauseFenceUnavailable ?? 0,
            job?.DroppedBecauseTimingDegraded ?? 0);
    }

    private double WorkerBudget() => Math.Clamp(options.GpuBudget * 0.70 + options.CpuBudget * 0.30, 0.05, 1.0);

    private static double Cost(ReservoirJob job)
    {
        var sliceCost = Math.Max(1, job.AcceptedSlices) * BaseSliceCostMs;
        var rejectedPenalty = Math.Max(0, job.RejectedSlices) * 0.25;
        var videoCost = Math.Max(0, job.VideoSources) * 2.0;
        var audioCost = Math.Max(0, job.AudioSources) * 0.5;
        var pressureCost = Math.Max(job.WellPollMs, job.WellPublishMs) * 0.10;
        return Math.Max(1.0, sliceCost + rejectedPenalty + videoCost + audioCost + pressureCost);
    }
}

internal sealed record ReservoirJob(
    long CaptureSequence,
    int TotalSlices,
    int AcceptedSlices,
    int RejectedSlices,
    long OldestAcceptedCanonicalNs,
    long NewestAcceptedCanonicalNs,
    double TimingConfidenceMin,
    long MaxDistanceFromPresentationNs,
    double PresentationDelayMs,
    double WellPollMs,
    double WellPublishMs,
    string SelectedCompositeVersion,
    string[] RejectionReasons,
    int AudioSources,
    int VideoSources,
    long CapturePageBytes,
    long DroppedBecauseBodyMissing,
    long DroppedBecauseFenceUnavailable,
    long DroppedBecauseTimingDegraded);

internal sealed record ReservoirWorkerSample(
    long InputCaptureSequence,
    string SelectedCompositeVersion,
    int AcceptedSlices,
    int RejectedSlices,
    string[] RejectionReasons,
    int QueuedJobs,
    int RunningJobs,
    long CompletedJobs,
    long DroppedJobs,
    long OldestAcceptedCanonicalNs,
    long NewestAcceptedCanonicalNs,
    string OutputProgramSurfaceId,
    long OutputFenceValue,
    string Status,
    string PressureReason,
    double TimingConfidenceMin,
    long MaxDistanceFromPresentationNs,
    double GpuQueueMs,
    double Utilization,
    long CapturePageBytes,
    int ReservoirHistoryRowsUsed,
    long DroppedBecauseBodyMissing,
    long DroppedBecauseFenceUnavailable,
    long DroppedBecauseTimingDegraded)
{
    public static ReservoirWorkerSample Empty { get; } = new(
        -1,
        "configuredComposite",
        0,
        0,
        [],
        0,
        0,
        0,
        0,
        0,
        0,
        "",
        0,
        "waiting",
        "no capture page accepted yet",
        0.0,
        0,
        0.0,
        0.0,
        0,
        0,
        0,
        0,
        0);
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
                workerStatus = state.WorkerDocument.Status,
                queuedJobs = state.WorkerDocument.QueuedJobs,
                runningJobs = state.WorkerDocument.RunningJobs,
                completedJobs = state.WorkerDocument.CompletedJobs,
                droppedJobs = state.WorkerDocument.DroppedJobs,
                cultCache = options.CultCachePath,
                cultMeshDocument = "mimir.fensalir_daemon_state",
                workerDocument = "mimir.fensalir_reservoir_worker_state",
                pressureDocument = "mimir.fensalir_reservoir_pressure",
            }, JsonOptions);
            await DaemonUtil.WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(health)).ConfigureAwait(false);
            return;
        }

        if (request.Path == "/eve/deck/manifest")
        {
            await DaemonUtil.WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Manifest(), JsonOptions))).ConfigureAwait(false);
            return;
        }

        if (request.Path == "/eve/deck/providers")
        {
            await DaemonUtil.WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                providers = new[] { Manifest() },
            }, JsonOptions))).ConfigureAwait(false);
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
        var worker = state.WorkerDocument;
        var pressure = state.PressureDocument;
        var dashboard = BuildDashboard(document, worker, pressure);
        if (binary)
        {
            var cultMesh = ToCultMesh(dashboard);
            await DaemonUtil.SendBinaryFrameAsync(stream, MessagePackSerializer.Serialize(cultMesh)).ConfigureAwait(false);
            return;
        }

        await DaemonUtil.SendTextFrameAsync(stream, JsonSerializer.Serialize(dashboard, JsonOptions)).ConfigureAwait(false);
    }

    private DashboardState BuildDashboard(
        MimirFensalirDaemonStateDocument doc,
        MimirFensalirReservoirWorkerStateDocument worker,
        MimirFensalirReservoirPressureDocument pressure)
    {
        var readiness = doc.TotalSlices <= 0 ? 0.0 : doc.ReadySlices / (double)doc.TotalSlices;
        var streamPressure = Math.Clamp(Math.Max(doc.PollAverageMs / 16.0, doc.PublishAverageMs / 16.0), 0.0, 1.0);
        var workerPressure = Math.Clamp(worker.GpuQueueMs / 1000.0, 0.0, 1.0);
        var observedDaemons = FensalirDaemonObserver.Collect(options);
        var nodes = new List<DashboardNode>
        {
            new("daemon", $"Fensalir Daemon\n{doc.Status}", "daemon", 0.0, -0.42, 0.70, 0.18, doc.Status),
            new("well", $"Well Drink\nseq {doc.LastWellSequence}", "well", -0.38, -0.08, 0.32, 0.18, doc.LastWellSequence >= 0 ? "live" : "waiting"),
            new("capture", $"Capture Pages\nseq {doc.LastCaptureSequence}", "cultcache", 0.0, -0.08, 0.32, 0.18, doc.LastCaptureSequence >= 0 ? "paged" : "waiting"),
            new("budget", $"Budget\nGPU {doc.GpuBudget:0.00} CPU {doc.CpuBudget:0.00}", "budget", 0.38, -0.08, 0.32, 0.18, "configured"),
            new("worker", $"Reservoir Worker\n{worker.Status}", "worker", 0.0, 0.26, 0.54, 0.18, worker.Status),
            new("online-daemons", $"Online Daemons\n{observedDaemons.Count(static daemon => daemon.Status == "online")}/{observedDaemons.Count}", "daemon-status", -0.38, 0.48, 0.70, 0.18, "observed"),
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
                    Ui.Row("daemon-row-worker",
                    [
                        Ui.Pane("worker-pane", "Reservoir Worker",
                        [
                            Ui.Text("worker-status", $"{worker.Status} | {worker.PressureReason}", "strong", WorkerBind("/status")),
                            Ui.Text("worker-counts", $"queued {worker.QueuedJobs} running {worker.RunningJobs} completed {worker.CompletedJobs} dropped {worker.DroppedJobs}", "mono", WorkerBind("/queuedJobs")),
                            Ui.Text("worker-slices", $"accepted {worker.AcceptedSlices} rejected {worker.RejectedSlices} confidence {worker.TimingConfidenceMin:0.000}", "caption", WorkerBind("/acceptedSlices")),
                            Ui.Bar("worker-queue", "GPU queue", workerPressure, DaemonUtil.Tone(1.0 - workerPressure), $"{worker.GpuQueueMs:0.0}ms"),
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
                            Ui.Bar("pressure-bar", "publish/poll pressure", streamPressure, DaemonUtil.Tone(1.0 - streamPressure), $"{Math.Max(doc.PollAverageMs, doc.PublishAverageMs):0.000}ms"),
                            Ui.Text("pressure-detail", $"poll {doc.PollAverageMs:0.000}ms publish {doc.PublishAverageMs:0.000}ms", "caption", Bind("/pollAverageMs")),
                            Ui.Text("pressure-drops", $"body {pressure.DroppedBecauseBodyMissing} fence {pressure.DroppedBecauseFenceUnavailable} timing {pressure.DroppedBecauseTimingDegraded}", "caption", PressureBind("/droppedBecauseBodyMissing")),
                            Ui.Text("pressure-distance", $"bytes {pressure.CapturePageBytes} max distance {pressure.MaxDistanceFromPresentationNs}ns", "caption", PressureBind("/capturePageBytes")),
                            Ui.Text("notes", string.Join("\n", doc.Notes.Take(4)), "caption", Bind("/notes")),
                        ]),
                    ]),
                    Ui.Row("daemon-row-observed",
                    [
                        Ui.Pane("observed-daemon-pane", "Observed Daemons",
                            observedDaemons.Select(static daemon =>
                                Ui.Text(
                                    $"observed-{daemon.Id}",
                                    $"{daemon.Name}: {daemon.Status} pid {daemon.Pid} {daemon.Detail}",
                                    daemon.Status == "online" ? "mono" : "caption"))
                                .Cast<UiElement>()
                                .ToArray()),
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

    private UiBinding WorkerBind(string path) => new()
    {
        DocumentSchema = "mimir.fensalir_reservoir_worker_state.v1",
        DocumentId = options.DaemonId + "-reservoir-worker",
        Path = path,
        ValueKind = "state",
        Access = "read",
        Authority = "Mimir.FensalirDaemon",
    };

    private UiBinding PressureBind(string path) => new()
    {
        DocumentSchema = "mimir.fensalir_reservoir_pressure.v1",
        DocumentId = options.DaemonId + "-reservoir-pressure",
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
            [
                "cultcache-state",
                "cultnet-websocket",
                "cultmesh-eve-dashboard",
                "well-tail",
                "stream-frame-tail",
                "reservoir-owner",
                "reservoir-worker-state",
                "reservoir-pressure-state"
            ],
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

internal static class FensalirDaemonObserver
{
    private static readonly (string Id, string Name, string[] PidFiles, string[] ProcessNames)[] LocalDaemons =
    [
        ("verse-relay", "Verse relay", ["verse-relay-direct.pid", "verse-relay.pid"], ["dotnet", "Mimir.EveSensorReceiver"]),
        ("verse-recorder", "Verse recorder", ["verse-recorder.pid"], ["dotnet", "Mimir.VerseRecorder"]),
        ("mimir-well", "Mimir Well", ["mimir-well-rebased.pid", "mimir-well-fixed.pid", "mimir-well.pid"], ["dotnet", "Mimir.Well"]),
        ("move-sync", "Move sync", ["move-sync-lean.pid", "move-sync.pid"], ["python", "python3", "py"]),
        ("fensalir", "Fensalir", ["fensalir-daemon.pid"], ["dotnet", "Mimir.FensalirDaemon"]),
    ];

    public static IReadOnlyList<ObservedDaemonStatus> Collect(FensalirDaemonOptions options)
    {
        var runDir = Path.GetDirectoryName(options.CultCachePath) ?? "";
        var statuses = new List<ObservedDaemonStatus>
        {
            new("fensalir-self", "Fensalir self", Environment.ProcessId, "online", "eve/cultmesh provider")
        };
        if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
        {
            statuses.Add(new("supervisor-run", "Supervisor run", 0, "missing", "run directory missing"));
            return statuses;
        }

        foreach (var daemon in LocalDaemons)
        {
            statuses.Add(ObserveLocal(runDir, daemon.Id, daemon.Name, daemon.PidFiles, daemon.ProcessNames));
        }

        return statuses
            .GroupBy(static status => status.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static status => status.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static ObservedDaemonStatus ObserveLocal(
        string runDir,
        string id,
        string name,
        IReadOnlyList<string> pidFiles,
        IReadOnlyList<string> expectedProcessNames)
    {
        foreach (var pidFile in pidFiles)
        {
            var path = Path.Combine(runDir, pidFile);
            if (!File.Exists(path))
            {
                continue;
            }

            var raw = File.ReadAllText(path).Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                return new ObservedDaemonStatus(id, name, 0, "invalid", pidFile);
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return new ObservedDaemonStatus(id, name, pid, "stopped", pidFile);
                }

                var processName = process.ProcessName;
                if (!expectedProcessNames.Any(expected => processName.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                {
                    return new ObservedDaemonStatus(id, name, pid, "stale-pid", $"{pidFile} now belongs to {processName}");
                }

                var detail = $"{pidFile} {processName}";
                if (id == "move-sync")
                {
                    var trace = MoveTraceDetail(runDir);
                    if (!string.IsNullOrWhiteSpace(trace.Detail))
                    {
                        detail = $"{detail}; {trace.Detail}";
                    }

                    if (trace.WaitingForFrames)
                    {
                        return new ObservedDaemonStatus(id, name, pid, "waiting", detail);
                    }
                }

                return new ObservedDaemonStatus(id, name, pid, "online", detail);
            }
            catch (ArgumentException)
            {
                return new ObservedDaemonStatus(id, name, pid, "stopped", pidFile);
            }
            catch (InvalidOperationException)
            {
                return new ObservedDaemonStatus(id, name, pid, "stopped", pidFile);
            }
        }

        return new ObservedDaemonStatus(id, name, 0, "missing", "no pid file");
    }

    private static (bool WaitingForFrames, string Detail) MoveTraceDetail(string runDir)
    {
        var path = Path.Combine(runDir, "move-sync", "online-sync.jsonl");
        if (!File.Exists(path))
        {
            return (true, "trace missing");
        }

        var info = new FileInfo(path);
        if (info.Length <= 0)
        {
            return (true, "trace empty");
        }

        var age = DateTimeOffset.Now - info.LastWriteTime;
        return (false, $"trace bytes={info.Length} age={age.TotalSeconds:0}s");
    }
}

internal sealed record ObservedDaemonStatus(
    string Id,
    string Name,
    int Pid,
    string Status,
    string Detail);

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
