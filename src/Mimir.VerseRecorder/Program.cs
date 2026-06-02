using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = VerseRecorderOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);
var runId = string.IsNullOrWhiteSpace(options.RunId)
    ? DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")
    : options.RunId;
var runDirectory = Path.Combine(options.OutputDirectory, runId);
Directory.CreateDirectory(runDirectory);
var jsonlPath = Path.Combine(runDirectory, "observations.jsonl");
var sessionPath = Path.Combine(runDirectory, "session.json");
var bodyPager = options.WriteBodies
    ? new MimirRecorderBodyPager(runId, runDirectory, options.BodyPageBytes)
    : null;

await File.WriteAllTextAsync(
    sessionPath,
    JsonSerializer.Serialize(
        new
        {
            kind = "mimir.verse_recorder_session.v1",
            runId,
            options.Url,
            startedAt = DateTimeOffset.Now.ToString("O"),
            options.Seconds,
            observations = jsonlPath,
            bodies = bodyPager is null
                ? null
                : new
                {
                    enabled = true,
                    pageBytes = options.BodyPageBytes,
                    directory = bodyPager.BodyDirectory,
                    index = bodyPager.IndexPath,
                    document = "mimir.recorder_body_index.v1",
                    acceptedBodyDocuments = new[]
                    {
                        "mimir.cultmesh_stream_frame.v1",
                        "mimir.well_capture_page.v1",
                    },
                },
        },
        new JsonSerializerOptions { WriteIndented = true }));

Console.Error.WriteLine($"Mimir Verse recorder subscribing to {options.Url}");
Console.Error.WriteLine($"Writing {jsonlPath}");

using var stopping = new CancellationTokenSource();
if (options.Seconds > 0)
{
    stopping.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
}

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

var count = 0L;
var lastMeter = DateTimeOffset.UtcNow;
await using var output = new StreamWriter(new FileStream(jsonlPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);

while (!stopping.IsCancellationRequested)
{
    using var socket = new ClientWebSocket();
    try
    {
        await socket.ConnectAsync(options.Url, stopping.Token);
        while (socket.State == WebSocketState.Open && !stopping.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(socket, stopping.Token);
            if (text == null)
            {
                break;
            }

            var storedText = bodyPager is null
                ? text
                : await bodyPager.PageBodiesAsync(text, stopping.Token).ConfigureAwait(false);
            await output.WriteLineAsync(storedText);
            count++;
            if (DateTimeOffset.UtcNow - lastMeter >= TimeSpan.FromSeconds(options.MeterSeconds))
            {
                await output.FlushAsync();
                if (bodyPager is not null)
                {
                    await bodyPager.FlushAsync(stopping.Token).ConfigureAwait(false);
                }

                Console.Error.WriteLine($"verse-recorder observations={count} path={jsonlPath}");
                lastMeter = DateTimeOffset.UtcNow;
            }
        }
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"verse-recorder reconnect after {ex.GetType().Name}: {ex.Message}");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.ReconnectSeconds), stopping.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

await output.FlushAsync();
if (bodyPager is not null)
{
    await bodyPager.DisposeAsync().ConfigureAwait(false);
}

Console.Error.WriteLine($"verse-recorder complete observations={count} path={jsonlPath}");

static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken stopping)
{
    var buffer = new byte[64 * 1024];
    using var memory = new MemoryStream();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, stopping);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        memory.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
        {
            return Encoding.UTF8.GetString(memory.ToArray());
        }
    }
}

internal sealed record VerseRecorderOptions(
    Uri Url,
    string OutputDirectory,
    string RunId,
    double Seconds,
    double ReconnectSeconds,
    double MeterSeconds,
    bool WriteBodies,
    long BodyPageBytes)
{
    public static VerseRecorderOptions Parse(string[] args)
    {
        var defaultOutput = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "Mimir",
            "VerseCaptures");
        return new VerseRecorderOptions(
            new Uri(ParseString(args, "--url", "ws://127.0.0.1:8796/eve/periwinkle/subscribe")),
            ParseString(args, "--out-dir", defaultOutput),
            ParseString(args, "--run-id", ""),
            ParseDouble(args, "--seconds", 0.0),
            ParseDouble(args, "--reconnect-seconds", 2.0),
            ParseDouble(args, "--meter-seconds", 5.0),
            ParseBool(args, "--write-bodies", true),
            ParseLong(args, "--body-page-bytes", 128L * 1024L * 1024L));
    }

    private static string ParseString(IReadOnlyList<string> args, string name, string fallback)
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

    private static double ParseDouble(IReadOnlyList<string> args, string name, double fallback)
    {
        return double.TryParse(ParseString(args, name, ""), out var value)
            ? value
            : fallback;
    }

    private static long ParseLong(IReadOnlyList<string> args, string name, long fallback)
    {
        return long.TryParse(ParseString(args, name, ""), out var value)
            ? value
            : fallback;
    }

    private static bool ParseBool(IReadOnlyList<string> args, string name, bool fallback)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index == args.Count - 1 ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return true;
            }

            return bool.TryParse(args[index + 1], out var value) ? value : fallback;
        }

        return fallback;
    }
}

internal sealed class MimirRecorderBodyPager : IAsyncDisposable
{
    private readonly string runId;
    private readonly long pageByteLimit;
    private readonly StreamWriter indexWriter;
    private FileStream? currentPage;
    private long currentPageBytes;
    private int pageIndex;

    public MimirRecorderBodyPager(string runId, string runDirectory, long pageByteLimit)
    {
        this.runId = runId;
        this.pageByteLimit = Math.Max(1024 * 1024, pageByteLimit);
        BodyDirectory = Path.Combine(runDirectory, "bodies");
        Directory.CreateDirectory(BodyDirectory);
        IndexPath = Path.Combine(BodyDirectory, "index.jsonl");
        indexWriter = new StreamWriter(new FileStream(IndexPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
    }

    public string BodyDirectory { get; }

    public string IndexPath { get; }

    public async Task<string> PageBodiesAsync(string text, CancellationToken stopping)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return text;
        }

        if (root is not JsonObject rootObject)
        {
            return text;
        }

        if (string.Equals(rootObject["document"]?.GetValue<string>(), "mimir.cultmesh_stream_frame.v1", StringComparison.Ordinal))
        {
            return await PageStreamFrameBodyAsync(rootObject, text, stopping).ConfigureAwait(false);
        }

        if (!string.Equals(rootObject["document"]?.GetValue<string>(), "mimir.well_capture_page.v1", StringComparison.Ordinal) ||
            rootObject["samples"] is not JsonArray samples)
        {
            return text;
        }

        var wroteAny = false;
        foreach (var sampleNode in samples)
        {
            if (sampleNode is not JsonObject sampleObject ||
                sampleObject["body"] is not JsonObject bodyObject ||
                !string.Equals(bodyObject["status"]?.GetValue<string>(), "inline", StringComparison.Ordinal) ||
                !string.Equals(bodyObject["encoding"]?.GetValue<string>(), "base64", StringComparison.Ordinal))
            {
                continue;
            }

            var encoded = bodyObject["data"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(encoded))
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                continue;
            }

            var bodyId = sampleObject["bodyId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
            var bodyRef = await WriteBodyAsync(bodyId, bytes, sampleObject, stopping).ConfigureAwait(false);
            sampleObject.Remove("body");
            sampleObject["bodyRef"] = bodyRef;
            wroteAny = true;
        }

        return wroteAny
            ? rootObject.ToJsonString()
            : text;
    }

    private async Task<string> PageStreamFrameBodyAsync(
        JsonObject rootObject,
        string originalText,
        CancellationToken stopping)
    {
        if (rootObject["body"] is not JsonObject bodyObject ||
            !string.Equals(bodyObject["status"]?.GetValue<string>(), "inline", StringComparison.Ordinal) ||
            !string.Equals(bodyObject["encoding"]?.GetValue<string>(), "base64", StringComparison.Ordinal))
        {
            return originalText;
        }

        var encoded = bodyObject["data"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return originalText;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return originalText;
        }

        var bodyId = rootObject["bodyId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        var bodyRef = await WriteBodyAsync(bodyId, bytes, rootObject, stopping).ConfigureAwait(false);
        rootObject.Remove("body");
        rootObject["bodyRef"] = bodyRef;
        return rootObject.ToJsonString();
    }

    private async Task<JsonObject> WriteBodyAsync(
        string bodyId,
        byte[] bytes,
        JsonObject sampleObject,
        CancellationToken stopping)
    {
        await EnsurePageAsync(bytes.LongLength, stopping).ConfigureAwait(false);
        var pageName = $"page-{pageIndex:000000}.bin";
        var offset = currentPageBytes;
        await currentPage!.WriteAsync(bytes, stopping).ConfigureAwait(false);
        currentPageBytes += bytes.LongLength;

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var bodyRef = new JsonObject
        {
            ["storage"] = "mimir-recorder-page",
            ["document"] = "mimir.recorder_body_ref.v1",
            ["bodyId"] = bodyId,
            ["runId"] = runId,
            ["page"] = $"bodies/{pageName}",
            ["offset"] = offset,
            ["byteLength"] = bytes.LongLength,
            ["sha256"] = hash,
            ["encoding"] = "raw",
        };

        var indexRecord = new JsonObject
        {
            ["document"] = "mimir.recorder_body_index.v1",
            ["bodyId"] = bodyId,
            ["runId"] = runId,
            ["page"] = $"bodies/{pageName}",
            ["offset"] = offset,
            ["byteLength"] = bytes.LongLength,
            ["sha256"] = hash,
            ["sourceId"] = sampleObject["sample"]?["SourceId"]?.GetValue<string>(),
            ["kind"] = sampleObject["sample"]?["Kind"]?.GetValue<string>(),
            ["origin"] = sampleObject["sample"]?["Origin"]?.GetValue<string>(),
            ["sequence"] = sampleObject["sample"]?["Sequence"]?.GetValue<ulong>(),
            ["timestampNs"] = sampleObject["sample"]?["TimestampNs"]?.GetValue<long>(),
        };
        await indexWriter.WriteLineAsync(indexRecord.ToJsonString()).ConfigureAwait(false);
        return bodyRef;
    }

    private async Task EnsurePageAsync(long nextBodyBytes, CancellationToken stopping)
    {
        if (currentPage is not null && currentPageBytes + nextBodyBytes <= pageByteLimit)
        {
            return;
        }

        if (currentPage is not null)
        {
            await currentPage.FlushAsync(stopping).ConfigureAwait(false);
            await currentPage.DisposeAsync().ConfigureAwait(false);
            currentPage = null;
            pageIndex++;
            currentPageBytes = 0;
        }

        var pagePath = Path.Combine(BodyDirectory, $"page-{pageIndex:000000}.bin");
        currentPage = new FileStream(pagePath, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);

        if (currentPage is not null)
        {
            await currentPage.DisposeAsync().ConfigureAwait(false);
        }

        await indexWriter.DisposeAsync().ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken stopping)
    {
        if (currentPage is not null)
        {
            await currentPage.FlushAsync(stopping).ConfigureAwait(false);
        }

        await indexWriter.FlushAsync().ConfigureAwait(false);
    }
}
