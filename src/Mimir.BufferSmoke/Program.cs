using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Mesh;
using MessagePack;
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

if (args.Any(arg => string.Equals(arg, "--bioacoustic-train", StringComparison.OrdinalIgnoreCase)))
{
    return await RunBioacousticTrainingAsync(
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseDoubleOption(args, "--seconds", 0.75),
        ParseStringOption(args, "--output", "artifacts/bioacoustic-training")).ConfigureAwait(false);
}

if (args.Any(arg => string.Equals(arg, "--bioacoustic-contestants", StringComparison.OrdinalIgnoreCase)))
{
    return await RunBioacousticContestantsAsync(
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseDoubleOption(args, "--seconds", 1.25),
        ParseStringOption(args, "--output", "artifacts/bioacoustic-contestants"),
        ParseIntOption(args, "--max-songs", MimirBioacousticContestants.BuiltIn.Count),
        ParseIntOption(args, "--max-decoders", MimirBioacousticDecoderConfiguration.BuiltInProfiles.Count),
        ParseIntOption(args, "--max-degradations", CepstralTrainingDegradationSettings().Count),
        ParseStringOption(args, "--song", ""),
        ParseStringOption(args, "--decoder", "")).ConfigureAwait(false);
}

if (args.Any(arg => string.Equals(arg, "--bioacoustic-actuator-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunBioacousticActuatorSelfTest(
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseDoubleOption(args, "--delay-samples", 317.375));
}

if (args.Any(arg => string.Equals(arg, "--complex-contour-tracker-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunComplexContourTrackerSelfTest(
        ParseIntOption(args, "--sample-rate", 48_000),
        ParseDoubleOption(args, "--delay-samples", 173.375),
        ParseDoubleOption(args, "--reflection-delay-samples", 29.0));
}

if (args.Any(arg => string.Equals(arg, "--complex-contour-runtime-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunComplexContourRuntimeSelfTest(
        ParseIntOption(args, "--sample-rate", 192_000),
        ParseDoubleOption(args, "--delay-samples", 693.5));
}

if (args.Any(arg => string.Equals(arg, "--perfect-machine-profile-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return RunPerfectMachineProfileSmoke();
}

if (args.Any(arg => string.Equals(arg, "--perfect-machine-contract-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return await RunPerfectMachineContractSmokeAsync(
        ParseStringOption(args, "--output", "artifacts/perfect-machine/contracts.cc"))
        .ConfigureAwait(false);
}

if (args.Any(arg => string.Equals(arg, "--move-tracking-contract-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return RunMoveTrackingContractSmoke();
}

if (args.Any(arg => string.Equals(arg, "--move-native-reservoir-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return RunMoveNativeReservoirSmoke(
        ParseStringOption(args, "--native-reservoir", DefaultNativeReservoirPath()));
}

if (args.Any(arg => string.Equals(arg, "--muninn-move-evidence-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return RunMuninnMoveEvidenceSmoke(
        ParseStringOption(args, "--native-reservoir", DefaultNativeReservoirPath()));
}

if (args.Any(arg => string.Equals(arg, "--muninn-move-identity-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return RunMuninnMoveIdentitySmoke();
}

if (args.Any(arg => string.Equals(arg, "--muninn-move-cultmesh-stream-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return RunMuninnMoveCultMeshStreamSmoke(
        ParseStringOption(args, "--native-reservoir", DefaultNativeReservoirPath()));
}

if (args.Any(arg => string.Equals(arg, "--move-fusion-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return RunMoveFusionSmoke();
}

if (args.Any(arg => string.Equals(arg, "--move-calibration-protocol-smoke", StringComparison.OrdinalIgnoreCase)))
{
    return await RunMoveCalibrationProtocolSmokeAsync(
        ParseStringOption(args, "--output", "artifacts/move-calibration/protocol.cc"))
        .ConfigureAwait(false);
}

if (args.Any(arg => string.Equals(arg, "--import-obs-program-scene", StringComparison.OrdinalIgnoreCase)))
{
    return await ImportObsProgramSceneAsync(
        ParseStringOption(args, "--input", DefaultObsScenePath()),
        ParseStringOption(args, "--scene", ""),
        ParseStringOption(args, "--output", "state/mimir-program-composition.cc"))
        .ConfigureAwait(false);
}

if (args.Any(arg => string.Equals(arg, "--perfect-machine-manifest", StringComparison.OrdinalIgnoreCase)))
{
    return await WritePerfectMachineManifestAsync(
        ParseStringOption(args, "--output", "artifacts/perfect-machine/manifest.json"))
        .ConfigureAwait(false);
}

if (args.Any(arg => string.Equals(arg, "--perfect-machine-lowering-benchmark", StringComparison.OrdinalIgnoreCase)))
{
    return RunPerfectMachineLoweringBenchmark(
        ParseIntOption(args, "--iterations", 10_000));
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

if (args.Any(arg => string.Equals(arg, "--render-contestant-f32", StringComparison.OrdinalIgnoreCase)))
{
    return RenderContestantFloat32(
        ParseStringOption(args, "--output", "artifacts/asio/canary-packet-f32.raw"),
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseDoubleOption(args, "--seconds", 3.0),
        ParseStringOption(args, "--song", MimirBioacousticContestants.CanaryPacketTrill.Id));
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

if (args.Any(arg => string.Equals(arg, "--inspect-asio-f32", StringComparison.OrdinalIgnoreCase)))
{
    return InspectAsioFloat32(
        ParseStringOption(args, "--input", "artifacts/asio/scarlett-channel-id-f32.raw"),
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseIntOption(args, "--channels", 4));
}

if (args.Any(arg => string.Equals(arg, "--analyze-contestant-asio-f32", StringComparison.OrdinalIgnoreCase)))
{
    return AnalyzeContestantAsioFloat32(
        ParseStringOption(args, "--input", "artifacts/asio/scarlett-canary-packet-f32.raw"),
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseIntOption(args, "--channels", 4),
        ParseIntOption(args, "--candidate-channel", -1),
        ParseDoubleOption(args, "--seconds", 3.0),
        ParseDoubleOption(args, "--schedule-offset-samples", 0.0),
        ParseStringOption(args, "--song", MimirBioacousticContestants.CanaryPacketTrill.Id));
}

if (args.Any(arg => string.Equals(arg, "--complex-contour-asio-f32", StringComparison.OrdinalIgnoreCase)))
{
    return AnalyzeComplexContourAsioFloat32(
        ParseStringOption(args, "--input", "artifacts/asio/scarlett-canary-packet-anchor-rich-192k-f32.raw"),
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseIntOption(args, "--channels", 4),
        ParseIntOption(args, "--reference-channel", 2),
        ParseIntOption(args, "--candidate-channel", 1),
        ParseDoubleOption(args, "--seconds", 3.0),
        ParseDoubleOption(args, "--schedule-offset-samples", 1623.0),
        ParseDoubleOption(args, "--predicted-delay-samples", 0.0),
        ParseStringOption(args, "--channel-model", ""),
        ParseStringOption(args, "--song", MimirBioacousticContestants.CanaryPacketTrill.Id));
}

if (args.Any(arg => string.Equals(arg, "--complex-contour-replay-panel", StringComparison.OrdinalIgnoreCase)))
{
    return RunComplexContourReplayPanel(
        ParseStringOption(args, "--output", "calibration/bioacoustic/complex-contour-replay-panel.json"));
}

if (args.Any(arg => string.Equals(arg, "--learn-complex-contour-channel-model", StringComparison.OrdinalIgnoreCase)))
{
    return LearnComplexContourChannelModel(
        ParseStringOption(args, "--input", "calibration/bioacoustic/complex-contour-replay-panel.json"),
        ParseStringOption(args, "--output", "calibration/bioacoustic/complex-contour-channel-model.json"));
}

if (args.Any(arg => string.Equals(arg, "--evaluate-complex-contour-channel-model", StringComparison.OrdinalIgnoreCase)))
{
    return EvaluateComplexContourChannelModel(
        ParseStringOption(args, "--receipt", "calibration/bioacoustic/complex-contour-replay-panel.json"),
        ParseStringOption(args, "--channel-model", "calibration/bioacoustic/complex-contour-channel-model.json"),
        ParseStringOption(args, "--output", "calibration/bioacoustic/complex-contour-channel-model-evaluation.json"));
}

if (args.Any(arg => string.Equals(arg, "--calibrate-contestant-asio-f32", StringComparison.OrdinalIgnoreCase)))
{
    return CalibrateContestantAsioFloat32(
        ParseStringOption(args, "--input", "artifacts/asio/scarlett-canary-packet-f32.raw"),
        ParseStringOption(args, "--output", "calibration/bioacoustic/latest.json"),
        ParseIntOption(args, "--sample-rate", MimirBioacousticTimeline.SampleRate),
        ParseIntOption(args, "--channels", 4),
        ParseIntOption(args, "--reference-channel", 2),
        ParseDoubleOption(args, "--seconds", 3.0),
        ParseDoubleOption(args, "--schedule-offset-samples", 0.0),
        ParseDoubleOption(args, "--search-radius-us", 180.0),
        ParseDoubleOption(args, "--delay-search-us", 6_000.0),
        ParseStringOption(args, "--song", MimirBioacousticContestants.CanaryPacketTrill.Id),
        ParseDoubleOption(args, "--min-realtime-factor", 10.0));
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

foreach (var source in configuration.Sources.ToArray())
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
    var settings = CepstralSmokeDegradationSettings();

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
        var clock = FitBioacousticClockHypothesis(observations, timeline, sampleRate);
        var passed = correctAnchors >= Math.Min(3, expectedEvents.Count) &&
            precision >= 0.50 &&
            recall >= 0.35 &&
            confidence >= 0.35;
        Console.WriteLine(
            $"bioacoustic-cepstral-smoke decoder=indexed-mfcc-word setting={setting.Name} observations={observations.Count} correct={correctAnchors}/{expectedEvents.Count} " +
            $"precision={precision:0.000} recall={recall:0.000} confidence={confidence:0.000} mae={mae:0.000} " +
            $"globalAnchors={clock?.AnchorCount ?? 0} globalMae={clock?.MeanAbsoluteErrorSamples ?? double.PositiveInfinity:0.000} globalOffset={clock?.SourceOffsetSamples ?? double.NaN:0.000} globalRate={clock?.EffectiveSampleRate ?? double.NaN:0.000} " +
            $"melBins={analysis.MelBins} cepstra={analysis.CepstralCoefficients} stftFrames={analysis.FrameCount} rmsRatio={analysis.RmsRatio:0.000} pass={passed}");
        if (!passed)
        {
            failures++;
        }
    }

    if (failures > 0)
    {
        Console.Error.WriteLine($"bioacoustic cepstral smoke failed: {failures}/{settings.Count} degradation settings lost too much word identity");
        return 1;
    }

    return 0;
}

static async Task<int> RunBioacousticTrainingAsync(int sampleRate, double seconds, string outputRoot)
{
    var runStarted = DateTimeOffset.UtcNow;
    var runId = $"bioacoustic-{runStarted:yyyyMMdd-HHmmss}";
    var runDirectory = Path.GetFullPath(Path.Combine(outputRoot, runId));
    Directory.CreateDirectory(runDirectory);

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
    var degradations = CepstralTrainingDegradationSettings();
    var hypotheses = BioacousticTrainingHypotheses();
    var expectedEvents = timeline.EventsOverlapping(0.0, source.Length / (double)sampleRate)
        .Select(timelineEvent => timelineEvent.Index)
        .ToHashSet();
    var cachePath = Path.Combine(runDirectory, "bioacoustic-training.cc");
    var results = new List<BioacousticTrainingResult>();

    WriteWave(Path.Combine(runDirectory, "source-pre-warp.wav"), source, sampleRate);
    foreach (var hypothesis in hypotheses)
    {
        var hypothesisDirectory = Path.Combine(runDirectory, hypothesis.Id);
        Directory.CreateDirectory(hypothesisDirectory);
        var templateIndex = BuildCepstralWordIndex(sampleRate, hypothesis.Decoder);
        foreach (var degradation in degradations)
        {
            var settingDirectory = Path.Combine(hypothesisDirectory, degradation.Name);
            Directory.CreateDirectory(settingDirectory);
            var preWarpPath = Path.Combine(settingDirectory, "pre-warp.wav");
            var postWarpPath = Path.Combine(settingDirectory, "post-warp.wav");
            var reconstructedPath = Path.Combine(settingDirectory, "reconstructed-from-detections.wav");
            var summaryPath = Path.Combine(settingDirectory, "summary.json");

            WriteWave(preWarpPath, source, sampleRate);
            var degraded = RoundTripThroughDegradedCepstrum(source, sampleRate, degradation, out var analysis);
            WriteWave(postWarpPath, degraded, sampleRate);

            var decodeStamp = Stopwatch.GetTimestamp();
            var observations = DecodeCepstralIndexedWordsWithIndex(degraded, sampleRate, expectedEvents.Count, hypothesis.Decoder, templateIndex);
            var decodeMilliseconds = Stopwatch.GetElapsedTime(decodeStamp).TotalMilliseconds;
            var correctAnchors = observations.Count(observation => expectedEvents.Contains(observation.EventIndex));
            var precision = observations.Count == 0 ? 0.0 : correctAnchors / (double)observations.Count;
            var recall = expectedEvents.Count == 0 ? 0.0 : correctAnchors / (double)expectedEvents.Count;
            var confidence = observations.Count == 0 ? 0.0 : observations.Average(observation => observation.Confidence);
            var timingMae = MeanCepstralTimingError(observations, timeline, sampleRate);
            var clock = FitBioacousticClockHypothesis(observations, timeline, sampleRate);
            var globalTimingMae = clock?.MeanAbsoluteErrorSamples ?? double.PositiveInfinity;
            var anchorCoverage = clock?.AnchorCoverage ?? 0.0;
            var realtime = decodeMilliseconds <= 0.0 ? double.PositiveInfinity : seconds * 1000.0 / decodeMilliseconds;
            var identityScore = precision * 0.45 + recall * 0.45 + confidence * 0.10;
            var timingScore = clock?.Confidence ?? 0.0;
            var speedScore = Math.Clamp(realtime / 50.0, 0.0, 1.0);
            var totalScore = identityScore * 0.62 + timingScore * 0.18 + speedScore * 0.20;
            var reconstructed = ReconstructDetectedBioacousticSong(observations, source.Length, sampleRate);
            WriteWave(reconstructedPath, reconstructed, sampleRate);

            var artifactRoot = RelativePath(runDirectory, settingDirectory);
            var result = new BioacousticTrainingResult(
                $"{runId}:{hypothesis.Id}:{degradation.Name}",
                runId,
                runStarted.ToString("O"),
                "Mimir.BufferSmoke --bioacoustic-train",
                hypothesis.Id,
                hypothesis.Notes,
                degradation.Name,
                sampleRate,
                seconds,
                expectedEvents.Count,
                observations.Count,
                correctAnchors,
                precision,
                recall,
                confidence,
                timingMae,
                decodeMilliseconds,
                realtime,
                identityScore,
                timingScore,
                speedScore,
                totalScore,
                analysis.MelBins,
                analysis.CepstralCoefficients,
                analysis.FrameCount,
                analysis.RmsRatio,
                clock == null
                    ? new BioacousticClockHypothesisSnapshot(0, 0, 0, 0, 0, 0, 0)
                    : new BioacousticClockHypothesisSnapshot(
                        clock.AnchorCount,
                        clock.SourceOffsetSamples,
                        clock.EffectiveSampleRate,
                        clock.MeanAbsoluteErrorSamples,
                        clock.Confidence,
                        clock.Score,
                        anchorCoverage),
                new BioacousticTrainingDecoderSnapshot(
                    hypothesis.Decoder.FftSize,
                    hypothesis.Decoder.HopSize,
                    hypothesis.Decoder.MelBins,
                    hypothesis.Decoder.CepstralCoefficients,
                    hypothesis.Decoder.TableCount,
                    hypothesis.Decoder.HashBits,
                    hypothesis.Decoder.NearHashRadius,
                    hypothesis.Decoder.DenseStepSeconds,
                    hypothesis.Decoder.ProposalBudgetMultiplier,
                    hypothesis.Decoder.TemplateAugmentations.Select(setting => setting.Name).ToArray(),
                    hypothesis.Decoder.ProposalMode.ToString()),
                [
                    new BioacousticTrainingArtifact("pre-warp-audio", $"{artifactRoot}/pre-warp.wav", Sha256File(preWarpPath)),
                    new BioacousticTrainingArtifact("post-warp-audio", $"{artifactRoot}/post-warp.wav", Sha256File(postWarpPath)),
                    new BioacousticTrainingArtifact("reconstructed-from-detections", $"{artifactRoot}/reconstructed-from-detections.wav", Sha256File(reconstructedPath)),
                    new BioacousticTrainingArtifact("summary-json", $"{artifactRoot}/summary.json", "")
                ],
                observations
                    .Select(observation => new BioacousticTrainingObservation(
                        observation.EventIndex,
                        observation.SampleOffset,
                        observation.Confidence))
                    .ToArray());

            await File.WriteAllTextAsync(
                summaryPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            result = result with
            {
                Artifacts = result.Artifacts
                    .Select(artifact => artifact.Kind == "summary-json"
                        ? artifact with { ContentHash = Sha256File(summaryPath) }
                        : artifact)
                    .ToArray()
            };
            results.Add(result);
            Console.WriteLine(
                $"bioacoustic-train hypothesis={hypothesis.Id} degradation={degradation.Name} total={totalScore:0.000} identity={identityScore:0.000} timing={timingScore:0.000} speed={speedScore:0.000} " +
                $"precision={precision:0.000} recall={recall:0.000} confidence={confidence:0.000} mae={timingMae:0.000} globalMae={globalTimingMae:0.000} globalAnchors={clock?.AnchorCount ?? 0} anchorCoverage={anchorCoverage:0.000} decodeMs={decodeMilliseconds:0.000} realtime={realtime:0.0}x artifacts={RelativePath(Directory.GetCurrentDirectory(), settingDirectory)}");
        }
    }

    await WriteBioacousticTrainingCacheAsync(cachePath, results).ConfigureAwait(false);
    var best = results.OrderByDescending(result => result.TotalScore).First();
    var runSummaryPath = Path.Combine(runDirectory, "run-summary.json");
    await File.WriteAllTextAsync(
        runSummaryPath,
        JsonSerializer.Serialize(new
        {
            runId,
            startedAtUtc = runStarted.ToString("O"),
            sampleRate,
            seconds,
            resultCount = results.Count,
            cache = Path.GetFileName(cachePath),
            best = new
            {
                best.ResultId,
                best.HypothesisId,
                best.DegradationName,
                best.TotalScore,
                best.IdentityScore,
                best.TimingScore,
                best.SpeedScore,
                best.DecodeMilliseconds,
                best.RealtimeFactor
            }
        }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    Console.WriteLine($"bioacoustic-training-run path={runDirectory} cache={cachePath} results={results.Count} best={best.HypothesisId}/{best.DegradationName} total={best.TotalScore:0.000}");
    return 0;
}

static async Task<int> RunBioacousticContestantsAsync(
    int sampleRate,
    double seconds,
    string outputRoot,
    int maxSongs,
    int maxDecoders,
    int maxDegradations,
    string songFilter,
    string decoderFilter)
{
    var started = DateTimeOffset.UtcNow;
    var runId = $"contestants-{started:yyyyMMdd-HHmmss}";
    var runDirectory = Path.GetFullPath(Path.Combine(outputRoot, runId));
    Directory.CreateDirectory(runDirectory);
    var degradations = CepstralTrainingDegradationSettings().Take(Math.Max(1, maxDegradations)).ToArray();
    var decoders = BioacousticTrainingHypotheses()
        .Where(hypothesis => string.IsNullOrWhiteSpace(decoderFilter) || string.Equals(hypothesis.Id, decoderFilter, StringComparison.OrdinalIgnoreCase))
        .Take(Math.Max(1, maxDecoders))
        .ToArray();
    var results = new List<BioacousticContestantResult>();

    foreach (var profile in MimirBioacousticContestants.BuiltIn
                 .Where(profile => string.IsNullOrWhiteSpace(songFilter) || string.Equals(profile.Id, songFilter, StringComparison.OrdinalIgnoreCase))
                 .Take(Math.Max(1, maxSongs)))
    {
        var renderer = new MimirBioacousticContestantRenderer(profile);
        var source = renderer.RenderSequenceMonoFloat(seconds, sampleRate);
        var expectedEvents = renderer.ExpectedEvents(source.Length / (double)sampleRate);
        var contestantDirectory = Path.Combine(runDirectory, profile.Id);
        Directory.CreateDirectory(contestantDirectory);
        WriteWave(Path.Combine(contestantDirectory, "source.wav"), source, sampleRate);

        foreach (var decoder in decoders)
        {
            var index = BuildCepstralContestantWordIndex(sampleRate, decoder.Decoder, renderer);
            foreach (var degradation in degradations)
            {
                var degraded = RoundTripThroughDegradedCepstrum(source, sampleRate, degradation, out var analysis);
                var stamp = Stopwatch.GetTimestamp();
                var observations = DecodeCepstralIndexedWordsWithContestantIndex(
                    degraded,
                    sampleRate,
                    expectedEvents.Count,
                    decoder.Decoder,
                    index,
                    renderer);
                var expectedObservations = observations
                    .Where(observation => expectedEvents.Contains(observation.EventIndex))
                    .ToArray();
                var payloadClassifications = ClassifyContestantPayloads(
                    degraded,
                    sampleRate,
                    decoder.Decoder,
                    renderer,
                    expectedObservations,
                    decoder.Decoder.ProposalMode == CepstralProposalMode.StreamingPacketRazor);
                var decodeMs = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                var correct = expectedObservations.Length;
                var payloadCorrect = expectedObservations.Count(observation =>
                    payloadClassifications.TryGetValue(observation.EventIndex, out var payload) &&
                    payload == renderer.PayloadSymbolForEvent(observation.EventIndex));
                var precision = observations.Count == 0 ? 0.0 : correct / (double)observations.Count;
                var recall = expectedEvents.Count == 0 ? 0.0 : correct / (double)expectedEvents.Count;
                var payloadAccuracy = observations.Count == 0 || expectedEvents.Count == 0
                    ? 0.0
                    : Math.Sqrt(payloadCorrect / (double)observations.Count * payloadCorrect / (double)expectedEvents.Count);
                var confidence = observations.Count == 0 ? 0.0 : observations.Average(observation => observation.Confidence);
                var clock = FitContestantClockHypothesis(expectedObservations, renderer, sampleRate, expectedEvents.Count);
                var timingAccuracy = clock == null
                    ? 0.0
                    : 1.0 / (1.0 + clock.MeanAbsoluteErrorSamples / Math.Max(1.0, sampleRate * 0.00025));
                var frequencyAccuracy = expectedObservations.Length == 0
                    ? 0.0
                    : expectedObservations.Average(observation => observation.ShapeAccuracy);
                var convergence = clock == null
                    ? 0.0
                    : Math.Clamp(clock.AnchorCoverage, 0.0, 1.0) * Math.Clamp(clock.Confidence, 0.0, 1.0);
                var realtime = decodeMs <= 0.0 ? double.PositiveInfinity : seconds * 1000.0 / decodeMs;
                var performance = Math.Sqrt(Math.Clamp(realtime / 50.0, 0.0, 1.0) * Math.Max(0.0, convergence));
                var anchorAccuracy = Math.Sqrt(Math.Max(0.0, timingAccuracy) * Math.Max(0.0, frequencyAccuracy));
                var contestScore = performance * anchorAccuracy;
                var payloadBitrate = profile.EventSpacingSeconds <= 0.0
                    ? 0.0
                    : profile.PayloadBitsPerEvent * payloadAccuracy / profile.EventSpacingSeconds;
                var languageScore = realtime * timingAccuracy * frequencyAccuracy * payloadBitrate;
                var result = new BioacousticContestantResult(
                    $"{runId}:{profile.Id}:{decoder.Id}:{degradation.Name}",
                    runId,
                    profile.Id,
                    profile.Kind.ToString(),
                    decoder.Id,
                    degradation.Name,
                    sampleRate,
                    seconds,
                    expectedEvents.Count,
                    observations.Count,
                    correct,
                    precision,
                    recall,
                    confidence,
                    timingAccuracy,
                    frequencyAccuracy,
                    payloadAccuracy,
                    convergence,
                    realtime,
                    payloadBitrate,
                    languageScore,
                    contestScore,
                    decodeMs,
                    analysis.MelBins,
                    analysis.CepstralCoefficients,
                    clock == null
                        ? new BioacousticClockHypothesisSnapshot(0, 0, 0, 0, 0, 0, 0)
                        : new BioacousticClockHypothesisSnapshot(
                            clock.AnchorCount,
                            clock.SourceOffsetSamples,
                            clock.EffectiveSampleRate,
                            clock.MeanAbsoluteErrorSamples,
                            clock.Confidence,
                            clock.Score,
                            clock.AnchorCoverage),
                    observations.Select(observation => new BioacousticContestantObservation(
                        observation.EventIndex,
                        observation.PayloadSymbol,
                        payloadClassifications.TryGetValue(observation.EventIndex, out var classifiedPayload) ? classifiedPayload : -1,
                        observation.SampleOffset,
                        observation.Confidence,
                        observation.ShapeAccuracy)).ToArray(),
                    profile.BeautyNotes);
                results.Add(result);
                Console.WriteLine(
                    $"bioacoustic-contestant song={profile.Id} decoder={decoder.Id} degradation={degradation.Name} languageScore={languageScore:0.000} bitrate={payloadBitrate:0.0}bps payload={payloadAccuracy:0.000} score={contestScore:0.000} perf={performance:0.000} anchor={anchorAccuracy:0.000} timing={timingAccuracy:0.000} freq={frequencyAccuracy:0.000} convergence={convergence:0.000} realtime={realtime:0.0}x correct={correct}/{expectedEvents.Count} observations={observations.Count} confidence={confidence:0.000}");
            }
        }
    }

    var summaryPath = Path.Combine(runDirectory, "contestant-summary.json");
    await File.WriteAllTextAsync(
        summaryPath,
        JsonSerializer.Serialize(new
        {
            runId,
            startedAtUtc = started.ToString("O"),
            sampleRate,
            seconds,
            best = results.OrderByDescending(result => result.LanguageScore).FirstOrDefault(),
            bySong = results
                .GroupBy(result => result.SongId)
                .Select(group => new
                {
                    song = group.Key,
                    average = group.Average(result => result.LanguageScore),
                    best = group.Max(result => result.LanguageScore),
                    worst = group.Min(result => result.LanguageScore)
                })
                .OrderByDescending(row => row.average)
                .ToArray(),
            results
        }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    Console.WriteLine($"bioacoustic-contestants-run path={runDirectory} results={results.Count} best={results.OrderByDescending(result => result.LanguageScore).First().SongId} languageScore={results.Max(result => result.LanguageScore):0.000}");
    return results.Count > 0 && results.Max(result => result.LanguageScore) > 0.0 ? 0 : 1;
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

static int RunBioacousticActuatorSelfTest(int sampleRate, double delaySamples)
{
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

    var delayed = ApplyFractionalDelay(reference, delaySamples);
    var before = EstimateBioacousticDelay(reference, delayed, sampleRate);
    if (before == null)
    {
        Console.Error.WriteLine("bioacoustic actuator self-test failed: analyzer could not estimate the delayed candidate");
        return 1;
    }

    var corrected = ApplyFractionalDelay(delayed, -before.FractionalDelaySamples);
    var after = EstimateBioacousticDelay(reference, corrected, sampleRate);
    if (after == null)
    {
        Console.Error.WriteLine("bioacoustic actuator self-test failed: analyzer could not estimate the corrected candidate");
        return 1;
    }

    var beforeError = Math.Abs(before.FractionalDelaySamples - delaySamples);
    var afterResidual = Math.Abs(after.FractionalDelaySamples);
    Console.WriteLine(
        $"bioacoustic-actuator-self-test estimatedDelay={before.FractionalDelaySamples:0.000000} expected={delaySamples:0.000000} beforeErrorSamples={beforeError:0.000000} " +
        $"correctedResidualSamples={afterResidual:0.000000} correctedResidualUs={afterResidual * 1_000_000.0 / sampleRate:0.000} beforeConfidence={before.Confidence:0.000} afterConfidence={after.Confidence:0.000}");
    return beforeError < 0.05 &&
        afterResidual * 1_000_000.0 / sampleRate < 1.0 &&
        after.Confidence > 0.70
            ? 0
            : 1;
}

static int RunComplexContourTrackerSelfTest(int sampleRate, double delaySamples, double reflectionDelaySamples)
{
    var renderer = new MimirBioacousticContestantRenderer(MimirBioacousticContestants.CanaryPacketTrill);
    var seconds = 1.45;
    var reference = renderer.RenderSequenceMonoFloat(seconds, sampleRate);
    var direct = ApplyFractionalDelay(reference, delaySamples);
    var reflection = ApplyFractionalDelay(reference, delaySamples + reflectionDelaySamples);
    var candidate = new float[reference.Length];
    for (var index = 0; index < candidate.Length; index++)
    {
        candidate[index] = (float)(direct[index] * 0.62 + reflection[index] * 1.00);
    }

    var bank = new MimirComplexContourMatchedFilterBank(renderer, sampleRate);
    var eventIndices = Enumerable.Range(0, renderer.ExpectedEventCount(seconds)).Select(index => (ulong)index).ToArray();
    var referenceHits = bank.AnalyzeEvents(reference, eventIndices, 0.0, Math.Max(2, sampleRate / 4_000));
    var candidateHits = bank.AnalyzeEvents(candidate, eventIndices, delaySamples, Math.Max(48, sampleRate / 900));
    var tracker = new MimirDirectPathTracker(sampleRate);
    var estimate = tracker.Update(referenceHits, candidateHits, delaySamples);
    if (estimate == null)
    {
        Console.WriteLine(
            $"complex-contour-tracker-self-test status=no-lock referenceHits={referenceHits.Count} candidateHits={candidateHits.Count}");
        return 1;
    }

    var errorSamples = Math.Abs(estimate.DelaySamples - delaySamples);
    var firstReflection = estimate.ReflectionTaps.FirstOrDefault();
    Console.WriteLine(
        $"complex-contour-tracker-self-test delaySamples={estimate.DelaySamples:0.000000} expected={delaySamples:0.000000} " +
        $"errorSamples={errorSamples:0.000000} errorUs={errorSamples * 1_000_000.0 / sampleRate:0.000} " +
        $"confidence={estimate.Confidence:0.000} directHits={estimate.DirectHitCount} maeSamples={estimate.MeanAbsoluteErrorSamples:0.000000} " +
        $"referenceHits={referenceHits.Count} candidateHits={candidateHits.Count} reflectionTaps={estimate.ReflectionTaps.Count} " +
        $"firstReflectionSamples={(firstReflection?.RelativeDelaySamples ?? double.NaN):0.000}");
    return errorSamples < 3.0 && estimate.DirectHitCount >= 8
        ? 0
        : 1;
}

static int RunComplexContourRuntimeSelfTest(int sampleRate, double delaySamples)
{
    var renderer = new MimirBioacousticContestantRenderer(MimirBioacousticContestants.CanaryPacketTrill);
    const double runtimeSeconds = 1.75;
    var reference = renderer.RenderSequenceMonoFloat(runtimeSeconds - 0.5, sampleRate);
    var candidate = ApplyFractionalDelay(reference, delaySamples);
    var referenceBuffer = BuildSyntheticAudioBuffer("asio-ch2", sampleRate, reference);
    var candidateBuffer = BuildSyntheticAudioBuffer("asio-ch1", sampleRate, candidate);
    var analyzer = new MimirComplexContourRuntimeAnalyzer(
        new MimirComplexContourRuntimeOptions(
            ProfileId: MimirBioacousticContestants.CanaryPacketTrill.Id,
            ScheduleStartSeconds: 0.5,
            SearchRadiusSeconds: 0.020,
            ReferenceSearchRadiusSeconds: 0.020));
    var report = analyzer.Analyze([referenceBuffer, candidateBuffer], "asio-ch2", runtimeSeconds).FirstOrDefault();
    if (report == null)
    {
        Console.WriteLine("complex-contour-runtime-self-test status=no-report");
        return 1;
    }

    var errorUs = Math.Abs(report.FractionalDelaySamples - delaySamples) * 1_000_000.0 / sampleRate;
    Console.WriteLine(
        $"complex-contour-runtime-self-test delaySamples={report.FractionalDelaySamples:0.000000} expected={delaySamples:0.000000} errorUs={errorUs:0.000} confidence={report.Confidence:0.000} directHits={report.TimelineMatchedEvents}");
    return errorUs < 20.0 && report.TimelineMatchedEvents >= 8 ? 0 : 1;
}

static MimirRollingStreamBuffer BuildSyntheticAudioBuffer(string sourceId, int sampleRate, float[] samples)
{
    var descriptor = new MimirStreamDescriptor(sourceId, MimirStreamKind.Audio, MimirStreamOrigin.LocalDevice);
    var buffer = new MimirRollingStreamBuffer(descriptor, TimeSpan.FromSeconds(5));
    var bytes = new byte[samples.Length * sizeof(float)];
    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
    buffer.Append(new MimirStreamSample(
        sourceId,
        MimirStreamKind.Audio,
        MimirStreamOrigin.LocalDevice,
        TimestampNs: (long)Math.Round(samples.Length * 1_000_000_000.0 / sampleRate),
        ArrivalNs: (long)Math.Round(samples.Length * 1_000_000_000.0 / sampleRate),
        Sequence: 1,
        PayloadHandle: 0,
        ByteLength: bytes.Length,
        Data: bytes,
        AudioBlock: new MimirAudioBlockDescriptor(
            sampleRate,
            1,
            MimirAudioSampleFormat.Float32,
            samples.Length,
            0)));
    return buffer;
}

static int RunPerfectMachineProfileSmoke()
{
    var profiles = MimirPerfectMachineProfiles.All;
    var calibrationPlans = MimirCalibrationSessionPlans.BuiltIn;
    var audioFields = MimirAudioFieldConfigurations.BuiltIn;
    var visualFields = MimirVisualFusionConfigurations.BuiltIn;
    var computePlans = MimirComputeOffloadConfigurations.BuiltIn;
    var assemblyPlans = MimirMachineAssemblyPlans.BuiltIn;
    var captureProfiles = MimirNativeCaptureConfigurations.BuiltIn;
    var publications = MimirProgramPublicationConfigurations.BuiltIn;
    var moduleCatalog = MimirModuleLibrary.Entries;
    var languageProfiles = MimirBioacousticLanguageConfigurations.BuiltIn;
    var pathLearningProfiles = MimirAcousticPathLearningConfigurations.BuiltIn;
    var benchmarkPanels = MimirBenchmarkPanelConfigurations.BuiltIn;
    var actuatorStrategies = MimirAudioActuatorConfigurations.BuiltIn;
    var cameraIngestStrategies = MimirCameraIngestConfigurations.BuiltIn;
    var reservoirStrategies = MimirReservoirConfigurations.BuiltIn;
    var localizationProfiles = MimirAcousticLocalizationConfigurations.BuiltIn;
    var distributedWitnesses = MimirDistributedWitnessConfigurations.BuiltIn;
    var networkTransports = MimirNetworkTransportConfigurations.BuiltIn;
    var authorityPolicies = MimirAuthorityPolicyConfigurations.BuiltIn;
    var codebook = MimirCultMeshContractFactory.CreateCodebookState(
        "mimir-bioacoustic-default",
        "deterministic-segment-schedule-v1",
        MimirBioacousticTimeline.Default);
    var decoder = MimirCultMeshContractFactory.CreateDecoderState(
        "baseline-mfcc-index",
        codebook.CodebookId,
        MimirBioacousticDecoderConfiguration.BaselineMfccIndex);
    var controller = new MimirSroPllActuatorController();
    var command = controller.Update(
        "scarlett-host-mic",
        0,
        delaySamples: 317.375,
        confidence: 0.82,
        dtSeconds: 0.5);
    var actuator = MimirCultMeshContractFactory.CreateActuatorState(
        "scarlett-host-mic-actuator",
        MimirAlignmentActuatorProfile.SixSourceFaust.Id,
        command);
    var scene = MimirCultMeshContractFactory.CreateObsSceneMirror("mimir-current-program");
    var output = MimirCultMeshContractFactory.CreateProgramOutput(
        "mimir-site-program",
        scene.SceneId,
        MimirProgramPublicationConfigurations.YggdrasilSiteProgram);
    var eveSurface = MimirCultMeshContractFactory.CreateOperatorSurface(
        "mimir-eve-gui-compositor",
        scene.SceneId,
        MimirProgramPublicationConfigurations.OperatorSurfaces[0]);
    var observations = new[]
    {
        new MimirBioacousticWordObservation(0, MimirBioacousticTimeline.Default.EventForIndex(0).StartSeconds * MimirBioacousticTimeline.SampleRate + 317.375, 0.95),
        new MimirBioacousticWordObservation(1, MimirBioacousticTimeline.Default.EventForIndex(1).StartSeconds * MimirBioacousticTimeline.SampleRate + 317.375, 0.95),
        new MimirBioacousticWordObservation(2, MimirBioacousticTimeline.Default.EventForIndex(2).StartSeconds * MimirBioacousticTimeline.SampleRate + 317.375, 0.95),
        new MimirBioacousticWordObservation(3, MimirBioacousticTimeline.Default.EventForIndex(3).StartSeconds * MimirBioacousticTimeline.SampleRate + 317.375, 0.95),
    };
    var clock = new MimirBioacousticClockSolver().Fit(
        observations,
        MimirBioacousticTimeline.Default,
        MimirBioacousticTimeline.SampleRate,
        expectedEventCount: 4);
    var videoBuffer = new MimirRollingStreamBuffer(
        new MimirStreamDescriptor("kiyo-pro-rgb", MimirStreamKind.Video, MimirStreamOrigin.LocalDevice),
        TimeSpan.FromSeconds(5));
    videoBuffer.Append(new MimirStreamSample(
        "kiyo-pro-rgb",
        MimirStreamKind.Video,
        MimirStreamOrigin.LocalDevice,
        TimestampNs: 1_000_000_000L,
        ArrivalNs: 1_000_000_010L,
        Sequence: 1,
        PayloadHandle: 0,
        VideoFrame: new MimirVideoFrameDescriptor(
            1920,
            1080,
            MimirVideoPixelFormat.Bgra8,
            1920 * 4,
            1_000_000_000L,
            NativeHandle: 42,
            NativeHandleKind: "shared-d3d12-texture")));
    var lowerer = new MimirFensalirFieldLowering();
    var gpuFrame = lowerer.BuildGpuSensorFrame([videoBuffer]);
    var acousticFrame = lowerer.BuildAcousticFieldFrame([
        new MimirAudioSynchronizationState(
            "loopback-scarlett-speakers",
            "scarlett-host-mic",
            192_000,
            61.25,
            61.25,
            0.319,
            0.875,
            0.94,
            [],
            1_000_000_000L,
            10,
            10)
    ]);
    var localizationGrid = MimirSrpPhatGridSolver.BuildGrid(
        new Vector3(-0.5f, 0.0f, 1.0f),
        new Vector3(0.5f, 0.0f, 1.0f),
        spacingMeters: 0.5);
    var localization = new MimirSrpPhatGridSolver().FindBestCandidate(
        [
            new MimirMicrophonePose("mic-left", new Vector3(-0.5f, 0.0f, 0.0f)),
            new MimirMicrophonePose("mic-right", new Vector3(0.5f, 0.0f, 0.0f))
        ],
        localizationGrid,
        [
            new MimirPairDelayObservation("mic-left", "mic-right", 0.0, 0.95)
        ]);
    var authority = new MimirAuthorityPolicyEvaluator().Evaluate(new MimirAuthorityEvaluationInput(
        "raven-scarlett-witness",
        new HashSet<string>(["known-node", "matching-codebook", "clock-fit-confidence", "response-profile"], StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal)));
    var transport = new MimirNetworkTransportSelector().Select(new MimirNetworkTransportRequest(
        MimirNetworkPayloadKind.TypedTimingState,
        RequiresClockInfluence: true,
        AllowsRawMedia: false,
        MaximumLatencyMilliseconds: 25.0,
        new HashSet<string>(
            ["mimir.bioacoustic_codebook_state", "mimir.bioacoustic_decoder_state", "mimir.acoustic_path_state"],
            StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal)));
    var trackingSmoke = BuildMoveTrackingSmokeHub();
    var trackingObservations = trackingSmoke.Hub.TrackingObservations;

    Console.WriteLine(
        $"perfect-machine-profile-smoke profiles={profiles.Count} calibrationPlans={calibrationPlans.Count} audioFields={audioFields.Count} visualFields={visualFields.Count} computePlans={computePlans.Count} assemblyPlans={assemblyPlans.Count} captureProfiles={captureProfiles.Count} publications={publications.Count} languageProfiles={languageProfiles.Count} pathLearning={pathLearningProfiles.Count} localization={localizationProfiles.Count} benchmarkPanels={benchmarkPanels.Count} actuatorStrategies={actuatorStrategies.Count} cameraIngest={cameraIngestStrategies.Count} reservoirs={reservoirStrategies.Count} distributedWitnesses={distributedWitnesses.Count} networkTransports={networkTransports.Count} authorityPolicies={authorityPolicies.Count} modules={moduleCatalog.Count}");
    Console.WriteLine(
        $"perfect-machine-codebook id={codebook.CodebookId} motifs={codebook.Motifs.Length} decoder={decoder.Configuration.Id} augmentations={decoder.Configuration.TemplateAugmentations.Length}");
    Console.WriteLine(
        $"perfect-machine-actuator source={actuator.SourceId} delay={actuator.TargetDelaySamples:0.000} ratio={actuator.ResampleRatio:0.000000000} controls={actuator.FaustControls.Length}");
    Console.WriteLine(
        $"perfect-machine-program scene={scene.SceneId} layers={scene.Layers.Length} output={output.OutputId} route={output.PublisherRoute} eveCommands={eveSurface.CommandTopics.Length}");
    Console.WriteLine(
        $"perfect-machine-clock anchors={clock?.AnchorCount ?? 0} coverage={clock?.AnchorCoverage ?? 0:0.000} offset={clock?.SourceOffsetSamples ?? double.NaN:0.000} confidence={clock?.Confidence ?? 0:0.000}");
    Console.WriteLine(
        $"perfect-machine-fensalir gpuCameras={gpuFrame.Cameras.Count} gpuTextures={gpuFrame.ExternalTextures.Count} acousticConstraints={acousticFrame.Constraints.Count} timingConfidence={acousticFrame.TimingConfidence:0.000}");
    Console.WriteLine(
        $"perfect-machine-localization candidates={localizationGrid.Count} best=({localization?.PositionMeters.X ?? float.NaN:0.000},{localization?.PositionMeters.Y ?? float.NaN:0.000},{localization?.PositionMeters.Z ?? float.NaN:0.000}) score={localization?.Score ?? 0.0:0.000}");
    Console.WriteLine(
        $"perfect-machine-authority appliesTo=raven-scarlett-witness decision={authority.Decision} rule={authority.RuleId}");
    Console.WriteLine(
        $"perfect-machine-transport payload=TypedTimingState status={transport.Status} selected={transport.Transport?.Id ?? "none"}");
    Console.WriteLine(
        $"perfect-machine-move-tracking consumed={trackingSmoke.Consumed} observations={trackingObservations.Count} hosts={string.Join(",", trackingObservations.Select(observation => observation.HostId).Order(StringComparer.Ordinal))}");

    return profiles.Count >= 6 &&
        calibrationPlans.Count >= 3 &&
        audioFields.Count >= 4 &&
        visualFields.Count >= 4 &&
        computePlans.Count >= 3 &&
        assemblyPlans.Count >= 4 &&
        captureProfiles.Count >= 8 &&
        publications.Count >= 3 &&
        languageProfiles.Count >= 5 &&
        pathLearningProfiles.Count >= 3 &&
        localizationProfiles.Count >= 4 &&
        benchmarkPanels.Count >= 2 &&
        actuatorStrategies.Count >= 4 &&
        cameraIngestStrategies.Count >= 4 &&
        reservoirStrategies.Count >= 3 &&
        distributedWitnesses.Count >= 4 &&
        networkTransports.Count >= 4 &&
        authorityPolicies.Count >= 1 &&
        moduleCatalog.Count >= 17 &&
        codebook.Motifs.Length == MimirBioacousticTimeline.SymbolCount &&
        decoder.Configuration.TemplateAugmentations.Length > 0 &&
        actuator.FaustControls.Length >= 2 &&
        gpuFrame.HasInput &&
        acousticFrame.HasInput &&
        localization is { Score: > 0.90 } &&
        authority.Decision == MimirAuthorityDecision.TrustedEvidence &&
        transport.Transport?.Id == MimirNetworkTransportConfigurations.CultMeshTimingState.Id &&
        trackingSmoke.Consumed == 2 &&
        trackingObservations.Count == 2 &&
        trackingObservations.Any(observation => observation.HostId == "starfire" && observation.ProducerId == "muninn:starfire:move") &&
        trackingObservations.Any(observation => observation.HostId == "nightwing" && observation.ProducerId == "muninn:nightwing:move") &&
        clock is { AnchorCount: >= 4, Confidence: > 0.70 }
            ? 0
            : 1;
}

static int RunMoveTrackingContractSmoke()
{
    using var trackingSmoke = BuildMoveTrackingSmokeHub();
    var observations = trackingSmoke.Hub.TrackingObservations;
    foreach (var observation in observations)
    {
        Console.WriteLine(
            $"move-tracking-observation stream={observation.StreamId} device={observation.DeviceId} host={observation.HostId} producer={observation.ProducerId} discovery={observation.DiscoveryProviderId} confidence={observation.Confidence:0.000} pos=({observation.PositionMeters.X:0.000},{observation.PositionMeters.Y:0.000},{observation.PositionMeters.Z:0.000})");
    }

    Console.WriteLine(
        $"move-tracking-contract-smoke consumed={trackingSmoke.Consumed} observations={observations.Count} summary=\"{trackingSmoke.Hub.Summary()}\"");
    return trackingSmoke.Consumed == 2 &&
        observations.Count == 2 &&
        observations.All(observation => observation.Kind == MimirTrackingObservationKind.PsMoveController) &&
        observations.Any(observation => observation.HostId == "starfire" && observation.DiscoveryProviderId == "odin") &&
        observations.Any(observation => observation.HostId == "nightwing" && observation.DiscoveryProviderId == "odin")
            ? 0
            : 1;
}

static int RunMoveNativeReservoirSmoke(string nativeReservoirPath)
{
    if (!File.Exists(nativeReservoirPath))
    {
        Console.Error.WriteLine($"move-native-reservoir-smoke missing-native path={nativeReservoirPath}");
        return 1;
    }

    using var runtime = new MimirNativeReservoirRuntime(nativeReservoirPath);
    var samples = new[]
    {
        new MimirNativeMoveEvidenceSample(
            WitnessIdHash: 0x57A2_F12E_0001,
            ControllerIdHash: 0xC011_7A01,
            SourceTimestampNs: 1_000_000_000,
            ArrivalNs: 1_000_000_700,
            Sequence: 41,
            EvidenceKind: (uint)MimirNativeMoveEvidenceKind.OpticalMarker,
            Flags: 0,
            ImageX: 318.25f,
            ImageY: 119.5f,
            RadiusPx: 13.75f,
            Confidence: 0.91f,
            AccelX: 0.0f,
            AccelY: 0.0f,
            AccelZ: 0.0f,
            GyroX: 0.0f,
            GyroY: 0.0f,
            GyroZ: 0.0f,
            Trigger: 0.0f,
            ButtonsMask: 0,
            Reserved: 0,
            Battery01: float.NaN,
            Reserved1: 0,
            Reserved2: 0),
        new MimirNativeMoveEvidenceSample(
            WitnessIdHash: 0x516E_7716_0001,
            ControllerIdHash: 0xC011_7A01,
            SourceTimestampNs: 1_000_000_500,
            ArrivalNs: 1_000_001_000,
            Sequence: 42,
            EvidenceKind: (uint)MimirNativeMoveEvidenceKind.ControllerState,
            Flags: 0,
            ImageX: float.NaN,
            ImageY: float.NaN,
            RadiusPx: float.NaN,
            Confidence: 0.84f,
            AccelX: -0.02f,
            AccelY: 0.11f,
            AccelZ: 0.98f,
            GyroX: 0.01f,
            GyroY: -0.02f,
            GyroZ: 0.03f,
            Trigger: 0.42f,
            ButtonsMask: 0b101,
            Reserved: 0,
            Battery01: 0.77f,
            Reserved1: 0,
            Reserved2: 0)
    };

    var handle = runtime.AdmitMoveEvidence(
        "mimir:move-native-reservoir-smoke",
        samples,
        "synthetic-move-calibration",
        "mimir-stage-space");
    var status = runtime.Status;

    Console.WriteLine(
        $"move-native-reservoir-smoke native={nativeReservoirPath} stride={MimirNativeReservoirRuntime.MoveEvidenceSampleStrideBytes} samples={samples.Length} payload=0x{handle.PayloadHandle:X} total={status.TotalSampleCount} moveEvidence={status.MoveEvidenceCount} edgeNs={status.EdgeNs}");

    return MimirNativeReservoirRuntime.MoveEvidenceSampleStrideBytes == 112 &&
        samples.Length == 2 &&
        handle.PayloadHandle != 0 &&
        status.TotalSampleCount.ToUInt64() == 1 &&
        status.MoveEvidenceCount.ToUInt64() == 1 &&
        status.EdgeNs == 1_000_000_500
            ? 0
            : 1;
}

static int RunMuninnMoveEvidenceSmoke(string nativeReservoirPath)
{
    if (!File.Exists(nativeReservoirPath))
    {
        Console.Error.WriteLine($"muninn-move-evidence-smoke missing-native path={nativeReservoirPath}");
        return 1;
    }

    var markers = new[]
    {
        new MuninnMoveMarkerCandidateDocument(
            StreamId: "nightwing:ps3-eye-0:move-marker-candidates",
            HostId: "nightwing",
            CameraId: "ps3-eye-0",
            FrameSequence: 9001,
            SourceIdHash: 0xA11CE,
            TileX: 19,
            TileY: 7,
            CenterXPx: 313.25f,
            CenterYPx: 118.75f,
            RadiusPx: 12.5f,
            AreaPx: 491,
            MeanLuma: 0.62f,
            PeakLuma: 252,
            Score: 0.88f,
            ObservedAt: "2026-06-11T18:30:00.0010000Z"),
        new MuninnMoveMarkerCandidateDocument(
            StreamId: "starfire:ps3-eye-1:move-marker-candidates",
            HostId: "starfire",
            CameraId: "ps3-eye-1",
            FrameSequence: 9002,
            SourceIdHash: 0xB0B,
            TileX: 11,
            TileY: 10,
            CenterXPx: 201.5f,
            CenterYPx: 160.25f,
            RadiusPx: 10.75f,
            AreaPx: 363,
            MeanLuma: 0.58f,
            PeakLuma: 246,
            Score: 0.79f,
            ObservedAt: "2026-06-11T18:30:00.0020000Z")
    };
    var controllers = new[]
    {
        new MuninnMoveControllerStateDocument(
            StreamId: "nightwing:move-usb:move-controller-state",
            HostId: "nightwing",
            MoveId: "move-usb",
            Sequence: 77,
            SourceTimestampNs: 1_781_194_200_003_000_000,
            AccelerometerXyz: [-0.02f, 0.11f, 0.98f],
            GyroscopeXyz: [0.01f, -0.02f, 0.03f],
            MagnetometerXyz: [0.0f, 0.0f, 0.0f],
            TriggerValue: 0.42f,
            Buttons: ["move", "trigger"],
            Battery01: float.NaN,
            ObservedAt: "2026-06-11T18:30:00.0030000Z")
    };

    var samples = MimirMuninnMoveEvidenceAdapter.BuildNativeSamples(markers, controllers);
    using var runtime = new MimirNativeReservoirRuntime(nativeReservoirPath);
    var handle = runtime.AdmitMoveEvidence(
        "mimir:muninn-move-evidence-smoke",
        samples,
        "mimir-move-stage-calibration-v1",
        "mimir-stage-space");
    var status = runtime.Status;
    var optical = samples.Count(sample => sample.EvidenceKind == (uint)MimirNativeMoveEvidenceKind.OpticalMarker);
    var controller = samples.Count(sample => sample.EvidenceKind == (uint)MimirNativeMoveEvidenceKind.ControllerState);
    var buttonMask = samples.Single(sample => sample.EvidenceKind == (uint)MimirNativeMoveEvidenceKind.ControllerState).ButtonsMask;

    Console.WriteLine(
        $"muninn-move-evidence-smoke native={nativeReservoirPath} samples={samples.Count} optical={optical} controller={controller} buttonMask=0x{buttonMask:X} payload=0x{handle.PayloadHandle:X} total={status.TotalSampleCount} moveEvidence={status.MoveEvidenceCount} edgeNs={status.EdgeNs}");

    return samples.Count == 3 &&
        optical == 2 &&
        controller == 1 &&
        buttonMask == ((1u << 17) | (1u << 18)) &&
        handle.PayloadHandle != 0 &&
        status.TotalSampleCount.ToUInt64() == 1 &&
        status.MoveEvidenceCount.ToUInt64() == 1
            ? 0
            : 1;
}

static int RunMuninnMoveIdentitySmoke()
{
    var identity = new MuninnMoveIdentityDocument(
        IdentityId: "starfire:move-000704a800d0:move-identity",
        HostId: "starfire",
        MoveId: "move-000704a800d0",
        SourcePath: @"windows-psmove:\\?\hid#vid_054c&pid_03d5&col01#a&976df89&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        BluetoothHostAddress: "5C:93:A2:9C:A8:A8",
        State: "usb-visible",
        Detail: "Muninn discovered this PS Move on a local USB/HID input path.",
        ObservedAt: "2026-06-17T02:24:16.0000000Z");
    var nightwingWaiting = new MuninnMoveIdentityDocument(
        IdentityId: "nightwing:move-000704a800d0:move-identity",
        HostId: "nightwing",
        MoveId: "move-000704a800d0",
        SourcePath: "bluetooth:00:07:04:A8:00:D0",
        BluetoothHostAddress: "5C:93:A2:9C:A8:A8",
        State: "bluetooth-waiting",
        Detail: "Muninn sees this trusted PS Move in BlueZ and will attempt bounded pickup while disconnected.",
        ObservedAt: "2026-06-17T02:24:20.0000000Z");
    var connected = new MuninnMoveIdentityDocument(
        IdentityId: "nightwing:move-000704a6be5f:move-identity",
        HostId: "nightwing",
        MoveId: "move-000704a6be5f",
        SourcePath: "bluetooth:00:07:04:A6:BE:5F",
        BluetoothHostAddress: "5C:93:A2:9C:A8:A8",
        State: "bluetooth-connected",
        Detail: "Muninn sees this PS Move connected through BlueZ.",
        ObservedAt: "2026-06-17T02:24:19.0000000Z");

    var encoded = MessagePackSerializer.Serialize(identity);
    var decoded = MessagePackSerializer.Deserialize<MuninnMoveIdentityDocument>(encoded);
    var snapshot = MimirMuninnMoveEvidenceAdapter.BuildIdentitySnapshots([decoded]).Single();
    var expectedHash = MimirMuninnMoveEvidenceAdapter.Fnva64("starfire:move-000704a800d0");
    var roster = MimirMuninnMoveEvidenceAdapter.BuildIdentityRoster([decoded, nightwingWaiting, connected]);
    var rotating = roster.Single(entry => entry.MoveId == "move-000704a800d0");
    var pickedUp = roster.Single(entry => entry.MoveId == "move-000704a6be5f");

    Console.WriteLine(
        $"muninn-move-identity-smoke identity={snapshot.IdentityId} move={snapshot.MoveId} source={snapshot.SourcePath} bluetoothHost={snapshot.BluetoothHostAddress} state={snapshot.State} observedNs={snapshot.ObservedAtNs} controllerHash=0x{snapshot.ControllerIdHash:X} roster={roster.Count} rotating={rotating.StateSummary} usbHosts={string.Join(',', rotating.UsbHostIds)} pickupHosts={string.Join(',', rotating.BluetoothPickupHostIds)} pickedUp={pickedUp.StateSummary}");

    return snapshot.IdentityId == identity.IdentityId &&
        snapshot.HostId == "starfire" &&
        snapshot.MoveId == "move-000704a800d0" &&
        snapshot.SourcePath.Contains("vid_054c&pid_03d5", StringComparison.OrdinalIgnoreCase) &&
        snapshot.BluetoothHostAddress == "5C:93:A2:9C:A8:A8" &&
        snapshot.State == "usb-visible" &&
        snapshot.ObservedAtNs > 0 &&
        snapshot.ControllerIdHash == expectedHash &&
        roster.Count == 2 &&
        rotating.HasUsbWitness &&
        rotating.UsbHostIds.SequenceEqual(["starfire"]) &&
        rotating.BluetoothPickupHostIds.SequenceEqual(["nightwing"]) &&
        rotating.StateSummary == "usb-visible+bluetooth-waiting" &&
        rotating.SourcePaths.Length == 2 &&
        pickedUp.StateSummary == "bluetooth-connected" &&
        pickedUp.BluetoothPickupHostIds.SequenceEqual(["nightwing"])
            ? 0
            : 1;
}

static int RunMuninnMoveCultMeshStreamSmoke(string nativeReservoirPath)
{
    if (!File.Exists(nativeReservoirPath))
    {
        Console.Error.WriteLine($"muninn-move-cultmesh-stream-smoke missing-native path={nativeReservoirPath}");
        return 1;
    }

    var streamId = "muninn:nightwing:move-evidence";
    var catalog = CultMesh.CreateStreamCatalog();
    catalog.Declare(MimirMuninnMoveEvidenceAdapter.CreateStreamDescriptor(
        streamId,
        "mimir-live",
        "muninn:nightwing",
        "muninn:nightwing:clock"));
    using var ring = catalog.CreateSharedMemoryRing(streamId, slotCount: 4, slotByteLength: 8192);
    var frame = new MimirMuninnMoveEvidenceStreamFrame(
        "nightwing-move-frame:78",
        "muninn:nightwing",
        1_781_202_600_004_000_000,
        [
            new MuninnMoveMarkerCandidateDocument(
                StreamId: "nightwing:ps3-eye-0:move-marker-candidates",
                HostId: "nightwing",
                CameraId: "ps3-eye-0",
                FrameSequence: 9003,
                SourceIdHash: 0xA11CE,
                TileX: 19,
                TileY: 7,
                CenterXPx: 314.0f,
                CenterYPx: 119.0f,
                RadiusPx: 12.25f,
                AreaPx: 470,
                MeanLuma: 0.61f,
                PeakLuma: 253,
                Score: 0.87f,
                ObservedAt: "2026-06-11T18:30:00.0040000Z")
        ],
        [
            new MuninnMoveControllerStateDocument(
                StreamId: "nightwing:move-usb:move-controller-state",
                HostId: "nightwing",
                MoveId: "move-usb",
                Sequence: 78,
                SourceTimestampNs: 1_781_202_600_004_000_000,
                AccelerometerXyz: [-0.01f, 0.10f, 0.99f],
                GyroscopeXyz: [0.02f, -0.01f, 0.04f],
                MagnetometerXyz: [0.0f, 0.0f, 0.0f],
                TriggerValue: 0.5f,
                Buttons: ["move"],
                Battery01: float.NaN,
                ObservedAt: "2026-06-11T18:30:00.0040000Z")
        ]);
    var payload = MimirMuninnMoveEvidenceAdapter.SerializeStreamFrame(frame);
    if (!ring.TryPublishCopy(payload, frame.PublishedAtNs, durationNs: 0, out var published))
    {
        Console.Error.WriteLine("muninn-move-cultmesh-stream-smoke publish-failed");
        return 1;
    }

    catalog.PublishFrame(published);
    using var runtime = new MimirNativeReservoirRuntime(nativeReservoirPath);
    var admitted = MimirMuninnMoveEvidenceAdapter.TryAdmitLatestCultMeshFrame(
        ring,
        runtime,
        "mimir:muninn-move-cultmesh-stream-smoke",
        "mimir-move-stage-calibration-v1",
        "mimir-stage-space",
        out var handle,
        out var sampleCount);
    var status = runtime.Status;
    var latest = catalog.LatestFrame(streamId);

    Console.WriteLine(
        $"muninn-move-cultmesh-stream-smoke stream={streamId} bytes={payload.Length} publishedSeq={published.Sequence} latestSeq={latest?.Sequence.ToString() ?? "none"} samples={sampleCount} payload=0x{handle.PayloadHandle:X} total={status.TotalSampleCount} moveEvidence={status.MoveEvidenceCount} edgeNs={status.EdgeNs}");

    return admitted &&
        payload.Length > 0 &&
        sampleCount == 2 &&
        latest?.Sequence == published.Sequence &&
        handle.PayloadHandle != 0 &&
        status.TotalSampleCount.ToUInt64() == 1 &&
        status.MoveEvidenceCount.ToUInt64() == 1
            ? 0
            : 1;
}

static int RunMoveFusionSmoke()
{
    const ulong leftWitnessHash = 0x00000000000A11CE;
    const ulong rightWitnessHash = 0x0000000000000B0B;
    var observedAt = "2026-06-11T18:30:00.0040000Z";
    var markers = new[]
    {
        new MuninnMoveMarkerCandidateDocument(
            StreamId: "nightwing:ps3-eye-0:move-marker-candidates",
            HostId: "nightwing",
            CameraId: "ps3-eye-0",
            FrameSequence: 9101,
            SourceIdHash: leftWitnessHash,
            TileX: 10,
            TileY: 10,
            CenterXPx: 170.0f,
            CenterYPx: 120.0f,
            RadiusPx: 12.0f,
            AreaPx: 452,
            MeanLuma: 0.64f,
            PeakLuma: 252,
            Score: 0.90f,
            ObservedAt: observedAt),
        new MuninnMoveMarkerCandidateDocument(
            StreamId: "starfire:ps3-eye-1:move-marker-candidates",
            HostId: "starfire",
            CameraId: "ps3-eye-1",
            FrameSequence: 9102,
            SourceIdHash: rightWitnessHash,
            TileX: 11,
            TileY: 10,
            CenterXPx: 150.0f,
            CenterYPx: 120.0f,
            RadiusPx: 12.0f,
            AreaPx: 449,
            MeanLuma: 0.62f,
            PeakLuma: 250,
            Score: 0.86f,
            ObservedAt: observedAt)
    };
    var controllers = new[]
    {
        new MuninnMoveControllerStateDocument(
            StreamId: "nightwing:move-usb:move-controller-state",
            HostId: "nightwing",
            MoveId: "move-usb",
            Sequence: 79,
            SourceTimestampNs: 1_781_202_600_004_000_000,
            AccelerometerXyz: [-0.01f, 0.10f, 0.99f],
            GyroscopeXyz: [0.02f, -0.01f, 0.04f],
            MagnetometerXyz: [0.0f, 0.0f, 0.0f],
            TriggerValue: 0.5f,
            Buttons: ["move", "trigger"],
            Battery01: 0.72f,
            ObservedAt: observedAt)
    };
    var calibration = new MimirMoveFusionRigCalibration(
        CalibrationId: "mimir-move-stage-calibration-smoke",
        TrackingSpaceId: "mimir-stage-space",
        Cameras:
        [
            new MimirMoveFusionCameraCalibration(
                CameraId: "nightwing:ps3-eye-0",
                WitnessIdHash: leftWitnessHash,
                PositionMeters: new MimirVector3Snapshot(-0.1, 0.0, 0.0),
                Orientation: new MimirQuaternionSnapshot(0.0, 0.0, 0.0, 1.0),
                FocalLengthXPx: 100.0,
                FocalLengthYPx: 100.0,
                PrincipalPointXPx: 160.0,
                PrincipalPointYPx: 120.0),
            new MimirMoveFusionCameraCalibration(
                CameraId: "starfire:ps3-eye-1",
                WitnessIdHash: rightWitnessHash,
                PositionMeters: new MimirVector3Snapshot(0.1, 0.0, 0.0),
                Orientation: new MimirQuaternionSnapshot(0.0, 0.0, 0.0, 1.0),
                FocalLengthXPx: 100.0,
                FocalLengthYPx: 100.0,
                PrincipalPointXPx: 160.0,
                PrincipalPointYPx: 120.0)
        ],
        MaximumAssociationSkewMilliseconds: 20.0);

    var samples = MimirMuninnMoveEvidenceAdapter.BuildNativeSamples(markers, controllers);
    var fused = MimirMoveFusion.Fuse(samples, calibration);
    var uncalibrated = MimirMoveFusion.Fuse(samples, calibration with { Cameras = [] });
    var pose = fused.Poses.SingleOrDefault();
    var streamId = "mimir:starfire:move-controller-poses";
    var catalog = CultMesh.CreateStreamCatalog();
    catalog.Declare(MimirMovePoseStream.CreateStreamDescriptor(
        streamId,
        "mimir-live",
        "mimir:starfire",
        "mimir:starfire:clock",
        calibration.TrackingSpaceId,
        calibration.CalibrationId));
    using var ring = catalog.CreateSharedMemoryRing(streamId, slotCount: 4, slotByteLength: 8192);
    var poseFrame = MimirMovePoseStream.CreateFrame(
        frameId: "mimir-move-pose-frame:79",
        producerPeerId: "mimir:starfire",
        publishedAtNs: pose?.EstimatedAtNs ?? 0,
        trackingSpaceId: calibration.TrackingSpaceId,
        calibrationId: calibration.CalibrationId,
        poses: fused.Poses);
    var posePayload = MimirMovePoseStream.SerializeFrame(poseFrame);
    var streamOk = ring.TryPublishCopy(posePayload, poseFrame.PublishedAtNs, durationNs: 0, out var published);
    MimirMoveControllerPoseStreamFrame? decodedPoseFrame = null;
    if (streamOk)
    {
        catalog.PublishFrame(published);
        streamOk = ring.TryAcquireLatestRead(out var lease);
        if (streamOk)
        {
            using (lease)
            {
                decodedPoseFrame = MimirMovePoseStream.DeserializeFrame(lease.Memory[..lease.Handle.ByteLength]);
            }
        }
    }

    var positionOk = pose is not null &&
        Math.Abs(pose.PositionMeters.X) <= 0.025 &&
        Math.Abs(pose.PositionMeters.Y) <= 0.025 &&
        Math.Abs(pose.PositionMeters.Z - 1.0) <= 0.05;
    var evidenceOk = pose is not null &&
        pose.EvidenceKinds.Contains("optical-marker:triangulated", StringComparer.Ordinal) &&
        pose.EvidenceKinds.Contains("orientation:imu-unresolved", StringComparer.Ordinal) &&
        pose.Buttons.Any(button => button.Id == "move" && button.Pressed) &&
        pose.Buttons.Any(button => button.Id == "trigger" && button.Value >= 0.49);
    streamOk = streamOk &&
        posePayload.Length > 0 &&
        catalog.LatestFrame(streamId)?.Sequence == published.Sequence &&
        decodedPoseFrame?.Poses.Length == 1 &&
        decodedPoseFrame.Poses[0].PoseId == pose?.PoseId &&
        decodedPoseFrame.TrackingSpaceId == calibration.TrackingSpaceId &&
        decodedPoseFrame.CalibrationId == calibration.CalibrationId;

    Console.WriteLine(
        $"move-fusion-smoke poses={fused.Poses.Count} controllers={fused.ControllerEvidenceCount} optical={fused.OpticalEvidenceCount} calibrated={fused.CalibratedOpticalEvidenceCount} uncalibratedPoses={uncalibrated.Poses.Count} streamPoses={decodedPoseFrame?.Poses.Length ?? 0} pos=({pose?.PositionMeters.X:F3},{pose?.PositionMeters.Y:F3},{pose?.PositionMeters.Z:F3}) confidence={pose?.Confidence:F3} evidence={string.Join(",", pose?.EvidenceKinds ?? [])}");

    return fused.Poses.Count == 1 &&
        fused.ControllerEvidenceCount == 1 &&
        fused.OpticalEvidenceCount == 2 &&
        fused.CalibratedOpticalEvidenceCount == 2 &&
        uncalibrated.Poses.Count == 0 &&
        positionOk &&
        evidenceOk &&
        streamOk
            ? 0
            : 1;
}

static async Task<int> RunMoveCalibrationProtocolSmokeAsync(string outputPath)
{
    var protocol = MimirMoveCalibrationProtocol.CreateStarfireNightwingProtocol(
        new DateTimeOffset(2026, 6, 12, 14, 45, 0, TimeSpan.Zero));
    var errors = MimirMoveCalibrationProtocol.Validate(protocol);
    if (errors.Length > 0)
    {
        Console.Error.WriteLine($"move-calibration-protocol-smoke invalid errors={string.Join(",", errors)}");
        return 1;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    using var cache = await CultCacheMessagePack.OpenAsync(outputPath, new CultCacheOpenOptions
    {
        PullOnOpen = File.Exists(outputPath)
    }).ConfigureAwait(false);
    await cache.UpsertAsync(
        protocol,
        new CultRecordHandle<MimirMoveCalibrationProtocolDocument>(new CultRecordKey($"mimir-move-calibration-protocol:{protocol.ProtocolId}")))
        .ConfigureAwait(false);
    await cache.FlushAsync().ConfigureAwait(false);

    var requiredStreams = protocol.StreamRequirements.Count(stream => stream.Required);
    var optionalStreams = protocol.StreamRequirements.Length - requiredStreams;
    var totalDuration = protocol.Phases.Sum(phase => phase.DurationSeconds);
    Console.WriteLine(
        $"move-calibration-protocol-smoke path={outputPath} protocol={protocol.ProtocolId} requiredStreams={requiredStreams} optionalStreams={optionalStreams} phases={protocol.Phases.Length} durationSeconds={totalDuration:0.0} outputs={protocol.Outputs.Length} questOptional={protocol.StreamRequirements.Any(stream => stream.StreamId.StartsWith("quest:", StringComparison.Ordinal))}");

    return requiredStreams >= 3 &&
        protocol.Phases.Length == 7 &&
        protocol.Outputs.Length == 4 &&
        protocol.Acceptance.RequiredDerivedOutputs.Length == 4
            ? 0
            : 1;
}

static MoveTrackingSmokeContext BuildMoveTrackingSmokeHub()
{
    var starfireDescriptor = new MimirStreamDescriptor(
        "starfire:move:primary",
        MimirStreamKind.Tracking,
        MimirStreamOrigin.LocalDevice,
        DisplayName: "Starfire PS Move primary");
    var nightwingDescriptor = new MimirStreamDescriptor(
        "nightwing:move:primary",
        MimirStreamKind.Tracking,
        MimirStreamOrigin.Network,
        DisplayName: "Nightwing PS Move primary");
    var settings = new MimirSynchronizationSettings
    {
        Streams = [starfireDescriptor, nightwingDescriptor],
    };
    var hub = new MimirSynchronizationHub(settings);
    var starfire = new MimirNativeIngestStreamSource(starfireDescriptor);
    var nightwing = new MimirNativeIngestStreamSource(nightwingDescriptor);
    hub.AddSource(starfire);
    hub.AddSource(nightwing);
    starfire.PushTrackingObservation(MimirTrackingObservation.PsMove(
        starfireDescriptor.SourceId,
        "psmove:starfire:00",
        sequence: 1,
        sourceTimestampNs: 1_000_000_000L,
        arrivalTimestampNs: 1_000_000_120L,
        positionMeters: new MimirVector3Snapshot(0.21, 1.12, 0.84),
        orientation: new MimirQuaternionSnapshot(0.0, 0.0, 0.0, 1.0),
        linearVelocityMetersPerSecond: new MimirVector3Snapshot(0.04, 0.0, -0.02),
        angularVelocityRadiansPerSecond: new MimirVector3Snapshot(0.0, 0.1, 0.0),
        confidence: 0.93,
        calibrationId: "starfire-move-usb-calibration-v1",
        trackingSpaceId: "starfire-move-space",
        producerId: "muninn:starfire:move",
        hostId: "starfire",
        latencyMilliseconds: 1.2,
        battery01: 0.8,
        buttons:
        [
            new MimirTrackingButtonSnapshot("move", Pressed: false, Value: 0.0),
            new MimirTrackingButtonSnapshot("trigger", Pressed: true, Value: 0.72)
        ]));
    nightwing.PushTrackingObservation(MimirTrackingObservation.PsMove(
        nightwingDescriptor.SourceId,
        "psmove:nightwing:00",
        sequence: 7,
        sourceTimestampNs: 1_000_008_333L,
        arrivalTimestampNs: 1_000_012_900L,
        positionMeters: new MimirVector3Snapshot(-0.34, 1.05, 1.42),
        orientation: new MimirQuaternionSnapshot(0.0, 0.18, 0.0, 0.984),
        linearVelocityMetersPerSecond: new MimirVector3Snapshot(-0.02, 0.01, 0.03),
        angularVelocityRadiansPerSecond: new MimirVector3Snapshot(0.2, 0.0, 0.0),
        confidence: 0.89,
        calibrationId: "nightwing-move-usb-calibration-v1",
        trackingSpaceId: "nightwing-move-space",
        producerId: "muninn:nightwing:move",
        hostId: "nightwing",
        latencyMilliseconds: 4.6,
        battery01: 0.67,
        buttons:
        [
            new MimirTrackingButtonSnapshot("move", Pressed: true, Value: 1.0),
            new MimirTrackingButtonSnapshot("trigger", Pressed: false, Value: 0.0)
        ]));

    var consumed = hub.PollSources();
    return new MoveTrackingSmokeContext(hub, consumed);
}

static MimirAudioSynchronizationReport? EstimateBioacousticDelay(float[] reference, float[] candidate, int sampleRate)
{
    const string referenceSourceId = "actuator-reference";
    const string candidateSourceId = "actuator-candidate";
    var referenceBuffer = new MimirRollingStreamBuffer(
        new MimirStreamDescriptor(referenceSourceId, MimirStreamKind.Audio, MimirStreamOrigin.LocalDevice),
        TimeSpan.FromSeconds(5));
    var candidateBuffer = new MimirRollingStreamBuffer(
        new MimirStreamDescriptor(candidateSourceId, MimirStreamKind.Audio, MimirStreamOrigin.LocalDevice),
        TimeSpan.FromSeconds(5));
    AppendFloatBlock(referenceBuffer, referenceSourceId, reference, sampleRate);
    AppendFloatBlock(candidateBuffer, candidateSourceId, candidate, sampleRate);

    var analyzer = new MimirAudioSynchronizationAnalyzer();
    return analyzer
        .Analyze([referenceBuffer, candidateBuffer], referenceSourceId, MimirAudioSyncMode.ChirpOnly)
        .SingleOrDefault();
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

static async Task<int> RunPerfectMachineContractSmokeAsync(string outputPath)
{
    var timeline = MimirBioacousticTimeline.Default;
    var createdAt = DateTimeOffset.UtcNow;
    var codebook = MimirCultMeshContractFactory.CreateCodebookState(
        "mimir-bioacoustic-default",
        "mimir-bioacoustic-segmented-birdcall-v1",
        timeline,
        createdAt);
    var decoder = MimirCultMeshContractFactory.CreateDecoderState(
        "mimir-decoder-baseline-mfcc-index",
        codebook.CodebookId,
        MimirBioacousticDecoderConfiguration.BaselineMfccIndex,
        createdAt);
    var pathState = MimirCultMeshContractFactory.CreatePathState(
        new MimirAudioSynchronizationState(
            "loopback-scarlett-speakers",
            "scarlett-host-mic",
            192_000,
            61.25,
            61.25,
            0.319,
            0.875,
            0.94,
            [new MimirChirpletBandResponse(5_600.0, 0.92), new MimirChirpletBandResponse(8_400.0, 0.86), new MimirChirpletBandResponse(12_600.0, 0.71)],
            1_000_000_000L,
            10,
            10),
        "loopback-scarlett-speakers",
        "bioacoustic-contract-smoke");
    var controller = new MimirSroPllActuatorController();
    var command = controller.Update(
        "scarlett-host-mic",
        0,
        new MimirAudioSynchronizationState(
            "loopback-scarlett-speakers",
            "scarlett-host-mic",
            192_000,
            61.25,
            61.25,
            0.319,
            0.875,
            0.94,
            [],
            1_000_000_000L,
            10,
            10),
        dtSeconds: 0.001);
    var actuator = MimirCultMeshContractFactory.CreateActuatorState(
        "scarlett-host-mic-alignment",
        MimirAlignmentActuatorProfile.SixSourceFaust.Id,
        command,
        createdAt);
    var scene = MimirCultMeshContractFactory.CreateObsSceneMirror(
        "mimir-current-program",
        createdAt);
    var output = MimirCultMeshContractFactory.CreateProgramOutput(
        "mimir-yggdrasil-site-program",
        scene.SceneId,
        MimirProgramPublicationConfigurations.YggdrasilSiteProgram,
        createdAt);
    var eveSurface = MimirCultMeshContractFactory.CreateOperatorSurface(
        "mimir-eve-gui-compositor",
        scene.SceneId,
        MimirProgramPublicationConfigurations.OperatorSurfaces[0],
        createdAt);
    var starfireMove = MimirTrackingObservation.PsMove(
        "starfire:move:primary",
        "psmove:starfire:00",
        sequence: 1,
        sourceTimestampNs: 1_000_000_000L,
        arrivalTimestampNs: 1_000_000_120L,
        positionMeters: new MimirVector3Snapshot(0.21, 1.12, 0.84),
        orientation: new MimirQuaternionSnapshot(0.0, 0.0, 0.0, 1.0),
        linearVelocityMetersPerSecond: new MimirVector3Snapshot(0.04, 0.0, -0.02),
        angularVelocityRadiansPerSecond: new MimirVector3Snapshot(0.0, 0.1, 0.0),
        confidence: 0.93,
        calibrationId: "starfire-move-usb-calibration-v1",
        trackingSpaceId: "starfire-move-space",
        producerId: "muninn:starfire:move",
        hostId: "starfire",
        latencyMilliseconds: 1.2,
        battery01: 0.8);
    var nightwingMove = MimirTrackingObservation.PsMove(
        "nightwing:move:primary",
        "psmove:nightwing:00",
        sequence: 7,
        sourceTimestampNs: 1_000_008_333L,
        arrivalTimestampNs: 1_000_012_900L,
        positionMeters: new MimirVector3Snapshot(-0.34, 1.05, 1.42),
        orientation: new MimirQuaternionSnapshot(0.0, 0.18, 0.0, 0.984),
        linearVelocityMetersPerSecond: new MimirVector3Snapshot(-0.02, 0.01, 0.03),
        angularVelocityRadiansPerSecond: new MimirVector3Snapshot(0.2, 0.0, 0.0),
        confidence: 0.89,
        calibrationId: "nightwing-move-usb-calibration-v1",
        trackingSpaceId: "nightwing-move-space",
        producerId: "muninn:nightwing:move",
        hostId: "nightwing",
        latencyMilliseconds: 4.6,
        battery01: 0.67);
    var fusedMovePose = MimirMoveControllerPoseDocument.FromObservation(
        nightwingMove,
        "move:right",
        [
            "muninn:nightwing:ps3-eye-0:move-marker-candidates",
            "muninn:nightwing:ps3-eye-1:move-marker-candidates",
            "muninn:nightwing:move-usb:controller-state",
            "muninn:starfire:move-usb:controller-state"
        ],
        ["optical-marker-candidate", "optical-marker-candidate", "imu-controller-state", "imu-controller-state"],
        "mimir-move-stage-calibration-v1");

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    using var cache = await CultCacheMessagePack.OpenAsync(outputPath, new CultCacheOpenOptions
    {
        PullOnOpen = File.Exists(outputPath)
    }).ConfigureAwait(false);
    await cache.UpsertAsync(
        codebook,
        new CultRecordHandle<MimirBioacousticCodebookState>(new CultRecordKey($"mimir-codebook:{codebook.CodebookId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        decoder,
        new CultRecordHandle<MimirBioacousticDecoderState>(new CultRecordKey($"mimir-decoder:{decoder.DecoderId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        pathState,
        new CultRecordHandle<MimirAcousticPathState>(new CultRecordKey($"mimir-path:{pathState.PathId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        actuator,
        new CultRecordHandle<MimirActuatorStateDocument>(new CultRecordKey($"mimir-actuator:{actuator.ActuatorId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        scene,
        new CultRecordHandle<MimirProgramSceneDocument>(new CultRecordKey($"mimir-program-scene:{scene.SceneId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        output,
        new CultRecordHandle<MimirProgramOutputDocument>(new CultRecordKey($"mimir-program-output:{output.OutputId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        eveSurface,
        new CultRecordHandle<MimirEveOperatorSurfaceDocument>(new CultRecordKey($"mimir-eve-surface:{eveSurface.SurfaceId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        starfireMove,
        new CultRecordHandle<MimirTrackingObservation>(new CultRecordKey($"mimir-move-tracking:{starfireMove.ObservationId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        nightwingMove,
        new CultRecordHandle<MimirTrackingObservation>(new CultRecordKey($"mimir-move-tracking:{nightwingMove.ObservationId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        fusedMovePose,
        new CultRecordHandle<MimirMoveControllerPoseDocument>(new CultRecordKey($"mimir-move-controller-pose:{fusedMovePose.PoseId}")))
        .ConfigureAwait(false);
    await cache.FlushAsync().ConfigureAwait(false);

    Console.WriteLine(
        $"perfect-machine-contract-smoke path={outputPath} codebookMotifs={codebook.Motifs.Length} decoder={decoder.Configuration.Id} pathBands={pathState.BandResponses.Length} actuatorControls={actuator.FaustControls.Length} sceneLayers={scene.Layers.Length} programOutput={output.OutputId} eveCommands={eveSurface.CommandTopics.Length} moveTracking=2 fusedMovePose={fusedMovePose.PoseId} authority={fusedMovePose.FusionAuthorityId} consumer={fusedMovePose.ConsumerContract} hosts={starfireMove.HostId},{nightwingMove.HostId}");
    return 0;
}

static async Task<int> ImportObsProgramSceneAsync(string inputPath, string sceneName, string outputPath)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"OBS scene file not found: {inputPath}");
        return 1;
    }

    var importedAt = DateTimeOffset.UtcNow;
    var scene = MimirObsSceneImporter.ImportFile(
        inputPath,
        string.IsNullOrWhiteSpace(sceneName) ? null : sceneName,
        importedAt);
    var output = MimirCultMeshContractFactory.CreateProgramOutput(
        "mimir-imported-program",
        scene.SceneId,
        MimirProgramPublicationConfigurations.YggdrasilSiteProgram,
        importedAt);
    var eveSurface = MimirCultMeshContractFactory.CreateOperatorSurface(
        "mimir-eve-gui-compositor",
        scene.SceneId,
        MimirProgramPublicationConfigurations.OperatorSurfaces[0],
        importedAt);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    using var cache = await CultCacheMessagePack.OpenAsync(outputPath, new CultCacheOpenOptions
    {
        PullOnOpen = File.Exists(outputPath)
    }).ConfigureAwait(false);
    await cache.UpsertAsync(
        scene,
        new CultRecordHandle<MimirProgramSceneDocument>(new CultRecordKey($"mimir-program-scene:{scene.SceneId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        output,
        new CultRecordHandle<MimirProgramOutputDocument>(new CultRecordKey($"mimir-program-output:{output.OutputId}")))
        .ConfigureAwait(false);
    await cache.UpsertAsync(
        eveSurface,
        new CultRecordHandle<MimirEveOperatorSurfaceDocument>(new CultRecordKey($"mimir-eve-surface:{eveSurface.SurfaceId}")))
        .ConfigureAwait(false);
    await cache.FlushAsync().ConfigureAwait(false);

    var visible = scene.Layers.Count(layer => layer.Visible);
    var keyed = scene.Layers.Count(layer => layer.ChromaKey is not null);
    var cropped = scene.Layers.Count(layer =>
        layer.Crop.Left != 0.0 || layer.Crop.Top != 0.0 || layer.Crop.Right != 0.0 || layer.Crop.Bottom != 0.0);
    Console.WriteLine(
        $"obs-program-scene-import input={inputPath} output={outputPath} scene={scene.SceneId} canvas={scene.CanvasWidth}x{scene.CanvasHeight} layers={scene.Layers.Length} visible={visible} cropped={cropped} keyed={keyed} route={output.PublisherRoute}");
    foreach (var layer in scene.Layers.OrderBy(layer => layer.ZIndex))
    {
        Console.WriteLine(
            $"layer {layer.ZIndex}:{layer.LayerId} visible={layer.Visible} source={layer.SourceRef} kind={layer.SourceKind} pos={layer.X:0.###},{layer.Y:0.###} size={layer.Width:0.###}x{layer.Height:0.###} crop={layer.Crop.Left:0.###},{layer.Crop.Top:0.###},{layer.Crop.Right:0.###},{layer.Crop.Bottom:0.###} key={(layer.ChromaKey is null ? "none" : layer.ChromaKey.KeyColorRgba.ToString("x8"))}");
    }

    return 0;
}

static string DefaultObsScenePath() =>
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio",
        "basic",
        "scenes",
        "Untitled.json");

static string DefaultNativeReservoirPath()
{
    var fromBase = Path.Combine(AppContext.BaseDirectory, "localcast_reservoir.dll");
    if (File.Exists(fromBase))
    {
        return fromBase;
    }

    return Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "native",
        "reservoir",
        "target",
        "debug",
        "localcast_reservoir.dll"));
}

static async Task<int> WritePerfectMachineManifestAsync(string outputPath)
{
    var manifest = MimirPerfectMachineManifestFactory.Create();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    await File.WriteAllTextAsync(
        outputPath,
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }))
        .ConfigureAwait(false);
    Console.WriteLine(
        $"perfect-machine-manifest path={outputPath} schema={manifest.Schema} modules={manifest.Modules.Count} profiles={manifest.NodeProfiles.Count} captures={manifest.CaptureProfiles.Count} publications={manifest.Publications.Count}");
    return manifest.Modules.Count >= 13 &&
        manifest.CaptureProfiles.Count >= 8 &&
        manifest.Publications.Count >= 3
            ? 0
            : 1;
}

static int RunPerfectMachineLoweringBenchmark(int iterations)
{
    iterations = Math.Max(1, iterations);
    var buffers = new List<MimirRollingStreamBuffer>();
    foreach (var profile in MimirNativeCaptureConfigurations.LocalSixCameraProfiles)
    {
        var buffer = new MimirRollingStreamBuffer(
            new MimirStreamDescriptor(profile.Id, MimirStreamKind.Video, MimirStreamOrigin.LocalDevice),
            TimeSpan.FromSeconds(profile.RollingBufferSeconds));
        buffer.Append(new MimirStreamSample(
            profile.Id,
            MimirStreamKind.Video,
            MimirStreamOrigin.LocalDevice,
            TimestampNs: 1_000_000_000L,
            ArrivalNs: 1_000_000_050L,
            Sequence: 1,
            PayloadHandle: 0,
            VideoFrame: new MimirVideoFrameDescriptor(
                Math.Max(1, profile.PreferredWidth),
                Math.Max(1, profile.PreferredHeight),
                profile.Id.Contains("leap", StringComparison.OrdinalIgnoreCase) ? MimirVideoPixelFormat.LeapStereoIr : MimirVideoPixelFormat.Bgra8,
                Math.Max(1, profile.PreferredWidth) * 4,
                1_000_000_000L,
                NativeHandle: (ulong)(100 + buffers.Count),
                NativeHandleKind: "benchmark-shared-texture")));
        buffers.Add(buffer);
    }

    var states = new[]
    {
        new MimirAudioSynchronizationState(
            "loopback-scarlett-speakers",
            "scarlett-input-1",
            192_000,
            41.50,
            41.50,
            0.216,
            0.30,
            0.91,
            [],
            1_000_000_000L,
            1,
            1),
        new MimirAudioSynchronizationState(
            "loopback-scarlett-speakers",
            "raven-scarlett-input-1",
            192_000,
            225.00,
            225.00,
            1.172,
            -0.45,
            0.78,
            [],
            1_000_000_000L,
            1,
            1)
    };
    var lowerer = new MimirFensalirFieldLowering();
    _ = lowerer.BuildGpuSensorFrame(buffers);
    _ = lowerer.BuildAcousticFieldFrame(states);

    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var stamp = Stopwatch.GetTimestamp();
    var cameras = 0;
    var constraints = 0;
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        var gpuFrame = lowerer.BuildGpuSensorFrame(buffers);
        var acousticFrame = lowerer.BuildAcousticFieldFrame(states);
        cameras += gpuFrame.Cameras.Count;
        constraints += acousticFrame.Constraints.Count;
    }

    var elapsed = Stopwatch.GetElapsedTime(stamp);
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    var perIterationUs = elapsed.TotalMilliseconds * 1000.0 / iterations;
    var allocatedPerIteration = allocatedBytes / (double)iterations;
    Console.WriteLine(
        $"perfect-machine-lowering-benchmark iterations={iterations} camerasPer={cameras / (double)iterations:0.0} constraintsPer={constraints / (double)iterations:0.0} elapsedMs={elapsed.TotalMilliseconds:0.000} perIterationUs={perIterationUs:0.000} allocatedPerIterationBytes={allocatedPerIteration:0.0}");
    return perIterationUs < 250.0 && allocatedPerIteration < 10_000.0
        ? 0
        : 1;
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

static int RenderContestantFloat32(string outputPath, int sampleRate, double seconds, string songId)
{
    var profile = MimirBioacousticContestants.BuiltIn.FirstOrDefault(profile =>
        string.Equals(profile.Id, songId, StringComparison.OrdinalIgnoreCase));
    if (profile == null)
    {
        Console.Error.WriteLine($"contestant render failed: unknown song '{songId}'");
        return 1;
    }

    var renderer = new MimirBioacousticContestantRenderer(profile);
    var samples = renderer.RenderSequenceMonoFloat(seconds, sampleRate);
    var outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrWhiteSpace(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }

    var bytes = new byte[samples.Length * sizeof(float)];
    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
    File.WriteAllBytes(outputPath, bytes);
    Console.WriteLine($"contestant-render-f32 path={outputPath} song={profile.Id} sampleRate={sampleRate} seconds={seconds:0.000} samples={samples.Length} expectedEvents={renderer.ExpectedEventCount(seconds)}");
    return 0;
}

static int AnalyzeContestantAsioFloat32(
    string inputPath,
    int sampleRate,
    int channels,
    int candidateChannel,
    double seconds,
    double scheduleOffsetSamples,
    string songId)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"contestant-asio analysis failed: input not found: {inputPath}");
        return 1;
    }

    var profile = MimirBioacousticContestants.BuiltIn.FirstOrDefault(profile =>
        string.Equals(profile.Id, songId, StringComparison.OrdinalIgnoreCase));
    if (profile == null)
    {
        Console.Error.WriteLine($"contestant-asio analysis failed: unknown song '{songId}'");
        return 1;
    }

    var channelSamples = ReadInterleavedFloat32(inputPath, channels, out var frameCount);
    if (frameCount == 0)
    {
        Console.Error.WriteLine("contestant-asio analysis failed: capture file contains no complete frames");
        return 1;
    }

    var renderer = new MimirBioacousticContestantRenderer(profile);
    var expectedEvents = renderer.ExpectedEvents(seconds > 0.0 ? seconds : frameCount / (double)sampleRate);
    var options = CepstralDecoderOptions.FromRuntime(MimirBioacousticDecoderConfiguration.PacketRazorIndex) with
    {
        ProposalMode = CepstralProposalMode.StreamingPacketRazor,
        ProposalBudgetMultiplier = 1.0,
        DenseStepSeconds = 0.0
    };

    var firstChannel = candidateChannel < 0 ? 0 : candidateChannel;
    var lastChannel = candidateChannel < 0 ? channels - 1 : candidateChannel;
    var failures = 0;
    for (var channel = firstChannel; channel <= lastChannel; channel++)
    {
        if (channel < 0 || channel >= channels)
        {
            Console.Error.WriteLine($"contestant-asio channel {channel} outside capture channel count {channels}");
            failures++;
            continue;
        }

        var observations = DecodeStreamingPacketRazorWords(
            channelSamples[channel],
            sampleRate,
            expectedEvents.Count,
            renderer,
            renderer.RenderEventMonoFloat(0, sampleRate).Length,
            scheduleOffsetSamples);
        var expectedObservations = observations
            .Where(observation => expectedEvents.Contains(observation.EventIndex))
            .ToArray();
        var payloads = ClassifyContestantPayloads(
            channelSamples[channel],
            sampleRate,
            options,
            renderer,
            expectedObservations,
            trustObservationPayload: true);
        var payloadCorrect = expectedObservations.Count(observation =>
            payloads.TryGetValue(observation.EventIndex, out var payload) &&
            payload == renderer.PayloadSymbolForEvent(observation.EventIndex));
        var clock = FitContestantClockHypothesis(expectedObservations, renderer, sampleRate, expectedEvents.Count);
        var payloadAccuracy = expectedEvents.Count == 0
            ? 0.0
            : payloadCorrect / (double)expectedEvents.Count;
        var timingAccuracy = clock == null
            ? 0.0
            : 1.0 / (1.0 + clock.MeanAbsoluteErrorSamples / Math.Max(1.0, sampleRate * 0.00025));
        var confidence = expectedObservations.Length == 0
            ? 0.0
            : expectedObservations.Average(observation => observation.Confidence);
        Console.WriteLine(
            $"contestant-asio-f32 channel={channel} song={profile.Id} decoder=packet-razor-streaming-faust events={expectedObservations.Length}/{expectedEvents.Count} payload={payloadAccuracy:0.000} timing={timingAccuracy:0.000} confidence={confidence:0.000} " +
            $"delaySamples={(clock?.SourceOffsetSamples ?? 0.0):0.000000} delayUs={(clock?.SourceOffsetSamples ?? 0.0) * 1_000_000.0 / sampleRate:0.000} maeSamples={(clock?.MeanAbsoluteErrorSamples ?? 0.0):0.000000}");
    }

    return failures == 0 ? 0 : 1;
}

static int InspectAsioFloat32(string inputPath, int sampleRate, int channels)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"asio-f32 inspect failed: input not found: {inputPath}");
        return 1;
    }

    if (channels <= 0 || sampleRate <= 0)
    {
        Console.Error.WriteLine("asio-f32 inspect failed: sample rate and channel count must be positive");
        return 1;
    }

    var channelSamples = ReadInterleavedFloat32(inputPath, channels, out var frameCount);
    var analyzer = new MimirAudioSpectrumAnalyzer(8192, 48);
    Console.WriteLine($"asio-f32-inspect input={inputPath} sampleRate={sampleRate} channels={channels} frames={frameCount} seconds={frameCount / (double)sampleRate:0.000}");
    for (var channel = 0; channel < channels; channel++)
    {
        var samples = channelSamples[channel];
        var full = SignalStats(samples);
        var speech = BandLimitedStats(samples, sampleRate, 80.0, 8000.0);
        var ultrasonic = BandLimitedStats(samples, sampleRate, 16000.0, 48000.0);
        var spectrum = analyzer.AnalyzeSamples($"asio-ch{channel}", samples, sampleRate);
        Console.WriteLine(
            $"asio-f32-channel ch={channel} rms={full.Rms:0.000000} peak={full.Peak:0.000000} dc={full.Mean:0.000000} " +
            $"speechRms={speech.Rms:0.000000} speechPeak={speech.Peak:0.000000} ultrasonicRms={ultrasonic.Rms:0.000000} " +
            $"peaks={DescribeSpectrumPeaks(spectrum)}");
    }

    return 0;
}

static int AnalyzeComplexContourAsioFloat32(
    string inputPath,
    int sampleRate,
    int channels,
    int referenceChannel,
    int candidateChannel,
    double seconds,
    double scheduleOffsetSamples,
    double predictedDelaySamples,
    string channelModelPath,
    string songId)
{
    var pathModel = LoadComplexContourPathModel(channelModelPath, sampleRate, referenceChannel, candidateChannel);
    var result = EstimateComplexContourAsioFloat32(
        inputPath,
        sampleRate,
        channels,
        referenceChannel,
        candidateChannel,
        seconds,
        scheduleOffsetSamples,
        predictedDelaySamples,
        pathModel?.ToRuntimeModel(),
        songId);
    if (result == null)
    {
        return 1;
    }

    var bands = string.Join(",", result.StrongestBands.Select(group =>
        $"{group.CenterHz:0}Hz:{group.DelayResidualSamples:+0.00;-0.00;0.00}samp/{group.PhaseResidualRadians:+0.00;-0.00;0.00}rad"));
    Console.WriteLine(
        $"complex-contour-asio-f32 input={result.InputPath} reference=asio-ch{result.ReferenceChannel} candidate=asio-ch{result.CandidateChannel} " +
        $"delaySamples={result.DelaySamples:0.000000} delayUs={result.DelayMicroseconds:0.000} " +
        $"predictedDelaySamples={result.PredictedDelaySamples:0.000000} predictionErrorSamples={result.PredictionErrorSamples:0.000000} predictionErrorUs={result.PredictionErrorMicroseconds:0.000} " +
        $"confidence={result.Confidence:0.000} directHits={result.DirectHits} maeSamples={result.MeanAbsoluteErrorSamples:0.000000} phaseMaeRad={result.MeanAbsolutePhaseErrorRadians:0.000} " +
        $"referenceHits={result.ReferenceHits} candidateHits={result.CandidateHits} reflectionTaps={result.ReflectionTaps.Length} " +
        $"firstReflectionSamples={result.FirstReflectionSamples:0.000} channelModel={(pathModel?.PathId ?? "none")} bands={bands}");
    return 0;
}

static ComplexContourReplayResult? EstimateComplexContourAsioFloat32(
    string inputPath,
    int sampleRate,
    int channels,
    int referenceChannel,
    int candidateChannel,
    double seconds,
    double scheduleOffsetSamples,
    double predictedDelaySamples,
    MimirDirectPathChannelModel? channelModel,
    string songId)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"complex-contour ASIO analysis failed: input not found: {inputPath}");
        return null;
    }

    var profile = MimirBioacousticContestants.BuiltIn.FirstOrDefault(profile =>
        string.Equals(profile.Id, songId, StringComparison.OrdinalIgnoreCase));
    if (profile == null)
    {
        Console.Error.WriteLine($"complex-contour ASIO analysis failed: unknown song '{songId}'");
        return null;
    }

    if (referenceChannel < 0 || referenceChannel >= channels ||
        candidateChannel < 0 || candidateChannel >= channels)
    {
        Console.Error.WriteLine("complex-contour ASIO analysis failed: channel index outside capture channel count");
        return null;
    }

    var channelSamples = ReadInterleavedFloat32(inputPath, channels, out var frameCount);
    var renderer = new MimirBioacousticContestantRenderer(profile);
    var captureSeconds = seconds > 0.0 ? Math.Min(seconds, frameCount / (double)sampleRate) : frameCount / (double)sampleRate;
    var eventIndices = renderer.ExpectedEvents(captureSeconds)
        .Order()
        .ToArray();
    var bank = new MimirComplexContourMatchedFilterBank(renderer, sampleRate);
    var referenceHits = bank.AnalyzeEvents(
        channelSamples[referenceChannel],
        eventIndices,
        scheduleOffsetSamples,
        Math.Max(2, sampleRate / 4_000));
    var candidateHits = bank.AnalyzeEvents(
        channelSamples[candidateChannel],
        eventIndices,
        scheduleOffsetSamples + predictedDelaySamples,
        Math.Max(48, sampleRate / 900));
    var estimate = new MimirDirectPathTracker(
        sampleRate,
        new MimirDirectPathTrackerOptions(ChannelModel: channelModel)).Update(referenceHits, candidateHits, predictedDelaySamples);
    if (estimate == null)
    {
        Console.WriteLine(
            $"complex-contour-asio-f32 status=no-lock input={inputPath} reference=asio-ch{referenceChannel} candidate=asio-ch{candidateChannel} " +
            $"referenceHits={referenceHits.Count} candidateHits={candidateHits.Count} predictedDelaySamples={predictedDelaySamples:0.000}");
        return null;
    }

    var bandResiduals = estimate.BandObservations
        .GroupBy(observation => Math.Round(observation.CenterHz / 250.0) * 250.0)
        .Select(group =>
        {
            var totalWeight = Math.Max(1.0e-9, group.Sum(observation => Math.Max(observation.Weight, 1.0e-9)));
            return new ComplexContourBandResidual(
                group.Key,
                totalWeight,
                group.Sum(observation => observation.DelayResidualSamples * Math.Max(observation.Weight, 1.0e-9)) / totalWeight,
                group.Sum(observation => observation.PhaseResidualRadians * Math.Max(observation.Weight, 1.0e-9)) / totalWeight);
        })
        .OrderByDescending(group => group.Weight)
        .ToArray();
    var strongestBands = bandResiduals.Take(8).ToArray();
    var predictionError = estimate.DelaySamples - predictedDelaySamples;
    return new ComplexContourReplayResult(
        "",
        inputPath,
        sampleRate,
        referenceChannel,
        candidateChannel,
        predictedDelaySamples,
        estimate.DelaySamples,
        estimate.DelayMicroseconds,
        predictionError,
        predictionError * 1_000_000.0 / sampleRate,
        estimate.Confidence,
        estimate.DirectHitCount,
        estimate.MeanAbsoluteErrorSamples,
        estimate.MeanAbsolutePhaseErrorRadians,
        referenceHits.Count,
        candidateHits.Count,
        estimate.ReflectionTaps.ToArray(),
        estimate.ReflectionTaps.FirstOrDefault()?.RelativeDelaySamples ?? double.NaN,
        strongestBands,
        bandResiduals);
}

static int RunComplexContourReplayPanel(string outputPath)
{
    var cases = new[]
    {
        new ComplexContourReplayCase(
            "stored-shotgun",
            "artifacts/asio/scarlett-canary-packet-anchor-rich-192k-f32.raw",
            CandidateChannel: 1,
            PredictedDelaySamples: 534.343605),
        new ComplexContourReplayCase(
            "stored-cardioid",
            "artifacts/asio/scarlett-canary-packet-anchor-rich-192k-f32.raw",
            CandidateChannel: 0,
            PredictedDelaySamples: 839.071083),
        new ComplexContourReplayCase(
            "fresh-shotgun",
            "artifacts/asio/scarlett-canary-packet-anchor-rich-latest-192k-f32.raw",
            CandidateChannel: 1,
            PredictedDelaySamples: 544.244999),
        new ComplexContourReplayCase(
            "fresh-cardioid",
            "artifacts/asio/scarlett-canary-packet-anchor-rich-latest-192k-f32.raw",
            CandidateChannel: 0,
            PredictedDelaySamples: 781.490952)
    };

    var failures = 0;
    var results = new List<ComplexContourReplayResult>(cases.Length);
    foreach (var replayCase in cases)
    {
        Console.WriteLine($"complex-contour-panel case={replayCase.Id}");
        var result = EstimateComplexContourAsioFloat32(
            replayCase.InputPath,
            sampleRate: 192_000,
            channels: 4,
            referenceChannel: 2,
            replayCase.CandidateChannel,
            seconds: 4.0,
            scheduleOffsetSamples: 1623.0,
            replayCase.PredictedDelaySamples,
            channelModel: null,
            MimirBioacousticContestants.CanaryPacketTrill.Id);
        if (result == null)
        {
            failures++;
            continue;
        }

        results.Add(result with { CaseId = replayCase.Id });
        var bands = string.Join(",", result.StrongestBands.Take(4).Select(group =>
            $"{group.CenterHz:0}Hz:{group.DelayResidualSamples:+0.00;-0.00;0.00}samp/{group.PhaseResidualRadians:+0.00;-0.00;0.00}rad"));
        Console.WriteLine(
            $"complex-contour-asio-f32 input={result.InputPath} reference=asio-ch{result.ReferenceChannel} candidate=asio-ch{result.CandidateChannel} " +
            $"delaySamples={result.DelaySamples:0.000000} delayUs={result.DelayMicroseconds:0.000} " +
            $"predictedDelaySamples={result.PredictedDelaySamples:0.000000} predictionErrorSamples={result.PredictionErrorSamples:0.000000} predictionErrorUs={result.PredictionErrorMicroseconds:0.000} " +
            $"confidence={result.Confidence:0.000} directHits={result.DirectHits} maeSamples={result.MeanAbsoluteErrorSamples:0.000000} phaseMaeRad={result.MeanAbsolutePhaseErrorRadians:0.000} " +
            $"referenceHits={result.ReferenceHits} candidateHits={result.CandidateHits} reflectionTaps={result.ReflectionTaps.Length} " +
            $"firstReflectionSamples={result.FirstReflectionSamples:0.000} bands={bands}");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
    File.WriteAllText(
        outputPath,
        JsonSerializer.Serialize(
            new ComplexContourReplayPanelReceipt(
                "mimir.bioacoustic.complex-contour-replay.v1",
                DateTimeOffset.UtcNow,
                results.ToArray()),
            new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"complex-contour-panel-receipt path={outputPath} cases={results.Count}");
    return failures == 0 ? 0 : 1;
}

static int LearnComplexContourChannelModel(string inputPath, string outputPath)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"complex-contour channel learning failed: input not found: {inputPath}");
        return 1;
    }

    var receipt = JsonSerializer.Deserialize<ComplexContourReplayPanelReceipt>(File.ReadAllText(inputPath));
    if (receipt == null || receipt.Cases.Length == 0)
    {
        Console.Error.WriteLine("complex-contour channel learning failed: receipt contains no cases");
        return 1;
    }

    var pathModels = receipt.Cases
        .GroupBy(result => (result.SampleRate, result.ReferenceChannel, result.CandidateChannel))
        .Select(group => LearnComplexContourPathModel(group.Key.SampleRate, group.Key.ReferenceChannel, group.Key.CandidateChannel, group.ToArray()))
        .Where(model => model.Corrections.Length > 0)
        .OrderBy(model => model.PathId, StringComparer.Ordinal)
        .ToArray();
    var document = new ComplexContourChannelModelDocument(
        "mimir.bioacoustic.complex-contour-channel-model.v1",
        DateTimeOffset.UtcNow,
        inputPath,
        pathModels);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
    File.WriteAllText(
        outputPath,
        JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    foreach (var path in pathModels)
    {
        Console.WriteLine(
            $"complex-contour-channel-model path={path.PathId} cases={path.CaseIds.Length} corrections={path.Corrections.Length} " +
            $"usableBands={path.UsableBandCount} reliability={path.Reliability:0.000} delaySpreadSamples={path.DelaySpreadSamples:0.000}");
    }

    Console.WriteLine($"complex-contour-channel-model-receipt path={outputPath} paths={pathModels.Length}");
    return pathModels.Length == 0 ? 1 : 0;
}

static ComplexContourPathChannelModel LearnComplexContourPathModel(
    int sampleRate,
    int referenceChannel,
    int candidateChannel,
    ComplexContourReplayResult[] cases)
{
    var corrections = cases
        .SelectMany(result => (result.BandResiduals is { Length: > 0 } ? result.BandResiduals : result.StrongestBands)
            .Select(band => (Result: result, Band: band)))
        .GroupBy(pair => pair.Band.CenterHz)
        .Select(group => LearnComplexContourBandCorrection(group.Key, group.Select(pair => pair.Band).ToArray()))
        .Where(correction => correction != null)
        .Select(correction => correction!)
        .OrderBy(correction => correction.CenterHz)
        .ToArray();
    var reflectionTaps = cases
        .SelectMany(result => result.ReflectionTaps.Select(tap => tap.RelativeDelaySamples))
        .GroupBy(delay => Math.Round(delay / 2.0) * 2.0)
        .Select(group => new ComplexContourReflectionCorrection(
            group.Key,
            group.Count(),
            group.Average()))
        .OrderByDescending(tap => tap.ObservationCount)
        .ThenBy(tap => tap.RelativeDelaySamples)
        .Take(8)
        .ToArray();
    var reliability = corrections.Length == 0
        ? 0.0
        : corrections.Average(correction => correction.Reliability);
    var delaySpread = corrections.Length <= 1
        ? 0.0
        : corrections.Max(correction => correction.DelayCorrectionSamples) - corrections.Min(correction => correction.DelayCorrectionSamples);
    return new ComplexContourPathChannelModel(
        $"asio-ch{referenceChannel}->asio-ch{candidateChannel}@{sampleRate}",
        sampleRate,
        referenceChannel,
        candidateChannel,
        cases.Select(result => result.CaseId).Order(StringComparer.Ordinal).ToArray(),
        corrections,
        reflectionTaps,
        corrections.Count(correction => correction.Usable),
        reliability,
        delaySpread);
}

static ComplexContourBandCorrection? LearnComplexContourBandCorrection(double centerHz, ComplexContourBandResidual[] bands)
{
    if (bands.Length < 2)
    {
        return null;
    }

    var totalWeight = Math.Max(1.0e-9, bands.Sum(band => Math.Max(band.Weight, 1.0e-9)));
    var delay = bands.Sum(band => band.DelayResidualSamples * Math.Max(band.Weight, 1.0e-9)) / totalWeight;
    var phaseVector = bands.Aggregate(
        Complex.Zero,
        (current, band) => current + Complex.FromPolarCoordinates(Math.Max(band.Weight, 1.0e-9), band.PhaseResidualRadians));
    var phase = Math.Atan2(phaseVector.Imaginary, phaseVector.Real);
    var variance = bands.Sum(band =>
    {
        var delta = band.DelayResidualSamples - delay;
        return delta * delta * Math.Max(band.Weight, 1.0e-9);
    }) / totalWeight;
    var stdDev = Math.Sqrt(Math.Max(0.0, variance));
    var signCoherent = bands.All(band => Math.Sign(band.DelayResidualSamples) == Math.Sign(delay) || Math.Abs(band.DelayResidualSamples) < 0.75);
    var reliability = Math.Clamp(
        (bands.Length / 2.0) *
        (1.0 / (1.0 + stdDev / 1.5)) *
        (phaseVector.Magnitude / totalWeight) *
        (signCoherent ? 1.0 : 0.55),
        0.0,
        1.0);
    var usable = reliability >= 0.35 && stdDev <= 3.0;
    if (!usable)
    {
        return null;
    }

    return new ComplexContourBandCorrection(
        centerHz,
        delay,
        phase,
        totalWeight,
        bands.Length,
        stdDev,
        reliability,
        usable);
}

static ComplexContourPathChannelModel? LoadComplexContourPathModel(
    string channelModelPath,
    int sampleRate,
    int referenceChannel,
    int candidateChannel)
{
    if (string.IsNullOrWhiteSpace(channelModelPath))
    {
        return null;
    }

    if (!File.Exists(channelModelPath))
    {
        Console.Error.WriteLine($"complex-contour channel model not found: {channelModelPath}");
        return null;
    }

    var document = JsonSerializer.Deserialize<ComplexContourChannelModelDocument>(File.ReadAllText(channelModelPath));
    var model = document?.Paths.FirstOrDefault(path =>
        path.SampleRate == sampleRate &&
        path.ReferenceChannel == referenceChannel &&
        path.CandidateChannel == candidateChannel);
    if (model == null)
    {
        Console.Error.WriteLine(
            $"complex-contour channel model has no path for asio-ch{referenceChannel}->asio-ch{candidateChannel}@{sampleRate}");
    }

    return model;
}

static int EvaluateComplexContourChannelModel(string receiptPath, string channelModelPath, string outputPath)
{
    if (!File.Exists(receiptPath))
    {
        Console.Error.WriteLine($"complex-contour model evaluation failed: receipt not found: {receiptPath}");
        return 1;
    }

    if (!File.Exists(channelModelPath))
    {
        Console.Error.WriteLine($"complex-contour model evaluation failed: channel model not found: {channelModelPath}");
        return 1;
    }

    var receipt = JsonSerializer.Deserialize<ComplexContourReplayPanelReceipt>(File.ReadAllText(receiptPath));
    var document = JsonSerializer.Deserialize<ComplexContourChannelModelDocument>(File.ReadAllText(channelModelPath));
    if (receipt == null || document == null)
    {
        Console.Error.WriteLine("complex-contour model evaluation failed: invalid JSON input");
        return 1;
    }

    var results = new List<ComplexContourChannelModelEvaluationCase>();
    foreach (var replayCase in receipt.Cases)
    {
        var pathModel = document.Paths.FirstOrDefault(path =>
            path.SampleRate == replayCase.SampleRate &&
            path.ReferenceChannel == replayCase.ReferenceChannel &&
            path.CandidateChannel == replayCase.CandidateChannel);
        var baseline = EstimateComplexContourAsioFloat32(
            replayCase.InputPath,
            replayCase.SampleRate,
            channels: 4,
            replayCase.ReferenceChannel,
            replayCase.CandidateChannel,
            seconds: 4.0,
            scheduleOffsetSamples: 1623.0,
            replayCase.PredictedDelaySamples,
            channelModel: null,
            MimirBioacousticContestants.CanaryPacketTrill.Id);
        var modeled = EstimateComplexContourAsioFloat32(
            replayCase.InputPath,
            replayCase.SampleRate,
            channels: 4,
            replayCase.ReferenceChannel,
            replayCase.CandidateChannel,
            seconds: 4.0,
            scheduleOffsetSamples: 1623.0,
            replayCase.PredictedDelaySamples,
            pathModel?.ToRuntimeModel(),
            MimirBioacousticContestants.CanaryPacketTrill.Id);
        if (baseline == null || modeled == null)
        {
            continue;
        }

        var baselineAbsError = Math.Abs(baseline.PredictionErrorMicroseconds);
        var modeledAbsError = Math.Abs(modeled.PredictionErrorMicroseconds);
        results.Add(new ComplexContourChannelModelEvaluationCase(
            replayCase.CaseId,
            pathModel?.PathId ?? "",
            baseline.DelayMicroseconds,
            modeled.DelayMicroseconds,
            baselineAbsError,
            modeledAbsError,
            baseline.MeanAbsoluteErrorSamples,
            modeled.MeanAbsoluteErrorSamples,
            baseline.MeanAbsolutePhaseErrorRadians,
            modeled.MeanAbsolutePhaseErrorRadians,
            baseline.Confidence,
            modeled.Confidence,
            modeledAbsError - baselineAbsError,
            modeled.MeanAbsoluteErrorSamples - baseline.MeanAbsoluteErrorSamples,
            modeled.MeanAbsolutePhaseErrorRadians - baseline.MeanAbsolutePhaseErrorRadians));
        Console.WriteLine(
            $"complex-contour-channel-eval case={replayCase.CaseId} path={pathModel?.PathId ?? "none"} " +
            $"baselineAbsUs={baselineAbsError:0.000} modeledAbsUs={modeledAbsError:0.000} deltaAbsUs={modeledAbsError - baselineAbsError:+0.000;-0.000;0.000} " +
            $"maeSamples={baseline.MeanAbsoluteErrorSamples:0.000}->{modeled.MeanAbsoluteErrorSamples:0.000} phaseMae={baseline.MeanAbsolutePhaseErrorRadians:0.000}->{modeled.MeanAbsolutePhaseErrorRadians:0.000}");
    }

    var improved = results.Count(result => result.ModeledAbsolutePredictionErrorMicroseconds < result.BaselineAbsolutePredictionErrorMicroseconds);
    var documentOut = new ComplexContourChannelModelEvaluationDocument(
        "mimir.bioacoustic.complex-contour-channel-model-evaluation.v1",
        DateTimeOffset.UtcNow,
        receiptPath,
        channelModelPath,
        results.Count,
        improved,
        results.Count == 0 ? 0.0 : results.Average(result => result.DeltaAbsolutePredictionErrorMicroseconds),
        results.Count == 0 ? 0.0 : results.Average(result => result.DeltaMeanAbsoluteErrorSamples),
        results.ToArray());
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
    File.WriteAllText(outputPath, JsonSerializer.Serialize(documentOut, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(
        $"complex-contour-channel-evaluation path={outputPath} cases={documentOut.CasesEvaluated} improved={documentOut.CasesImproved} " +
        $"meanDeltaAbsUs={documentOut.MeanDeltaAbsolutePredictionErrorMicroseconds:+0.000;-0.000;0.000} meanDeltaMaeSamples={documentOut.MeanDeltaMeanAbsoluteErrorSamples:+0.000;-0.000;0.000}");
    return results.Count == 0 ? 1 : 0;
}

static int CalibrateContestantAsioFloat32(
    string inputPath,
    string outputPath,
    int sampleRate,
    int channels,
    int referenceChannel,
    double seconds,
    double scheduleOffsetSamples,
    double searchRadiusUs,
    double delaySearchUs,
    string songId,
    double minRealtimeFactor)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"contestant calibration failed: input not found: {inputPath}");
        return 1;
    }

    var profile = MimirBioacousticContestants.BuiltIn.FirstOrDefault(profile =>
        string.Equals(profile.Id, songId, StringComparison.OrdinalIgnoreCase));
    if (profile == null)
    {
        Console.Error.WriteLine($"contestant calibration failed: unknown song '{songId}'");
        return 1;
    }

    if (referenceChannel < 0 || referenceChannel >= channels)
    {
        Console.Error.WriteLine("contestant calibration failed: reference channel is outside the capture channel count");
        return 1;
    }

    var channelSamples = ReadInterleavedFloat32(inputPath, channels, out var frameCount);
    if (frameCount == 0)
    {
        Console.Error.WriteLine("contestant calibration failed: capture file contains no complete frames");
        return 1;
    }

    var captureSeconds = seconds > 0.0 ? Math.Min(seconds, frameCount / (double)sampleRate) : frameCount / (double)sampleRate;
    var renderer = new MimirBioacousticContestantRenderer(profile);
    var expectedEvents = renderer.ExpectedEvents(captureSeconds)
        .Order()
        .ToArray();
    if (expectedEvents.Length == 0)
    {
        Console.Error.WriteLine("contestant calibration failed: no complete expected packet events in capture");
        return 1;
    }

    var searchRadiusSamples = Math.Max(1, (int)Math.Round(searchRadiusUs * sampleRate / 1_000_000.0));
    var delaySearchSamples = Math.Max(0, (int)Math.Round(delaySearchUs * sampleRate / 1_000_000.0));
    var stopwatch = Stopwatch.StartNew();
    var channelCalibrations = new List<BioacousticPhysicalChannelCalibration>(channels);
    for (var channel = 0; channel < channels; channel++)
    {
        channelCalibrations.Add(CalibrateContestantChannel(
            $"asio-ch{channel}",
            channelSamples[channel],
            sampleRate,
            renderer,
            expectedEvents,
            scheduleOffsetSamples,
            searchRadiusSamples,
            delaySearchSamples));
    }

    stopwatch.Stop();
    var reference = channelCalibrations[referenceChannel];
    var paths = channelCalibrations
        .Select((channel, index) => BuildContestantPathCalibration(
            reference,
            channel,
            sampleRate,
            channelSamples[referenceChannel],
            channelSamples[index]))
        .ToArray();
    var analyzedAudioSeconds = captureSeconds * channels;
    var realtimeFactor = analyzedAudioSeconds / Math.Max(stopwatch.Elapsed.TotalSeconds, 1.0e-9);
    var model = new BioacousticPhysicalCalibrationModel(
        Schema: "mimir.bioacoustic.physical-calibration.v1",
        CreatedUtc: DateTimeOffset.UtcNow,
        InputPath: inputPath,
        SongId: profile.Id,
        SampleRate: sampleRate,
        Channels: channels,
        ReferenceSourceId: reference.SourceId,
        CaptureSeconds: captureSeconds,
        SearchRadiusSamples: searchRadiusSamples,
        ScheduleOffsetSamples: scheduleOffsetSamples,
        DecodeMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
        RealtimeFactor: realtimeFactor,
        ChannelsModel: channelCalibrations.ToArray(),
        Paths: paths);

    var outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrWhiteSpace(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }

    File.WriteAllText(
        outputPath,
        JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine(
        $"contestant-calibration path={outputPath} input={inputPath} song={profile.Id} sampleRate={sampleRate} channels={channels} events={expectedEvents.Length} decodeMs={stopwatch.Elapsed.TotalMilliseconds:0.000} realtime={realtimeFactor:0.0}x budget={minRealtimeFactor:0.0}x");
    foreach (var channel in channelCalibrations)
    {
        Console.WriteLine(
            $"contestant-calibration-channel {channel.SourceId}: events={channel.DetectedEvents}/{channel.ExpectedEvents} payload={channel.PayloadAccuracy:0.000} confidence={channel.Confidence:0.000} polarity={channel.Polarity:+0;-0;0} scheduleOffsetSamples={channel.ScheduleOffsetSamples:0.000} offsetSamples={channel.SourceOffsetSamples:0.000000} offsetUs={channel.SourceOffsetSamples * 1_000_000.0 / sampleRate:0.000} maeSamples={channel.MeanAbsoluteErrorSamples:0.000000} maeUs={channel.MeanAbsoluteErrorSamples * 1_000_000.0 / sampleRate:0.000} anchors={channel.AnchorCount} anchorMeanResidualSamples={channel.MeanAnchorResidualSamples:0.000000} rms={channel.Rms:0.000000} peak={channel.Peak:0.000000} usableBands={channel.Bands.Count(band => band.Usable)}/{channel.Bands.Length}");
    }

    foreach (var path in paths)
    {
        Console.WriteLine(
            $"contestant-calibration-path {path.ReferenceSourceId}->{path.SourceId}: delaySamples={path.DelaySamples:0.000000} delayUs={path.DelayMicroseconds:0.000} syncMaeUs={path.SyncMeanAbsoluteErrorMicroseconds:0.000} confidence={path.Confidence:0.000} waveform={path.WaveformConfidence:0.000} gain={path.RelativeGain:0.000} polarity={path.RelativePolarity:+0;-0;0} matchedAnchors={path.MatchedAnchors} normalizedBands={path.ResponseNormalizationBands.Count(band => band.Usable)}/{path.ResponseNormalizationBands.Length}");
    }

    if (realtimeFactor < minRealtimeFactor)
    {
        Console.Error.WriteLine($"contestant calibration failed budget: realtime={realtimeFactor:0.000}x below required {minRealtimeFactor:0.000}x");
        return 2;
    }

    return 0;
}

static IReadOnlyList<BioacousticTrainingHypothesis> BioacousticTrainingHypotheses()
{
    var baseProfiles = MimirBioacousticDecoderConfiguration.BuiltInProfiles
        .Select(profile => new BioacousticTrainingHypothesis(
            profile.Id,
            profile.Description,
            CepstralDecoderOptions.FromRuntime(profile)))
        .ToList();

    var razor = CepstralDecoderOptions.FromRuntime(MimirBioacousticDecoderConfiguration.PacketRazorIndex);
    baseProfiles.Add(new BioacousticTrainingHypothesis(
        "packet-razor-flux-index",
        "Packet razor receiver with log-mel spectral-flux proposals so sharp call-shape changes own the candidate budget.",
        razor with
        {
            ProposalMode = CepstralProposalMode.LogMelFlux,
            ProposalBudgetMultiplier = 1.65,
            DenseStepSeconds = 0.090
        }));

    var sprint = CepstralDecoderOptions.FromRuntime(MimirBioacousticDecoderConfiguration.PacketSprintIndex);
    baseProfiles.Add(new BioacousticTrainingHypothesis(
        "packet-sprint-flux-index",
        "Packet sprint receiver with log-mel spectral-flux proposals and a slightly wider budget for damaged paths.",
        sprint with
        {
            ProposalMode = CepstralProposalMode.LogMelFlux,
            ProposalBudgetMultiplier = 2.30,
            DenseStepSeconds = 0.080
        }));

    baseProfiles.Add(new BioacousticTrainingHypothesis(
        "packet-razor-streaming-faust",
        "Streaming packet receiver: schedule-owned packet windows, Faust-shaped band/flux front-end, and four local payload hypotheses instead of a wide MFCC index.",
        razor with
        {
            ProposalMode = CepstralProposalMode.StreamingPacketRazor,
            ProposalBudgetMultiplier = 1.0,
            DenseStepSeconds = 0.0
        }));

    return baseProfiles.ToArray();
}

static IReadOnlyList<CepstralDegradationSetting> CepstralSmokeDegradationSettings() =>
[
    new("clean-roundtrip", CepstralDegradationProfiles.Clean.WarpFrames, CepstralDegradationProfiles.Clean.WarpCoefficients, CepstralDegradationProfiles.Clean.BlurPasses, CepstralDegradationDomain.Cepstrum),
    new("blur-light", CepstralDegradationProfiles.Blur.WarpFrames, CepstralDegradationProfiles.Blur.WarpCoefficients, CepstralDegradationProfiles.Blur.BlurPasses, CepstralDegradationDomain.Cepstrum),
    new("warp-light", CepstralDegradationProfiles.WarpLight.WarpFrames, CepstralDegradationProfiles.WarpLight.WarpCoefficients, CepstralDegradationProfiles.WarpLight.BlurPasses, CepstralDegradationDomain.Cepstrum),
    new("warp-light-blur", CepstralDegradationProfiles.WarpBlur.WarpFrames, CepstralDegradationProfiles.WarpBlur.WarpCoefficients, CepstralDegradationProfiles.WarpBlur.BlurPasses, CepstralDegradationDomain.Cepstrum)
];

static IReadOnlyList<CepstralDegradationSetting> CepstralTrainingDegradationSettings() =>
[
    .. CepstralSmokeDegradationSettings(),
    new("warp-heavy-blur", CepstralDegradationProfiles.WarpHeavy.WarpFrames, CepstralDegradationProfiles.WarpHeavy.WarpCoefficients, CepstralDegradationProfiles.WarpHeavy.BlurPasses, CepstralDegradationDomain.Cepstrum),
    new("logmel-blur-light", CepstralDegradationProfiles.Blur.WarpFrames, CepstralDegradationProfiles.Blur.WarpCoefficients, CepstralDegradationProfiles.Blur.BlurPasses, CepstralDegradationDomain.LogMel),
    new("logmel-warp-light", CepstralDegradationProfiles.WarpLight.WarpFrames, CepstralDegradationProfiles.WarpLight.WarpCoefficients, CepstralDegradationProfiles.WarpLight.BlurPasses, CepstralDegradationDomain.LogMel),
    new("logmel-warp-light-blur", CepstralDegradationProfiles.WarpBlur.WarpFrames, CepstralDegradationProfiles.WarpBlur.WarpCoefficients, CepstralDegradationProfiles.WarpBlur.BlurPasses, CepstralDegradationDomain.LogMel),
    new("logmel-warp-heavy-blur", CepstralDegradationProfiles.WarpHeavy.WarpFrames, CepstralDegradationProfiles.WarpHeavy.WarpCoefficients, CepstralDegradationProfiles.WarpHeavy.BlurPasses, CepstralDegradationDomain.LogMel)
];

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

static ChannelSignalStats SignalStats(ReadOnlySpan<float> samples)
{
    if (samples.Length == 0)
    {
        return new ChannelSignalStats(0.0, 0.0, 0.0);
    }

    var sum = 0.0;
    var sumSquares = 0.0;
    var peak = 0.0;
    foreach (var sample in samples)
    {
        sum += sample;
        sumSquares += sample * sample;
        peak = Math.Max(peak, Math.Abs(sample));
    }

    return new ChannelSignalStats(
        Math.Sqrt(sumSquares / samples.Length),
        peak,
        sum / samples.Length);
}

static ChannelSignalStats BandLimitedStats(ReadOnlySpan<float> samples, int sampleRate, double lowHz, double highHz)
{
    if (samples.Length == 0 || sampleRate <= 0 || highHz <= lowHz)
    {
        return new ChannelSignalStats(0.0, 0.0, 0.0);
    }

    var highPassed = new float[samples.Length];
    var lowCut = Math.Clamp(lowHz, 1.0, sampleRate * 0.45);
    var highCut = Math.Clamp(highHz, lowCut + 1.0, sampleRate * 0.49);
    var dt = 1.0 / sampleRate;
    var hpRc = 1.0 / (2.0 * Math.PI * lowCut);
    var hpAlpha = hpRc / (hpRc + dt);
    var previousInput = (double)samples[0];
    var previousOutput = 0.0;
    for (var index = 0; index < samples.Length; index++)
    {
        var input = samples[index];
        var output = hpAlpha * (previousOutput + input - previousInput);
        highPassed[index] = (float)output;
        previousInput = input;
        previousOutput = output;
    }

    var lpRc = 1.0 / (2.0 * Math.PI * highCut);
    var lpAlpha = dt / (lpRc + dt);
    var lowPassed = 0.0;
    var sum = 0.0;
    var sumSquares = 0.0;
    var peak = 0.0;
    foreach (var sample in highPassed)
    {
        lowPassed += lpAlpha * (sample - lowPassed);
        sum += lowPassed;
        sumSquares += lowPassed * lowPassed;
        peak = Math.Max(peak, Math.Abs(lowPassed));
    }

    return new ChannelSignalStats(
        Math.Sqrt(sumSquares / samples.Length),
        peak,
        sum / samples.Length);
}

static string DescribeSpectrumPeaks(MimirAudioSpectrumSnapshot? spectrum)
{
    return spectrum == null || spectrum.Peaks.Count == 0
        ? "none"
        : string.Join(",", spectrum.Peaks.Select(peak => $"{peak.FrequencyHz / 1000.0:0.00}kHz/{peak.Decibels:0.0}dB"));
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
    var logMelFrames = new double[frameCount, melBins];
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
        for (var mel = 0; mel < melBins; mel++)
        {
            logMelFrames[frame, mel] = logMel[mel];
        }

        var cepstrum = Dct(logMel, cepstralCoefficients);
        for (var coefficient = 0; coefficient < cepstralCoefficients; coefficient++)
        {
            cepstra[frame, coefficient] = cepstrum[coefficient];
        }
    }

    var warped = setting.Domain == CepstralDegradationDomain.LogMel
        ? WarpCepstrum(logMelFrames, setting)
        : WarpCepstrum(cepstra, setting);
    for (var pass = 0; pass < setting.BlurPasses; pass++)
    {
        warped = BlurCepstrum5Tap(warped);
    }

    var output = new double[Math.Max(source.Length, (frameCount - 1) * hopSize + fftSize)];
    var outputWeight = new double[output.Length];
    for (var frame = 0; frame < frameCount; frame++)
    {
        var logMel = setting.Domain == CepstralDegradationDomain.LogMel
            ? Row(warped, frame)
            : InverseDct(Row(warped, frame), melBins);
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
    return DecodeCepstralIndexedWordsWithOptions(samples, sampleRate, expectedEventCount, CepstralDecoderOptions.Default);
}

static IReadOnlyList<CepstralWordObservation> DecodeCepstralIndexedWordsWithOptions(
    float[] samples,
    int sampleRate,
    int expectedEventCount,
    CepstralDecoderOptions options)
{
    var templateIndex = BuildCepstralWordIndex(sampleRate, options);
    return DecodeCepstralIndexedWordsWithIndex(samples, sampleRate, expectedEventCount, options, templateIndex);
}

static IReadOnlyList<CepstralWordObservation> DecodeCepstralIndexedWordsWithIndex(
    float[] samples,
    int sampleRate,
    int expectedEventCount,
    CepstralDecoderOptions options,
    CepstralWordIndex templateIndex)
{
    var motifSamples = MimirBioacousticTimeline.Default.RenderEventMonoFloat(0, sampleRate).Length;
    return DecodeCepstralIndexedWordsCore(
        samples,
        sampleRate,
        expectedEventCount,
        options,
        templateIndex,
        motifSamples,
        offset => PredictedBioacousticEventIndex(offset, sampleRate));
}

static IReadOnlyList<CepstralWordObservation> DecodeCepstralIndexedWordsWithContestantIndex(
    float[] samples,
    int sampleRate,
    int expectedEventCount,
    CepstralDecoderOptions options,
    CepstralWordIndex templateIndex,
    MimirBioacousticContestantRenderer? contestant)
{
    var motifSamples = contestant?.RenderEventMonoFloat(0, sampleRate).Length
        ?? MimirBioacousticTimeline.Default.RenderEventMonoFloat(0, sampleRate).Length;
    if (contestant != null && options.ProposalMode == CepstralProposalMode.StreamingPacketRazor)
    {
        return DecodeStreamingPacketRazorWords(samples, sampleRate, expectedEventCount, contestant, motifSamples);
    }

    return DecodeCepstralIndexedWordsCore(
        samples,
        sampleRate,
        expectedEventCount,
        options,
        templateIndex,
        motifSamples,
        offset => contestant == null
            ? PredictedBioacousticEventIndex(offset, sampleRate)
            : PredictedBioacousticContestantEventIndex(offset, sampleRate, contestant.Profile.EventSpacingSeconds),
        contestant == null
            ? null
            : (offset, eventIndex, length) => RefineContestantOffset(samples, sampleRate, offset, length, contestant, eventIndex));
}

static IReadOnlyList<CepstralWordObservation> DecodeCepstralIndexedWordsCore(
    float[] samples,
    int sampleRate,
    int expectedEventCount,
    CepstralDecoderOptions options,
    CepstralWordIndex templateIndex,
    int motifSamples,
    Func<int, ulong> predictEvent,
    Func<int, ulong, int, double>? refineOffset = null)
{
    var hopSamples = Math.Max(1, sampleRate / 1_000);
    var energyTrace = WindowEnergy(samples, motifSamples, hopSamples);
    var proposalTrace = options.ProposalMode == CepstralProposalMode.Energy
        ? energyTrace
        : LogMelSpectralFluxTrace(samples, sampleRate, options, hopSamples);
    if (proposalTrace.Length == 0)
    {
        proposalTrace = energyTrace;
    }

    var threshold = proposalTrace.Length == 0
        ? double.PositiveInfinity
        : proposalTrace.Average(value => (double)value) + Math.Sqrt(proposalTrace.Sum(value => Math.Pow(value - proposalTrace.Average(), 2.0)) / proposalTrace.Length) * 0.10;
    var proposals = new List<int>();
    for (var index = 1; index < proposalTrace.Length - 1; index++)
    {
        if (proposalTrace[index] >= threshold &&
            proposalTrace[index] >= proposalTrace[index - 1] &&
            proposalTrace[index] >= proposalTrace[index + 1])
        {
            proposals.Add(index * hopSamples);
        }
    }

    var denseStep = Math.Max(1, (int)Math.Round(sampleRate * options.DenseStepSeconds));
    for (var offset = 0; offset + motifSamples <= samples.Length; offset += denseStep)
    {
        proposals.Add(offset);
    }

    var proposalBudget = Math.Max((int)Math.Ceiling(expectedEventCount * options.ProposalBudgetMultiplier), 16);
    var observations = new List<CepstralWordObservation>();
    foreach (var offset in proposals
                 .OrderByDescending(offset => proposalTrace[Math.Clamp(offset / hopSamples, 0, proposalTrace.Length - 1)])
                 .Take(proposalBudget)
                 .Order())
    {
        if (offset < 0 || offset + motifSamples > samples.Length)
        {
            continue;
        }

        var feature = CepstralFingerprintWithOptions(samples.AsSpan(offset, motifSamples), sampleRate, options);
        var candidateIndexes = new HashSet<int>();
        for (var table = 0; table < options.TableCount; table++)
        {
            var key = ProjectionHash(feature, table, options.HashBits);
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
                                 HammingDistance(pair.Key.Hash, key) <= options.NearHashRadius)
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

        var predictedEvent = predictEvent(offset);
        var best = candidateIndexes
            .Select(index =>
            {
                var template = templateIndex.Templates[index];
                var distance = CepstralDistance(feature, template.Feature);
                var timePenalty = Math.Abs((long)template.EventIndex - (long)predictedEvent) * 3.0;
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

        var refinedOffset = refineOffset == null
            ? offset
            : refineOffset(offset, best.Template.EventIndex, motifSamples);
        var shapeAccuracy = double.IsFinite(best.Distance)
            ? 1.0 / (1.0 + best.Distance / 4.0)
            : 0.0;
        observations.Add(new CepstralWordObservation(best.Template.EventIndex, refinedOffset, confidence, best.Template.PayloadSymbol, shapeAccuracy));
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

static ulong PredictedBioacousticContestantEventIndex(double sampleOffset, int sampleRate, double eventSpacingSeconds)
{
    const double firstEventSeconds = MimirBioacousticContestants.FirstEventSeconds;
    var index = (long)Math.Round((sampleOffset / sampleRate - firstEventSeconds) / eventSpacingSeconds);
    return (ulong)Math.Max(0, index);
}

static double RefineContestantOffset(
    float[] samples,
    int sampleRate,
    int coarseOffset,
    int motifSamples,
    MimirBioacousticContestantRenderer contestant,
    ulong eventIndex)
{
    var template = contestant.RenderEventMonoFloat(eventIndex, sampleRate);
    var searchRadius = Math.Max(4, sampleRate / 16);
    var start = Math.Max(0, coarseOffset - searchRadius);
    var end = Math.Min(samples.Length - motifSamples, coarseOffset + searchRadius);
    if (start > end)
    {
        return coarseOffset;
    }

    var coarseStep = Math.Max(1, sampleRate / 2_000);
    var bestOffset = ScoreContestantOffset(samples, template, start, motifSamples) > ScoreContestantOffset(samples, template, coarseOffset, motifSamples)
        ? start
        : coarseOffset;
    var bestScore = ScoreContestantOffset(samples, template, bestOffset, motifSamples);
    for (var offset = start; offset <= end; offset += coarseStep)
    {
        var score = ScoreContestantOffset(samples, template, offset, motifSamples);
        if (score > bestScore)
        {
            bestScore = score;
            bestOffset = offset;
        }
    }

    var fineStart = Math.Max(start, bestOffset - coarseStep);
    var fineEnd = Math.Min(end, bestOffset + coarseStep);
    for (var offset = fineStart; offset <= fineEnd; offset++)
    {
        var score = ScoreContestantOffset(samples, template, offset, motifSamples);
        if (score > bestScore)
        {
            bestScore = score;
            bestOffset = offset;
        }
    }

    return bestOffset;
}

static IReadOnlyList<CepstralWordObservation> DecodeStreamingPacketRazorWords(
    float[] samples,
    int sampleRate,
    int expectedEventCount,
    MimirBioacousticContestantRenderer contestant,
    int motifSamples,
    double scheduleOffsetSamples = 0.0)
{
    if (expectedEventCount <= 0 || samples.Length < motifSamples)
    {
        return [];
    }

    var payloadCount = 1 << Math.Clamp(contestant.Profile.PayloadBitsPerEvent, 0, 4);
    var searchRadius = Math.Max(2, (int)Math.Round(sampleRate * 0.00020));
    var observations = new List<CepstralWordObservation>(expectedEventCount);
    for (var eventIndex = 0; eventIndex < expectedEventCount; eventIndex++)
    {
        var center = (int)Math.Round(contestant.EventStartSeconds((ulong)eventIndex) * sampleRate + scheduleOffsetSamples);
        var start = Math.Max(0, center - searchRadius);
        var end = Math.Min(samples.Length - motifSamples, center + searchRadius);
        if (start > end)
        {
            continue;
        }

        var bestScore = double.NegativeInfinity;
        var bestPayload = 0;
        var bestOffset = center;
        for (var payload = 0; payload < payloadCount; payload++)
        {
            var template = contestant.RenderEventMonoFloat((ulong)eventIndex, sampleRate, payload);
            for (var offset = start; offset <= end; offset += 2)
            {
                var score = ScoreStreamingPacketOffset(samples, template, offset, motifSamples);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPayload = payload;
                    bestOffset = offset;
                }
            }
        }

        if (bestScore < 0.08)
        {
            continue;
        }

        var refinedOffset = RefineStreamingOffset(samples, contestant, sampleRate, motifSamples, (ulong)eventIndex, bestPayload, bestOffset);
        var confidence = Math.Clamp((bestScore + 1.0) * 0.5, 0.0, 1.0);
        observations.Add(new CepstralWordObservation(
            (ulong)eventIndex,
            refinedOffset,
            confidence,
            bestPayload,
            confidence));
    }

    return observations;
}

static double RefineStreamingOffset(
    float[] samples,
    MimirBioacousticContestantRenderer contestant,
    int sampleRate,
    int motifSamples,
    ulong eventIndex,
    int payload,
    int bestOffset)
{
    if (bestOffset <= 0 || bestOffset >= samples.Length - motifSamples - 1)
    {
        return bestOffset;
    }

    var template = contestant.RenderEventMonoFloat(eventIndex, sampleRate, payload);
    var left = ScoreStreamingPacketOffset(samples, template, bestOffset - 1, motifSamples);
    var center = ScoreStreamingPacketOffset(samples, template, bestOffset, motifSamples);
    var right = ScoreStreamingPacketOffset(samples, template, bestOffset + 1, motifSamples);
    var denominator = left - 2.0 * center + right;
    return Math.Abs(denominator) <= 1.0e-12
        ? bestOffset
        : bestOffset + Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
}

static double ScoreStreamingPacketOffset(float[] samples, float[] template, int offset, int motifSamples)
{
    var score = ScoreContestantOffset(samples, template, offset, motifSamples);
    return double.IsFinite(score) ? Math.Abs(score) : score;
}

static double ScoreContestantOffset(float[] samples, float[] template, int offset, int motifSamples)
{
    if (offset < 0 || offset + motifSamples > samples.Length)
    {
        return double.NegativeInfinity;
    }

    var dot = 0.0;
    var sampleEnergy = 0.0;
    var templateEnergy = 0.0;
    for (var index = 0; index < Math.Min(motifSamples, template.Length); index++)
    {
        var sample = samples[offset + index];
        var expected = template[index];
        dot += sample * expected;
        sampleEnergy += sample * sample;
        templateEnergy += expected * expected;
    }

    return sampleEnergy <= 1.0e-12 || templateEnergy <= 1.0e-12
        ? double.NegativeInfinity
        : dot / Math.Sqrt(sampleEnergy * templateEnergy);
}

static BioacousticPhysicalChannelCalibration CalibrateContestantChannel(
    string sourceId,
    float[] samples,
    int sampleRate,
    MimirBioacousticContestantRenderer renderer,
    IReadOnlyList<ulong> expectedEvents,
    double scheduleOffsetSamples,
    int searchRadiusSamples,
    int delaySearchSamples)
{
    var motifSamples = renderer.RenderEventMonoFloat(0, sampleRate).Length;
    var channelScheduleOffsetSamples = delaySearchSamples <= 0
        ? scheduleOffsetSamples
        : EstimateContestantScheduleOffset(
            samples,
            sampleRate,
            renderer,
            expectedEvents,
            scheduleOffsetSamples,
            delaySearchSamples,
            motifSamples);
    var observations = new List<BioacousticPhysicalEventCalibration>(expectedEvents.Count);
    var payloadCorrect = 0;
    foreach (var eventIndex in expectedEvents)
    {
        var expectedPayload = renderer.PayloadSymbolForEvent(eventIndex);
        var template = renderer.RenderEventMonoFloat(eventIndex, sampleRate, expectedPayload);
        var center = (int)Math.Round(renderer.EventStartSeconds(eventIndex) * sampleRate + channelScheduleOffsetSamples);
        var start = Math.Max(0, center - searchRadiusSamples);
        var end = Math.Min(samples.Length - motifSamples, center + searchRadiusSamples);
        if (start > end)
        {
            continue;
        }

        var bestOffset = center;
        var bestScore = double.NegativeInfinity;
        for (var offset = start; offset <= end; offset += 2)
        {
            var score = ScoreStreamingPacketOffset(samples, template, offset, motifSamples);
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }

        if (bestScore < 0.06)
        {
            continue;
        }

        var refinedOffset = RefineContestantExpectedPayloadOffset(samples, template, bestOffset, motifSamples);
        var signedScore = ScoreContestantOffset(samples, template, (int)Math.Round(refinedOffset), motifSamples);
        var polarity = signedScore < 0.0 ? -1 : 1;
        var payload = ClassifyContestantPayloadAt(samples, sampleRate, renderer, eventIndex, refinedOffset, motifSamples);
        if (payload == expectedPayload)
        {
            payloadCorrect++;
        }

        var residual = ComputeContestantResidual(samples, template, (int)Math.Round(refinedOffset), motifSamples, signedScore);
        var anchors = MeasureContestantAnchors(
            samples,
            template,
            renderer,
            eventIndex,
            sampleRate,
            refinedOffset,
            motifSamples);
        var delaySamples = refinedOffset - renderer.EventStartSeconds(eventIndex) * sampleRate;
        observations.Add(new BioacousticPhysicalEventCalibration(
            eventIndex,
            expectedPayload,
            payload,
            refinedOffset,
            delaySamples,
            Math.Clamp(Math.Abs(signedScore), 0.0, 1.0),
            polarity,
            residual,
            EstimateContestantEventBands(samples, template, renderer, eventIndex, sampleRate, (int)Math.Round(refinedOffset), motifSamples),
            anchors));
    }

    var wordClock = FitContestantClockHypothesis(
        observations
            .Select(observation => new CepstralWordObservation(
                observation.EventIndex,
                observation.SampleOffset,
                observation.Confidence,
                observation.ExpectedPayloadSymbol,
                observation.Confidence))
            .ToArray(),
        renderer,
        sampleRate,
        expectedEvents.Count);
    var anchorClock = FitContestantClockFromAnchors(observations, renderer, sampleRate, expectedEvents.Count);
    var clock = anchorClock != null &&
        wordClock != null &&
        anchorClock.MeanAbsoluteErrorSamples <= wordClock.MeanAbsoluteErrorSamples * 0.82
            ? anchorClock
            : wordClock ?? anchorClock;
    var sourceOffsetSamples = clock?.SourceOffsetSamples ?? 0.0;
    var meanAbsoluteErrorSamples = clock?.MeanAbsoluteErrorSamples ?? 0.0;
    var confidence = observations.Count == 0
        ? 0.0
        : (clock?.Confidence ?? 0.0) * 0.55 + observations.Average(observation => observation.Confidence) * 0.30 + Math.Clamp(observations.Count / (double)expectedEvents.Count, 0.0, 1.0) * 0.15;
    var polarityVote = observations.Sum(observation => observation.Polarity * observation.Confidence);
    return new BioacousticPhysicalChannelCalibration(
        sourceId,
        expectedEvents.Count,
        observations.Count,
        expectedEvents.Count == 0 ? 0.0 : payloadCorrect / (double)expectedEvents.Count,
        confidence,
        polarityVote < 0.0 ? -1 : 1,
        channelScheduleOffsetSamples,
        sourceOffsetSamples,
        sourceOffsetSamples * 1_000_000.0 / sampleRate,
        meanAbsoluteErrorSamples,
        meanAbsoluteErrorSamples * 1_000_000.0 / sampleRate,
        observations.Sum(observation => observation.Anchors.Length),
        observations.SelectMany(observation => observation.Anchors).DefaultIfEmpty().Average(anchor => anchor?.TimingResidualSamples ?? 0.0),
        samples.Length,
        RootMeanSquare(samples),
        samples.Length == 0 ? 0.0 : samples.Max(sample => Math.Abs(sample)),
        CollapseContestantBands(observations),
        observations.ToArray());
}

static double EstimateContestantScheduleOffset(
    float[] samples,
    int sampleRate,
    MimirBioacousticContestantRenderer renderer,
    IReadOnlyList<ulong> expectedEvents,
    double seedOffsetSamples,
    int delaySearchSamples,
    int motifSamples)
{
    var coarseStep = Math.Max(4, sampleRate / 6_000);
    var stride = Math.Max(4, sampleRate / 24_000);
    var scoringEvents = expectedEvents
        .Take(Math.Min(8, expectedEvents.Count))
        .ToArray();
    var scoringTemplates = scoringEvents
        .Select(eventIndex => (
            EventIndex: eventIndex,
            Template: renderer.RenderEventMonoFloat(eventIndex, sampleRate, renderer.PayloadSymbolForEvent(eventIndex)),
            TimelineSamples: renderer.EventStartSeconds(eventIndex) * sampleRate))
        .ToArray();
    var bestOffset = seedOffsetSamples;
    var bestScore = double.NegativeInfinity;
    for (var candidate = (int)Math.Round(seedOffsetSamples) - delaySearchSamples;
         candidate <= (int)Math.Round(seedOffsetSamples) + delaySearchSamples;
         candidate += coarseStep)
    {
        var score = 0.0;
        var count = 0;
        foreach (var scoringTemplate in scoringTemplates)
        {
            var offset = (int)Math.Round(scoringTemplate.TimelineSamples + candidate);
            var eventScore = ScoreContestantOffsetStrided(samples, scoringTemplate.Template, offset, motifSamples, stride);
            if (!double.IsFinite(eventScore))
            {
                continue;
            }

            score += Math.Abs(eventScore);
            count++;
        }

        if (count == 0)
        {
            continue;
        }

        score /= count;
        if (score > bestScore)
        {
            bestScore = score;
            bestOffset = candidate;
        }
    }

    var fineStart = (int)Math.Round(bestOffset) - coarseStep;
    var fineEnd = (int)Math.Round(bestOffset) + coarseStep;
    for (var candidate = fineStart; candidate <= fineEnd; candidate += 2)
    {
        var score = 0.0;
        var count = 0;
        foreach (var scoringTemplate in scoringTemplates)
        {
            var offset = (int)Math.Round(scoringTemplate.TimelineSamples + candidate);
            var eventScore = ScoreContestantOffsetStrided(samples, scoringTemplate.Template, offset, motifSamples, stride);
            if (!double.IsFinite(eventScore))
            {
                continue;
            }

            score += Math.Abs(eventScore);
            count++;
        }

        if (count > 0 && score / count > bestScore)
        {
            bestScore = score / count;
            bestOffset = candidate;
        }
    }

    return bestOffset;
}

static double ScoreContestantOffsetStrided(float[] samples, float[] template, int offset, int motifSamples, int stride)
{
    if (offset < 0 || offset + motifSamples > samples.Length)
    {
        return double.NegativeInfinity;
    }

    var dot = 0.0;
    var sampleEnergy = 0.0;
    var templateEnergy = 0.0;
    for (var index = 0; index < Math.Min(motifSamples, template.Length); index += Math.Max(1, stride))
    {
        var sample = samples[offset + index];
        var expected = template[index];
        dot += sample * expected;
        sampleEnergy += sample * sample;
        templateEnergy += expected * expected;
    }

    return sampleEnergy <= 1.0e-12 || templateEnergy <= 1.0e-12
        ? double.NegativeInfinity
        : dot / Math.Sqrt(sampleEnergy * templateEnergy);
}

static double RefineContestantExpectedPayloadOffset(float[] samples, float[] template, int bestOffset, int motifSamples)
{
    if (bestOffset <= 0 || bestOffset >= samples.Length - motifSamples - 1)
    {
        return bestOffset;
    }

    var left = ScoreStreamingPacketOffset(samples, template, bestOffset - 1, motifSamples);
    var center = ScoreStreamingPacketOffset(samples, template, bestOffset, motifSamples);
    var right = ScoreStreamingPacketOffset(samples, template, bestOffset + 1, motifSamples);
    var denominator = left - 2.0 * center + right;
    return Math.Abs(denominator) <= 1.0e-12
        ? bestOffset
        : bestOffset + Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
}

static BioacousticPhysicalAnchorCalibration[] MeasureContestantAnchors(
    float[] samples,
    float[] template,
    MimirBioacousticContestantRenderer renderer,
    ulong eventIndex,
    int sampleRate,
    double eventSampleOffset,
    int motifSamples)
{
    var anchors = renderer.AnchorPlan(eventIndex);
    var output = new List<BioacousticPhysicalAnchorCalibration>(anchors.Count);
    foreach (var anchor in anchors
                 .OrderByDescending(anchor => anchor.Weight)
                 .Take(6)
                 .OrderBy(anchor => anchor.StartSeconds))
    {
        var anchorSamples = Math.Clamp(
            (int)Math.Round(anchor.DurationSeconds * sampleRate),
            Math.Max(12, sampleRate / 24_000),
            Math.Max(16, motifSamples / 5));
        var expectedLocalOffset = (int)Math.Round(anchor.StartSeconds * sampleRate);
        var expectedObservedOffset = (int)Math.Round(eventSampleOffset) + expectedLocalOffset;
        var templateStart = Math.Clamp(expectedLocalOffset, 0, Math.Max(0, template.Length - anchorSamples));
        var observedStart = Math.Clamp(expectedObservedOffset, 0, Math.Max(0, samples.Length - anchorSamples));
        if (templateStart + anchorSamples > template.Length ||
            observedStart + anchorSamples > samples.Length)
        {
            continue;
        }

        var observedWindow = samples.AsSpan(observedStart, anchorSamples);
        var expectedWindow = template.AsSpan(templateStart, anchorSamples);
        var bestScore = ScoreAnchorWindow(observedWindow, expectedWindow);
        var refinedOffset = observedStart +
            EnergyCentroid(observedWindow) -
            EnergyCentroid(expectedWindow);
        var residual = refinedOffset - (eventSampleOffset + expectedLocalOffset);
        var observedEnergy = WindowMeanSquare(observedWindow);
        var expectedEnergy = WindowMeanSquare(expectedWindow);
        output.Add(new BioacousticPhysicalAnchorCalibration(
            anchor.Kind.ToString(),
            anchor.SyllableIndex,
            anchor.StartSeconds,
            anchor.DurationSeconds,
            anchor.CenterHz,
            refinedOffset,
            residual,
            Math.Clamp(Math.Abs(bestScore) * anchor.Weight, 0.0, 1.0),
            expectedEnergy <= 1.0e-12 ? 0.0 : Math.Sqrt(Math.Max(0.0, observedEnergy) / expectedEnergy)));
    }

    return output.ToArray();
}

static double ScoreAnchorWindow(ReadOnlySpan<float> observed, ReadOnlySpan<float> expected)
{
    var count = Math.Min(observed.Length, expected.Length);
    if (count <= 0)
    {
        return double.NegativeInfinity;
    }

    var dot = 0.0;
    var observedEnergy = 0.0;
    var expectedEnergy = 0.0;
    for (var index = 0; index < count; index++)
    {
        dot += observed[index] * expected[index];
        observedEnergy += observed[index] * observed[index];
        expectedEnergy += expected[index] * expected[index];
    }

    return observedEnergy <= 1.0e-12 || expectedEnergy <= 1.0e-12
        ? double.NegativeInfinity
        : dot / Math.Sqrt(observedEnergy * expectedEnergy);
}

static double EnergyCentroid(ReadOnlySpan<float> samples)
{
    var weighted = 0.0;
    var energy = 0.0;
    for (var index = 0; index < samples.Length; index++)
    {
        var value = samples[index] * samples[index];
        weighted += index * value;
        energy += value;
    }

    return energy <= 1.0e-12
        ? samples.Length * 0.5
        : weighted / energy;
}

static MimirBioacousticClockHypothesis? FitContestantClockFromAnchors(
    IReadOnlyList<BioacousticPhysicalEventCalibration> observations,
    MimirBioacousticContestantRenderer renderer,
    int sampleRate,
    int expectedEventCount)
{
    var wordAnchors = observations
        .Select(observation => new MimirBioacousticClockAnchor(
            observation.EventIndex,
            renderer.EventStartSeconds(observation.EventIndex),
            observation.SampleOffset,
            Math.Clamp(observation.Confidence, 0.001, 1.0)));
    var contourAnchors = observations.SelectMany(observation =>
        observation.Anchors
            .Where(anchor => anchor.Confidence >= 0.12)
            .Select(anchor => new MimirBioacousticClockAnchor(
                observation.EventIndex,
                renderer.EventStartSeconds(observation.EventIndex) + anchor.ExpectedStartSeconds,
                anchor.SampleOffset,
                Math.Clamp(anchor.Confidence, 0.001, 1.0))));
    var anchors = wordAnchors
        .Concat(contourAnchors)
        .OrderByDescending(anchor => anchor.Confidence)
        .Take(160)
        .ToArray();
    if (anchors.Length < 3)
    {
        return null;
    }

    var totalWeight = anchors.Sum(anchor => Math.Max(1.0e-6, anchor.Confidence));
    var meanTimeline = anchors.Sum(anchor => anchor.TimelineSeconds * Math.Max(1.0e-6, anchor.Confidence)) / totalWeight;
    var meanSample = anchors.Sum(anchor => anchor.SampleOffset * Math.Max(1.0e-6, anchor.Confidence)) / totalWeight;
    var covariance = 0.0;
    var variance = 0.0;
    foreach (var anchor in anchors)
    {
        var weight = Math.Max(1.0e-6, anchor.Confidence);
        var dt = anchor.TimelineSeconds - meanTimeline;
        covariance += weight * dt * (anchor.SampleOffset - meanSample);
        variance += weight * dt * dt;
    }

    var effectiveRate = variance > 1.0e-12 ? covariance / variance : sampleRate;
    if (!double.IsFinite(effectiveRate) || effectiveRate < sampleRate * 0.98 || effectiveRate > sampleRate * 1.02)
    {
        effectiveRate = sampleRate;
    }

    var sourceOffset = meanSample - effectiveRate * meanTimeline;
    var residual = anchors.Sum(anchor =>
    {
        var predicted = sourceOffset + anchor.TimelineSeconds * effectiveRate;
        return Math.Abs(anchor.SampleOffset - predicted) * Math.Max(1.0e-6, anchor.Confidence);
    }) / totalWeight;
    var residualConfidence = 1.0 / (1.0 + residual / Math.Max(1.0, sampleRate * 0.00025));
    var countConfidence = Math.Clamp(anchors.Length / 80.0, 0.0, 1.0);
    var anchorConfidence = Math.Clamp(anchors.Average(anchor => anchor.Confidence), 0.0, 1.0);
    var confidence = residualConfidence * 0.45 + countConfidence * 0.30 + anchorConfidence * 0.25;
    var coverage = expectedEventCount <= 0 ? 0.0 : observations.Count / (double)expectedEventCount;
    var score = confidence + Math.Min(anchors.Length, 80) * 0.015 - residual / Math.Max(1.0, sampleRate * 0.004);
    return new MimirBioacousticClockHypothesis(
        sourceOffset,
        effectiveRate,
        anchors.Length,
        coverage,
        residual,
        confidence,
        score,
        anchors);
}

static int ClassifyContestantPayloadAt(
    float[] samples,
    int sampleRate,
    MimirBioacousticContestantRenderer renderer,
    ulong eventIndex,
    double sampleOffset,
    int motifSamples)
{
    var payloadCount = 1 << Math.Clamp(renderer.Profile.PayloadBitsPerEvent, 0, 8);
    var offset = (int)Math.Round(sampleOffset);
    var bestPayload = 0;
    var bestScore = double.NegativeInfinity;
    for (var payload = 0; payload < payloadCount; payload++)
    {
        var template = renderer.RenderEventMonoFloat(eventIndex, sampleRate, payload);
        var score = ScoreStreamingPacketOffset(samples, template, offset, motifSamples);
        if (score > bestScore)
        {
            bestScore = score;
            bestPayload = payload;
        }
    }

    return bestPayload;
}

static double ComputeContestantResidual(float[] samples, float[] template, int offset, int motifSamples, double signedCorrelation)
{
    if (offset < 0 || offset + motifSamples > samples.Length)
    {
        return 1.0;
    }

    var sampleEnergy = 0.0;
    var templateEnergy = 0.0;
    var dot = 0.0;
    for (var index = 0; index < Math.Min(motifSamples, template.Length); index++)
    {
        var sample = samples[offset + index];
        var expected = template[index];
        sampleEnergy += sample * sample;
        templateEnergy += expected * expected;
        dot += sample * expected;
    }

    if (sampleEnergy <= 1.0e-12 || templateEnergy <= 1.0e-12)
    {
        return 1.0;
    }

    var gain = dot / templateEnergy;
    var residual = Math.Max(0.0, sampleEnergy - 2.0 * gain * dot + gain * gain * templateEnergy);
    return Math.Sqrt(residual / sampleEnergy) * (signedCorrelation < 0.0 ? -1.0 : 1.0);
}

static BioacousticPhysicalBandCalibration[] EstimateContestantEventBands(
    float[] samples,
    float[] template,
    MimirBioacousticContestantRenderer renderer,
    ulong eventIndex,
    int sampleRate,
    int offset,
    int motifSamples)
{
    if (offset < 0 || offset + motifSamples > samples.Length)
    {
        return [];
    }

    var bands = EstimateContestantBandCenters(renderer, eventIndex);
    var output = new BioacousticPhysicalBandCalibration[bands.Length];
    for (var index = 0; index < bands.Length; index++)
    {
        var centerHz = bands[index];
        var bandStart = (int)Math.Round(index * motifSamples / (double)Math.Max(1, bands.Length + 1));
        var bandLength = Math.Max(1, Math.Min(motifSamples - bandStart, motifSamples / Math.Max(3, bands.Length)));
        var observed = WindowMeanSquare(samples.AsSpan(offset + bandStart, bandLength));
        var expected = WindowMeanSquare(template.AsSpan(bandStart, Math.Min(bandLength, template.Length - bandStart)));
        var gain = expected <= 1.0e-12 ? 0.0 : Math.Sqrt(Math.Max(0.0, observed) / expected);
        output[index] = new BioacousticPhysicalBandCalibration(centerHz, observed, expected, gain, observed > expected * 0.08);
    }

    return output;
}

static double[] EstimateContestantBandCenters(MimirBioacousticContestantRenderer renderer, ulong eventIndex)
{
    var symbolId = (int)((eventIndex * 73UL + eventIndex / 5UL * 19UL) % MimirBioacousticContestants.SymbolCount);
    var t = symbolId / (double)(MimirBioacousticContestants.SymbolCount - 1);
    var root = Math.Exp(Math.Log(renderer.Profile.LowestRootHz) + (Math.Log(renderer.Profile.HighestRootHz) - Math.Log(renderer.Profile.LowestRootHz)) * t);
    var centers = new double[Math.Min(renderer.Profile.SyllableCount, 8)];
    for (var index = 0; index < centers.Length; index++)
    {
        centers[index] = Math.Clamp(root * Math.Pow(2.0, (-0.5 + index * 0.72) / 12.0), 200.0, renderer.Profile.HighestRootHz * 1.8);
    }

    return centers;
}

static double WindowMeanSquare(ReadOnlySpan<float> samples)
{
    if (samples.Length == 0)
    {
        return 0.0;
    }

    var energy = 0.0;
    for (var index = 0; index < samples.Length; index++)
    {
        energy += samples[index] * samples[index];
    }

    return energy / samples.Length;
}

static BioacousticPhysicalBandCalibration[] CollapseContestantBands(IReadOnlyList<BioacousticPhysicalEventCalibration> observations)
{
    return observations
        .SelectMany(observation => observation.Bands)
        .GroupBy(band => Math.Round(band.CenterHz / 25.0) * 25.0)
        .Select(group => new BioacousticPhysicalBandCalibration(
            group.Key,
            group.Average(band => band.ObservedEnergy),
            group.Average(band => band.ExpectedEnergy),
            group.Average(band => band.Gain),
            group.Count(band => band.Usable) >= Math.Max(1, group.Count() / 2)))
        .OrderBy(band => band.CenterHz)
        .ToArray();
}

static BioacousticPhysicalPathCalibration BuildContestantPathCalibration(
    BioacousticPhysicalChannelCalibration reference,
    BioacousticPhysicalChannelCalibration candidate,
    int sampleRate,
    float[] referenceSamples,
    float[] candidateSamples)
{
    var candidateByEvent = candidate.Events.ToDictionary(observation => observation.EventIndex);
    var matched = reference.Events
        .Where(observation => candidateByEvent.ContainsKey(observation.EventIndex))
        .Select(observation =>
        {
            var other = candidateByEvent[observation.EventIndex];
            var weight = Math.Sqrt(observation.Confidence * other.Confidence);
            return (Delay: other.SampleOffset - observation.SampleOffset, Weight: weight);
        })
        .ToArray();
    var delay = matched.Length == 0
        ? candidate.SourceOffsetSamples - reference.SourceOffsetSamples
        : matched.Sum(pair => pair.Delay * Math.Max(pair.Weight, 1.0e-6)) / matched.Sum(pair => Math.Max(pair.Weight, 1.0e-6));
    var mae = matched.Length == 0
        ? Math.Abs(candidate.MeanAbsoluteErrorSamples - reference.MeanAbsoluteErrorSamples)
        : matched.Sum(pair => Math.Abs(pair.Delay - delay) * Math.Max(pair.Weight, 1.0e-6)) / matched.Sum(pair => Math.Max(pair.Weight, 1.0e-6));
    var anchorEstimate = EstimatePathDelayFromMatchedAnchors(reference, candidate);
    if (anchorEstimate is { } anchorDelay &&
        anchorDelay.MatchedAnchors >= 24 &&
        anchorDelay.MeanAbsoluteErrorSamples < mae)
    {
        delay = anchorDelay.DelaySamples;
        mae = anchorDelay.MeanAbsoluteErrorSamples;
    }

    var waveformEstimate = EstimatePathDelayFromWaveform(referenceSamples, candidateSamples, delay, sampleRate);
    if (waveformEstimate is { Confidence: >= 0.18 } waveformDelay &&
        waveformDelay.EstimatedErrorSamples < mae)
    {
        delay = waveformDelay.DelaySamples;
        mae = waveformDelay.EstimatedErrorSamples;
    }

    var eventWaveformEstimate = EstimatePathDelayFromEventWaveforms(
        reference,
        candidate,
        referenceSamples,
        candidateSamples,
        sampleRate,
        delay);
    if (eventWaveformEstimate is { MatchedEvents: >= 8 } eventWaveformDelay &&
        eventWaveformDelay.MeanAbsoluteErrorSamples < mae)
    {
        delay = eventWaveformDelay.DelaySamples;
        mae = eventWaveformDelay.MeanAbsoluteErrorSamples;
    }

    var relativeGain = reference.Rms <= 1.0e-12 ? 0.0 : candidate.Rms / reference.Rms;
    var referenceBands = reference.Bands.ToDictionary(band => band.CenterHz);
    var normalizedBands = candidate.Bands
        .Select(band =>
        {
            var normalizer = referenceBands.TryGetValue(band.CenterHz, out var referenceBand) && referenceBand.Gain > 1.0e-12
                ? referenceBand.Gain
                : 1.0;
            return band with { Gain = band.Gain / normalizer };
        })
        .ToArray();
    var confidence = Math.Sqrt(reference.Confidence * candidate.Confidence) *
        (1.0 / (1.0 + mae / Math.Max(1.0, sampleRate * 0.00010)));
    return new BioacousticPhysicalPathCalibration(
        reference.SourceId,
        candidate.SourceId,
        delay,
        delay * 1_000_000.0 / sampleRate,
        mae,
        mae * 1_000_000.0 / sampleRate,
        confidence,
        relativeGain,
        reference.Polarity * candidate.Polarity,
        normalizedBands,
        anchorEstimate?.MatchedAnchors ?? 0,
        Math.Max(waveformEstimate?.Confidence ?? 0.0, eventWaveformEstimate?.Confidence ?? 0.0));
}

static (double DelaySamples, double MeanAbsoluteErrorSamples, int MatchedEvents, double Confidence)? EstimatePathDelayFromEventWaveforms(
    BioacousticPhysicalChannelCalibration reference,
    BioacousticPhysicalChannelCalibration candidate,
    float[] referenceSamples,
    float[] candidateSamples,
    int sampleRate,
    double initialDelaySamples)
{
    (double DelaySamples, double MeanAbsoluteErrorSamples, int MatchedEvents, double Confidence)? best = null;
    foreach (var windowSeconds in new[] { 0.012, 0.026, 0.055 })
    {
        var estimate = EstimatePathDelayFromEventWaveformsForWindow(
            reference,
            candidate,
            referenceSamples,
            candidateSamples,
            sampleRate,
            initialDelaySamples,
            Math.Max(256, (int)Math.Round(sampleRate * windowSeconds)));
        if (estimate != null &&
            (best == null || estimate.Value.MeanAbsoluteErrorSamples < best.Value.MeanAbsoluteErrorSamples))
        {
            best = estimate;
        }
    }

    return best;
}

static (double DelaySamples, double MeanAbsoluteErrorSamples, int MatchedEvents, double Confidence)? EstimatePathDelayFromEventWaveformsForWindow(
    BioacousticPhysicalChannelCalibration reference,
    BioacousticPhysicalChannelCalibration candidate,
    float[] referenceSamples,
    float[] candidateSamples,
    int sampleRate,
    double initialDelaySamples,
    int windowSamples)
{
    var candidateByEvent = candidate.Events.ToDictionary(e => e.EventIndex);
    var searchRadius = Math.Max(4, sampleRate / 6_000);
    var delays = new List<(double Value, double Weight)>();
    foreach (var referenceEvent in reference.Events)
    {
        if (!candidateByEvent.TryGetValue(referenceEvent.EventIndex, out var candidateEvent) ||
            referenceEvent.Confidence < 0.25 ||
            candidateEvent.Confidence < 0.25)
        {
            continue;
        }

        var referenceStart = (int)Math.Round(referenceEvent.SampleOffset);
        if (referenceStart < 0 || referenceStart + windowSamples >= referenceSamples.Length)
        {
            continue;
        }

        var centerLag = (int)Math.Round(initialDelaySamples);
        var bestLag = centerLag;
        var bestScore = double.NegativeInfinity;
        for (var lag = centerLag - searchRadius; lag <= centerLag + searchRadius; lag++)
        {
            var candidateStart = referenceStart + lag;
            if (candidateStart < 0 || candidateStart + windowSamples >= candidateSamples.Length)
            {
                continue;
            }

            var score = Math.Abs(ScoreAnchorWindow(
                candidateSamples.AsSpan(candidateStart, windowSamples),
                referenceSamples.AsSpan(referenceStart, windowSamples)));
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (!double.IsFinite(bestScore) || bestScore < 0.035)
        {
            continue;
        }

        var refined = (double)bestLag;
        if (bestLag > centerLag - searchRadius && bestLag < centerLag + searchRadius)
        {
            var left = Math.Abs(ScoreAnchorWindow(
                candidateSamples.AsSpan(referenceStart + bestLag - 1, windowSamples),
                referenceSamples.AsSpan(referenceStart, windowSamples)));
            var middle = Math.Abs(ScoreAnchorWindow(
                candidateSamples.AsSpan(referenceStart + bestLag, windowSamples),
                referenceSamples.AsSpan(referenceStart, windowSamples)));
            var right = Math.Abs(ScoreAnchorWindow(
                candidateSamples.AsSpan(referenceStart + bestLag + 1, windowSamples),
                referenceSamples.AsSpan(referenceStart, windowSamples)));
            var denominator = left - 2.0 * middle + right;
            if (Math.Abs(denominator) > 1.0e-12)
            {
                refined += Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
            }
        }

        delays.Add((refined, Math.Sqrt(referenceEvent.Confidence * candidateEvent.Confidence) * bestScore));
    }

    if (delays.Count < 3)
    {
        return null;
    }

    var median = WeightedMedian(delays);
    var residuals = delays.Select(d => (Value: Math.Abs(d.Value - median), d.Weight)).ToArray();
    var mad = WeightedMedian(residuals);
    var radius = Math.Clamp(mad * 3.0 + 0.75, 1.5, 18.0);
    var inliers = delays.Where(d => Math.Abs(d.Value - median) <= radius).ToArray();
    if (inliers.Length < 3)
    {
        return null;
    }

    var totalWeight = inliers.Sum(d => Math.Max(d.Weight, 1.0e-6));
    var delay = inliers.Sum(d => d.Value * Math.Max(d.Weight, 1.0e-6)) / totalWeight;
    var mae = inliers.Sum(d => Math.Abs(d.Value - delay) * Math.Max(d.Weight, 1.0e-6)) / totalWeight;
    var confidence = Math.Clamp(inliers.Average(d => d.Weight) * Math.Clamp(inliers.Length / 24.0, 0.0, 1.0), 0.0, 1.0);
    return (delay, mae, inliers.Length, confidence);
}

static (double DelaySamples, double EstimatedErrorSamples, double Confidence)? EstimatePathDelayFromWaveform(
    float[] referenceSamples,
    float[] candidateSamples,
    double initialDelaySamples,
    int sampleRate)
{
    if (referenceSamples.Length == 0 || candidateSamples.Length == 0)
    {
        return null;
    }

    var radius = Math.Max(8, sampleRate / 1_500);
    var center = (int)Math.Round(initialDelaySamples);
    var first = Math.Max(0, center - radius);
    var last = Math.Min(candidateSamples.Length - 2, center + radius);
    if (first > last)
    {
        return null;
    }

    var bestLag = center;
    var bestScore = double.NegativeInfinity;
    var secondScore = double.NegativeInfinity;
    for (var lag = first; lag <= last; lag++)
    {
        var score = Math.Abs(NormalizedPathLagScore(referenceSamples, candidateSamples, lag));
        if (score > bestScore)
        {
            secondScore = bestScore;
            bestScore = score;
            bestLag = lag;
        }
        else if (score > secondScore)
        {
            secondScore = score;
        }
    }

    var fractionalLag = (double)bestLag;
    if (bestLag > first && bestLag < last)
    {
        var left = Math.Abs(NormalizedPathLagScore(referenceSamples, candidateSamples, bestLag - 1));
        var middle = Math.Abs(NormalizedPathLagScore(referenceSamples, candidateSamples, bestLag));
        var right = Math.Abs(NormalizedPathLagScore(referenceSamples, candidateSamples, bestLag + 1));
        var denominator = left - 2.0 * middle + right;
        if (Math.Abs(denominator) > 1.0e-12)
        {
            fractionalLag += Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
        }
    }

    var peakMargin = Math.Max(0.0, bestScore - (double.IsFinite(secondScore) ? secondScore : 0.0));
    var confidence = Math.Clamp(bestScore * 0.65 + peakMargin * 80.0, 0.0, 1.0);
    var estimatedError = 1.0 / Math.Max(0.04, confidence * 1.8);
    return (fractionalLag, estimatedError, confidence);
}

static double NormalizedPathLagScore(float[] referenceSamples, float[] candidateSamples, int lag)
{
    var count = Math.Min(referenceSamples.Length, candidateSamples.Length - lag);
    if (count <= 0)
    {
        return double.NegativeInfinity;
    }

    var dot = 0.0;
    var referenceEnergy = 0.0;
    var candidateEnergy = 0.0;
    for (var index = 0; index < count; index++)
    {
        var reference = referenceSamples[index];
        var candidate = candidateSamples[index + lag];
        dot += reference * candidate;
        referenceEnergy += reference * reference;
        candidateEnergy += candidate * candidate;
    }

    return referenceEnergy <= 1.0e-12 || candidateEnergy <= 1.0e-12
        ? double.NegativeInfinity
        : dot / Math.Sqrt(referenceEnergy * candidateEnergy);
}

static (double DelaySamples, double MeanAbsoluteErrorSamples, int MatchedAnchors)? EstimatePathDelayFromMatchedAnchors(
    BioacousticPhysicalChannelCalibration reference,
    BioacousticPhysicalChannelCalibration candidate)
{
    var referenceAnchors = reference.Events
        .SelectMany(e => e.Anchors.Select(anchor => (Event: e.EventIndex, Anchor: anchor)))
        .Where(pair => pair.Anchor.Confidence >= 0.20)
        .GroupBy(pair => (pair.Event, pair.Anchor.Kind, pair.Anchor.SyllableIndex))
        .ToDictionary(
            group => group.Key,
            group => group.OrderByDescending(pair => pair.Anchor.Confidence).First().Anchor);
    var delays = new List<(double Value, double Weight)>();
    foreach (var candidateEvent in candidate.Events)
    {
        foreach (var candidateAnchor in candidateEvent.Anchors.Where(anchor => anchor.Confidence >= 0.20))
        {
            if (!referenceAnchors.TryGetValue(
                    (candidateEvent.EventIndex, candidateAnchor.Kind, candidateAnchor.SyllableIndex),
                    out var referenceAnchor))
            {
                continue;
            }

            var weight = Math.Sqrt(candidateAnchor.Confidence * referenceAnchor.Confidence);
            delays.Add((candidateAnchor.SampleOffset - referenceAnchor.SampleOffset, weight));
        }
    }

    if (delays.Count < 3)
    {
        return null;
    }

    var delay = WeightedMedian(delays);
    var residuals = delays
        .Select(pair => (Value: Math.Abs(pair.Value - delay), pair.Weight))
        .ToArray();
    var medianResidual = WeightedMedian(residuals);
    var inlierRadius = Math.Clamp(medianResidual * 3.0 + 1.5, 3.0, 28.0);
    var inliers = delays
        .Where(pair => Math.Abs(pair.Value - delay) <= inlierRadius)
        .ToArray();
    if (inliers.Length < 3)
    {
        return null;
    }

    var totalWeight = inliers.Sum(pair => Math.Max(pair.Weight, 1.0e-6));
    var refinedDelay = inliers.Sum(pair => pair.Value * Math.Max(pair.Weight, 1.0e-6)) / totalWeight;
    var mae = inliers.Sum(pair => Math.Abs(pair.Value - refinedDelay) * Math.Max(pair.Weight, 1.0e-6)) / totalWeight;
    return (refinedDelay, mae, inliers.Length);
}

static IReadOnlyDictionary<ulong, int> ClassifyContestantPayloads(
    float[] samples,
    int sampleRate,
    CepstralDecoderOptions options,
    MimirBioacousticContestantRenderer contestant,
    IReadOnlyList<CepstralWordObservation> observations,
    bool trustObservationPayload = false)
{
    if (observations.Count == 0)
    {
        return new Dictionary<ulong, int>();
    }

    var payloadCount = 1 << Math.Clamp(contestant.Profile.PayloadBitsPerEvent, 0, 8);
    if (payloadCount <= 1)
    {
        return observations.ToDictionary(observation => observation.EventIndex, _ => 0);
    }

    if (trustObservationPayload)
    {
        return observations.ToDictionary(
            observation => observation.EventIndex,
            observation => Math.Clamp(observation.PayloadSymbol, 0, payloadCount - 1));
    }

    var motifSamples = contestant.RenderEventMonoFloat(0, sampleRate).Length;
    var decoded = new Dictionary<ulong, int>();
    foreach (var observation in observations)
    {
        var offset = (int)Math.Round(observation.SampleOffset);
        if (offset < 0 || offset + motifSamples > samples.Length)
        {
            continue;
        }

        var bestPayload = 0;
        var bestScore = double.NegativeInfinity;
        for (var payload = 0; payload < payloadCount; payload++)
        {
            var template = contestant.RenderEventMonoFloat(observation.EventIndex, sampleRate, payload);
            var score = ScoreContestantOffset(samples, template, offset, motifSamples);
            if (score > bestScore)
            {
                bestScore = score;
                bestPayload = payload;
            }
        }

        decoded[observation.EventIndex] = bestPayload;
    }

    return decoded;
}

static CepstralWordIndex BuildCepstralWordIndex(int sampleRate, CepstralDecoderOptions options)
{
    var templates = new List<CepstralWordTemplate>(MimirBioacousticTimeline.SymbolCount);
    for (ulong eventIndex = 0; eventIndex < MimirBioacousticTimeline.SymbolCount; eventIndex++)
    {
        var samples = MimirBioacousticTimeline.Default.RenderEventMonoFloat(eventIndex, sampleRate);
        foreach (var setting in options.TemplateAugmentations)
        {
            var templateSamples = setting.BlurPasses == 0 && setting.WarpFrames == 0.0 && setting.WarpCoefficients == 0.0
                ? samples
                : RoundTripThroughDegradedCepstrum(samples, sampleRate, setting, out _);
            templates.Add(new CepstralWordTemplate(eventIndex, 0, CepstralFingerprintWithOptions(templateSamples, sampleRate, options)));
        }
    }

    return BuildCepstralWordIndexFromTemplates(templates, options);
}

static CepstralWordIndex BuildCepstralContestantWordIndex(
    int sampleRate,
    CepstralDecoderOptions options,
    MimirBioacousticContestantRenderer contestant)
{
    var templates = new List<CepstralWordTemplate>(MimirBioacousticTimeline.SymbolCount);
    for (ulong eventIndex = 0; eventIndex < MimirBioacousticTimeline.SymbolCount; eventIndex++)
    {
        var samples = contestant.RenderEventMonoFloat(eventIndex, sampleRate);
        foreach (var setting in options.TemplateAugmentations)
        {
            var templateSamples = setting.BlurPasses == 0 && setting.WarpFrames == 0.0 && setting.WarpCoefficients == 0.0
                ? samples
                : RoundTripThroughDegradedCepstrum(samples, sampleRate, setting, out _);
            templates.Add(new CepstralWordTemplate(eventIndex, contestant.PayloadSymbolForEvent(eventIndex), CepstralFingerprintWithOptions(templateSamples, sampleRate, options)));
        }
    }

    return BuildCepstralWordIndexFromTemplates(templates, options);
}

static CepstralWordIndex BuildCepstralWordIndexFromTemplates(
    List<CepstralWordTemplate> templates,
    CepstralDecoderOptions options)
{
    var buckets = new Dictionary<(int Table, int Hash), List<int>>();
    for (var index = 0; index < templates.Count; index++)
    {
        for (var table = 0; table < options.TableCount; table++)
        {
            var key = (table, ProjectionHash(templates[index].Feature, table, options.HashBits));
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

static double[] CepstralFingerprintWithOptions(ReadOnlySpan<float> samples, int sampleRate, CepstralDecoderOptions options)
{
    var window = HannWindow(options.FftSize);
    var melFilters = BuildMelFilterBank(options.MelBins, options.FftSize, sampleRate, options.MinFrequencyHz, options.MaxFrequencyHz);
    var melNormalizer = melFilters.Select(filter => Math.Max(1.0e-12, filter.Sum())).ToArray();
    var frameCount = Math.Max(1, 1 + Math.Max(0, samples.Length - options.FftSize) / options.HopSize);
    var mean = new double[options.CepstralCoefficients];
    var delta = new double[options.CepstralCoefficients];
    double[]? previous = null;
    for (var frame = 0; frame < frameCount; frame++)
    {
        var offset = frame * options.HopSize;
        var spectrum = new Complex[options.FftSize];
        for (var index = 0; index < options.FftSize; index++)
        {
            var sampleIndex = offset + index;
            var sample = sampleIndex < samples.Length ? samples[sampleIndex] : 0.0f;
            spectrum[index] = new Complex(sample * window[index], 0.0);
        }

        FastFourierTransform(spectrum, inverse: false);
        var cepstrum = Dct(SpectrumToLogMel(spectrum, melFilters, melNormalizer), options.CepstralCoefficients);
        for (var coefficient = 0; coefficient < options.CepstralCoefficients; coefficient++)
        {
            mean[coefficient] += cepstrum[coefficient];
            if (previous != null)
            {
                delta[coefficient] += Math.Abs(cepstrum[coefficient] - previous[coefficient]);
            }
        }

        previous = cepstrum;
    }

    var output = new double[options.CepstralCoefficients * 2];
    for (var coefficient = 0; coefficient < options.CepstralCoefficients; coefficient++)
    {
        output[coefficient] = coefficient == 0 ? 0.0 : mean[coefficient] / frameCount;
        output[coefficient + options.CepstralCoefficients] = delta[coefficient] / Math.Max(1, frameCount - 1);
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

static MimirBioacousticClockHypothesis? FitBioacousticClockHypothesis(
    IReadOnlyList<CepstralWordObservation> observations,
    MimirBioacousticTimeline timeline,
    int sampleRate) =>
    new MimirBioacousticClockSolver().Fit(
        observations
            .Select(observation => new MimirBioacousticWordObservation(
                observation.EventIndex,
                observation.SampleOffset,
                observation.Confidence))
            .ToArray(),
        timeline,
        sampleRate,
        observations.Count);

static MimirBioacousticClockHypothesis? FitContestantClockHypothesis(
    IReadOnlyList<CepstralWordObservation> observations,
    MimirBioacousticContestantRenderer contestant,
    int sampleRate,
    int expectedEventCount)
{
    var anchors = observations
        .Select(observation => new MimirBioacousticClockAnchor(
            observation.EventIndex,
            contestant.EventStartSeconds(observation.EventIndex),
            observation.SampleOffset,
            Math.Clamp(observation.Confidence, 0.001, 1.0)))
        .OrderByDescending(anchor => anchor.Confidence)
        .Take(24)
        .ToArray();
    if (anchors.Length == 0)
    {
        return null;
    }

    if (anchors.Length >= 3)
    {
        var delayHypothesis = WeightedMedian(anchors
            .Select(anchor => (
                Value: anchor.SampleOffset - anchor.TimelineSeconds * sampleRate,
                Weight: Math.Clamp(anchor.Confidence, 0.001, 1.0)))
            .ToArray());
        var residuals = anchors
            .Select(anchor => Math.Abs(anchor.SampleOffset - (delayHypothesis + anchor.TimelineSeconds * sampleRate)))
            .Order()
            .ToArray();
        var medianResidual = residuals[residuals.Length / 2];
        var inlierRadius = Math.Clamp(
            medianResidual * 3.0 + sampleRate * 0.00025,
            sampleRate * 0.003,
            sampleRate * 0.006);
        var inliers = anchors
            .Where(anchor => Math.Abs(anchor.SampleOffset - (delayHypothesis + anchor.TimelineSeconds * sampleRate)) <= inlierRadius)
            .ToArray();
        if (inliers.Length >= 2)
        {
            anchors = inliers;
        }
    }

    var totalWeight = anchors.Sum(anchor => Math.Max(1.0e-6, anchor.Confidence));
    var meanTimeline = anchors.Sum(anchor => anchor.TimelineSeconds * Math.Max(1.0e-6, anchor.Confidence)) / totalWeight;
    var meanSample = anchors.Sum(anchor => anchor.SampleOffset * Math.Max(1.0e-6, anchor.Confidence)) / totalWeight;
    var covariance = 0.0;
    var variance = 0.0;
    foreach (var anchor in anchors)
    {
        var weight = Math.Max(1.0e-6, anchor.Confidence);
        var dt = anchor.TimelineSeconds - meanTimeline;
        covariance += weight * dt * (anchor.SampleOffset - meanSample);
        variance += weight * dt * dt;
    }

    var effectiveRate = anchors.Length >= 3 && variance > 1.0e-12 ? covariance / variance : sampleRate;
    if (!double.IsFinite(effectiveRate) || effectiveRate < sampleRate * 0.98 || effectiveRate > sampleRate * 1.02)
    {
        effectiveRate = sampleRate;
    }

    var sourceOffset = meanSample - effectiveRate * meanTimeline;
    var residual = anchors.Sum(anchor =>
    {
        var predicted = sourceOffset + anchor.TimelineSeconds * effectiveRate;
        return Math.Abs(anchor.SampleOffset - predicted) * Math.Max(1.0e-6, anchor.Confidence);
    }) / totalWeight;
    var residualConfidence = 1.0 / (1.0 + residual / Math.Max(1.0, sampleRate * 0.001));
    var countConfidence = Math.Clamp(anchors.Length / 5.0, 0.0, 1.0);
    var anchorConfidence = Math.Clamp(anchors.Average(anchor => anchor.Confidence), 0.0, 1.0);
    var confidence = residualConfidence * 0.35 + countConfidence * 0.45 + anchorConfidence * 0.20;
    var coverage = expectedEventCount <= 0 ? 0.0 : anchors.Length / (double)expectedEventCount;
    var score = confidence + Math.Min(anchors.Length, 8) * 0.15 - residual / Math.Max(1.0, sampleRate * 0.010);
    return new MimirBioacousticClockHypothesis(
        sourceOffset,
        effectiveRate,
        anchors.Length,
        coverage,
        residual,
        confidence,
        score,
        anchors);
}

static double WeightedMedian(IReadOnlyList<(double Value, double Weight)> values)
{
    if (values.Count == 0)
    {
        return 0.0;
    }

    var ordered = values
        .OrderBy(value => value.Value)
        .ToArray();
    var half = ordered.Sum(value => Math.Max(0.0, value.Weight)) * 0.5;
    var cumulative = 0.0;
    foreach (var value in ordered)
    {
        cumulative += Math.Max(0.0, value.Weight);
        if (cumulative >= half)
        {
            return value.Value;
        }
    }

    return ordered[^1].Value;
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

static float[] LogMelSpectralFluxTrace(
    float[] samples,
    int sampleRate,
    CepstralDecoderOptions options,
    int outputHopSamples)
{
    if (samples.Length < options.FftSize)
    {
        return [];
    }

    var analysisHop = Math.Max(1, Math.Min(options.HopSize, outputHopSamples));
    var frameCount = 1 + Math.Max(0, (samples.Length - options.FftSize) / analysisHop);
    var outputLength = 1 + Math.Max(0, (samples.Length - options.FftSize) / outputHopSamples);
    var output = new float[outputLength];
    var window = HannWindow(options.FftSize);
    var melFilters = BuildMelFilterBank(options.MelBins, options.FftSize, sampleRate, options.MinFrequencyHz, options.MaxFrequencyHz);
    var melNormalizer = melFilters.Select(filter => Math.Max(1.0e-12, filter.Sum())).ToArray();
    double[]? previous = null;
    for (var frame = 0; frame < frameCount; frame++)
    {
        var offset = frame * analysisHop;
        var spectrum = new Complex[options.FftSize];
        for (var index = 0; index < options.FftSize; index++)
        {
            var sampleIndex = offset + index;
            var sample = sampleIndex < samples.Length ? samples[sampleIndex] : 0.0f;
            spectrum[index] = new Complex(sample * window[index], 0.0);
        }

        FastFourierTransform(spectrum, inverse: false);
        var logMel = SpectrumToLogMel(spectrum, melFilters, melNormalizer);
        if (previous != null)
        {
            var sum = 0.0;
            for (var mel = 0; mel < logMel.Length; mel++)
            {
                var delta = Math.Max(0.0, logMel[mel] - previous[mel]);
                sum += delta * delta;
            }

            var outputFrame = Math.Clamp(offset / outputHopSamples, 0, output.Length - 1);
            output[outputFrame] = Math.Max(output[outputFrame], (float)Math.Sqrt(sum / logMel.Length));
        }

        previous = logMel;
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

static float[] ReconstructDetectedBioacousticSong(IReadOnlyList<CepstralWordObservation> observations, int sampleCount, int sampleRate)
{
    var output = new float[sampleCount];
    foreach (var observation in observations)
    {
        var eventSamples = MimirBioacousticTimeline.Default.RenderEventMonoFloat(observation.EventIndex, sampleRate);
        var offset = (int)Math.Round(observation.SampleOffset);
        for (var index = 0; index < eventSamples.Length; index++)
        {
            var target = offset + index;
            if (target >= 0 && target < output.Length)
            {
                output[target] += eventSamples[index] * (float)Math.Clamp(observation.Confidence, 0.0, 1.0);
            }
        }
    }

    return output;
}

static async Task WriteBioacousticTrainingCacheAsync(string cachePath, IReadOnlyList<BioacousticTrainingResult> results)
{
    using var cache = await CultCacheMessagePack.OpenAsync(cachePath, new CultCacheOpenOptions
    {
        PullOnOpen = File.Exists(cachePath)
    }).ConfigureAwait(false);
    foreach (var result in results)
    {
        await cache.UpsertAsync(
            result,
            new CultRecordHandle<BioacousticTrainingResult>(new CultRecordKey($"bioacoustic-training-result:{result.ResultId}")))
            .ConfigureAwait(false);
    }

    await cache.FlushAsync().ConfigureAwait(false);
}

static void WriteWave(string path, IReadOnlyList<float> samples, int sampleRate)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    var dataBytes = samples.Count * sizeof(short);
    writer.Write("RIFF"u8);
    writer.Write(36 + dataBytes);
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(sampleRate);
    writer.Write(sampleRate * sizeof(short));
    writer.Write((short)sizeof(short));
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(dataBytes);
    for (var index = 0; index < samples.Count; index++)
    {
        writer.Write((short)Math.Clamp(Math.Round(samples[index] * short.MaxValue), short.MinValue, short.MaxValue));
    }
}

static string Sha256File(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static string RelativePath(string root, string path)
{
    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
    return relative == "." ? "" : relative;
}

internal sealed class DisposableConfiguration : IDisposable
{
    private readonly List<IMimirStreamSource> ownedSources;

    public DisposableConfiguration(MimirRuntimeConfiguration value)
    {
        Value = value;
        ownedSources = value.CreateSources().ToList();
    }

    public MimirRuntimeConfiguration Value { get; }

    public IReadOnlyList<IMimirStreamSource> Sources => ownedSources;

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

internal readonly record struct ChannelSignalStats(double Rms, double Peak, double Mean);

internal sealed record CepstralDegradationSetting(
    string Name,
    double WarpFrames,
    double WarpCoefficients,
    int BlurPasses,
    CepstralDegradationDomain Domain = CepstralDegradationDomain.Cepstrum)
{
    public static CepstralDegradationSetting FromRuntime(MimirCepstralDegradationProfile profile) =>
        new(profile.Id, profile.WarpFrames, profile.WarpCoefficients, profile.BlurPasses, CepstralDegradationDomain.Cepstrum);
}

internal enum CepstralDegradationDomain
{
    Cepstrum,
    LogMel
}

internal enum CepstralProposalMode
{
    Energy,
    LogMelFlux,
    StreamingPacketRazor
}

internal sealed record CepstralDecoderOptions(
    int FftSize,
    int HopSize,
    int MelBins,
    int CepstralCoefficients,
    double MinFrequencyHz,
    double MaxFrequencyHz,
    int TableCount,
    int HashBits,
    int NearHashRadius,
    double DenseStepSeconds,
    double ProposalBudgetMultiplier,
    CepstralProposalMode ProposalMode,
    IReadOnlyList<CepstralDegradationSetting> TemplateAugmentations)
{
    public static CepstralDecoderOptions Default { get; } =
        FromRuntime(MimirBioacousticDecoderConfiguration.BaselineMfccIndex);

    public static CepstralDecoderOptions FromRuntime(MimirBioacousticDecoderConfiguration configuration) =>
        new(
            configuration.FftSize,
            configuration.HopSize,
            configuration.MelBins,
            configuration.CepstralCoefficients,
            configuration.MinFrequencyHz,
            configuration.MaxFrequencyHz,
            configuration.ProjectionTableCount,
            configuration.ProjectionHashBits,
            configuration.NearHashRadius,
            configuration.DenseStepSeconds,
            configuration.ProposalBudgetMultiplier,
            CepstralProposalMode.Energy,
            configuration.TemplateAugmentations
                .Select(CepstralDegradationSetting.FromRuntime)
                .ToArray());
}

internal sealed record BioacousticTrainingHypothesis(
    string Id,
    string Notes,
    CepstralDecoderOptions Decoder);

internal sealed record CepstralRoundTripAnalysis(
    int FrameCount,
    int MelBins,
    int CepstralCoefficients,
    double RmsRatio);

internal sealed record CepstralWordTemplate(
    ulong EventIndex,
    int PayloadSymbol,
    IReadOnlyList<double> Feature);

internal sealed record CepstralWordObservation(
    ulong EventIndex,
    double SampleOffset,
    double Confidence,
    int PayloadSymbol,
    double ShapeAccuracy);

internal sealed record CepstralWordIndex(
    IReadOnlyList<CepstralWordTemplate> Templates,
    IReadOnlyDictionary<(int Table, int Hash), List<int>> Buckets);

[CultDocument("mimir.bioacoustic_training_result", "mimir.bioacoustic_training_result.v1")]
[MessagePackObject]
public sealed record BioacousticTrainingResult(
    [property: Key(0)]
    [property: CultName]
    string ResultId,
    [property: Key(1)] string RunId,
    [property: Key(2)] string StartedAtUtc,
    [property: Key(3)] string Toolchain,
    [property: Key(4)] string HypothesisId,
    [property: Key(5)] string HypothesisNotes,
    [property: Key(6)] string DegradationName,
    [property: Key(7)] int SampleRate,
    [property: Key(8)] double Seconds,
    [property: Key(9)] int ExpectedEvents,
    [property: Key(10)] int ObservationCount,
    [property: Key(11)] int CorrectAnchors,
    [property: Key(12)] double Precision,
    [property: Key(13)] double Recall,
    [property: Key(14)] double Confidence,
    [property: Key(15)] double TimingMeanAbsoluteErrorSamples,
    [property: Key(16)] double DecodeMilliseconds,
    [property: Key(17)] double RealtimeFactor,
    [property: Key(18)] double IdentityScore,
    [property: Key(19)] double TimingScore,
    [property: Key(20)] double SpeedScore,
    [property: Key(21)] double TotalScore,
    [property: Key(22)] int RoundTripMelBins,
    [property: Key(23)] int RoundTripCepstralCoefficients,
    [property: Key(24)] int RoundTripFrameCount,
    [property: Key(25)] double RoundTripRmsRatio,
    [property: Key(26)] BioacousticClockHypothesisSnapshot ClockHypothesis,
    [property: Key(27)] BioacousticTrainingDecoderSnapshot Decoder,
    [property: Key(28)] BioacousticTrainingArtifact[] Artifacts,
    [property: Key(29)] BioacousticTrainingObservation[] Observations);

[MessagePackObject]
public sealed record BioacousticClockHypothesisSnapshot(
    [property: Key(0)] int AnchorCount,
    [property: Key(1)] double SourceOffsetSamples,
    [property: Key(2)] double EffectiveSampleRate,
    [property: Key(3)] double MeanAbsoluteErrorSamples,
    [property: Key(4)] double Confidence,
    [property: Key(5)] double Score,
    [property: Key(6)] double AnchorCoverage);

[MessagePackObject]
public sealed record BioacousticTrainingDecoderSnapshot(
    [property: Key(0)] int FftSize,
    [property: Key(1)] int HopSize,
    [property: Key(2)] int MelBins,
    [property: Key(3)] int CepstralCoefficients,
    [property: Key(4)] int TableCount,
    [property: Key(5)] int HashBits,
    [property: Key(6)] int NearHashRadius,
    [property: Key(7)] double DenseStepSeconds,
    [property: Key(8)] double ProposalBudgetMultiplier,
    [property: Key(9)] string[] TemplateAugmentations,
    [property: Key(10)] string ProposalMode);

[MessagePackObject]
public sealed record BioacousticTrainingArtifact(
    [property: Key(0)] string Kind,
    [property: Key(1)] string Uri,
    [property: Key(2)] string ContentHash);

[MessagePackObject]
public sealed record BioacousticTrainingObservation(
    [property: Key(0)] ulong EventIndex,
    [property: Key(1)] double SampleOffset,
    [property: Key(2)] double Confidence);

public sealed record BioacousticContestantResult(
    string ResultId,
    string RunId,
    string SongId,
    string SongKind,
    string DecoderId,
    string DegradationName,
    int SampleRate,
    double Seconds,
    int ExpectedEvents,
    int ObservedEvents,
    int CorrectEvents,
    double Precision,
    double Recall,
    double Confidence,
    double TimingAccuracy,
    double FrequencyAccuracy,
    double PayloadAccuracy,
    double Convergence,
    double RealtimeFactor,
    double PayloadBitrate,
    double LanguageScore,
    double ContestScore,
    double DecodeMilliseconds,
    int RoundTripMelBins,
    int RoundTripCepstralCoefficients,
    BioacousticClockHypothesisSnapshot ClockHypothesis,
    BioacousticContestantObservation[] Observations,
    string BeautyNotes);

public sealed record BioacousticContestantObservation(
    ulong EventIndex,
    int PayloadSymbol,
    int ClassifiedPayloadSymbol,
    double SampleOffset,
    double Confidence,
    double ShapeAccuracy);

public sealed record BioacousticPhysicalCalibrationModel(
    string Schema,
    DateTimeOffset CreatedUtc,
    string InputPath,
    string SongId,
    int SampleRate,
    int Channels,
    string ReferenceSourceId,
    double CaptureSeconds,
    int SearchRadiusSamples,
    double ScheduleOffsetSamples,
    double DecodeMilliseconds,
    double RealtimeFactor,
    BioacousticPhysicalChannelCalibration[] ChannelsModel,
    BioacousticPhysicalPathCalibration[] Paths);

public sealed record BioacousticPhysicalChannelCalibration(
    string SourceId,
    int ExpectedEvents,
    int DetectedEvents,
    double PayloadAccuracy,
    double Confidence,
    int Polarity,
    double ScheduleOffsetSamples,
    double SourceOffsetSamples,
    double SourceOffsetMicroseconds,
    double MeanAbsoluteErrorSamples,
    double MeanAbsoluteErrorMicroseconds,
    int AnchorCount,
    double MeanAnchorResidualSamples,
    int SampleCount,
    double Rms,
    double Peak,
    BioacousticPhysicalBandCalibration[] Bands,
    BioacousticPhysicalEventCalibration[] Events);

public sealed record BioacousticPhysicalPathCalibration(
    string ReferenceSourceId,
    string SourceId,
    double DelaySamples,
    double DelayMicroseconds,
    double SyncMeanAbsoluteErrorSamples,
    double SyncMeanAbsoluteErrorMicroseconds,
    double Confidence,
    double RelativeGain,
    int RelativePolarity,
    BioacousticPhysicalBandCalibration[] ResponseNormalizationBands,
    int MatchedAnchors,
    double WaveformConfidence);

public sealed record BioacousticPhysicalEventCalibration(
    ulong EventIndex,
    int ExpectedPayloadSymbol,
    int ObservedPayloadSymbol,
    double SampleOffset,
    double DelaySamples,
    double Confidence,
    int Polarity,
    double NormalizedResidual,
    BioacousticPhysicalBandCalibration[] Bands,
    BioacousticPhysicalAnchorCalibration[] Anchors);

public sealed record BioacousticPhysicalAnchorCalibration(
    string Kind,
    int SyllableIndex,
    double ExpectedStartSeconds,
    double DurationSeconds,
    double CenterHz,
    double SampleOffset,
    double TimingResidualSamples,
    double Confidence,
    double Gain);

public sealed record BioacousticPhysicalBandCalibration(
    double CenterHz,
    double ObservedEnergy,
    double ExpectedEnergy,
    double Gain,
    bool Usable);

public sealed record ComplexContourReplayCase(
    string Id,
    string InputPath,
    int CandidateChannel,
    double PredictedDelaySamples);

public sealed record ComplexContourReplayPanelReceipt(
    string Schema,
    DateTimeOffset CreatedUtc,
    ComplexContourReplayResult[] Cases);

public sealed record ComplexContourReplayResult(
    string CaseId,
    string InputPath,
    int SampleRate,
    int ReferenceChannel,
    int CandidateChannel,
    double PredictedDelaySamples,
    double DelaySamples,
    double DelayMicroseconds,
    double PredictionErrorSamples,
    double PredictionErrorMicroseconds,
    double Confidence,
    int DirectHits,
    double MeanAbsoluteErrorSamples,
    double MeanAbsolutePhaseErrorRadians,
    int ReferenceHits,
    int CandidateHits,
    MimirAcousticReflectionTap[] ReflectionTaps,
    double FirstReflectionSamples,
    ComplexContourBandResidual[] StrongestBands,
    ComplexContourBandResidual[] BandResiduals);

public sealed record ComplexContourBandResidual(
    double CenterHz,
    double Weight,
    double DelayResidualSamples,
    double PhaseResidualRadians);

public sealed record ComplexContourChannelModelDocument(
    string Schema,
    DateTimeOffset CreatedUtc,
    string SourceReceiptPath,
    ComplexContourPathChannelModel[] Paths);

public sealed record ComplexContourPathChannelModel(
    string PathId,
    int SampleRate,
    int ReferenceChannel,
    int CandidateChannel,
    string[] CaseIds,
    ComplexContourBandCorrection[] Corrections,
    ComplexContourReflectionCorrection[] ReflectionTaps,
    int UsableBandCount,
    double Reliability,
    double DelaySpreadSamples)
{
    public MimirDirectPathChannelModel ToRuntimeModel() =>
        new(Corrections
            .Where(correction => correction.Usable)
            .Select(correction => new MimirDirectPathBandCorrection(
                correction.CenterHz,
                correction.DelayCorrectionSamples,
                correction.PhaseCorrectionRadians,
                correction.Weight * correction.Reliability))
            .ToArray());
}

public sealed record ComplexContourBandCorrection(
    double CenterHz,
    double DelayCorrectionSamples,
    double PhaseCorrectionRadians,
    double Weight,
    int ObservationCount,
    double DelayStdDevSamples,
    double Reliability,
    bool Usable);

sealed record MoveTrackingSmokeContext(MimirSynchronizationHub Hub, int Consumed) : IDisposable
{
    public void Dispose() => Hub.Dispose();
}

public sealed record ComplexContourReflectionCorrection(
    double RelativeDelaySamples,
    int ObservationCount,
    double MeanRelativeDelaySamples);

public sealed record ComplexContourChannelModelEvaluationDocument(
    string Schema,
    DateTimeOffset CreatedUtc,
    string ReplayReceiptPath,
    string ChannelModelPath,
    int CasesEvaluated,
    int CasesImproved,
    double MeanDeltaAbsolutePredictionErrorMicroseconds,
    double MeanDeltaMeanAbsoluteErrorSamples,
    ComplexContourChannelModelEvaluationCase[] Cases);

public sealed record ComplexContourChannelModelEvaluationCase(
    string CaseId,
    string PathId,
    double BaselineDelayMicroseconds,
    double ModeledDelayMicroseconds,
    double BaselineAbsolutePredictionErrorMicroseconds,
    double ModeledAbsolutePredictionErrorMicroseconds,
    double BaselineMeanAbsoluteErrorSamples,
    double ModeledMeanAbsoluteErrorSamples,
    double BaselineMeanAbsolutePhaseErrorRadians,
    double ModeledMeanAbsolutePhaseErrorRadians,
    double BaselineConfidence,
    double ModeledConfidence,
    double DeltaAbsolutePredictionErrorMicroseconds,
    double DeltaMeanAbsoluteErrorSamples,
    double DeltaMeanAbsolutePhaseErrorRadians);
