using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mimir.Runtime.Synchronization;

var options = WellObsOptions.Parse(args);
if (string.IsNullOrWhiteSpace(options.WellLogPath))
{
    Console.Error.WriteLine("Mimir.WellObsDaemon requires --well-log <path> for this cut.");
    return 2;
}

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

using var publisher = new MimirObsStemSharedMemoryPublisher(options.MapName);
var state = new WellLogTailState(options.WellLogPath, options.FromStart);
var lastPublishedCaptureSequence = -1L;
var lastStatus = DateTimeOffset.MinValue;
Console.Error.WriteLine($"Mimir Well OBS daemon drinking {options.WellLogPath}");
Console.Error.WriteLine($"Mimir Well OBS daemon publishing stems to {options.MapName}");

while (!stopping.IsCancellationRequested)
{
    foreach (var document in state.ReadNewDocuments())
    {
        using var _ = document;
        var publication = WellObsPublication.TryBuild(document, options);
        if (publication == null || publication.CaptureSequence <= lastPublishedCaptureSequence)
        {
            continue;
        }

        lastPublishedCaptureSequence = publication.CaptureSequence;
        publisher.Publish(publication.Snapshot);
        Console.Error.WriteLine(
            $"mimir-well-obs published capture={publication.CaptureSequence} stems={publication.Snapshot.ReadyStems.Count} compositeFrames={publication.CompositeFrameCount} sources={string.Join(",", publication.SourceIds)}");
        if (options.Once)
        {
            return 0;
        }
    }

    if (DateTimeOffset.UtcNow - lastStatus >= TimeSpan.FromSeconds(options.StatusSeconds))
    {
        lastStatus = DateTimeOffset.UtcNow;
        Console.Error.WriteLine($"mimir-well-obs status lastCapture={lastPublishedCaptureSequence} offset={state.Position}");
    }

    try
    {
        await Task.Delay(options.PollMs, stopping.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

return 0;

internal sealed record WellObsOptions(
    string WellLogPath,
    string MapName,
    string CompositeStemId,
    string PerSourceStemPrefix,
    int PollMs,
    int StatusSeconds,
    bool FromStart,
    bool Once)
{
    public static WellObsOptions Parse(IReadOnlyList<string> args) => new(
        ParseString(args, "--well-log", ""),
        ParseString(args, "--map", MimirObsStemSharedMemoryPublisher.DefaultMapName),
        ParseString(args, "--composite-stem-id", "well_composite"),
        ParseString(args, "--source-stem-prefix", "well_"),
        Math.Max(5, ParseInt(args, "--poll-ms", 50)),
        Math.Max(1, ParseInt(args, "--status-seconds", 5)),
        ParseBool(args, "--from-start", false),
        ParseBool(args, "--once", false));

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

internal sealed class WellLogTailState
{
    private readonly string path;
    private long position;

    public WellLogTailState(string path, bool fromStart)
    {
        this.path = path;
        position = !fromStart && File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public long Position => position;

    public IEnumerable<JsonDocument> ReadNewDocuments()
    {
        if (!File.Exists(path))
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

internal sealed record WellObsPublication(
    long CaptureSequence,
    int CompositeFrameCount,
    IReadOnlyList<string> SourceIds,
    MimirObsStemPublicationSnapshot Snapshot)
{
    public static WellObsPublication? TryBuild(JsonDocument document, WellObsOptions options)
    {
        var root = document.RootElement;
        var kind = JsonText(root, "document");
        if (kind is not "mimir.well_capture_page.v1")
        {
            return null;
        }

        var captureSequence = (long)JsonNumber(root, "captureSequence");
        var feeds = ParseAudioFeeds(root).ToArray();
        var samples = ParseAudioSamples(root).ToArray();
        if (feeds.Length == 0 || samples.Length == 0)
        {
            return null;
        }

        var selectedFeeds = SelectFeeds(feeds);
        var selectedSamples = selectedFeeds
            .Select(feed => (Feed: feed, Sample: samples.LastOrDefault(sample => string.Equals(sample.SourceId, feed.SourceId, StringComparison.Ordinal))))
            .Where(pair => pair.Sample != null)
            .Select(pair => (pair.Feed, Sample: pair.Sample!))
            .ToArray();
        if (selectedSamples.Length == 0)
        {
            return null;
        }

        var sampleRate = selectedSamples[0].Sample.SampleRate;
        selectedSamples = selectedSamples
            .Where(pair => pair.Sample.SampleRate == sampleRate)
            .ToArray();
        if (selectedSamples.Length == 0)
        {
            return null;
        }

        var frameCount = selectedSamples.Min(pair => pair.Sample.Samples.Length);
        if (frameCount <= 0)
        {
            return null;
        }

        var stems = new List<MimirObsPublishedAudioStem>();
        var mix = new float[frameCount];
        var mixWeight = 0.0;
        var sequence = 0L;
        foreach (var (feed, sample) in selectedSamples)
        {
            var sourceSamples = sample.Samples.AsSpan(0, frameCount).ToArray();
            var gain = Math.Clamp(feed.Gain, 0.0, 8.0);
            for (var index = 0; index < sourceSamples.Length; index++)
            {
                sourceSamples[index] = (float)Math.Clamp(sourceSamples[index] * gain, -1.0, 1.0);
                mix[index] += sourceSamples[index];
            }

            mixWeight += Math.Max(0.000001, gain);
            sequence = Math.Max(sequence, sample.Sequence);
            stems.Add(new MimirObsPublishedAudioStem(
                options.PerSourceStemPrefix + StableStemId(sample.SourceId),
                feed.DisplayName,
                sample.SourceId,
                0,
                frameCount,
                sampleRate,
                sample.Sequence,
                Configured: true,
                sourceSamples));
        }

        if (mixWeight > 0.0)
        {
            for (var index = 0; index < mix.Length; index++)
            {
                mix[index] = (float)Math.Clamp(mix[index] / mixWeight, -1.0, 1.0);
            }
        }

        stems.Insert(0, new MimirObsPublishedAudioStem(
            options.CompositeStemId,
            "Well configured composite",
            string.Join("+", selectedSamples.Select(static pair => pair.Sample.SourceId)),
            0,
            frameCount,
            sampleRate,
            sequence,
            Configured: true,
            mix));

        return new WellObsPublication(
            captureSequence,
            frameCount,
            selectedSamples.Select(static pair => pair.Sample.SourceId).ToArray(),
            new MimirObsStemPublicationSnapshot(
                "well-tightest-configured-composite",
                MimirObsAudioPublicationKind.FaustStemBus,
                stems,
                [],
                [],
                sequence));
    }

    private static IEnumerable<WellAudioFeed> ParseAudioFeeds(JsonElement root)
    {
        var audio = JsonArray(JsonGet(JsonGet(root, "configuredComposite"), "audio"));
        foreach (var item in audio)
        {
            yield return new WellAudioFeed(
                JsonText(item, "SourceId", "sourceId") ?? "unknown",
                JsonText(item, "DisplayName", "displayName") ?? JsonText(item, "SourceId", "sourceId") ?? "unknown",
                JsonBool(item, "Muted", "muted"),
                JsonBool(item, "Solo", "solo"),
                JsonNumber(item, "Gain", "gain", fallback: 1.0));
        }
    }

    private static IEnumerable<WellAudioSample> ParseAudioSamples(JsonElement root)
    {
        foreach (var item in JsonArray(JsonGet(root, "samples")))
        {
            var slice = JsonGet(item, "slice");
            var sample = JsonGet(item, "sample");
            if (!string.Equals(JsonText(slice, "Kind", "kind"), "Audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(JsonText(slice, "Status", "status"), "Ready", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var audio = JsonGet(sample, "audio");
            var body = JsonGet(item, "body");
            if (!string.Equals(JsonText(body, "status"), "inline", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(JsonText(body, "encoding"), "base64", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = Convert.FromBase64String(JsonText(body, "data") ?? "");
            var sourceId = JsonText(sample, "SourceId", "sourceId") ?? JsonText(slice, "SourceId", "sourceId") ?? "unknown";
            var sampleRate = (int)JsonNumber(audio, "SampleRate", "sampleRate");
            var channels = Math.Max(1, (int)JsonNumber(audio, "Channels", "channels", fallback: 1.0));
            var format = JsonText(audio, "SampleFormat", "sampleFormat") ?? "Float32";
            var decoded = DecodeMono(bytes, format, channels);
            if (sampleRate > 0 && decoded.Length > 0)
            {
                yield return new WellAudioSample(
                    sourceId,
                    sampleRate,
                    (long)JsonNumber(sample, "Sequence", "sequence"),
                    decoded);
            }
        }
    }

    private static IReadOnlyList<WellAudioFeed> SelectFeeds(IReadOnlyList<WellAudioFeed> feeds)
    {
        var unmuted = feeds.Where(static feed => !feed.Muted).ToArray();
        var solo = unmuted.Where(static feed => feed.Solo).ToArray();
        return solo.Length > 0 ? solo : unmuted;
    }

    private static float[] DecodeMono(byte[] bytes, string format, int channels)
    {
        var sampleSize = format switch
        {
            "Float32" => 4,
            "Int32" or "Int32LSB" => 4,
            "Int16" => 2,
            _ => 0,
        };
        if (sampleSize == 0)
        {
            return [];
        }

        var frameCount = bytes.Length / sampleSize / Math.Max(1, channels);
        var output = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0.0;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (frame * channels + channel) * sampleSize;
                sum += format switch
                {
                    "Float32" => BitConverter.ToSingle(bytes, offset),
                    "Int32" or "Int32LSB" => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)) / 2147483648.0,
                    "Int16" => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2)) / 32768.0,
                    _ => 0.0,
                };
            }

            output[frame] = (float)Math.Clamp(sum / channels, -1.0, 1.0);
        }

        return output;
    }

    private static string StableStemId(string sourceId)
    {
        var builder = new StringBuilder(sourceId.Length);
        foreach (var ch in sourceId)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
        }

        return builder.ToString().Trim('_');
    }

    private static JsonElement JsonGet(JsonElement element, params string[] names)
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

    private static IEnumerable<JsonElement> JsonArray(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    private static string? JsonText(JsonElement element, params string[] names)
    {
        var value = names.Length == 0 ? element : JsonGet(element, names);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double JsonNumber(JsonElement element, params string[] names) =>
        JsonNumber(element, names, 0.0);

    private static double JsonNumber(JsonElement element, string name1, string name2, double fallback) =>
        JsonNumber(element, [name1, name2], fallback);

    private static double JsonNumber(JsonElement element, string name, double fallback) =>
        JsonNumber(element, [name], fallback);

    private static double JsonNumber(JsonElement element, string[] names, double fallback)
    {
        var value = names.Length == 0 ? element : JsonGet(element, names);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : fallback;
    }

    private static bool JsonBool(JsonElement element, params string[] names)
    {
        var value = JsonGet(element, names);
        return value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);
    }
}

internal sealed record WellAudioFeed(string SourceId, string DisplayName, bool Muted, bool Solo, double Gain);

internal sealed record WellAudioSample(string SourceId, int SampleRate, long Sequence, float[] Samples);
