using Mimir.Runtime.Synchronization;

var duration = TimeSpan.FromSeconds(ParseDoubleOption(args, "--seconds", 10.0));
var pollDelay = TimeSpan.FromMilliseconds(ParseDoubleOption(args, "--poll-ms", 10.0));
var requireSamples = args.Any(arg => string.Equals(arg, "--require-samples", StringComparison.OrdinalIgnoreCase));
var syncReference = ParseStringOption(args, "--sync-reference", "loopback-scarlett-speakers");
using var configuration = new DisposableConfiguration(MimirRuntimeConfiguration.Load());
using var hub = new MimirSynchronizationHub(configuration.Value.Settings);

foreach (var source in configuration.Value.Sources)
{
    configuration.Detach(source);
    hub.AddSource(source);
}

var deadline = DateTimeOffset.UtcNow + duration;
while (DateTimeOffset.UtcNow < deadline)
{
    hub.PollSources(maxSamplesPerSource: 4096);
    await Task.Delay(pollDelay).ConfigureAwait(false);
}

hub.PollSources(maxSamplesPerSource: 16384);

Console.WriteLine($"sources={hub.SourceCount} ingested={hub.IngestedSamples} buffers={hub.Buffers.Buffers.Count}");
var emptyBuffers = new List<string>();
foreach (var buffer in hub.Buffers.Buffers.OrderBy(buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal))
{
    var latest = buffer.Latest;
    if (latest?.VideoFrame is { } frame)
    {
        Console.WriteLine(
            $"{buffer.Descriptor.SourceId}: count={buffer.Count} edgeNs={buffer.EdgeNs} latest={frame.Width}x{frame.Height} {frame.PixelFormat} bytes={latest.Value.ByteLength}");
    }
    else if (latest?.AudioBlock is { } block)
    {
        Console.WriteLine(
            $"{buffer.Descriptor.SourceId}: count={buffer.Count} edgeNs={buffer.EdgeNs} latest={block.Channels}ch {block.SampleRate}Hz {block.SampleFormat} frames={block.FrameCount} bytes={latest.Value.ByteLength}");
    }
    else
    {
        Console.WriteLine($"{buffer.Descriptor.SourceId}: count={buffer.Count} edgeNs={buffer.EdgeNs}");
    }

    if (buffer.Count == 0)
    {
        emptyBuffers.Add(buffer.Descriptor.SourceId);
    }
}

var reports = hub.AnalyzeAudioSynchronization(syncReference);
foreach (var report in reports.OrderBy(report => report.SourceId, StringComparer.Ordinal))
{
    Console.WriteLine(
        $"sync {report.ReferenceSourceId}->{report.SourceId}: delaySamples={report.DelaySamples} delayMs={report.DelayMilliseconds:0.000} confidence={report.Confidence:0.000} compared={report.ComparedSamples}");
}

var aligned = hub.BuildAlignedAudioFrame(syncReference);
if (aligned != null)
{
    Console.WriteLine(
        $"aligned-audio reference={aligned.ReferenceSourceId} sampleRate={aligned.SampleRate} frameCount={aligned.FrameCount} channels={aligned.Channels.Count}");
    foreach (var channel in aligned.Channels)
    {
        Console.WriteLine(
            $"aligned-channel {channel.SourceId}: delaySamples={channel.DelaySamples} confidence={channel.Confidence:0.000}");
    }
}

if (requireSamples && emptyBuffers.Count > 0)
{
    Console.Error.WriteLine($"empty buffers: {string.Join(", ", emptyBuffers)}");
    return 1;
}

return 0;

static double ParseDoubleOption(IReadOnlyList<string> args, string name, double fallback)
{
    for (var index = 0; index < args.Count - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)
            && double.TryParse(args[index + 1], out var parsed)
            && parsed > 0)
        {
            return parsed;
        }
    }

    return fallback;
}

static string ParseStringOption(IReadOnlyList<string> args, string name, string fallback)
{
    for (var index = 0; index < args.Count - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return args[index + 1];
        }
    }

    return fallback;
}

internal sealed class DisposableConfiguration : IDisposable
{
    private readonly List<IMimirStreamSource> ownedSources;

    public DisposableConfiguration(MimirRuntimeConfiguration value)
    {
        Value = value;
        ownedSources = value.Sources.ToList();
    }

    public MimirRuntimeConfiguration Value { get; }

    public void Detach(IMimirStreamSource source)
    {
        ownedSources.Remove(source);
    }

    public void Dispose()
    {
        foreach (var source in ownedSources)
        {
            source.Dispose();
        }
    }
}
