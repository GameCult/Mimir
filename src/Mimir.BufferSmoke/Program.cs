using Mimir.Runtime.Synchronization;

if (args.Any(arg => string.Equals(arg, "--chirplet-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunChirpletSelfTest();
}

if (args.Any(arg => string.Equals(arg, "--passive-sync-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunPassiveSyncSelfTest();
}

var duration = TimeSpan.FromSeconds(ParseDoubleOption(args, "--seconds", 10.0));
var pollDelay = TimeSpan.FromMilliseconds(ParseDoubleOption(args, "--poll-ms", 10.0));
var requireSamples = args.Any(arg => string.Equals(arg, "--require-samples", StringComparison.OrdinalIgnoreCase));
var syncReference = ParseStringOption(args, "--sync-reference", "loopback-scarlett-speakers");
using var configuration = new DisposableConfiguration(MimirRuntimeConfiguration.Load());
using var hub = new MimirSynchronizationHub(configuration.Value.Settings);
var syncMode = configuration.Value.Settings.Audio.Mode;

foreach (var source in configuration.Value.Sources)
{
    configuration.Detach(source);
    hub.AddSource(source);
}

var deadline = DateTimeOffset.UtcNow + duration;
var nextSyncPoll = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
while (DateTimeOffset.UtcNow < deadline)
{
    hub.PollSources(maxSamplesPerSource: 4096);
    if (DateTimeOffset.UtcNow >= nextSyncPoll)
    {
        hub.AnalyzeAudioSynchronization(syncReference, syncMode);
        nextSyncPoll += TimeSpan.FromSeconds(1);
    }

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

var reports = hub.AnalyzeAudioSynchronization(syncReference, syncMode);
foreach (var report in reports.OrderBy(report => report.SourceId, StringComparer.Ordinal))
{
    Console.WriteLine(
        $"sync {report.ReferenceSourceId}->{report.SourceId}: evidence={report.EvidenceKind} delaySamples={report.DelaySamples} fractionalDelaySamples={report.FractionalDelaySamples:0.000} delayMs={report.DelayMilliseconds:0.000} confidence={report.Confidence:0.000} bands={DescribeBands(report.BandResponses)} compared={report.ComparedSamples}");
}

foreach (var state in hub.AudioSynchronizationStates)
{
    Console.WriteLine(
        $"sync-state {state.ReferenceSourceId}->{state.SourceId}: smoothedDelaySamples={state.SmoothedDelaySamples:0.000} delayMs={state.DelayMilliseconds:0.000} sroPpm={state.SamplingRateOffsetPpm:0.000} confidence={state.Confidence:0.000} bands={DescribeBands(state.BandResponses)}");
}

foreach (var trace in hub.AudioSynchronizationDecodeTraces.OrderBy(trace => trace.SourceId, StringComparer.Ordinal))
{
    Console.WriteLine(
        $"sync-decode {trace.ReferenceSourceId}->{trace.SourceId}: status={trace.Status} compared={trace.ComparedSamples} rate={trace.SampleRate} refFrames={trace.ReferenceFrames} refAnchors={trace.ReferenceAnchors} refClock={trace.ReferenceClockConfidence:0.000} refEnergy={trace.ReferenceBestEnergy:0.000} candFrames={trace.CandidateFrames} candAnchors={trace.CandidateAnchors} candClock={trace.CandidateClockConfidence:0.000} candEnergy={trace.CandidateBestEnergy:0.000} matched={trace.MatchedEvents} confidence={trace.Confidence:0.000}");
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

static string DescribeBands(IReadOnlyList<MimirChirpletBandResponse> bands)
{
    return bands.Count == 0
        ? "none"
        : string.Join(",", bands.Select(band => $"{band.CenterHz:0}Hz:{band.Energy:0.000}"));
}

static int RunChirpletSelfTest()
{
    var timeline = MimirChirpletTimeline.Default;
    var decoder = new MimirChirpletStreamDecoder(windowDuration: TimeSpan.FromSeconds(2));
    MimirChirpletStreamDecode decode = new([], [], [], null);
    for (ulong segment = 0; segment < 4; segment++)
    {
        decode = decoder.Append(timeline.RenderSegmentMonoFloat(segment));
    }

    var meanAbsoluteError = decode.ClockFit?.MeanAbsoluteErrorSamples ?? double.PositiveInfinity;
    Console.WriteLine(
        $"chirplet-self-test frames={decode.Frames.Count} symbols={decode.Symbols.Count} anchors={decode.Anchors.Count} " +
        $"clock={(decode.ClockFit is null ? "none" : decode.ClockFit.EffectiveSampleRate.ToString("0.000000"))} " +
        $"confidence={(decode.ClockFit?.Confidence ?? 0.0):0.000} mae={meanAbsoluteError:0.000000}");

    foreach (var anchor in decode.Anchors)
    {
        var expected = timeline.EventForIndex(anchor.EventIndex).StartSeconds * MimirChirpletTimeline.SampleRate;
        Console.WriteLine(
            $"chirplet-anchor event={anchor.EventIndex} actual={anchor.SampleOffset:0.000} expected={expected:0.000} " +
            $"error={anchor.SampleOffset - expected:0.000} confidence={anchor.Confidence:0.000}");
    }

    if (decode.ClockFit == null || decode.Anchors.Count < 12 || meanAbsoluteError > 0.25)
    {
        Console.Error.WriteLine("chirplet self-test failed: canonical timeline did not decode to sub-frame anchors");
        return 1;
    }

    return 0;
}

static int RunPassiveSyncSelfTest()
{
    const int sampleRate = 48_000;
    const int delaySamples = 1732;
    const int sampleCount = 48_000;
    var reference = new float[sampleCount];
    var candidate = new float[sampleCount];
    for (var index = 0; index < sampleCount; index++)
    {
        reference[index] = (float)(
            0.45 * Math.Sin(2.0 * Math.PI * 611.0 * index / sampleRate) +
            0.28 * Math.Sin(2.0 * Math.PI * 1471.0 * index / sampleRate) +
            0.12 * Math.Sin(2.0 * Math.PI * 3253.0 * index / sampleRate));
        var source = index - delaySamples;
        candidate[index] = source >= 0 ? reference[source] : 0.0f;
    }

    var estimator = new MimirPassiveAudioSynchronizationEstimator();
    var estimate = estimator.Estimate(reference, candidate, sampleRate);
    var error = estimate.DelaySamples - delaySamples;
    Console.WriteLine(
        $"passive-sync-self-test delaySamples={estimate.DelaySamples:0.000} expected={delaySamples} " +
        $"error={error:0.000} confidence={estimate.Confidence:0.000} peak={estimate.Peak:0.000000} floor={estimate.NoiseFloor:0.000000} status={estimate.Status}");
    if (Math.Abs(error) > 1.0 || estimate.Confidence < 0.08)
    {
        Console.Error.WriteLine("passive sync self-test failed: delayed program signal did not produce a confident passive estimate");
        return 1;
    }

    return 0;
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
