using System.Diagnostics;
using Mimir.Runtime.Synchronization;

if (args.Any(arg => string.Equals(arg, "--chirplet-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunChirpletSelfTest();
}

if (args.Any(arg => string.Equals(arg, "--chirp-bin-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunChirpBinSelfTest();
}

if (args.Any(arg => string.Equals(arg, "--bioacoustic-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunBioacousticSelfTest(ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate));
}

if (args.Any(arg => string.Equals(arg, "--standalone-bioacoustic-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunStandaloneBioacousticSelfTest(
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseDoubleOption(args, "--delay-samples", 1269.5));
}

if (args.Any(arg => string.Equals(arg, "--standalone-chirp-bin-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunStandaloneChirpBinSelfTest(
        ParseIntOption(args, "--sample-rate", MimirChirpBinTimeline.SampleRate),
        ParseDoubleOption(args, "--delay-samples", 1269.5));
}

if (args.Any(arg => string.Equals(arg, "--passive-sync-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunPassiveSyncSelfTest();
}

if (args.Any(arg => string.Equals(arg, "--hybrid-sync-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunHybridSyncSelfTest(ParseIntOption(args, "--sample-rate", MimirChirpBinTimeline.SampleRate));
}

if (args.Any(arg => string.Equals(arg, "--chirp-only-sync-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunActiveSyncSelfTest(ParseIntOption(args, "--sample-rate", MimirChirpBinTimeline.SampleRate), MimirAudioSyncMode.ChirpOnly);
}

if (args.Any(arg => string.Equals(arg, "--render-chirplet-f32", StringComparison.OrdinalIgnoreCase)))
{
    return RenderChirpletFloat32(
        ParseStringOption(args, "--output", "artifacts/asio/chirplet-f32.raw"),
        ParseIntOption(args, "--sample-rate", MimirChirpletTimeline.SampleRate),
        ParseDoubleOption(args, "--seconds", 3.0));
}

if (args.Any(arg => string.Equals(arg, "--render-chirp-bin-f32", StringComparison.OrdinalIgnoreCase)))
{
    return RenderChirpBinFloat32(
        ParseStringOption(args, "--output", "artifacts/asio/chirp-bin-f32.raw"),
        ParseIntOption(args, "--sample-rate", MimirChirpBinTimeline.SampleRate),
        ParseDoubleOption(args, "--seconds", 3.0),
        LoadOptionalCalibration(args)?.EmissionPlan);
}

if (args.Any(arg => string.Equals(arg, "--render-bioacoustic-f32", StringComparison.OrdinalIgnoreCase)))
{
    return RenderBioacousticFloat32(
        ParseStringOption(args, "--output", "artifacts/asio/bioacoustic-f32.raw"),
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseDoubleOption(args, "--seconds", 3.0));
}

if (args.Any(arg => string.Equals(arg, "--analyze-asio-f32", StringComparison.OrdinalIgnoreCase)))
{
    return AnalyzeAsioFloat32(
        ParseStringOption(args, "--input", "artifacts/asio/scarlett-chirplet-f32.raw"),
        ParseIntOption(args, "--sample-rate", MimirChirpletTimeline.SampleRate),
        ParseIntOption(args, "--channels", 4),
        ParseIntOption(args, "--reference-channel", 2),
        ParseIntOption(args, "--candidate-channel", -1),
        ParseStringOption(args, "--calibration", ""));
}

if (args.Any(arg => string.Equals(arg, "--calibrate-chirp-bin-asio-f32", StringComparison.OrdinalIgnoreCase)))
{
    return CalibrateChirpBinAsioFloat32(
        args,
        ParseStringOption(args, "--input", "artifacts/asio/scarlett-chirp-bin-192k-f32.raw"),
        ParseStringOption(args, "--output", "calibration/chirp-bin/latest.json"),
        ParseIntOption(args, "--sample-rate", MimirChirpBinTimeline.SampleRate),
        ParseIntOption(args, "--channels", 4),
        ParseIntOption(args, "--reference-channel", 2));
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
        $"sync {report.ReferenceSourceId}->{report.SourceId}: evidence={report.EvidenceKind} delaySamples={report.DelaySamples} fractionalDelaySamples={report.FractionalDelaySamples:0.000000} delayUs={report.DelayMicroseconds:0.000} delayMs={report.DelayMilliseconds:0.000} confidence={report.Confidence:0.000} bands={DescribeBands(report.BandResponses)} compared={report.ComparedSamples}");
}

foreach (var state in hub.AudioSynchronizationStates)
{
    Console.WriteLine(
        $"sync-state {state.ReferenceSourceId}->{state.SourceId}: smoothedDelaySamples={state.SmoothedDelaySamples:0.000000} delayUs={state.DelayMicroseconds:0.000} delayMs={state.DelayMilliseconds:0.000} sroPpm={state.SamplingRateOffsetPpm:0.000} confidence={state.Confidence:0.000} bands={DescribeBands(state.BandResponses)}");
}

foreach (var trace in hub.AudioSynchronizationDecodeTraces.OrderBy(trace => trace.SourceId, StringComparer.Ordinal))
{
    Console.WriteLine(
        $"sync-decode {trace.ReferenceSourceId}->{trace.SourceId}: status={trace.Status} compared={trace.ComparedSamples} rate={trace.SampleRate} refFrames={trace.ReferenceFrames} refAnchors={trace.ReferenceAnchors} refClock={trace.ReferenceClockConfidence:0.000} refEnergy={trace.ReferenceBestEnergy:0.000} candFrames={trace.CandidateFrames} candAnchors={trace.CandidateAnchors} candClock={trace.CandidateClockConfidence:0.000} candEnergy={trace.CandidateBestEnergy:0.000} matched={trace.MatchedEvents} confidence={trace.Confidence:0.000}");
}

foreach (var profile in hub.AudioChirpBinCalibrationProfiles)
{
    Console.WriteLine(DescribeCalibrationProfile("sync-calibration", profile));
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

static string DescribeCalibrationProfile(string prefix, MimirChirpBinCalibrationProfile profile)
{
    var top = profile.Bands.Count == 0
        ? "none"
        : string.Join(",", profile.StrongestBands(8).Select(band =>
            $"{band.CenterHz:0}Hz:mean={band.MeanEnergy:0.000}:rel={band.RelativeGain:0.000}:n={band.ObservationCount}"));
    var mae = double.IsFinite(profile.MeanAnchorErrorSamples)
        ? profile.MeanAnchorErrorSamples.ToString("0.000")
        : "none";
    return $"{prefix} {profile.SourceId}: sampleRate={profile.SampleRate} frames={profile.FrameCount} anchors={profile.AnchorCount} clock={profile.ClockConfidence:0.000} maeSamples={mae} usableBins={profile.UsableBandCount}/{profile.Bands.Count} top={top}";
}

static int RunChirpletSelfTest()
{
    var timeline = MimirChirpletTimeline.Default;
    var decoder = new MimirChirpletStreamDecoder(windowDuration: TimeSpan.FromSeconds(2));
    MimirChirpletStreamDecode decode = new([], [], [], null, []);
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

static int RunChirpBinSelfTest()
{
    var timeline = MimirChirpBinTimeline.Default;
    var samples = new List<float>();
    for (ulong segment = 0; segment < 6; segment++)
    {
        samples.AddRange(timeline.RenderSegmentMonoFloat(segment));
    }

    var decode = timeline.DecodeStreamWindow(samples.ToArray(), MimirChirpBinTimeline.SampleRate);
    var meanAbsoluteError = decode.ClockFit?.MeanAbsoluteErrorSamples ?? double.PositiveInfinity;
    Console.WriteLine(
        $"chirp-bin-self-test frames={decode.Frames.Count} symbols={decode.Symbols.Count} anchors={decode.Anchors.Count} " +
        $"clock={(decode.ClockFit is null ? "none" : decode.ClockFit.EffectiveSampleRate.ToString("0.000000"))} " +
        $"confidence={(decode.ClockFit?.Confidence ?? 0.0):0.000} mae={meanAbsoluteError:0.000000}");
    Console.WriteLine("chirp-bin-expected " + string.Join(",", Enumerable.Range(0, 12).Select(index => timeline.EventForIndex((ulong)index).SymbolId)));
    Console.WriteLine("chirp-bin-symbols " + string.Join(",", decode.Symbols.Take(12).Select(symbol => $"{symbol.SymbolId}@{symbol.SampleOffset:0}:{symbol.Energy:0.000}")));

    foreach (var anchor in decode.Anchors.Take(16))
    {
        var expected = timeline.EventForIndex(anchor.EventIndex).StartSeconds * MimirChirpBinTimeline.SampleRate;
        Console.WriteLine(
            $"chirp-bin-anchor event={anchor.EventIndex} actual={anchor.SampleOffset:0.000} expected={expected:0.000} " +
            $"error={anchor.SampleOffset - expected:0.000} confidence={anchor.Confidence:0.000}");
    }

    if (decode.ClockFit == null || decode.Anchors.Count < 16 || meanAbsoluteError > 2.0)
    {
        Console.Error.WriteLine("chirp-bin self-test failed: dechirp/bin decoder did not recover stable timeline anchors");
        return 1;
    }

    return 0;
}

static int RunStandaloneChirpBinSelfTest(int sampleRate, double delaySamples)
{
    var timeline = MimirChirpBinTimeline.Default;
    var samples = new List<float>();
    for (ulong segment = 0; segment < 8; segment++)
    {
        samples.AddRange(timeline.RenderSegmentMonoFloat(segment, sampleRate));
    }

    var delayed = ApplyFractionalDelay(samples.ToArray(), delaySamples);
    var decode = timeline.DecodeStreamWindow(delayed, sampleRate);
    var expectedDelay = delaySamples;
    var actualDelay = decode.ClockFit?.SourceOffsetSamples ?? double.NaN;
    var error = Math.Abs(actualDelay - expectedDelay);
    Console.WriteLine(
        $"standalone-chirp-bin-self-test frames={decode.Frames.Count} anchors={decode.Anchors.Count} " +
        $"clock={(decode.ClockFit is null ? "none" : decode.ClockFit.EffectiveSampleRate.ToString("0.000000"))} " +
        $"sourceOffset={actualDelay:0.000000} expected={expectedDelay:0.000000} errorSamples={error:0.000000} " +
        $"errorUs={error * 1_000_000.0 / sampleRate:0.000} confidence={(decode.ClockFit?.Confidence ?? 0.0):0.000}");

    foreach (var anchor in decode.Anchors.Take(16))
    {
        var expected = timeline.EventForIndex(anchor.EventIndex).StartSeconds * sampleRate + expectedDelay;
        Console.WriteLine(
            $"standalone-anchor event={anchor.EventIndex} actual={anchor.SampleOffset:0.000} expected={expected:0.000} " +
            $"error={anchor.SampleOffset - expected:0.000} confidence={anchor.Confidence:0.000}");
    }

    if (decode.ClockFit == null ||
        decode.Anchors.Count < 12 ||
        decode.ClockFit.Confidence < 0.70 ||
        error * 1_000_000.0 / sampleRate > 1.0)
    {
        Console.Error.WriteLine("standalone chirp-bin self-test failed: receiver did not recover canonical source time from delayed audio alone");
        return 1;
    }

    return 0;
}

static int RunBioacousticSelfTest(int sampleRate)
{
    var timeline = MimirBioacousticTimeline.Default;
    var samples = new List<float>();
    for (ulong segment = 0; segment < 6; segment++)
    {
        samples.AddRange(timeline.RenderSegmentMonoFloat(segment, sampleRate));
    }

    var decode = timeline.DecodeStreamWindow(samples.ToArray(), sampleRate);
    var meanAbsoluteError = decode.ClockFit?.MeanAbsoluteErrorSamples ?? double.PositiveInfinity;
    Console.WriteLine(
        $"bioacoustic-self-test frames={decode.Frames.Count} symbols={decode.Symbols.Count} anchors={decode.Anchors.Count} " +
        $"clock={(decode.ClockFit is null ? "none" : decode.ClockFit.EffectiveSampleRate.ToString("0.000000"))} " +
        $"confidence={(decode.ClockFit?.Confidence ?? 0.0):0.000} mae={meanAbsoluteError:0.000000}");
    Console.WriteLine("bioacoustic-expected " + string.Join(",", Enumerable.Range(0, 12).Select(index => timeline.EventForIndex((ulong)index).SymbolId)));
    Console.WriteLine("bioacoustic-symbols " + string.Join(",", decode.Symbols.Take(12).Select(symbol => $"{symbol.SymbolId}@{symbol.SampleOffset:0}:{symbol.Energy:0.000}")));

    foreach (var anchor in decode.Anchors.Take(16))
    {
        var expected = timeline.EventForIndex(anchor.EventIndex).StartSeconds * sampleRate;
        Console.WriteLine(
            $"bioacoustic-anchor event={anchor.EventIndex} actual={anchor.SampleOffset:0.000} expected={expected:0.000} " +
            $"error={anchor.SampleOffset - expected:0.000} confidence={anchor.Confidence:0.000}");
    }

    if (decode.ClockFit == null || decode.Anchors.Count < 10 || meanAbsoluteError > 12.0)
    {
        Console.Error.WriteLine("bioacoustic self-test failed: log-motif decoder did not recover stable timeline anchors");
        return 1;
    }

    return 0;
}

static int RunStandaloneBioacousticSelfTest(int sampleRate, double delaySamples)
{
    var timeline = MimirBioacousticTimeline.Default;
    var samples = new List<float>();
    for (ulong segment = 0; segment < 8; segment++)
    {
        samples.AddRange(timeline.RenderSegmentMonoFloat(segment, sampleRate));
    }

    var delayed = ApplyFractionalDelay(samples.ToArray(), delaySamples);
    var decode = timeline.DecodeStreamWindow(delayed, sampleRate);
    var expectedDelay = delaySamples;
    var actualDelay = decode.ClockFit?.SourceOffsetSamples ?? double.NaN;
    var error = Math.Abs(actualDelay - expectedDelay);
    Console.WriteLine(
        $"standalone-bioacoustic-self-test frames={decode.Frames.Count} anchors={decode.Anchors.Count} " +
        $"clock={(decode.ClockFit is null ? "none" : decode.ClockFit.EffectiveSampleRate.ToString("0.000000"))} " +
        $"sourceOffset={actualDelay:0.000000} expected={expectedDelay:0.000000} errorSamples={error:0.000000} " +
        $"errorUs={error * 1_000_000.0 / sampleRate:0.000} confidence={(decode.ClockFit?.Confidence ?? 0.0):0.000}");

    foreach (var anchor in decode.Anchors.Take(16))
    {
        var expected = timeline.EventForIndex(anchor.EventIndex).StartSeconds * sampleRate + expectedDelay;
        Console.WriteLine(
            $"standalone-bioacoustic-anchor event={anchor.EventIndex} actual={anchor.SampleOffset:0.000} expected={expected:0.000} " +
            $"error={anchor.SampleOffset - expected:0.000} confidence={anchor.Confidence:0.000}");
    }

    if (decode.ClockFit == null ||
        decode.Anchors.Count < 12 ||
        decode.ClockFit.Confidence < 0.60 ||
        error * 1_000_000.0 / sampleRate > 1.0)
    {
        Console.Error.WriteLine("standalone bioacoustic self-test failed: receiver did not recover canonical source time from delayed audio alone");
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
    var random = new Random(1729);
    var noise = new double[sampleCount];
    for (var index = 0; index < sampleCount; index++)
    {
        noise[index] = random.NextDouble() * 2.0 - 1.0;
    }

    for (var index = 0; index < sampleCount; index++)
    {
        reference[index] = (float)(
            0.45 * Math.Sin(2.0 * Math.PI * 611.0 * index / sampleRate) +
            0.28 * Math.Sin(2.0 * Math.PI * 1471.0 * index / sampleRate) +
            0.12 * Math.Sin(2.0 * Math.PI * 3253.0 * index / sampleRate) +
            0.08 * noise[index]);
        var source = index - delaySamples;
        candidate[index] = source >= 0 ? reference[source] : 0.0f;
    }

    var estimator = new MimirPassiveAudioSynchronizationEstimator();
    var estimate = estimator.Estimate(reference, candidate, sampleRate);
    var error = estimate.DelaySamples - delaySamples;
    Console.WriteLine(
        $"passive-sync-self-test delaySamples={estimate.DelaySamples:0.000} expected={delaySamples} " +
        $"error={error:0.000} confidence={estimate.Confidence:0.000} peak={estimate.Peak:0.000000} secondPeak={estimate.SecondPeak:0.000000} floor={estimate.NoiseFloor:0.000000} status={estimate.Status}");
    if (Math.Abs(error) > 1.0 || estimate.Confidence < 0.08 || estimate.Confidence >= 1.0)
    {
        Console.Error.WriteLine("passive sync self-test failed: delayed program signal did not produce a confident passive estimate");
        return 1;
    }

    var impossible = estimator.Estimate(candidate, reference, sampleRate);
    Console.WriteLine(
        $"passive-sync-negative-test delaySamples={impossible.DelaySamples:0.000} confidence={impossible.Confidence:0.000} status={impossible.Status}");
    if (impossible.DelaySamples < 0.0 && impossible.Confidence > 0.0)
    {
        Console.Error.WriteLine("passive sync self-test failed: negative lag kept confidence");
        return 1;
    }

    return 0;
}

static int RunHybridSyncSelfTest(int sampleRate)
{
    return RunActiveSyncSelfTest(sampleRate, MimirAudioSyncMode.Hybrid);
}

static int RunActiveSyncSelfTest(int sampleRate, MimirAudioSyncMode mode)
{
    const string referenceSourceId = "loopback-test";
    const string candidateSourceId = "mic-test";
    var delaySamples = 317.375 * sampleRate / (double)MimirBioacousticTimeline.SampleRate;

    var segments = Enumerable.Range(0, 4)
        .Select(segment => MimirBioacousticTimeline.Default.RenderSegmentMonoFloat((ulong)segment, sampleRate))
        .ToArray();
    var reference = new float[segments.Sum(segment => segment.Length)];
    var writeOffset = 0;
    foreach (var segment in segments)
    {
        Array.Copy(segment, 0, reference, writeOffset, segment.Length);
        writeOffset += segment.Length;
    }
    var candidate = ApplyFractionalDelay(reference, delaySamples);

    var referenceBuffer = new MimirRollingStreamBuffer(
        new MimirStreamDescriptor(referenceSourceId, MimirStreamKind.Audio, MimirStreamOrigin.LocalDevice),
        TimeSpan.FromSeconds(5));
    var candidateBuffer = new MimirRollingStreamBuffer(
        new MimirStreamDescriptor(candidateSourceId, MimirStreamKind.Audio, MimirStreamOrigin.LocalDevice),
        TimeSpan.FromSeconds(5));

    AppendFloatBlock(referenceBuffer, referenceSourceId, reference, sampleRate);
    AppendFloatBlock(candidateBuffer, candidateSourceId, candidate, sampleRate);

    var analyzer = new MimirAudioSynchronizationAnalyzer();
    var reports = analyzer.Analyze([referenceBuffer, candidateBuffer], referenceSourceId, mode);
    var report = reports.SingleOrDefault();
    foreach (var trace in analyzer.LastDecodeTraces)
    {
        Console.WriteLine(
            $"{SyncModeLabel(mode)}-sync-trace {trace.ReferenceSourceId}->{trace.SourceId}: status={trace.Status} compared={trace.ComparedSamples} refFrames={trace.ReferenceFrames} refAnchors={trace.ReferenceAnchors} candFrames={trace.CandidateFrames} candAnchors={trace.CandidateAnchors} matched={trace.MatchedEvents} confidence={trace.Confidence:0.000}");
    }

    if (report == null)
    {
        Console.Error.WriteLine($"{SyncModeLabel(mode)} sync self-test failed: analyzer did not report from a short bioacoustic window");
        return 1;
    }

    var error = Math.Abs(report.FractionalDelaySamples - delaySamples);
    Console.WriteLine(
        $"{SyncModeLabel(mode)}-sync-self-test evidence={report.EvidenceKind} delaySamples={report.FractionalDelaySamples:0.000000} delayUs={report.DelayMicroseconds:0.000} expected={delaySamples:0.000000} errorSamples={error:0.000000} errorUs={error * 1_000_000.0 / report.SampleRate:0.000} confidence={report.Confidence:0.000} events={report.TimelineMatchedEvents} compared={report.ComparedSamples}");
    if (mode == MimirAudioSyncMode.Hybrid &&
        string.Equals(report.EvidenceKind, "passive", StringComparison.Ordinal))
    {
        return report.Confidence > 0.50 &&
            error * 1_000_000.0 / report.SampleRate < 5.0
                ? 0
                : 1;
    }

    return string.Equals(report.EvidenceKind, "bioacoustic", StringComparison.Ordinal) &&
        report.TimelineMatchedEvents >= 1 &&
        report.Confidence > 0.70 &&
        error * 1_000_000.0 / report.SampleRate < 1.0
            ? 0
            : 1;
}

static string SyncModeLabel(MimirAudioSyncMode mode)
{
    return mode == MimirAudioSyncMode.ChirpOnly ? "chirp-only" : "hybrid";
}

static int RenderChirpletFloat32(string outputPath, int sampleRate, double seconds)
{
    var segmentCount = Math.Max(1, (int)Math.Ceiling(seconds / MimirChirpletTimeline.SegmentSeconds));
    var samples = new List<float>(Math.Max(1, (int)Math.Ceiling(seconds * sampleRate)));
    for (var segment = 0; segment < segmentCount; segment++)
    {
        samples.AddRange(MimirChirpletTimeline.Default.RenderSegmentMonoFloat((ulong)segment, sampleRate));
    }

    var requestedSamples = Math.Max(1, (int)Math.Round(seconds * sampleRate));
    if (samples.Count > requestedSamples)
    {
        samples.RemoveRange(requestedSamples, samples.Count - requestedSamples);
    }

    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var bytes = new byte[samples.Count * sizeof(float)];
    for (var index = 0; index < samples.Count; index++)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float), sizeof(float)), samples[index]);
    }

    File.WriteAllBytes(outputPath, bytes);
    Console.WriteLine($"chirplet-render-f32 path={outputPath} sampleRate={sampleRate} seconds={seconds:0.000} samples={samples.Count}");
    return 0;
}

static int RenderChirpBinFloat32(
    string outputPath,
    int sampleRate,
    double seconds,
    MimirChirpBinCodebookPlan? codebookPlan = null)
{
    var segmentCount = Math.Max(1, (int)Math.Ceiling(seconds / MimirChirpBinTimeline.SegmentSeconds));
    var samples = new List<float>(Math.Max(1, (int)Math.Ceiling(seconds * sampleRate)));
    for (var segment = 0; segment < segmentCount; segment++)
    {
        samples.AddRange(MimirChirpBinTimeline.Default.RenderSegmentMonoFloat((ulong)segment, sampleRate, codebookPlan));
    }

    var requestedSamples = Math.Max(1, (int)Math.Round(seconds * sampleRate));
    if (samples.Count > requestedSamples)
    {
        samples.RemoveRange(requestedSamples, samples.Count - requestedSamples);
    }

    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var bytes = new byte[samples.Count * sizeof(float)];
    for (var index = 0; index < samples.Count; index++)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float), sizeof(float)), samples[index]);
    }

    File.WriteAllBytes(outputPath, bytes);
    Console.WriteLine($"chirp-bin-render-f32 path={outputPath} sampleRate={sampleRate} seconds={seconds:0.000} samples={samples.Count} adaptiveSymbols={codebookPlan?.ReliableSymbolIds.Count ?? MimirChirpBinTimeline.SymbolCount} order={codebookPlan?.RecommendedOrder ?? MimirChirpBinTimeline.TimelineOrder}");
    return 0;
}

static int RenderBioacousticFloat32(string outputPath, int sampleRate, double seconds)
{
    var segmentCount = Math.Max(1, (int)Math.Ceiling(seconds / MimirBioacousticTimeline.SegmentSeconds));
    var samples = new List<float>(Math.Max(1, (int)Math.Ceiling(seconds * sampleRate)));
    for (var segment = 0; segment < segmentCount; segment++)
    {
        samples.AddRange(MimirBioacousticTimeline.Default.RenderSegmentMonoFloat((ulong)segment, sampleRate));
    }

    var requestedSamples = Math.Max(1, (int)Math.Round(seconds * sampleRate));
    if (samples.Count > requestedSamples)
    {
        samples.RemoveRange(requestedSamples, samples.Count - requestedSamples);
    }

    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var bytes = new byte[samples.Count * sizeof(float)];
    for (var index = 0; index < samples.Count; index++)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float), sizeof(float)), samples[index]);
    }

    File.WriteAllBytes(outputPath, bytes);
    Console.WriteLine($"bioacoustic-render-f32 path={outputPath} sampleRate={sampleRate} seconds={seconds:0.000} samples={samples.Count} words={MimirBioacousticTimeline.WordCount} speakers={MimirBioacousticTimeline.SpeakerCount}");
    return 0;
}

static MimirChirpBinCalibrationModel? LoadOptionalCalibration(string[] args)
{
    var path = ParseStringOption(args, "--calibration", "");
    return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
        ? null
        : MimirChirpBinCalibrationModel.Load(path);
}

static int AnalyzeAsioFloat32(
    string inputPath,
    int sampleRate,
    int channels,
    int referenceChannel,
    int candidateChannel,
    string calibrationPath)
{
    if (channels <= 0)
    {
        Console.Error.WriteLine("asio-f32 analysis failed: channel count must be positive");
        return 1;
    }

    if (referenceChannel < 0 || referenceChannel >= channels)
    {
        Console.Error.WriteLine("asio-f32 analysis failed: reference channel is outside the capture channel count");
        return 1;
    }

    if (candidateChannel >= channels)
    {
        Console.Error.WriteLine("asio-f32 analysis failed: candidate channel is outside the capture channel count");
        return 1;
    }

    var bytes = File.ReadAllBytes(inputPath);
    var frameCount = bytes.Length / (sizeof(float) * channels);
    if (frameCount == 0)
    {
        Console.Error.WriteLine("asio-f32 analysis failed: capture file contains no complete frames");
        return 1;
    }

    var channelSamples = new float[channels][];
    for (var channel = 0; channel < channels; channel++)
    {
        channelSamples[channel] = new float[frameCount];
    }

    var span = bytes.AsSpan(0, frameCount * channels * sizeof(float));
    for (var frame = 0; frame < frameCount; frame++)
    {
        for (var channel = 0; channel < channels; channel++)
        {
            channelSamples[channel][frame] = BitConverter.ToSingle(span.Slice((frame * channels + channel) * sizeof(float), sizeof(float)));
        }
    }

    var buffers = new List<MimirRollingStreamBuffer>(channels);
    for (var channel = 0; channel < channels; channel++)
    {
        var sourceId = $"asio-ch{channel}";
        var buffer = new MimirRollingStreamBuffer(
            new MimirStreamDescriptor(sourceId, MimirStreamKind.Audio, MimirStreamOrigin.LocalDevice),
            TimeSpan.FromSeconds(10));
        AppendFloatBlock(buffer, sourceId, channelSamples[channel], sampleRate);
        buffers.Add(buffer);
    }

    var referenceSourceId = $"asio-ch{referenceChannel}";
    var candidates = candidateChannel >= 0
        ? new HashSet<string>(StringComparer.Ordinal) { $"asio-ch{candidateChannel}" }
        : null;
    var analyzer = new MimirAudioSynchronizationAnalyzer();
    var calibration = string.IsNullOrWhiteSpace(calibrationPath) || !File.Exists(calibrationPath)
        ? null
        : MimirChirpBinCalibrationModel.Load(calibrationPath);
    var reports = analyzer.Analyze(buffers, referenceSourceId, MimirAudioSyncMode.ChirpOnly, candidates, calibration);

    Console.WriteLine($"asio-f32-analysis input={inputPath} sampleRate={sampleRate} channels={channels} frames={frameCount} reference={referenceSourceId} candidate={(candidateChannel >= 0 ? $"asio-ch{candidateChannel}" : "all")} calibration={(calibration == null ? "none" : calibrationPath)}");
    foreach (var trace in analyzer.LastDecodeTraces.OrderBy(trace => trace.SourceId, StringComparer.Ordinal))
    {
        Console.WriteLine(
            $"asio-f32-trace {trace.ReferenceSourceId}->{trace.SourceId}: status={trace.Status} compared={trace.ComparedSamples} refFrames={trace.ReferenceFrames} refAnchors={trace.ReferenceAnchors} refClock={trace.ReferenceClockConfidence:0.000} candFrames={trace.CandidateFrames} candAnchors={trace.CandidateAnchors} candClock={trace.CandidateClockConfidence:0.000} matched={trace.MatchedEvents} confidence={trace.Confidence:0.000}");
    }

    foreach (var profile in analyzer.LastCalibrationProfiles)
    {
        Console.WriteLine(DescribeCalibrationProfile("asio-f32-calibration", profile));
    }

    foreach (var report in reports.OrderBy(report => report.SourceId, StringComparer.Ordinal))
    {
        Console.WriteLine(
            $"asio-f32-sync {report.ReferenceSourceId}->{report.SourceId}: evidence={report.EvidenceKind} delaySamples={report.FractionalDelaySamples:0.000000} delayUs={report.DelayMicroseconds:0.000} confidence={report.Confidence:0.000} events={report.TimelineMatchedEvents} bands={DescribeBands(report.BandResponses)} compared={report.ComparedSamples}");
    }

    return reports.Count > 0 ? 0 : 1;
}

static int CalibrateChirpBinAsioFloat32(
    string[] args,
    string inputPath,
    string outputPath,
    int sampleRate,
    int channels,
    int referenceChannel)
{
    var outputSourceId = ParseStringOption(args, "--output-source-id", "main-speakers");
    if (args.Any(arg => string.Equals(arg, "--capture-asio", StringComparison.OrdinalIgnoreCase)))
    {
        var probe = ParseStringOption(
            args,
            "--asio-probe",
            "native/probes/asio_audio_cadence/build/Release/asio_audio_cadence.exe");
        var seconds = ParseDoubleOption(args, "--seconds", 6.0);
        var gain = ParseDoubleOption(args, "--gain", 1.0);
        var renderPath = Path.Combine("artifacts", "asio", $"chirp-bin-calibration-{sampleRate}.raw");
        RenderChirpBinFloat32(renderPath, sampleRate, seconds, LoadOptionalCalibration(args)?.EmissionPlan);
        var probeExit = RunAsioProbeCapture(probe, renderPath, inputPath, sampleRate, seconds, gain);
        if (probeExit != 0)
        {
            return probeExit;
        }
    }

    var channelSamples = ReadInterleavedFloat32(inputPath, channels, out var frameCount);
    if (frameCount == 0)
    {
        Console.Error.WriteLine("chirp-bin calibration failed: capture file contains no complete frames");
        return 1;
    }

    if (referenceChannel < 0 || referenceChannel >= channels)
    {
        Console.Error.WriteLine("chirp-bin calibration failed: reference channel is outside the capture channel count");
        return 1;
    }

    var decodes = new Dictionary<string, MimirChirpletStreamDecode>(StringComparer.Ordinal);
    for (var channel = 0; channel < channels; channel++)
    {
        decodes[$"asio-ch{channel}"] = MimirChirpBinTimeline.Default.DecodeStreamWindow(channelSamples[channel], sampleRate);
    }

    var referenceSourceId = $"asio-ch{referenceChannel}";
    var model = MimirChirpBinCalibrationModel.FromDecodes(referenceSourceId, sampleRate, decodes, outputSourceId);
    model.Save(outputPath);
    Console.WriteLine($"chirp-bin-calibration path={outputPath} input={inputPath} sampleRate={sampleRate} channels={channels} frames={frameCount} reference={referenceSourceId}");
    foreach (var path in model.Paths)
    {
        var reliable = path.CodebookPlan.ReliableSymbolIds.Count == 0
            ? "none"
            : string.Join(",", path.CodebookPlan.ReliableSymbolIds.Take(16));
        Console.WriteLine(
            $"chirp-bin-calibration-path {path.SourceId}: frames={path.Profile.FrameCount} anchors={path.Profile.AnchorCount} clock={path.Profile.ClockConfidence:0.000} usableBins={path.Profile.UsableBandCount}/{path.Profile.Bands.Count} symbols={path.Symbols.Count} reliableSymbols={path.CodebookPlan.ReliableSymbolCount} recommendedOrder={path.CodebookPlan.RecommendedOrder} reliable={reliable}");
        foreach (var hypothesis in path.DelayHypotheses.Take(3))
        {
            Console.WriteLine(
                $"chirp-bin-delay-hypothesis {path.SourceId}: delaySamples={hypothesis.DelaySamples:0.000} binShift={hypothesis.BinShift} support={hypothesis.SupportCount} confidence={hypothesis.Confidence:0.000} residual={hypothesis.MeanResidualSamples:0.000}");
        }
    }

    return 0;
}

static int RunAsioProbeCapture(
    string probePath,
    string playbackPath,
    string capturePath,
    int sampleRate,
    double seconds,
    double gain)
{
    var startInfo = new ProcessStartInfo(Path.GetFullPath(probePath))
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("--set-sample-rate");
    startInfo.ArgumentList.Add(sampleRate.ToString());
    startInfo.ArgumentList.Add("--play-f32-mono");
    startInfo.ArgumentList.Add(Path.GetFullPath(playbackPath));
    startInfo.ArgumentList.Add("--play-gain");
    startInfo.ArgumentList.Add(gain.ToString("0.#####"));
    startInfo.ArgumentList.Add("--record-f32-interleaved");
    startInfo.ArgumentList.Add(Path.GetFullPath(capturePath));
    startInfo.ArgumentList.Add("--capture-seconds");
    startInfo.ArgumentList.Add(seconds.ToString("0.###"));

    using var process = Process.Start(startInfo);
    if (process == null)
    {
        Console.Error.WriteLine($"chirp-bin calibration failed: could not start ASIO probe {probePath}");
        return 1;
    }

    process.OutputDataReceived += (_, e) =>
    {
        if (e.Data != null)
        {
            Console.WriteLine(e.Data);
        }
    };
    process.ErrorDataReceived += (_, e) =>
    {
        if (e.Data != null)
        {
            Console.Error.WriteLine(e.Data);
        }
    };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    process.WaitForExit();
    return process.ExitCode;
}

static float[][] ReadInterleavedFloat32(string inputPath, int channels, out int frameCount)
{
    if (channels <= 0)
    {
        frameCount = 0;
        return [];
    }

    var bytes = File.ReadAllBytes(inputPath);
    frameCount = bytes.Length / (sizeof(float) * channels);
    var channelSamples = new float[channels][];
    for (var channel = 0; channel < channels; channel++)
    {
        channelSamples[channel] = new float[frameCount];
    }

    var span = bytes.AsSpan(0, frameCount * channels * sizeof(float));
    for (var frame = 0; frame < frameCount; frame++)
    {
        for (var channel = 0; channel < channels; channel++)
        {
            channelSamples[channel][frame] = BitConverter.ToSingle(span.Slice((frame * channels + channel) * sizeof(float), sizeof(float)));
        }
    }

    return channelSamples;
}

static int ParseIntOption(string[] args, string name, int fallback)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[index + 1], out var value))
        {
            return value;
        }
    }

    return fallback;
}

static float[] ApplyFractionalDelay(float[] source, double delaySamples)
{
    var delayed = new float[source.Length];
    for (var index = 0; index < delayed.Length; index++)
    {
        var sourcePosition = index - delaySamples;
        if (sourcePosition < 0.0 || sourcePosition >= source.Length - 1)
        {
            continue;
        }

        var left = (int)Math.Floor(sourcePosition);
        var fraction = sourcePosition - left;
        delayed[index] = (float)(source[left] + (source[left + 1] - source[left]) * fraction);
    }

    return delayed;
}

static void AppendFloatBlock(MimirRollingStreamBuffer buffer, string sourceId, float[] samples, int sampleRate)
{
    var bytes = new byte[samples.Length * sizeof(float)];
    for (var index = 0; index < samples.Length; index++)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float), sizeof(float)), samples[index]);
    }

    buffer.Append(new MimirStreamSample(
        sourceId,
        MimirStreamKind.Audio,
        MimirStreamOrigin.LocalDevice,
        1_000_000_000L,
        1_000_000_000L,
        1,
        0,
        bytes.Length,
        bytes,
        AudioBlock: new MimirAudioBlockDescriptor(
            sampleRate,
            1,
            MimirAudioSampleFormat.Float32,
            samples.Length,
            1_000_000_000L)));
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
