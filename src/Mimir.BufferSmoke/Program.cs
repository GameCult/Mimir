using System.Diagnostics;
using System.Numerics;
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

if (args.Any(arg => string.Equals(arg, "--bioacoustic-cepstral-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return RunBioacousticCepstralSmoke(
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseDoubleOption(args, "--seconds", 0.75));
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

static int RunBioacousticCepstralSmoke(int sampleRate, double seconds)
{
    var timeline = MimirBioacousticTimeline.Default;
    var segmentCount = Math.Max(1, (int)Math.Ceiling(seconds / MimirBioacousticTimeline.SegmentSeconds));
    var samples = new List<float>(Math.Max(1, (int)Math.Ceiling(seconds * sampleRate)));
    for (ulong segment = 0; segment < (ulong)segmentCount; segment++)
    {
        samples.AddRange(timeline.RenderSegmentMonoFloat(segment, sampleRate));
    }

    var requestedSamples = Math.Max(1, (int)Math.Round(seconds * sampleRate));
    if (samples.Count > requestedSamples)
    {
        samples.RemoveRange(requestedSamples, samples.Count - requestedSamples);
    }

    var source = samples.ToArray();
    var settings = new[]
    {
        new CepstralDegradationSetting("clean-roundtrip", 0.0, 0.0, 0),
        new CepstralDegradationSetting("blur-light", 0.0, 0.0, 1),
        new CepstralDegradationSetting("warp-light", 0.75, 1.25, 0),
        new CepstralDegradationSetting("warp-light-blur", 0.75, 1.25, 1),
    };

    var failures = 0;
    foreach (var setting in settings)
    {
        var degraded = RoundTripThroughDegradedCepstrum(source, sampleRate, setting, out var analysis);
        var expectedEvents = timeline.EventsOverlapping(0.0, degraded.Length / (double)sampleRate)
            .Select(timelineEvent => timelineEvent.Index)
            .ToHashSet();
        var observations = DecodeCepstralIndexedWords(degraded, sampleRate, expectedEvents.Count);
        var correctAnchors = observations.Count(observation => expectedEvents.Contains(observation.EventIndex));
        var precision = observations.Count == 0 ? 0.0 : correctAnchors / (double)observations.Count;
        var recall = expectedEvents.Count == 0 ? 0.0 : correctAnchors / (double)expectedEvents.Count;
        var confidence = observations.Count == 0 ? 0.0 : observations.Average(observation => observation.Confidence);
        var mae = MeanCepstralTimingError(observations, timeline, sampleRate);
        var passed = correctAnchors >= Math.Min(3, expectedEvents.Count) &&
            precision >= 0.50 &&
            recall >= 0.35 &&
            confidence >= 0.35;
        Console.WriteLine(
            $"bioacoustic-cepstral-smoke decoder=indexed-mfcc-word setting={setting.Name} observations={observations.Count} correct={correctAnchors}/{expectedEvents.Count} " +
            $"precision={precision:0.000} recall={recall:0.000} confidence={confidence:0.000} mae={mae:0.000} " +
            $"melBins={analysis.MelBins} cepstra={analysis.CepstralCoefficients} stftFrames={analysis.FrameCount} rmsRatio={analysis.RmsRatio:0.000} pass={passed}");
        if (!passed)
        {
            failures++;
        }
    }

    if (failures > 0)
    {
        Console.Error.WriteLine($"bioacoustic cepstral smoke failed: {failures}/{settings.Length} degradation settings lost too much word identity");
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

static float[] RoundTripThroughDegradedCepstrum(
    float[] source,
    int sampleRate,
    CepstralDegradationSetting setting,
    out CepstralRoundTripAnalysis analysis)
{
    const int fftSize = 2048;
    const int hopSize = 512;
    const int melBins = 48;
    const int cepstralCoefficients = 20;
    var frameCount = Math.Max(1, 1 + Math.Max(0, source.Length - fftSize) / hopSize);
    var window = HannWindow(fftSize);
    var melFilters = BuildMelFilterBank(melBins, fftSize, sampleRate, 180.0, 15_000.0);
    var melNormalizer = melFilters
        .Select(filter => Math.Max(1.0e-12, filter.Sum()))
        .ToArray();
    var spectra = new Complex[frameCount][];
    var cepstra = new double[frameCount, cepstralCoefficients];
    for (var frame = 0; frame < frameCount; frame++)
    {
        var offset = frame * hopSize;
        var spectrum = new Complex[fftSize];
        for (var index = 0; index < fftSize; index++)
        {
            var sampleIndex = offset + index;
            var sample = sampleIndex < source.Length ? source[sampleIndex] : 0.0f;
            spectrum[index] = new Complex(sample * window[index], 0.0);
        }

        FastFourierTransform(spectrum, inverse: false);
        spectra[frame] = spectrum;
        var logMel = SpectrumToLogMel(spectrum, melFilters, melNormalizer);
        var cepstrum = Dct(logMel, cepstralCoefficients);
        for (var coefficient = 0; coefficient < cepstralCoefficients; coefficient++)
        {
            cepstra[frame, coefficient] = cepstrum[coefficient];
        }
    }

    var warped = WarpCepstrum(cepstra, setting);
    for (var pass = 0; pass < setting.BlurPasses; pass++)
    {
        warped = BlurCepstrum5Tap(warped);
    }

    var output = new double[Math.Max(source.Length, (frameCount - 1) * hopSize + fftSize)];
    var outputWeight = new double[output.Length];
    for (var frame = 0; frame < frameCount; frame++)
    {
        var logMel = InverseDct(Row(warped, frame), melBins);
        var magnitudes = LogMelToMagnitude(logMel, melFilters, fftSize);
        var spectrum = new Complex[fftSize];
        for (var bin = 0; bin <= fftSize / 2; bin++)
        {
            var original = spectra[frame][bin];
            var phase = original.Magnitude <= 1.0e-12
                ? Complex.One
                : original / original.Magnitude;
            spectrum[bin] = phase * magnitudes[bin];
            if (bin > 0 && bin < fftSize / 2)
            {
                spectrum[fftSize - bin] = Complex.Conjugate(spectrum[bin]);
            }
        }

        FastFourierTransform(spectrum, inverse: true);
        var offset = frame * hopSize;
        for (var index = 0; index < fftSize && offset + index < output.Length; index++)
        {
            var weight = window[index] * window[index];
            output[offset + index] += spectrum[index].Real * window[index];
            outputWeight[offset + index] += weight;
        }
    }

    var reconstructed = new float[source.Length];
    var sourceRms = RootMeanSquare(source);
    for (var index = 0; index < reconstructed.Length; index++)
    {
        reconstructed[index] = outputWeight[index] <= 1.0e-12
            ? 0.0f
            : (float)(output[index] / outputWeight[index]);
    }

    var reconstructedRms = RootMeanSquare(reconstructed);
    if (reconstructedRms > 1.0e-9 && sourceRms > 1.0e-9)
    {
        var gain = sourceRms / reconstructedRms;
        for (var index = 0; index < reconstructed.Length; index++)
        {
            reconstructed[index] = (float)Math.Clamp(reconstructed[index] * gain, -1.0, 1.0);
        }
    }

    analysis = new CepstralRoundTripAnalysis(frameCount, melBins, cepstralCoefficients, reconstructedRms <= 1.0e-9 ? 0.0 : sourceRms / reconstructedRms);
    return reconstructed;
}

static IReadOnlyList<CepstralWordObservation> DecodeCepstralIndexedWords(float[] samples, int sampleRate, int expectedEventCount)
{
    const int tableCount = 4;
    const int hashBits = 14;
    var templateIndex = BuildCepstralWordIndex(sampleRate, tableCount, hashBits);
    var motifSamples = MimirBioacousticTimeline.Default.RenderEventMonoFloat(0, sampleRate).Length;
    var hopSamples = Math.Max(1, sampleRate / 1_000);
    var energyTrace = WindowEnergy(samples, motifSamples, hopSamples);
    var threshold = energyTrace.Length == 0
        ? double.PositiveInfinity
        : energyTrace.Average(value => (double)value) + Math.Sqrt(energyTrace.Sum(value => Math.Pow(value - energyTrace.Average(), 2.0)) / energyTrace.Length) * 0.10;
    var proposals = new List<int>();
    for (var index = 1; index < energyTrace.Length - 1; index++)
    {
        if (energyTrace[index] >= threshold &&
            energyTrace[index] >= energyTrace[index - 1] &&
            energyTrace[index] >= energyTrace[index + 1])
        {
            proposals.Add(index * hopSamples);
        }
    }

    var denseStep = Math.Max(1, (int)Math.Round(sampleRate * 0.040));
    for (var offset = 0; offset + motifSamples <= samples.Length; offset += denseStep)
    {
        proposals.Add(offset);
    }

    var proposalBudget = Math.Max(expectedEventCount * 8, 16);
    var observations = new List<CepstralWordObservation>();
    foreach (var offset in proposals
                 .OrderByDescending(offset => energyTrace[Math.Clamp(offset / hopSamples, 0, energyTrace.Length - 1)])
                 .Take(proposalBudget)
                 .Order())
    {
        if (offset < 0 || offset + motifSamples > samples.Length)
        {
            continue;
        }

        var feature = CepstralFingerprint(samples.AsSpan(offset, motifSamples), sampleRate);
        var candidateIndexes = new HashSet<int>();
        for (var table = 0; table < tableCount; table++)
        {
            var key = ProjectionHash(feature, table, hashBits);
            if (templateIndex.Buckets.TryGetValue((table, key), out var bucket))
            {
                foreach (var candidate in bucket)
                {
                    candidateIndexes.Add(candidate);
                }
            }

            if (candidateIndexes.Count < 4)
            {
                foreach (var near in templateIndex.Buckets
                             .Where(pair => pair.Key.Table == table &&
                                 HammingDistance(pair.Key.Hash, key) <= 4)
                             .SelectMany(pair => pair.Value))
                {
                    candidateIndexes.Add(near);
                }
            }
        }

        if (candidateIndexes.Count == 0)
        {
            continue;
        }

        var predictedEvent = PredictedBioacousticEventIndex(offset, sampleRate);
        var best = candidateIndexes
            .Select(index =>
            {
                var template = templateIndex.Templates[index];
                var distance = CepstralDistance(feature, template.Feature);
                var timePenalty = Math.Abs((long)template.EventIndex - (long)predictedEvent) * 0.20;
                return (Template: template, Distance: distance + timePenalty);
            })
            .OrderBy(pair => pair.Distance)
            .First();
        var confidence = double.IsFinite(best.Distance)
            ? 1.0 / (1.0 + best.Distance / 8.0)
            : 0.0;
        if (confidence < 0.05)
        {
            continue;
        }

        observations.Add(new CepstralWordObservation(best.Template.EventIndex, offset, confidence));
    }

    return observations
        .OrderByDescending(observation => observation.Confidence)
        .GroupBy(observation => observation.EventIndex)
        .Select(group => group.First())
        .OrderBy(observation => observation.SampleOffset)
        .ToArray();
}

static ulong PredictedBioacousticEventIndex(double sampleOffset, int sampleRate)
{
    const double firstEventSeconds = 0.08;
    const double eventSpacingSeconds = 0.16;
    var index = (long)Math.Round((sampleOffset / sampleRate - firstEventSeconds) / eventSpacingSeconds);
    return (ulong)Math.Max(0, index);
}

static CepstralWordIndex BuildCepstralWordIndex(int sampleRate, int tableCount, int hashBits)
{
    var augmentationSettings = new[]
    {
        new CepstralDegradationSetting("template-clean", 0.0, 0.0, 0),
        new CepstralDegradationSetting("template-blur", 0.0, 0.0, 1),
        new CepstralDegradationSetting("template-warp-light", 0.75, 1.25, 0),
        new CepstralDegradationSetting("template-warp-blur", 0.75, 1.25, 1),
    };
    var templates = new List<CepstralWordTemplate>(MimirBioacousticTimeline.SymbolCount);
    for (ulong eventIndex = 0; eventIndex < MimirBioacousticTimeline.SymbolCount; eventIndex++)
    {
        var samples = MimirBioacousticTimeline.Default.RenderEventMonoFloat(eventIndex, sampleRate);
        foreach (var setting in augmentationSettings)
        {
            var templateSamples = setting.BlurPasses == 0 && setting.WarpFrames == 0.0 && setting.WarpCoefficients == 0.0
                ? samples
                : RoundTripThroughDegradedCepstrum(samples, sampleRate, setting, out _);
            templates.Add(new CepstralWordTemplate(eventIndex, CepstralFingerprint(templateSamples, sampleRate)));
        }
    }

    var buckets = new Dictionary<(int Table, int Hash), List<int>>();
    for (var index = 0; index < templates.Count; index++)
    {
        for (var table = 0; table < tableCount; table++)
        {
            var key = (table, ProjectionHash(templates[index].Feature, table, hashBits));
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                buckets[key] = bucket;
            }

            bucket.Add(index);
        }
    }

    return new CepstralWordIndex(templates.ToArray(), buckets);
}

static double[] CepstralFingerprint(ReadOnlySpan<float> samples, int sampleRate)
{
    const int fftSize = 1024;
    const int hopSize = 256;
    const int melBins = 40;
    const int cepstralCoefficients = 14;
    var window = HannWindow(fftSize);
    var melFilters = BuildMelFilterBank(melBins, fftSize, sampleRate, 180.0, 15_000.0);
    var melNormalizer = melFilters.Select(filter => Math.Max(1.0e-12, filter.Sum())).ToArray();
    var frameCount = Math.Max(1, 1 + Math.Max(0, samples.Length - fftSize) / hopSize);
    var mean = new double[cepstralCoefficients];
    var delta = new double[cepstralCoefficients];
    double[]? previous = null;
    for (var frame = 0; frame < frameCount; frame++)
    {
        var offset = frame * hopSize;
        var spectrum = new Complex[fftSize];
        for (var index = 0; index < fftSize; index++)
        {
            var sampleIndex = offset + index;
            var sample = sampleIndex < samples.Length ? samples[sampleIndex] : 0.0f;
            spectrum[index] = new Complex(sample * window[index], 0.0);
        }

        FastFourierTransform(spectrum, inverse: false);
        var cepstrum = Dct(SpectrumToLogMel(spectrum, melFilters, melNormalizer), cepstralCoefficients);
        for (var coefficient = 0; coefficient < cepstralCoefficients; coefficient++)
        {
            mean[coefficient] += cepstrum[coefficient];
            if (previous != null)
            {
                delta[coefficient] += Math.Abs(cepstrum[coefficient] - previous[coefficient]);
            }
        }

        previous = cepstrum;
    }

    var output = new double[cepstralCoefficients * 2];
    for (var coefficient = 0; coefficient < cepstralCoefficients; coefficient++)
    {
        output[coefficient] = coefficient == 0 ? 0.0 : mean[coefficient] / frameCount;
        output[coefficient + cepstralCoefficients] = delta[coefficient] / Math.Max(1, frameCount - 1);
    }

    NormalizeFeature(output);
    return output;
}

static void NormalizeFeature(double[] feature)
{
    var mean = feature.Average();
    var norm = 0.0;
    for (var index = 0; index < feature.Length; index++)
    {
        feature[index] -= mean;
        norm += feature[index] * feature[index];
    }

    norm = Math.Sqrt(norm);
    if (norm <= 1.0e-12)
    {
        return;
    }

    for (var index = 0; index < feature.Length; index++)
    {
        feature[index] /= norm;
    }
}

static double MeanCepstralTimingError(
    IReadOnlyList<CepstralWordObservation> observations,
    MimirBioacousticTimeline timeline,
    int sampleRate)
{
    if (observations.Count == 0)
    {
        return double.PositiveInfinity;
    }

    return observations.Average(observation =>
        Math.Abs(observation.SampleOffset - timeline.EventForIndex(observation.EventIndex).StartSeconds * sampleRate));
}

static double CepstralDistance(IReadOnlyList<double> first, IReadOnlyList<double> second)
{
    var sum = 0.0;
    for (var index = 0; index < Math.Min(first.Count, second.Count); index++)
    {
        var diff = first[index] - second[index];
        if (!double.IsFinite(diff))
        {
            return double.PositiveInfinity;
        }

        sum += diff * diff;
    }

    return Math.Sqrt(sum);
}

static int HammingDistance(int first, int second)
{
    var value = (uint)(first ^ second);
    var count = 0;
    while (value != 0)
    {
        value &= value - 1;
        count++;
    }

    return count;
}

static int ProjectionHash(IReadOnlyList<double> feature, int table, int bits)
{
    var hash = 0;
    for (var bit = 0; bit < bits; bit++)
    {
        var dot = 0.0;
        for (var index = 0; index < feature.Count; index++)
        {
            dot += feature[index] * ProjectionWeight(table, bit, index);
        }

        if (dot >= 0.0)
        {
            hash |= 1 << bit;
        }
    }

    return hash;
}

static double ProjectionWeight(int table, int bit, int index)
{
    var value = Hash2D(table * 131 + bit * 17, index * 29 + 7);
    return ((value & 0xffff) / 32767.5) - 1.0;
}

static float[] WindowEnergy(float[] samples, int windowSamples, int hopSamples)
{
    if (samples.Length < windowSamples)
    {
        return [];
    }

    var output = new float[1 + (samples.Length - windowSamples) / hopSamples];
    var prefix = new double[samples.Length + 1];
    for (var index = 0; index < samples.Length; index++)
    {
        prefix[index + 1] = prefix[index] + samples[index] * samples[index];
    }

    for (var frame = 0; frame < output.Length; frame++)
    {
        var offset = frame * hopSamples;
        output[frame] = (float)((prefix[offset + windowSamples] - prefix[offset]) / windowSamples);
    }

    return output;
}

static double[] SpectrumToLogMel(Complex[] spectrum, double[][] melFilters, double[] melNormalizer)
{
    var magnitudes = new double[spectrum.Length / 2 + 1];
    for (var bin = 0; bin < magnitudes.Length; bin++)
    {
        magnitudes[bin] = spectrum[bin].Magnitude;
    }

    var logMel = new double[melFilters.Length];
    for (var mel = 0; mel < melFilters.Length; mel++)
    {
        var energy = 0.0;
        for (var bin = 0; bin < magnitudes.Length; bin++)
        {
            energy += magnitudes[bin] * melFilters[mel][bin];
        }

        logMel[mel] = Math.Log(1.0e-7 + energy / melNormalizer[mel]);
    }

    return logMel;
}

static double[] LogMelToMagnitude(double[] logMel, double[][] melFilters, int fftSize)
{
    var magnitudes = new double[fftSize / 2 + 1];
    var weights = new double[magnitudes.Length];
    for (var mel = 0; mel < melFilters.Length; mel++)
    {
        var value = Math.Exp(Math.Clamp(logMel[mel], -24.0, 6.0));
        for (var bin = 0; bin < magnitudes.Length; bin++)
        {
            var weight = melFilters[mel][bin];
            magnitudes[bin] += value * weight;
            weights[bin] += weight;
        }
    }

    for (var bin = 0; bin < magnitudes.Length; bin++)
    {
        magnitudes[bin] = weights[bin] <= 1.0e-12 ? 0.0 : magnitudes[bin] / weights[bin];
    }

    return magnitudes;
}

static double[,] WarpCepstrum(double[,] input, CepstralDegradationSetting setting)
{
    var frames = input.GetLength(0);
    var coefficients = input.GetLength(1);
    var output = new double[frames, coefficients];
    for (var frame = 0; frame < frames; frame++)
    {
        for (var coefficient = 0; coefficient < coefficients; coefficient++)
        {
            var warpT = setting.WarpFrames * SimplexNoise2D(frame * 0.071, coefficient * 0.137 + 19.0);
            var warpC = setting.WarpCoefficients * SimplexNoise2D(frame * 0.053 + 41.0, coefficient * 0.113);
            output[frame, coefficient] = SampleCepstrumBilinear(input, frame + warpT, coefficient + warpC);
        }
    }

    return output;
}

static double[,] BlurCepstrum5Tap(double[,] input)
{
    var kernel = new[] { 1.0, 4.0, 6.0, 4.0, 1.0 };
    var frames = input.GetLength(0);
    var coefficients = input.GetLength(1);
    var temp = new double[frames, coefficients];
    var output = new double[frames, coefficients];
    for (var frame = 0; frame < frames; frame++)
    {
        for (var coefficient = 0; coefficient < coefficients; coefficient++)
        {
            var sum = 0.0;
            var weightSum = 0.0;
            for (var tap = -2; tap <= 2; tap++)
            {
                var sourceCoefficient = Math.Clamp(coefficient + tap, 0, coefficients - 1);
                var weight = kernel[tap + 2];
                sum += input[frame, sourceCoefficient] * weight;
                weightSum += weight;
            }

            temp[frame, coefficient] = sum / weightSum;
        }
    }

    for (var frame = 0; frame < frames; frame++)
    {
        for (var coefficient = 0; coefficient < coefficients; coefficient++)
        {
            var sum = 0.0;
            var weightSum = 0.0;
            for (var tap = -2; tap <= 2; tap++)
            {
                var sourceFrame = Math.Clamp(frame + tap, 0, frames - 1);
                var weight = kernel[tap + 2];
                sum += temp[sourceFrame, coefficient] * weight;
                weightSum += weight;
            }

            output[frame, coefficient] = sum / weightSum;
        }
    }

    return output;
}

static double SampleCepstrumBilinear(double[,] input, double frame, double coefficient)
{
    var frames = input.GetLength(0);
    var coefficients = input.GetLength(1);
    var f0 = Math.Clamp((int)Math.Floor(frame), 0, frames - 1);
    var c0 = Math.Clamp((int)Math.Floor(coefficient), 0, coefficients - 1);
    var f1 = Math.Clamp(f0 + 1, 0, frames - 1);
    var c1 = Math.Clamp(c0 + 1, 0, coefficients - 1);
    var ft = Math.Clamp(frame - Math.Floor(frame), 0.0, 1.0);
    var ct = Math.Clamp(coefficient - Math.Floor(coefficient), 0.0, 1.0);
    var a = input[f0, c0] * (1.0 - ct) + input[f0, c1] * ct;
    var b = input[f1, c0] * (1.0 - ct) + input[f1, c1] * ct;
    return a * (1.0 - ft) + b * ft;
}

static double[][] BuildMelFilterBank(int melBins, int fftSize, int sampleRate, double minHz, double maxHz)
{
    var minMel = HzToMel(minHz);
    var maxMel = HzToMel(Math.Min(maxHz, sampleRate * 0.5));
    var points = Enumerable.Range(0, melBins + 2)
        .Select(index => MelToHz(minMel + (maxMel - minMel) * index / (melBins + 1)))
        .Select(hz => Math.Clamp((int)Math.Round(hz / sampleRate * fftSize), 0, fftSize / 2))
        .ToArray();
    var filters = new double[melBins][];
    for (var mel = 0; mel < melBins; mel++)
    {
        filters[mel] = new double[fftSize / 2 + 1];
        var left = points[mel];
        var center = Math.Max(points[mel + 1], left + 1);
        var right = Math.Max(points[mel + 2], center + 1);
        for (var bin = left; bin <= right && bin < filters[mel].Length; bin++)
        {
            filters[mel][bin] = bin <= center
                ? (bin - left) / (double)Math.Max(1, center - left)
                : (right - bin) / (double)Math.Max(1, right - center);
        }
    }

    return filters;
}

static double[] Dct(double[] values, int coefficientCount)
{
    var output = new double[coefficientCount];
    var scale = Math.PI / values.Length;
    for (var coefficient = 0; coefficient < coefficientCount; coefficient++)
    {
        var sum = 0.0;
        for (var index = 0; index < values.Length; index++)
        {
            sum += values[index] * Math.Cos(scale * (index + 0.5) * coefficient);
        }

        output[coefficient] = sum;
    }

    return output;
}

static double[] InverseDct(double[] coefficients, int valueCount)
{
    var output = new double[valueCount];
    var scale = Math.PI / valueCount;
    for (var index = 0; index < valueCount; index++)
    {
        var sum = coefficients[0] / valueCount;
        for (var coefficient = 1; coefficient < coefficients.Length; coefficient++)
        {
            sum += 2.0 * coefficients[coefficient] * Math.Cos(scale * (index + 0.5) * coefficient) / valueCount;
        }

        output[index] = sum;
    }

    return output;
}

static double[] Row(double[,] matrix, int row)
{
    var values = new double[matrix.GetLength(1)];
    for (var index = 0; index < values.Length; index++)
    {
        values[index] = matrix[row, index];
    }

    return values;
}

static double[] HannWindow(int length)
{
    var window = new double[length];
    for (var index = 0; index < length; index++)
    {
        window[index] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / Math.Max(1, length - 1));
    }

    return window;
}

static double RootMeanSquare(IReadOnlyList<float> samples)
{
    if (samples.Count == 0)
    {
        return 0.0;
    }

    var sum = 0.0;
    for (var index = 0; index < samples.Count; index++)
    {
        sum += samples[index] * samples[index];
    }

    return Math.Sqrt(sum / samples.Count);
}

static double HzToMel(double hz) =>
    2595.0 * Math.Log10(1.0 + hz / 700.0);

static double MelToHz(double mel) =>
    700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

static double SimplexNoise2D(double x, double y)
{
    const double f2 = 0.3660254037844386;
    const double g2 = 0.21132486540518713;
    var s = (x + y) * f2;
    var i = FastFloor(x + s);
    var j = FastFloor(y + s);
    var t = (i + j) * g2;
    var x0 = x - (i - t);
    var y0 = y - (j - t);
    var i1 = x0 > y0 ? 1 : 0;
    var j1 = x0 > y0 ? 0 : 1;
    var x1 = x0 - i1 + g2;
    var y1 = y0 - j1 + g2;
    var x2 = x0 - 1.0 + 2.0 * g2;
    var y2 = y0 - 1.0 + 2.0 * g2;
    return 70.0 * (
        SimplexCorner(i, j, x0, y0) +
        SimplexCorner(i + i1, j + j1, x1, y1) +
        SimplexCorner(i + 1, j + 1, x2, y2));
}

static double SimplexCorner(int i, int j, double x, double y)
{
    var t = 0.5 - x * x - y * y;
    if (t < 0.0)
    {
        return 0.0;
    }

    var hash = Hash2D(i, j) & 7;
    var gx = hash < 4 ? 1.0 : 2.0;
    var gy = hash < 4 ? 2.0 : 1.0;
    if ((hash & 1) != 0)
    {
        gx = -gx;
    }

    if ((hash & 2) != 0)
    {
        gy = -gy;
    }

    t *= t;
    return t * t * (gx * x + gy * y);
}

static int Hash2D(int x, int y)
{
    unchecked
    {
        var hash = x * 0x1f1f1f1f ^ y * 0x5f356495;
        hash ^= hash >> 16;
        hash *= 0x45d9f3b;
        hash ^= hash >> 16;
        return hash;
    }
}

static int FastFloor(double value) =>
    value >= 0.0 ? (int)value : (int)value - 1;

static void FastFourierTransform(Complex[] values, bool inverse)
{
    var j = 0;
    for (var i = 1; i < values.Length; i++)
    {
        var bit = values.Length >> 1;
        for (; (j & bit) != 0; bit >>= 1)
        {
            j ^= bit;
        }

        j ^= bit;
        if (i < j)
        {
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    for (var length = 2; length <= values.Length; length <<= 1)
    {
        var angle = 2.0 * Math.PI / length * (inverse ? 1.0 : -1.0);
        var wLength = new Complex(Math.Cos(angle), Math.Sin(angle));
        for (var i = 0; i < values.Length; i += length)
        {
            var w = Complex.One;
            for (var k = 0; k < length / 2; k++)
            {
                var even = values[i + k];
                var odd = values[i + k + length / 2] * w;
                values[i + k] = even + odd;
                values[i + k + length / 2] = even - odd;
                w *= wLength;
            }
        }
    }

    if (inverse)
    {
        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= values.Length;
        }
    }
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

internal sealed record CepstralDegradationSetting(
    string Name,
    double WarpFrames,
    double WarpCoefficients,
    int BlurPasses);

internal sealed record CepstralRoundTripAnalysis(
    int FrameCount,
    int MelBins,
    int CepstralCoefficients,
    double RmsRatio);

internal sealed record CepstralWordTemplate(
    ulong EventIndex,
    IReadOnlyList<double> Feature);

internal sealed record CepstralWordObservation(
    ulong EventIndex,
    double SampleOffset,
    double Confidence);

internal sealed record CepstralWordIndex(
    IReadOnlyList<CepstralWordTemplate> Templates,
    IReadOnlyDictionary<(int Table, int Hash), List<int>> Buckets);
