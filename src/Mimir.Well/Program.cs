using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mimir.Runtime.Synchronization;

var options = MimirWellOptions.Parse(args);
var runtimeConfig = MimirRuntimeConfiguration.Load();
using var hub = new MimirSynchronizationHub(runtimeConfig.Settings);
var sourceErrors = new List<object>();
foreach (var factory in runtimeConfig.SourceFactories)
{
    try
    {
        var source = factory.Create();
        if (source == null)
        {
            sourceErrors.Add(new { factory.Descriptor.SourceId, status = "not-created" });
            continue;
        }

        hub.AddSource(source);
    }
    catch (Exception ex)
    {
        sourceErrors.Add(new
        {
            factory.Descriptor.SourceId,
            status = "create-error",
            errorType = ex.GetType().Name,
            ex.Message,
        });
    }
}

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

await using var publisher = new MimirWellPublisher(options.PublishUrl);
await publisher.ConnectAsync(stopping.Token).ConfigureAwait(false);

var sequence = 0L;
var captureSequence = 0L;
var startedAt = DateTimeOffset.UtcNow;
var nextPublish = DateTimeOffset.MinValue;
var nextCapture = DateTimeOffset.MinValue;
var nextSync = DateTimeOffset.MinValue;
var nextVisualCalibration = DateTimeOffset.MinValue;
var nextHeartbeat = DateTimeOffset.MinValue;
var frameDegradedCount = 0L;
var nextFrameDegradedLog = DateTimeOffset.MinValue;
var streamTelemetry = new MimirWellStreamTelemetry();
var latencyController = new MimirWellLatencyController(options);
var featureSignals = new MimirWellFeatureSignalBank(options);
var sourceHeartbeat = new MimirWellSourceHeartbeat(options, runtimeConfig.SourceFactories, sourceErrors);
var presentation = new MimirPresentationControlState();
var exposureController = new MimirCameraExposureController(new MimirCameraExposureControlOptions(
    options.VisualCalibrationEnabled,
    options.VisualExpectedLedCount,
    options.VisualMinimumLuma,
    options.VisualSettingSeconds,
    options.VisualResweepSeconds));
var inlineComplexContourRuntimeEnabled = MimirWellEnvironment.IsTruthy(Environment.GetEnvironmentVariable("MIMIR_COMPLEX_CONTOUR_RUNTIME_INLINE"));
var inlineComplexContourRuntimeSuppressionLogged = false;
Console.Error.WriteLine($"Mimir Well publishing to {options.PublishUrl}");
Console.Error.WriteLine($"Mimir Well sources={runtimeConfig.SourceFactories.Count} buffers={hub.Buffers.Buffers.Count}");

while (!stopping.IsCancellationRequested)
{
    var ingestedSamplesThisPoll = new List<MimirStreamSample>();
    var pollStopwatch = Stopwatch.StartNew();
    var consumedSamples = hub.PollSources(
        options.MaxSamplesPerSource,
        sample => ingestedSamplesThisPoll.Add(sample));
    pollStopwatch.Stop();
    streamTelemetry.ObservePoll(consumedSamples, pollStopwatch.Elapsed);
    if (options.StreamFramesEnabled)
    {
        foreach (var streamFrame in ingestedSamplesThisPoll.Select(sample =>
                     MimirWellCapturePage.BuildIngestedStreamFrame(options, sample, startedAt)))
        {
            streamTelemetry.ObservePublish(
                await publisher.PublishAsync(streamFrame, "mimir.cultmesh_stream_frame.v1", stopping.Token).ConfigureAwait(false));
        }
    }

    var now = DateTimeOffset.UtcNow;
    if (now >= nextSync)
    {
        if (!string.IsNullOrWhiteSpace(runtimeConfig.Settings.Audio.ReferenceSourceId))
        {
            hub.AnalyzeAudioSynchronizationStep(
                runtimeConfig.Settings.Audio.ReferenceSourceId,
                runtimeConfig.Settings.Audio.Mode,
                options.SyncCandidatesPerStep);
            if (hub.ComplexContourRuntimeEnabled && inlineComplexContourRuntimeEnabled)
            {
                hub.AnalyzeComplexContourSynchronizationStep(
                    runtimeConfig.Settings.Audio.ReferenceSourceId,
                    (now - startedAt).TotalSeconds,
                    options.SyncCandidatesPerStep);
            }
            else if (hub.ComplexContourRuntimeEnabled && !inlineComplexContourRuntimeSuppressionLogged)
            {
                inlineComplexContourRuntimeSuppressionLogged = true;
                Console.Error.WriteLine("mimir-well complex-contour inline=false reason=well-loop-budget set MIMIR_COMPLEX_CONTOUR_RUNTIME_INLINE=1 to run the heavy matched filter inline");
            }
        }

        hub.UpdateBioacousticProbeSchedule(NowNs());
        nextSync = now + TimeSpan.FromMilliseconds(options.SyncIntervalMs);
    }

    if (now >= nextVisualCalibration)
    {
        exposureController.Update(now, hub.Buffers.Buffers, hub.CameraExposureGainActuators);
        nextVisualCalibration = now + TimeSpan.FromMilliseconds(options.VisualCalibrationIntervalMs);
    }

    if (now >= nextHeartbeat)
    {
        var heartbeat = sourceHeartbeat.Update(now, hub);
        streamTelemetry.ObservePublish(
            await publisher.PublishAsync(heartbeat, "mimir.well_heartbeat.v1", stopping.Token).ConfigureAwait(false));
        nextHeartbeat = now + TimeSpan.FromMilliseconds(options.HeartbeatIntervalMs);
    }

    if (now >= nextPublish)
    {
        presentation.SyncFromBuffers(hub.Buffers.Buffers);
        var latencyDecision = latencyController.Decide(hub, now);
        var frameResult = BuildFrameOrEmpty(hub, latencyDecision.PresentationDelay);
        latencyController.ObserveFrame(frameResult);
        var featureSignalFrame = featureSignals.Update(hub.Buffers.Buffers, frameResult.Frame);
        if (!string.IsNullOrWhiteSpace(frameResult.DegradedReason))
        {
            frameDegradedCount++;
            if (now >= nextFrameDegradedLog)
            {
                Console.Error.WriteLine(
                    $"mimir-well synchronized-frame degraded count={frameDegradedCount} reason={frameResult.DegradedReason}");
                nextFrameDegradedLog = now + TimeSpan.FromSeconds(5);
            }
        }

        var frame = frameResult.Frame;
        var snapshot = MimirWellSnapshot.Build(
            options,
            runtimeConfig,
            hub,
            presentation,
            frameResult,
            latencyDecision,
            featureSignalFrame,
            exposureController.Statuses,
            sourceErrors,
            streamTelemetry.Snapshot(now, subscribersAreExternal: true, publisher.SenderPressureSnapshot()),
            ++sequence,
            startedAt);
        streamTelemetry.ObservePublish(
            await publisher.PublishAsync(snapshot, "mimir.well_snapshot.v1", stopping.Token).ConfigureAwait(false));
        if (options.CapturePagesEnabled && now >= nextCapture)
        {
            var nextStreamSequence = captureSequence + 1;
            if (options.StreamFramesEnabled)
            {
                foreach (var streamFrame in MimirWellCapturePage.BuildStreamFrames(
                             options,
                             frameResult,
                             hub.Buffers.Buffers,
                             nextStreamSequence,
                             startedAt))
                {
                    streamTelemetry.ObservePublish(
                        await publisher.PublishAsync(streamFrame, "mimir.cultmesh_stream_frame.v1", stopping.Token).ConfigureAwait(false));
                }
            }

            var capturePage = MimirWellCapturePage.Build(
                options,
                presentation,
                frameResult,
                hub.Buffers.Buffers,
                ++captureSequence,
                startedAt);
            streamTelemetry.ObservePublish(
                await publisher.PublishAsync(capturePage, "mimir.well_capture_page.v1", stopping.Token).ConfigureAwait(false));
            nextCapture = now + TimeSpan.FromMilliseconds(options.CaptureIntervalMs);
        }

        if (sequence % Math.Max(1, options.MeterEvery) == 0)
        {
            Console.Error.WriteLine(
                $"mimir-well sequence={sequence} ingested={hub.IngestedSamples} ready={frame.Slices.Count(slice => slice.Status == MimirSynchronizedSliceStatus.Ready)}/{frame.Slices.Count}");
        }

        nextPublish = now + TimeSpan.FromMilliseconds(options.PublishIntervalMs);
    }

    try
    {
        await Task.Delay(Math.Max(1, options.PollMs), stopping.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

Console.Error.WriteLine($"Mimir Well complete sequence={sequence} ingested={hub.IngestedSamples}");

static long NowNs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

static MimirWellFrameBuildResult BuildFrameOrEmpty(MimirSynchronizationHub hub, TimeSpan presentationDelay)
{
    try
    {
        return new MimirWellFrameBuildResult(
            hub.BuildSynchronizedBufferFrame(presentationDelay),
            "",
            "");
    }
    catch (ArgumentException ex)
    {
        return new MimirWellFrameBuildResult(
            new MimirSynchronizedBufferFrame(0, 0, 0, presentationDelay, []),
            ex.GetType().Name,
            ex.Message);
    }
    catch (OverflowException ex)
    {
        return new MimirWellFrameBuildResult(
            new MimirSynchronizedBufferFrame(0, 0, 0, presentationDelay, []),
            ex.GetType().Name,
            ex.Message);
    }
}

internal sealed record MimirWellFrameBuildResult(
    MimirSynchronizedBufferFrame Frame,
    string DegradedKind,
    string DegradedReason);

internal sealed record MimirWellLatencyDecision(
    TimeSpan PresentationDelay,
    TimeSpan CeilingDelay,
    TimeSpan FloorDelay,
    TimeSpan RetainedOverlap,
    TimeSpan EdgeSkew,
    double ReadinessConfidence,
    double SyncConfidence,
    int ActiveBufferCount,
    int ReadySliceCount,
    int TotalSliceCount,
    string Reason);

internal sealed class MimirWellLatencyController
{
    private readonly MimirWellOptions options;
    private readonly TimeSpan floor;
    private TimeSpan current;
    private int completeStreak;
    private int degradedStreak;
    private MimirWellFrameBuildResult? lastFrame;

    public MimirWellLatencyController(MimirWellOptions options)
    {
        this.options = options;
        floor = TimeSpan.FromMilliseconds(Math.Clamp(options.MinPresentationDelayMs, 0, options.PresentationDelayMs));
        current = TimeSpan.FromMilliseconds(Math.Clamp(
            options.TargetPresentationDelayMs,
            options.MinPresentationDelayMs,
            options.PresentationDelayMs));
    }

    public MimirWellLatencyDecision Decide(MimirSynchronizationHub hub, DateTimeOffset now)
    {
        var active = hub.Buffers.Buffers
            .Where(static buffer => buffer.Latest.HasValue)
            .ToArray();
        if (active.Length == 0)
        {
            current = TimeSpan.FromMilliseconds(options.PresentationDelayMs);
            return Decision(TimeSpan.Zero, TimeSpan.Zero, 0.0, 0.0, 0, 0, 0, "waiting-for-sources");
        }

        var latestEdges = active
            .Select(static buffer => buffer.Latest?.TimestampNs ?? 0L)
            .Where(static value => value > 0)
            .ToArray();
        var starts = active
            .Select(static buffer => buffer.OldestSampleTimestampNs > 0 ? buffer.OldestSampleTimestampNs : buffer.WindowStartNs)
            .Where(static value => value > 0)
            .ToArray();
        var minEdge = latestEdges.Length == 0 ? 0L : latestEdges.Min();
        var maxEdge = latestEdges.Length == 0 ? 0L : latestEdges.Max();
        var maxStart = starts.Length == 0 ? 0L : starts.Max();
        var retainedOverlap = minEdge > 0 && maxStart > 0 ? TimeSpan.FromTicks(Math.Max(0L, (minEdge - maxStart) / 100L)) : TimeSpan.Zero;
        var edgeSkew = minEdge > 0 && maxEdge > minEdge ? TimeSpan.FromTicks((maxEdge - minEdge) / 100L) : TimeSpan.Zero;
        var syncConfidence = hub.AudioSynchronizationStates.Count == 0
            ? 0.0
            : hub.AudioSynchronizationStates.Average(static state => Math.Clamp(state.Confidence, 0.0, 1.0));
        var previousReady = lastFrame?.Frame.Slices.Count(static slice => slice.Status == MimirSynchronizedSliceStatus.Ready) ?? 0;
        var previousFuture = lastFrame?.Frame.Slices.Count(static slice => slice.Status == MimirSynchronizedSliceStatus.FutureSampleOnly) ?? 0;
        var previousMissing = lastFrame?.Frame.Slices.Count(static slice => slice.Status == MimirSynchronizedSliceStatus.Missing) ?? 0;
        var previousTotal = lastFrame?.Frame.Slices.Count ?? 0;
        var readiness = previousTotal == 0 ? 0.0 : previousReady / (double)previousTotal;

        var ceiling = TimeSpan.FromMilliseconds(options.PresentationDelayMs);
        var target = TimeSpan.FromMilliseconds(Math.Clamp(
            Math.Max(options.MinPresentationDelayMs, edgeSkew.TotalMilliseconds + options.LatencyGuardMs),
            options.MinPresentationDelayMs,
            options.PresentationDelayMs));
        var reason = "edge-skew-plus-guard";
        if (retainedOverlap > TimeSpan.Zero)
        {
            var overlapBound = retainedOverlap - TimeSpan.FromMilliseconds(options.LatencyGuardMs);
            if (overlapBound > floor)
            {
                target = target < overlapBound ? target : overlapBound;
            }
            else
            {
                target = floor;
                reason = "overlap-thin";
            }
        }

        if (lastFrame != null && !lastFrame.Frame.IsComplete)
        {
            target += TimeSpan.FromMilliseconds(options.LatencyStepMs * Math.Max(1, degradedStreak));
            reason = "backoff-after-incomplete-frame";
        }
        else if (previousFuture > 0 && previousMissing == 0)
        {
            target -= TimeSpan.FromMilliseconds(options.LatencyStepMs * Math.Max(1, completeStreak / Math.Max(1, options.LatencyConvergenceFrames)));
            reason = "chase-future-samples";
        }
        else if (completeStreak >= Math.Max(1, options.LatencyConvergenceFrames) && readiness >= options.LatencyReadinessTarget)
        {
            target -= TimeSpan.FromMilliseconds(options.LatencyStepMs);
            reason = "complete-frame-convergence";
        }

        current = ClampDelay(Smooth(current, target), floor, ceiling);
        return Decision(retainedOverlap, edgeSkew, readiness, syncConfidence, active.Length, previousReady, previousTotal, reason);
    }

    public void ObserveFrame(MimirWellFrameBuildResult result)
    {
        lastFrame = result;
        if (result.Frame.IsComplete)
        {
            completeStreak++;
            degradedStreak = 0;
        }
        else
        {
            degradedStreak++;
            completeStreak = 0;
        }
    }

    private MimirWellLatencyDecision Decision(
        TimeSpan retainedOverlap,
        TimeSpan edgeSkew,
        double readiness,
        double syncConfidence,
        int activeBuffers,
        int readySlices,
        int totalSlices,
        string reason) =>
        new(
            current,
            TimeSpan.FromMilliseconds(options.PresentationDelayMs),
            floor,
            retainedOverlap,
            edgeSkew,
            readiness,
            syncConfidence,
            activeBuffers,
            readySlices,
            totalSlices,
            reason);

    private static TimeSpan Smooth(TimeSpan current, TimeSpan target)
    {
        var alpha = target > current ? 0.65 : 0.20;
        return TimeSpan.FromTicks((long)Math.Round(current.Ticks + (target.Ticks - current.Ticks) * alpha));
    }

    private static TimeSpan ClampDelay(TimeSpan value, TimeSpan floor, TimeSpan ceiling) =>
        value < floor ? floor : value > ceiling ? ceiling : value;
}

internal sealed record MimirWellFeatureSignalFrame(
    string Document,
    long Sequence,
    IReadOnlyList<MimirWellFeatureSignal> Signals,
    double MeanConfidence,
    double MeanMotionPixelsPerSecond,
    int StableTrackCount,
    string FaustSignalContract);

internal sealed record MimirWellFeatureSignal(
    string SourceId,
    long TimestampNs,
    int Width,
    int Height,
    int StableTrackCount,
    double Confidence,
    double MeanMotionPixelsPerSecond,
    double MotionEnergy,
    double NormalizedCentroidX,
    double NormalizedCentroidY,
    IReadOnlyDictionary<string, float> FaustControls);

internal sealed class MimirWellFeatureSignalBank
{
    private readonly MimirWellOptions options;
    private readonly Dictionary<string, MimirSparseFeatureTracker> trackers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> lastSequences = new(StringComparer.Ordinal);
    private long sequence;

    public MimirWellFeatureSignalBank(MimirWellOptions options)
    {
        this.options = options;
    }

    public MimirWellFeatureSignalFrame Update(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        MimirSynchronizedBufferFrame frame)
    {
        if (!options.VideoFeatureSignalsEnabled)
        {
            return Empty();
        }

        var signals = frame.VideoSlices
            .Where(static slice => slice.Sample.HasValue)
            .Select(slice => BuildSignal(slice.Sample!.Value))
            .Where(static signal => signal != null)
            .Select(static signal => signal!)
            .ToArray();
        if (signals.Length == 0)
        {
            signals = buffers
                .Where(static buffer => buffer.Descriptor.Kind == MimirStreamKind.Video && buffer.Latest.HasValue)
                .Select(buffer => BuildSignal(buffer.Latest!.Value))
                .Where(static signal => signal != null)
                .Select(static signal => signal!)
                .ToArray();
        }

        if (signals.Length == 0)
        {
            return Empty();
        }

        return new MimirWellFeatureSignalFrame(
            "mimir.well_feature_signals.v1",
            ++sequence,
            signals,
            signals.Average(static signal => signal.Confidence),
            signals.Average(static signal => signal.MeanMotionPixelsPerSecond),
            signals.Sum(static signal => signal.StableTrackCount),
            "Faust controls are normalized scalar signals under video/<source>/{motion_energy,confidence,centroid_x,centroid_y}; Mimir estimates, Faust/native DSP consumes.");
    }

    private MimirWellFeatureSignalFrame Empty() =>
        new("mimir.well_feature_signals.v1", sequence, [], 0.0, 0.0, 0, "no feature-bearing CPU video slice available");

    private MimirWellFeatureSignal? BuildSignal(MimirStreamSample sample)
    {
        if (sample.VideoFrame is not { } video ||
            sample.Data.IsEmpty ||
            sample.Sequence == (lastSequences.TryGetValue(sample.SourceId, out var previous) ? previous : ulong.MaxValue))
        {
            return null;
        }

        var luma = ExtractLuma(video, sample.Data.Span);
        if (luma.Length == 0)
        {
            return null;
        }

        lastSequences[sample.SourceId] = sample.Sequence;
        if (!trackers.TryGetValue(sample.SourceId, out var tracker))
        {
            tracker = new MimirSparseFeatureTracker(new MimirSparseFeatureTrackerOptions(
                MaxFeatures: options.VideoFeatureMaxTracks,
                CellSizePixels: options.VideoFeatureCellSizePixels,
                SearchRadiusPixels: options.VideoFeatureSearchRadiusPixels));
            trackers.Add(sample.SourceId, tracker);
        }

        var tracked = tracker.Update(
            sample.SourceId,
            video.Width,
            video.Height,
            luma,
            sample.TimestampNs);
        if (tracked.Tracks.Count == 0)
        {
            return new MimirWellFeatureSignal(
                sample.SourceId,
                sample.TimestampNs,
                video.Width,
                video.Height,
                0,
                0.0,
                0.0,
                0.0,
                0.5,
                0.5,
                FaustControls(sample.SourceId, 0.0, 0.0, 0.5, 0.5));
        }

        var stableTracks = tracked.Tracks.Where(track => track.AgeFrames >= 3).ToArray();
        var points = stableTracks.Length == 0 ? tracked.Tracks : stableTracks;
        var centroidX = points.Average(static track => track.ImageX) / Math.Max(1.0, video.Width);
        var centroidY = points.Average(static track => track.ImageY) / Math.Max(1.0, video.Height);
        var motionEnergy = Math.Clamp(tracked.MeanSpeedPixelsPerSecond / Math.Max(1.0, Math.Max(video.Width, video.Height)), 0.0, 1.0);
        return new MimirWellFeatureSignal(
            sample.SourceId,
            sample.TimestampNs,
            video.Width,
            video.Height,
            tracked.StableTrackCount,
            tracked.Confidence,
            tracked.MeanSpeedPixelsPerSecond,
            motionEnergy,
            Math.Clamp(centroidX, 0.0, 1.0),
            Math.Clamp(centroidY, 0.0, 1.0),
            FaustControls(sample.SourceId, tracked.Confidence, motionEnergy, centroidX, centroidY));
    }

    private static IReadOnlyDictionary<string, float> FaustControls(
        string sourceId,
        double confidence,
        double motionEnergy,
        double centroidX,
        double centroidY)
    {
        var prefix = $"video/{sourceId}";
        return new Dictionary<string, float>(StringComparer.Ordinal)
        {
            [$"{prefix}/confidence"] = (float)Math.Clamp(confidence, 0.0, 1.0),
            [$"{prefix}/motion_energy"] = (float)Math.Clamp(motionEnergy, 0.0, 1.0),
            [$"{prefix}/centroid_x"] = (float)Math.Clamp(centroidX, 0.0, 1.0),
            [$"{prefix}/centroid_y"] = (float)Math.Clamp(centroidY, 0.0, 1.0),
        };
    }

    private static byte[] ExtractLuma(MimirVideoFrameDescriptor video, ReadOnlySpan<byte> data) =>
        video.PixelFormat switch
        {
            MimirVideoPixelFormat.Gray8 or MimirVideoPixelFormat.R8 or MimirVideoPixelFormat.Bayer8 =>
                CopySinglePlane(video.Width, video.Height, Math.Max(video.Width, video.StrideBytes), data),
            MimirVideoPixelFormat.LeapStereoIr or MimirVideoPixelFormat.Rg8 =>
                CopyInterleavedLuma(video.Width, video.Height, Math.Max(video.Width * 2, video.StrideBytes), 2, data),
            MimirVideoPixelFormat.Yuy2 =>
                CopyInterleavedLuma(video.Width, video.Height, Math.Max(video.Width * 2, video.StrideBytes), 2, data),
            MimirVideoPixelFormat.Bgra8 =>
                CopyBgraLuma(video.Width, video.Height, Math.Max(video.Width * 4, video.StrideBytes), data),
            _ => [],
        };

    private static byte[] CopySinglePlane(int width, int height, int stride, ReadOnlySpan<byte> data)
    {
        if (width <= 0 || height <= 0 || data.Length < stride * Math.Max(0, height - 1) + width)
        {
            return [];
        }

        var luma = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            data.Slice(y * stride, width).CopyTo(luma.AsSpan(y * width, width));
        }

        return luma;
    }

    private static byte[] CopyInterleavedLuma(int width, int height, int stride, int step, ReadOnlySpan<byte> data)
    {
        if (width <= 0 || height <= 0 || step <= 0 || data.Length < stride * Math.Max(0, height - 1) + width * step)
        {
            return [];
        }

        var luma = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = data.Slice(y * stride, width * step);
            for (var x = 0; x < width; x++)
            {
                luma[y * width + x] = row[x * step];
            }
        }

        return luma;
    }

    private static byte[] CopyBgraLuma(int width, int height, int stride, ReadOnlySpan<byte> data)
    {
        if (width <= 0 || height <= 0 || data.Length < stride * Math.Max(0, height - 1) + width * 4)
        {
            return [];
        }

        var luma = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = data.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                luma[y * width + x] = (byte)Math.Clamp(
                    (int)Math.Round(row[offset] * 0.114 + row[offset + 1] * 0.587 + row[offset + 2] * 0.299),
                    0,
                    255);
            }
        }

        return luma;
    }
}

internal sealed record MimirWellPublishReceipt(
    string Document,
    int ByteLength,
    double SerializeMilliseconds,
    double SendMilliseconds,
    double TotalMilliseconds,
    bool Dropped = false,
    int QueueDepth = 0);

internal sealed class MimirWellSourceHeartbeat
{
    private readonly MimirWellOptions options;
    private readonly IReadOnlyList<MimirStreamSourceFactory> factories;
    private readonly List<object> sourceErrors;
    private readonly Dictionary<string, SourceState> states = new(StringComparer.Ordinal);
    private long sequence;

    public MimirWellSourceHeartbeat(
        MimirWellOptions options,
        IReadOnlyList<MimirStreamSourceFactory> factories,
        List<object> sourceErrors)
    {
        this.options = options;
        this.factories = factories;
        this.sourceErrors = sourceErrors;
    }

    public object Update(DateTimeOffset now, MimirSynchronizationHub hub)
    {
        var checks = factories.Select(factory => CheckSource(now, hub, factory)).ToArray();
        var interventions = checks
            .Where(check => check.InterventionRequired)
            .Select(check => new
            {
                check.SourceId,
                check.Status,
                check.Reason,
                latestAgeMs = SafeAge(check.LatestAgeMs),
                check.ReacquireAttempts,
                operatorCommand = new
                {
                    providerId = options.OperatorDmProviderId,
                    command = options.OperatorDmCommand,
                    transport = "cultmesh-provider-command",
                    payload = new
                    {
                        document = "gamecult.operator_dm_request.v1",
                        severity = check.Status == "reacquire-exhausted" ? "error" : "warning",
                        service = "Mimir.Well",
                        sourceId = check.SourceId,
                        status = check.Status,
                        reason = check.Reason,
                        latestAgeMs = SafeAge(check.LatestAgeMs),
                    },
                },
            })
            .ToArray();

        return new
        {
            type = "cultmesh-observation",
            document = "mimir.well_heartbeat.v1",
            sourceId = "mimir-well",
            nodeId = options.NodeId,
            sequence = ++sequence,
            wallClockUtc = now.ToString("O"),
            authority = "Mimir.Well owns stream liveness, bounded local reacquire, and operator-alert publication. VoidBot owns owner-DM delivery through its CultMesh command provider.",
            sourceCount = checks.Length,
            healthyCount = checks.Count(static check => check.Status == "live"),
            interventionRequired = interventions.Length > 0,
            operatorAlertContract = new
            {
                providerId = options.OperatorDmProviderId,
                command = options.OperatorDmCommand,
                document = "gamecult.operator_dm_request.v1",
                deliveryOwner = "VoidBot",
                routingOwner = "CultMesh/Odin",
            },
            sources = checks.Select(check => new
            {
                check.SourceId,
                check.Kind,
                check.Origin,
                check.Status,
                check.Reason,
                check.Samples,
                latestAgeMs = SafeAge(check.LatestAgeMs),
                check.ReacquireAttempts,
                lastReacquireUtc = check.LastReacquireUtc?.ToString("O"),
                nextReacquireUtc = check.NextReacquireUtc?.ToString("O"),
            }).ToArray(),
            interventions,
        };
    }

    private SourceCheck CheckSource(DateTimeOffset now, MimirSynchronizationHub hub, MimirStreamSourceFactory factory)
    {
        var descriptor = factory.Descriptor;
        var state = GetState(descriptor.SourceId);
        var active = hub.ActiveSourceIds.Contains(descriptor.SourceId, StringComparer.Ordinal);
        var matchingBuffers = MatchingBuffers(hub.Buffers.Buffers, factory).ToArray();
        var samples = matchingBuffers.Sum(static buffer => buffer.Count);
        var latest = matchingBuffers
            .Select(static buffer => buffer.Latest)
            .Where(static sample => sample.HasValue)
            .OrderByDescending(static sample => sample!.Value.TimestampNs)
            .FirstOrDefault();
        var latestAgeMs = latest.HasValue
            ? Math.Max(0.0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000.0 - latest.Value.TimestampNs) / 1_000_000.0)
            : double.PositiveInfinity;
        if (samples > state.LastSampleCount)
        {
            state.FirstUnhealthyUtc = null;
            state.LastSampleCount = samples;
        }

        var stale = !latest.HasValue || latestAgeMs >= options.SourceStaleMs;
        var status = active && !stale
            ? "live"
            : descriptor.Origin == MimirStreamOrigin.Network
                ? "waiting-network"
                : active && samples > 0
                    ? "stalled"
                    : "waiting-device-samples";
        var reason = status switch
        {
            "live" => "samples arriving",
            "waiting-network" => "network producer has not supplied fresh samples; Well cannot reacquire a remote owner",
            "stalled" => "local source emitted samples earlier but latest sample is stale",
            _ => "local source has not emitted samples",
        };

        if (status == "live")
        {
            return Check(descriptor, status, reason, samples, latestAgeMs, state, false);
        }

        if (state.FirstUnhealthyUtc is null)
        {
            state.FirstUnhealthyUtc = now;
        }

        if (descriptor.Origin != MimirStreamOrigin.Network &&
            state.ReacquireAttempts < options.SourceMaxReacquireAttempts &&
            (state.NextReacquireUtc is null || now >= state.NextReacquireUtc))
        {
            status = TryReacquire(now, hub, factory, active, state, out var reacquireReason)
                ? "reacquire-started"
                : "reacquire-failed";
            reason = reacquireReason;
        }
        else if (descriptor.Origin != MimirStreamOrigin.Network &&
            state.ReacquireAttempts >= options.SourceMaxReacquireAttempts)
        {
            status = "reacquire-exhausted";
            reason = "bounded local reacquire attempts failed; operator intervention required";
        }

        return Check(descriptor, status, reason, samples, latestAgeMs, state, InterventionRequired(now, state, status));
    }

    private bool TryReacquire(
        DateTimeOffset now,
        MimirSynchronizationHub hub,
        MimirStreamSourceFactory factory,
        bool active,
        SourceState state,
        out string reason)
    {
        state.ReacquireAttempts++;
        state.LastReacquireUtc = now;
        state.NextReacquireUtc = now + TimeSpan.FromMilliseconds(options.SourceReacquireBackoffMs);
        try
        {
            if (active)
            {
                hub.RemoveSource(factory.Descriptor.SourceId);
            }

            var source = factory.Create();
            if (source is null)
            {
                reason = "source factory returned no source during reacquire";
                sourceErrors.Add(new { factory.Descriptor.SourceId, status = "reacquire-not-created", wallClockUtc = now.ToString("O") });
                return false;
            }

            hub.AddSource(source);
            reason = "local source reacquire started";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"reacquire failed: {ex.GetType().Name}: {ex.Message}";
            sourceErrors.Add(new { factory.Descriptor.SourceId, status = "reacquire-error", errorType = ex.GetType().Name, ex.Message, wallClockUtc = now.ToString("O") });
            return false;
        }
    }

    private bool InterventionRequired(DateTimeOffset now, SourceState state, string status) =>
        status == "reacquire-exhausted" ||
        (status != "live" &&
            state.FirstUnhealthyUtc.HasValue &&
            now - state.FirstUnhealthyUtc.Value >= TimeSpan.FromMilliseconds(options.SourceInterventionMs));

    private SourceState GetState(string sourceId)
    {
        if (!states.TryGetValue(sourceId, out var state))
        {
            state = new SourceState();
            states.Add(sourceId, state);
        }

        return state;
    }

    private static IEnumerable<MimirRollingStreamBuffer> MatchingBuffers(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        MimirStreamSourceFactory factory)
    {
        var descriptor = factory.Descriptor;
        var matching = buffers
            .Where(buffer => string.Equals(buffer.Descriptor.SourceId, descriptor.SourceId, StringComparison.Ordinal))
            .ToArray();
        if (matching.Length > 0 ||
            !descriptor.SourceId.Contains("asio", StringComparison.OrdinalIgnoreCase))
        {
            return matching;
        }

        return buffers.Where(buffer =>
            buffer.Descriptor.Kind == descriptor.Kind &&
            buffer.Descriptor.Origin == descriptor.Origin &&
            buffer.Descriptor.SourceId.StartsWith("asio-ch", StringComparison.OrdinalIgnoreCase));
    }

    private static SourceCheck Check(
        MimirStreamDescriptor descriptor,
        string status,
        string reason,
        int samples,
        double latestAgeMs,
        SourceState state,
        bool interventionRequired) =>
        new(
            descriptor.SourceId,
            descriptor.Kind.ToString(),
            descriptor.Origin.ToString(),
            status,
            reason,
            samples,
            latestAgeMs,
            state.ReacquireAttempts,
            state.LastReacquireUtc,
            state.NextReacquireUtc,
            interventionRequired);

    private static double? SafeAge(double ageMs) =>
        double.IsFinite(ageMs) ? Math.Round(ageMs, 1) : null;

    private sealed class SourceState
    {
        public int ReacquireAttempts { get; set; }
        public int LastSampleCount { get; set; }
        public DateTimeOffset? FirstUnhealthyUtc { get; set; }
        public DateTimeOffset? LastReacquireUtc { get; set; }
        public DateTimeOffset? NextReacquireUtc { get; set; }
    }

    private sealed record SourceCheck(
        string SourceId,
        string Kind,
        string Origin,
        string Status,
        string Reason,
        int Samples,
        double LatestAgeMs,
        int ReacquireAttempts,
        DateTimeOffset? LastReacquireUtc,
        DateTimeOffset? NextReacquireUtc,
        bool InterventionRequired);
}

internal static class MimirWellEnvironment
{
    public static bool IsTruthy(string? value) =>
        value is "1" or "true" or "TRUE" or "yes" or "YES" or "on" or "ON";
}

internal sealed class MimirWellStreamTelemetry
{
    private long pollIterations;
    private long consumedSamples;
    private long zeroPollIterations;
    private double maxPollMilliseconds;
    private double totalPollMilliseconds;
    private long publishedDocuments;
    private long publishedBytes;
    private long droppedDocuments;
    private double maxPublishMilliseconds;
    private double totalPublishMilliseconds;
    private int lastPublishQueueDepth;
    private int maxPublishQueueDepth;
    private string lastPublishedDocument = "";
    private int lastPublishedBytes;
    private double lastPublishMilliseconds;

    public void ObservePoll(int consumed, TimeSpan elapsed)
    {
        pollIterations++;
        consumedSamples += Math.Max(0, consumed);
        if (consumed <= 0)
        {
            zeroPollIterations++;
        }

        var milliseconds = elapsed.TotalMilliseconds;
        totalPollMilliseconds += milliseconds;
        maxPollMilliseconds = Math.Max(maxPollMilliseconds, milliseconds);
    }

    public void ObservePublish(MimirWellPublishReceipt receipt)
    {
        lastPublishQueueDepth = receipt.QueueDepth;
        maxPublishQueueDepth = Math.Max(maxPublishQueueDepth, receipt.QueueDepth);
        if (receipt.Dropped)
        {
            droppedDocuments++;
            return;
        }

        publishedDocuments++;
        publishedBytes += receipt.ByteLength;
        totalPublishMilliseconds += receipt.TotalMilliseconds;
        maxPublishMilliseconds = Math.Max(maxPublishMilliseconds, receipt.TotalMilliseconds);
        lastPublishedDocument = receipt.Document;
        lastPublishedBytes = receipt.ByteLength;
        lastPublishMilliseconds = receipt.TotalMilliseconds;
    }

    public object Snapshot(DateTimeOffset now, bool subscribersAreExternal, object senderPressure) => new
    {
        document = "mimir.well_stream_pressure.v1",
        status = "transitional-websocket-json",
        authority = "Mimir.Well observes producer pressure; CultMesh streaming organ should own durable body lanes.",
        wallClockUtc = now.ToString("O"),
        poll = new
        {
            iterations = pollIterations,
            consumedSamples,
            zeroPollIterations,
            averageMilliseconds = pollIterations == 0 ? 0.0 : totalPollMilliseconds / pollIterations,
            maxMilliseconds = maxPollMilliseconds,
        },
        publish = new
        {
            documents = publishedDocuments,
            bytes = publishedBytes,
            averageMilliseconds = publishedDocuments == 0 ? 0.0 : totalPublishMilliseconds / publishedDocuments,
            maxMilliseconds = maxPublishMilliseconds,
            lastQueueDepth = lastPublishQueueDepth,
            maxQueueDepth = maxPublishQueueDepth,
            droppedDocuments,
            lastDocument = lastPublishedDocument,
            lastBytes = lastPublishedBytes,
            lastMilliseconds = lastPublishMilliseconds,
            subscribersAreExternal,
        },
        sender = senderPressure,
        nextOrgan = new
        {
            controlLane = "CultMesh typed state and compact stream cursors",
            bodyLane = "CultCache page/shard append stream with refs, hashes, and backpressure",
            realtimeLane = "loss-aware latest-state observation stream for dashboards",
        },
    };
}

internal sealed record MimirWellOptions(
    Uri PublishUrl,
    string NodeId,
    double Seconds,
    int PollMs,
    int PublishIntervalMs,
    int SyncIntervalMs,
    int PresentationDelayMs,
    int TargetPresentationDelayMs,
    int MinPresentationDelayMs,
    int LatencyGuardMs,
    int LatencyStepMs,
    int LatencyConvergenceFrames,
    double LatencyReadinessTarget,
    int MaxSamplesPerSource,
    int SyncCandidatesPerStep,
    bool VideoFeatureSignalsEnabled,
    int VideoFeatureMaxTracks,
    int VideoFeatureCellSizePixels,
    int VideoFeatureSearchRadiusPixels,
    bool VisualCalibrationEnabled,
    int VisualCalibrationIntervalMs,
    int VisualExpectedLedCount,
    double VisualMinimumLuma,
    double VisualSettingSeconds,
    double VisualResweepSeconds,
    bool CapturePagesEnabled,
    int CaptureIntervalMs,
    int CaptureMaxBodyBytes,
    bool CaptureInlineBodies,
    bool StreamFramesEnabled,
    bool StreamFrameInlineBodies,
    int HeartbeatIntervalMs,
    int SourceStaleMs,
    int SourceInterventionMs,
    int SourceReacquireBackoffMs,
    int SourceMaxReacquireAttempts,
    string OperatorDmProviderId,
    string OperatorDmCommand,
    int MeterEvery)
{
    public static MimirWellOptions Parse(string[] args) => new(
        new Uri(ParseString(args, "--publish-url", "ws://127.0.0.1:8796/eve/periwinkle")),
        ParseString(args, "--node-id", Environment.MachineName.ToLowerInvariant()),
        ParseDouble(args, "--seconds", 0.0),
        ParseInt(args, "--poll-ms", 5),
        ParseInt(args, "--publish-ms", 250),
        ParseInt(args, "--sync-ms", 250),
        ParseInt(args, "--presentation-delay-ms", 2500),
        ParseInt(args, "--target-presentation-delay-ms", 750),
        ParseInt(args, "--min-presentation-delay-ms", 40),
        ParseInt(args, "--latency-guard-ms", 35),
        ParseInt(args, "--latency-step-ms", 25),
        ParseInt(args, "--latency-convergence-frames", 8),
        ParseDouble(args, "--latency-readiness-target", 0.98),
        ParseInt(args, "--max-samples-per-source", 4),
        ParseInt(args, "--sync-candidates", 1),
        ParseBool(args, "--video-feature-signals", true),
        ParseInt(args, "--video-feature-max-tracks", 96),
        ParseInt(args, "--video-feature-cell-size", 12),
        ParseInt(args, "--video-feature-search-radius", 18),
        ParseBool(args, "--visual-calibration", true),
        ParseInt(args, "--visual-calibration-ms", 250),
        ParseInt(args, "--visual-expected-leds", 38),
        ParseDouble(args, "--visual-minimum-luma", 0.55),
        ParseDouble(args, "--visual-setting-seconds", 0.75),
        ParseDouble(args, "--visual-resweep-seconds", 12.0),
        ParseBool(args, "--capture-pages", true),
        ParseInt(args, "--capture-ms", 250),
        ParseInt(args, "--capture-max-body-bytes", 4 * 1024 * 1024),
        ParseBool(args, "--capture-inline-bodies", true),
        ParseBool(args, "--stream-frames", true),
        ParseBool(args, "--stream-frame-inline-bodies", true),
        ParseInt(args, "--heartbeat-ms", 1000),
        ParseInt(args, "--source-stale-ms", 2000),
        ParseInt(args, "--source-intervention-ms", 10000),
        ParseInt(args, "--source-reacquire-backoff-ms", 3000),
        ParseInt(args, "--source-max-reacquire-attempts", 3),
        ParseString(args, "--operator-dm-provider", "voidbot.operator-dm"),
        ParseString(args, "--operator-dm-command", "owner.dm.send"),
        ParseInt(args, "--meter-every", 20));

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

    private static int ParseInt(IReadOnlyList<string> args, string name, int fallback) =>
        int.TryParse(ParseString(args, name, ""), out var value) ? value : fallback;

    private static double ParseDouble(IReadOnlyList<string> args, string name, double fallback) =>
        double.TryParse(ParseString(args, name, ""), out var value) ? value : fallback;

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

internal sealed class MimirWellPublisher(Uri url) : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly ConcurrentQueue<QueuedPublish> controlQueue = new();
    private readonly ConcurrentQueue<QueuedPublish> bodyQueue = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource senderStopping = new();
    private readonly object sendTelemetryLock = new();
    private Task? senderTask;
    private int queueDepth;
    private long sentDocuments;
    private long sentBytes;
    private double totalSendMilliseconds;
    private double maxSendMilliseconds;
    private string lastSentDocument = "";
    private int lastSentBytes;
    private double lastSendMilliseconds;

    private const int MaxQueuedDocuments = 256;
    private const int StreamFrameDropThreshold = 128;

    public async Task ConnectAsync(CancellationToken stopping)
    {
        await socket.ConnectAsync(url, stopping).ConfigureAwait(false);
        senderTask = Task.Run(() => SendLoopAsync(senderStopping.Token));
    }

    public Task<MimirWellPublishReceipt> PublishAsync(
        object document,
        string documentName,
        CancellationToken stopping)
    {
        var stopwatch = Stopwatch.StartNew();
        var json = JsonSerializer.Serialize(document);
        var bytes = Encoding.UTF8.GetBytes(json);
        var serializeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Stop();
        var depth = Volatile.Read(ref queueDepth);
        if (depth >= MaxQueuedDocuments ||
            (depth >= StreamFrameDropThreshold &&
                string.Equals(documentName, "mimir.cultmesh_stream_frame.v1", StringComparison.Ordinal)))
        {
            return Task.FromResult(new MimirWellPublishReceipt(
                documentName,
                bytes.Length,
                serializeMilliseconds,
                0.0,
                serializeMilliseconds,
                Dropped: true,
                QueueDepth: depth));
        }

        if (string.Equals(documentName, "mimir.cultmesh_stream_frame.v1", StringComparison.Ordinal))
        {
            bodyQueue.Enqueue(new QueuedPublish(documentName, bytes));
        }
        else
        {
            controlQueue.Enqueue(new QueuedPublish(documentName, bytes));
        }

        var queued = Interlocked.Increment(ref queueDepth);
        signal.Release();
        return Task.FromResult(new MimirWellPublishReceipt(
            documentName,
            bytes.Length,
            serializeMilliseconds,
            0.0,
            serializeMilliseconds,
            QueueDepth: queued));
    }

    private async Task SendLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (TryDequeueNext(out var item))
            {
                Interlocked.Decrement(ref queueDepth);
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    await socket.SendAsync(item.Bytes, WebSocketMessageType.Text, endOfMessage: true, stopping).ConfigureAwait(false);
                    stopwatch.Stop();
                    ObserveSend(item, stopwatch.Elapsed);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (WebSocketException ex)
                {
                    Console.Error.WriteLine($"mimir-well publish-warning document={item.Document} {ex.Message}");
                    return;
                }
            }
        }
    }

    private bool TryDequeueNext(out QueuedPublish item) =>
        controlQueue.TryDequeue(out item!) || bodyQueue.TryDequeue(out item!);

    public object SenderPressureSnapshot()
    {
        lock (sendTelemetryLock)
        {
            return new
            {
                document = "mimir.well_sender_pressure.v1",
                authority = "Mimir.Well websocket sender loop observes wire send pressure after enqueue.",
                sentDocuments,
                sentBytes,
                averageMilliseconds = sentDocuments == 0 ? 0.0 : totalSendMilliseconds / sentDocuments,
                maxMilliseconds = maxSendMilliseconds,
                lastDocument = lastSentDocument,
                lastBytes = lastSentBytes,
                lastMilliseconds = lastSendMilliseconds,
                currentQueueDepth = Volatile.Read(ref queueDepth),
            };
        }
    }

    private void ObserveSend(QueuedPublish item, TimeSpan elapsed)
    {
        var milliseconds = elapsed.TotalMilliseconds;
        lock (sendTelemetryLock)
        {
            sentDocuments++;
            sentBytes += item.Bytes.Length;
            totalSendMilliseconds += milliseconds;
            maxSendMilliseconds = Math.Max(maxSendMilliseconds, milliseconds);
            lastSentDocument = item.Document;
            lastSentBytes = item.Bytes.Length;
            lastSendMilliseconds = milliseconds;
        }
    }

    public async ValueTask DisposeAsync()
    {
        senderStopping.Cancel();
        if (senderTask != null)
        {
            try
            {
                await senderTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (socket.State == WebSocketState.Open)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "well-complete", cts.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        socket.Dispose();
        signal.Dispose();
        senderStopping.Dispose();
    }

    private sealed record QueuedPublish(string Document, byte[] Bytes);
}

internal static class MimirWellCapturePage
{
    public static object Build(
        MimirWellOptions options,
        MimirPresentationControlState presentation,
        MimirWellFrameBuildResult frameResult,
        IEnumerable<MimirRollingStreamBuffer> buffers,
        long captureSequence,
        DateTimeOffset startedAt)
    {
        var frame = frameResult.Frame;
        return new
        {
        type = "cultmesh-observation",
        document = "mimir.well_capture_page.v1",
        sourceId = "mimir-well",
        nodeId = options.NodeId,
        captureSequence,
        wallClockUtc = DateTimeOffset.UtcNow.ToString("O"),
        elapsedSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
        frame = new
        {
            frame.PresentationTimeNs,
            frame.WindowStartNs,
            frame.WindowEndNs,
            PresentationDelayMs = frame.PresentationDelay.TotalMilliseconds,
            frame.IsComplete,
            degradedKind = frameResult.DegradedKind,
            degradedReason = frameResult.DegradedReason,
        },
        storage = new
        {
            bodyTransport = options.CaptureInlineBodies ? "inline-base64" : "metadata-only",
            maxInlineBodyBytes = options.CaptureMaxBodyBytes,
            intendedSink = "mimir-recorder-paged-cultcache",
        },
        configuredComposite = new
        {
            video = presentation.VideoFeeds.Select(feed => new
            {
                feed.SourceId,
                feed.DisplayName,
                feed.Enabled,
                feed.Available,
                feed.SampleCount,
                Included = presentation.IncludesVideo(feed.SourceId),
                feed.Solo,
                feed.Opacity,
                feed.Layer,
            }).ToArray(),
            audio = presentation.AudioFeeds.Select(feed => new
            {
                feed.SourceId,
                feed.DisplayName,
                feed.Muted,
                feed.Solo,
                feed.Gain,
            }).ToArray(),
            postprocess = presentation.Postprocess,
            renderedProgramBody = new
            {
                status = "not-published-yet",
                reason = "Fensalir/OBS program-output capture is a separate producer; this page records the configured composite contract and synchronized source bodies.",
            },
        },
        samples = CaptureSamples(options, captureSequence, frame, buffers)
            .ToArray(),
    };
    }

    public static IEnumerable<object> BuildStreamFrames(
        MimirWellOptions options,
        MimirWellFrameBuildResult frameResult,
        IEnumerable<MimirRollingStreamBuffer> buffers,
        long captureSequence,
        DateTimeOffset startedAt)
    {
        var frame = frameResult.Frame;
        var sliceFrames = frame.Slices
            .Where(slice => slice.Sample.HasValue)
            .Select(slice => CaptureStreamFrame(options, captureSequence, startedAt, slice))
            .ToArray();
        if (sliceFrames.Length > 0)
        {
            return sliceFrames;
        }

        return buffers
            .Where(buffer => buffer.Latest.HasValue)
            .Select(buffer => CaptureLatestFallbackStreamFrame(options, captureSequence, startedAt, buffer))
            .ToArray();
    }

    public static object BuildIngestedStreamFrame(
        MimirWellOptions options,
        MimirStreamSample sample,
        DateTimeOffset startedAt) =>
        CaptureStreamFrame(
            options,
            captureSequence: 0,
            startedAt,
            sample,
            new
            {
                sample.SourceId,
                Kind = sample.Kind.ToString(),
                Origin = sample.Origin.ToString(),
                Status = "IngestedCanonicalSample",
                SourceTimestampNs = sample.TimestampNs,
                CanonicalStartNs = sample.TimestampNs,
                CanonicalEndNs = sample.TimestampNs + SampleDurationNs(sample),
                PresentationTimeNs = 0L,
                TimingOffsetNs = 0L,
                DistanceFromPresentationNs = 0L,
                TimingConfidence = 0.0,
                TimingEvidenceKind = "canonical-ingest",
            },
            bodyId: $"{options.NodeId}:ingest:{sample.SourceId}:{sample.Sequence}");

    private static IEnumerable<object> CaptureSamples(
        MimirWellOptions options,
        long captureSequence,
        MimirSynchronizedBufferFrame frame,
        IEnumerable<MimirRollingStreamBuffer> buffers)
    {
        var sliceSamples = frame.Slices
            .Where(slice => slice.Sample.HasValue)
            .Select(slice => CaptureSample(options, captureSequence, slice))
            .ToArray();
        if (sliceSamples.Length > 0)
        {
            return sliceSamples;
        }

        return buffers
            .Where(buffer => buffer.Latest.HasValue)
            .Select(buffer => CaptureLatestFallbackSample(options, captureSequence, buffer));
    }

    private static object CaptureSample(
        MimirWellOptions options,
        long captureSequence,
        MimirSynchronizedStreamSlice slice)
    {
        var sample = slice.Sample!.Value;
        var bodyId = $"{options.NodeId}:{captureSequence}:{sample.SourceId}:{sample.Sequence}";
        return new
        {
            bodyId,
            slice = new
            {
                slice.SourceId,
                Kind = slice.Kind.ToString(),
                Origin = slice.Origin.ToString(),
                Status = slice.Status.ToString(),
                slice.SourceTimestampNs,
                slice.CanonicalStartNs,
                slice.CanonicalEndNs,
                slice.PresentationTimeNs,
                slice.TimingOffsetNs,
                slice.DistanceFromPresentationNs,
                slice.TimingConfidence,
                slice.TimingEvidenceKind,
            },
            sample = SampleMetadata(sample),
            body = CaptureBody(options, sample, options.CaptureInlineBodies),
        };
    }

    private static object CaptureLatestFallbackSample(
        MimirWellOptions options,
        long captureSequence,
        MimirRollingStreamBuffer buffer)
    {
        var sample = buffer.Latest!.Value;
        var bodyId = $"{options.NodeId}:{captureSequence}:{sample.SourceId}:{sample.Sequence}:latest";
        return new
        {
            bodyId,
            slice = new
            {
                sample.SourceId,
                Kind = sample.Kind.ToString(),
                Origin = sample.Origin.ToString(),
                Status = "LatestUnalignedFallback",
                SourceTimestampNs = sample.TimestampNs,
                CanonicalStartNs = sample.TimestampNs,
                CanonicalEndNs = sample.TimestampNs,
                PresentationTimeNs = 0L,
                TimingOffsetNs = 0L,
                DistanceFromPresentationNs = 0L,
                TimingConfidence = 0.0,
                TimingEvidenceKind = "unsynchronized-fallback",
            },
            sample = SampleMetadata(sample),
            body = CaptureBody(options, sample, options.CaptureInlineBodies),
        };
    }

    private static object CaptureStreamFrame(
        MimirWellOptions options,
        long captureSequence,
        DateTimeOffset startedAt,
        MimirSynchronizedStreamSlice slice)
    {
        var sample = slice.Sample!.Value;
        return CaptureStreamFrame(
            options,
            captureSequence,
            startedAt,
            sample,
            new
            {
                slice.SourceId,
                Kind = slice.Kind.ToString(),
                Origin = slice.Origin.ToString(),
                Status = slice.Status.ToString(),
                slice.SourceTimestampNs,
                slice.CanonicalStartNs,
                slice.CanonicalEndNs,
                slice.PresentationTimeNs,
                slice.TimingOffsetNs,
                slice.DistanceFromPresentationNs,
                slice.TimingConfidence,
                slice.TimingEvidenceKind,
            });
    }

    private static object CaptureLatestFallbackStreamFrame(
        MimirWellOptions options,
        long captureSequence,
        DateTimeOffset startedAt,
        MimirRollingStreamBuffer buffer)
    {
        var sample = buffer.Latest!.Value;
        return CaptureStreamFrame(
            options,
            captureSequence,
            startedAt,
            sample,
            new
            {
                sample.SourceId,
                Kind = sample.Kind.ToString(),
                Origin = sample.Origin.ToString(),
                Status = "LatestUnalignedFallback",
                SourceTimestampNs = sample.TimestampNs,
                CanonicalStartNs = sample.TimestampNs,
                CanonicalEndNs = sample.TimestampNs,
                PresentationTimeNs = 0L,
                TimingOffsetNs = 0L,
                DistanceFromPresentationNs = 0L,
                TimingConfidence = 0.0,
                TimingEvidenceKind = "unsynchronized-fallback",
            });
    }

    private static object CaptureStreamFrame(
        MimirWellOptions options,
        long captureSequence,
        DateTimeOffset startedAt,
        MimirStreamSample sample,
        object slice)
    {
        var bodyId = $"{options.NodeId}:capture:{captureSequence}:{sample.SourceId}:{sample.Sequence}";
        return CaptureStreamFrame(options, captureSequence, startedAt, sample, slice, bodyId);
    }

    private static object CaptureStreamFrame(
        MimirWellOptions options,
        long captureSequence,
        DateTimeOffset startedAt,
        MimirStreamSample sample,
        object slice,
        string bodyId)
    {
        var inlineBody = options.StreamFrameInlineBodies &&
            (sample.Kind == MimirStreamKind.Audio || sample.VideoFrame?.NativeHandle is null or 0UL);
        var transport = PreferredBodyTransport(sample, inlineBody);
        return new
        {
            type = "cultmesh-observation",
            document = "mimir.cultmesh_stream_frame.v1",
            sourceId = "mimir-well",
            nodeId = options.NodeId,
            captureSequence,
            wallClockUtc = DateTimeOffset.UtcNow.ToString("O"),
            elapsedSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            bodyId,
            stream = new
            {
                streamId = sample.SourceId,
                verseId = "mimir-live",
                ownerPeerId = options.NodeId,
                kind = sample.Kind.ToString(),
                origin = sample.Origin.ToString(),
                preferredTransports = StreamTransports(sample, inlineBody),
                clockDomainId = sample.SourceId,
            },
            frame = new
            {
                streamId = sample.SourceId,
                sequence = sample.Sequence,
                timestampNs = sample.TimestampNs,
                arrivalNs = sample.ArrivalNs,
                byteLength = sample.ByteLength,
                bodyTransport = transport,
                nativeHandle = NativeHandle(sample),
                nativeHandleKind = NativeHandleKind(sample),
                resourceKey = sample.VideoFrame?.ResourceKey,
                producerFenceHandle = sample.VideoFrame?.ProducerFenceHandle,
                producerFenceValue = sample.VideoFrame?.ProducerFenceValue ?? 0UL,
                unavoidableCopyCount = sample.VideoFrame?.UnavoidableCopyCount ?? 0,
            },
            slice,
            sample = SampleMetadata(sample),
            body = CaptureBody(options, sample, inlineBody),
        };
    }

    private static long SampleDurationNs(MimirStreamSample sample)
    {
        if (sample.AudioBlock is { SampleRate: > 0 } audio)
        {
            return checked((long)Math.Round(audio.FrameCount * 1_000_000_000.0 / audio.SampleRate));
        }

        return 0L;
    }

    private static object SampleMetadata(MimirStreamSample sample) => new
    {
        sample.SourceId,
        Kind = sample.Kind.ToString(),
        Origin = sample.Origin.ToString(),
        sample.TimestampNs,
        sample.ArrivalNs,
        sample.Sequence,
        sample.PayloadHandle,
        sample.ByteLength,
        video = sample.VideoFrame is null ? null : new
        {
            sample.VideoFrame.Width,
            sample.VideoFrame.Height,
            PixelFormat = sample.VideoFrame.PixelFormat.ToString(),
            sample.VideoFrame.StrideBytes,
            sample.VideoFrame.DeviceTimestampNs,
            sample.VideoFrame.NativeHandle,
            sample.VideoFrame.NativeHandleKind,
            sample.VideoFrame.ResourceKey,
            sample.VideoFrame.ProducerFenceHandle,
            sample.VideoFrame.ProducerFenceValue,
            sample.VideoFrame.UnavoidableCopyCount,
        },
        audio = sample.AudioBlock is null ? null : new
        {
            sample.AudioBlock.SampleRate,
            sample.AudioBlock.Channels,
            SampleFormat = sample.AudioBlock.SampleFormat.ToString(),
            sample.AudioBlock.FrameCount,
            sample.AudioBlock.DeviceTimestampNs,
            sample.AudioBlock.NativeHandle,
            sample.AudioBlock.NativeHandleKind,
        },
    };

    private static string PreferredBodyTransport(MimirStreamSample sample, bool inlineBody)
    {
        var kind = NativeHandleKind(sample);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            return kind.Contains("d3d12", StringComparison.OrdinalIgnoreCase)
                ? "shared-d3d12-texture"
                : kind.Contains("d3d11", StringComparison.OrdinalIgnoreCase)
                    ? "shared-d3d11-texture"
                    : "native-handle";
        }

        return sample.Data.IsEmpty ? "metadata-only" : inlineBody ? "inline-bytes" : "mimir-recorder-page";
    }

    private static string[] StreamTransports(MimirStreamSample sample, bool inlineBody)
    {
        var transport = PreferredBodyTransport(sample, inlineBody);
        return transport switch
        {
            "shared-d3d12-texture" => ["shared-d3d12-texture", "mimir-recorder-page", "cultcache-page"],
            "shared-d3d11-texture" => ["shared-d3d11-texture", "mimir-recorder-page", "cultcache-page"],
            "native-handle" => ["native-handle", "mimir-recorder-page", "cultcache-page"],
            "inline-bytes" => ["mimir-recorder-page", "inline-bytes", "cultcache-page"],
            "mimir-recorder-page" => ["mimir-recorder-page", "cultcache-page"],
            _ => ["metadata-only"],
        };
    }

    private static ulong NativeHandle(MimirStreamSample sample) =>
        sample.VideoFrame?.NativeHandle ?? sample.AudioBlock?.NativeHandle ?? 0UL;

    private static string NativeHandleKind(MimirStreamSample sample) =>
        sample.VideoFrame?.NativeHandleKind ?? sample.AudioBlock?.NativeHandleKind ?? "";

    private static object CaptureBody(MimirWellOptions options, MimirStreamSample sample, bool inlineBodies)
    {
        if (!inlineBodies)
        {
            return new { status = "not-inline", sample.ByteLength };
        }

        if (sample.Data.IsEmpty || sample.ByteLength <= 0)
        {
            return new { status = "empty", sample.ByteLength };
        }

        if (sample.ByteLength > options.CaptureMaxBodyBytes)
        {
            return new { status = "too-large", sample.ByteLength };
        }

        return new
        {
            status = "inline",
            encoding = "base64",
            byteLength = sample.ByteLength,
            data = Convert.ToBase64String(sample.Data.Span),
        };
    }
}

internal static class MimirWellClockDiagnostics
{
    public static object Build(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        string referenceSourceId,
        MimirWellFrameBuildResult frameResult)
    {
        var activeBuffers = buffers
            .Where(static buffer => buffer.Latest.HasValue)
            .ToArray();
        var referenceBuffer = activeBuffers.FirstOrDefault(buffer =>
            string.Equals(buffer.Descriptor.SourceId, referenceSourceId, StringComparison.Ordinal));
        var referenceEdgeNs = referenceBuffer?.Latest?.TimestampNs ?? 0L;
        var referenceClockDomain = referenceBuffer?.Descriptor.EffectiveClockDomainId ?? "";

        var domains = activeBuffers
            .GroupBy(static buffer => buffer.Descriptor.EffectiveClockDomainId, StringComparer.Ordinal)
            .Select(group => BuildDomain(group.Key, group.ToArray(), referenceClockDomain, referenceEdgeNs))
            .OrderBy(domain => domain.ClockDomainId, StringComparer.Ordinal)
            .ToArray();

        return new
        {
            status = frameResult.Frame.Slices.Count > 0 ? "frame-built" : "degraded",
            frameResult.DegradedKind,
            frameResult.DegradedReason,
            referenceSourceId,
            referenceClockDomain,
            referenceEdgeNs,
            domainCount = domains.Length,
            activeSourceCount = activeBuffers.Length,
            domains,
        };
    }

    private static ClockDomainDiagnostics BuildDomain(
        string clockDomainId,
        IReadOnlyList<MimirRollingStreamBuffer> buffers,
        string referenceClockDomain,
        long referenceEdgeNs)
    {
        var latestEdges = buffers
            .Select(buffer => buffer.Latest?.TimestampNs ?? 0L)
            .Where(static value => value > 0)
            .ToArray();
        var windowStarts = buffers
            .Select(static buffer => buffer.OldestSampleTimestampNs > 0 ? buffer.OldestSampleTimestampNs : buffer.WindowStartNs)
            .Where(static value => value > 0)
            .ToArray();
        var minLatest = latestEdges.Length == 0 ? 0L : latestEdges.Min();
        var maxLatest = latestEdges.Length == 0 ? 0L : latestEdges.Max();
        var maxWindowStart = windowStarts.Length == 0 ? 0L : windowStarts.Max();
        var minWindowEnd = minLatest;
        var overlapNs = minWindowEnd > 0 && maxWindowStart > 0
            ? minWindowEnd - maxWindowStart
            : 0L;
        var domainEdgeNs = maxLatest;
        var provisionalOffsetToReferenceNs = referenceEdgeNs > 0 && domainEdgeNs > 0
            ? referenceEdgeNs - domainEdgeNs
            : 0L;

        return new ClockDomainDiagnostics(
            clockDomainId,
            buffers.Count,
            buffers
                .OrderBy(static buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal)
                .Select(buffer => new
                {
                    buffer.Descriptor.SourceId,
                    Kind = buffer.Descriptor.Kind.ToString(),
                    Origin = buffer.Descriptor.Origin.ToString(),
                    buffer.Count,
                    buffer.WindowStartNs,
                    buffer.OldestSampleTimestampNs,
                    EdgeNs = buffer.Latest?.TimestampNs ?? 0L,
                    ByteLength = buffer.Latest?.ByteLength ?? 0,
                })
                .ToArray(),
            maxWindowStart,
            minLatest,
            maxLatest,
            overlapNs,
            overlapNs >= 0,
            string.Equals(clockDomainId, referenceClockDomain, StringComparison.Ordinal),
            provisionalOffsetToReferenceNs,
            provisionalOffsetToReferenceNs / 1_000_000.0);
    }

    private sealed record ClockDomainDiagnostics(
        string ClockDomainId,
        int SourceCount,
        object[] Sources,
        long MaxWindowStartNs,
        long MinLatestEdgeNs,
        long MaxLatestEdgeNs,
        long OverlapNs,
        bool HasLocalOverlap,
        bool IsReferenceDomain,
        long ProvisionalOffsetToReferenceNs,
        double ProvisionalOffsetToReferenceMs);
}

internal static class MimirWellSnapshot
{
    public static object Build(
        MimirWellOptions options,
        MimirRuntimeConfiguration runtimeConfig,
        MimirSynchronizationHub hub,
        MimirPresentationControlState presentation,
        MimirWellFrameBuildResult frameResult,
        MimirWellLatencyDecision latencyDecision,
        MimirWellFeatureSignalFrame featureSignals,
        IReadOnlyList<MimirCameraExposureControlStatus> visualCalibration,
        IReadOnlyList<object> sourceErrors,
        object streamPressure,
        long sequence,
        DateTimeOffset startedAt)
    {
        var frame = frameResult.Frame;
        return new
        {
        type = "cultmesh-observation",
        document = "mimir.well_snapshot.v1",
        sourceId = "mimir-well",
        nodeId = options.NodeId,
        sequence,
        wallClockUtc = DateTimeOffset.UtcNow.ToString("O"),
        elapsedSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
        ingestedSamples = hub.IngestedSamples,
        configuredSources = runtimeConfig.SourceFactories.Count,
        liveSources = hub.SourceCount,
        sourceErrors,
        sourceHealth = runtimeConfig.SourceFactories
            .Select(factory => SourceHealthSnapshot(factory, hub.Buffers.Buffers, sourceErrors, startedAt))
            .ToArray(),
        buffers = hub.Buffers.Buffers.Select(BufferSnapshot).ToArray(),
        synchronizedFrame = new
        {
            frame.PresentationTimeNs,
            frame.WindowStartNs,
            frame.WindowEndNs,
            PresentationDelayMs = frame.PresentationDelay.TotalMilliseconds,
            frame.IsComplete,
            degradedKind = frameResult.DegradedKind,
            degradedReason = frameResult.DegradedReason,
            slices = frame.Slices.Select(SliceSnapshot).ToArray(),
        },
        latency = new
        {
            document = "mimir.well_latency_policy.v1",
            mode = "adaptive-bounded-ceiling",
            currentDelayMs = latencyDecision.PresentationDelay.TotalMilliseconds,
            ceilingDelayMs = latencyDecision.CeilingDelay.TotalMilliseconds,
            floorDelayMs = latencyDecision.FloorDelay.TotalMilliseconds,
            retainedOverlapMs = latencyDecision.RetainedOverlap.TotalMilliseconds,
            edgeSkewMs = latencyDecision.EdgeSkew.TotalMilliseconds,
            latencyDecision.ReadinessConfidence,
            latencyDecision.SyncConfidence,
            latencyDecision.ActiveBufferCount,
            latencyDecision.ReadySliceCount,
            latencyDecision.TotalSliceCount,
            latencyDecision.Reason,
            invariant = "Five seconds is a ceiling; the Well lowers requested presentation delay when overlap, slice readiness, and sync evidence allow it.",
        },
        clockDomains = MimirWellClockDiagnostics.Build(
            hub.Buffers.Buffers,
            runtimeConfig.Settings.Audio.ReferenceSourceId,
            frameResult),
        canonicalClockMaps = hub.CanonicalClockMaps.Select(map => new
        {
            map.StreamKey,
            map.OffsetNs,
            offsetMs = map.OffsetNs / 1_000_000.0,
            map.FirstSourceTimestampNs,
            map.FirstArrivalNs,
            map.LatestSourceTimestampNs,
            map.LatestCanonicalTimestampNs,
            map.SampleCount,
        }).ToArray(),
        streamPressure,
        featureSignals = new
        {
            featureSignals.Document,
            featureSignals.Sequence,
            featureSignals.MeanConfidence,
            featureSignals.MeanMotionPixelsPerSecond,
            featureSignals.StableTrackCount,
            featureSignals.FaustSignalContract,
            signals = featureSignals.Signals.Select(signal => new
            {
                signal.SourceId,
                signal.TimestampNs,
                signal.Width,
                signal.Height,
                signal.StableTrackCount,
                signal.Confidence,
                signal.MeanMotionPixelsPerSecond,
                signal.MotionEnergy,
                signal.NormalizedCentroidX,
                signal.NormalizedCentroidY,
                faustControls = signal.FaustControls.Select(pair => new
                {
                    path = pair.Key,
                    value = pair.Value,
                }).ToArray(),
            }).ToArray(),
        },
        audioSync = new
        {
            referenceSourceId = runtimeConfig.Settings.Audio.ReferenceSourceId,
            mode = runtimeConfig.Settings.Audio.Mode.ToString(),
            complexContour = hub.ComplexContourRuntimeEnabled,
            complexContourInline = MimirWellEnvironment.IsTruthy(Environment.GetEnvironmentVariable("MIMIR_COMPLEX_CONTOUR_RUNTIME_INLINE")),
            states = hub.AudioSynchronizationStates.Select(state => new
            {
                state.SourceId,
                state.ReferenceSourceId,
                state.SampleRate,
                state.SmoothedDelaySamples,
                state.SamplingRateOffsetPpm,
                state.Confidence,
                evidenceKind = "audio-sync",
            }).ToArray(),
            reports = hub.AudioSynchronizationReports.Select(report => new
            {
                report.SourceId,
                report.ReferenceSourceId,
                report.EvidenceKind,
                report.FractionalDelaySamples,
                report.Confidence,
                matchedEvents = report.TimelineMatchedEvents,
                report.ComparedSamples,
                report.AnalysisTimestampNs,
            }).ToArray(),
            probe = hub.LastBioacousticProbeSchedule is null
                ? null
                : new
                {
                    hub.LastBioacousticProbeSchedule.TimestampNs,
                    hub.LastBioacousticProbeSchedule.ShouldEmit,
                    hub.LastBioacousticProbeSchedule.ScheduledIntervalSeconds,
                    hub.LastBioacousticProbeSchedule.AggregateSyncConfidence,
                    hub.LastBioacousticProbeSchedule.AggregateFrequencyResponseConfidence,
                    reason = hub.LastBioacousticProbeSchedule.Reason.ToString(),
            },
        },
        visualCalibration = new
        {
            enabled = options.VisualCalibrationEnabled,
            expectedLedCount = options.VisualExpectedLedCount,
            minimumLuma = options.VisualMinimumLuma,
            settingSeconds = options.VisualSettingSeconds,
            resweepSeconds = options.VisualResweepSeconds,
            cameras = visualCalibration.Select(camera => new
            {
                camera.SourceId,
                camera.ControlKind,
                camera.SupportsExposureGain,
                camera.State,
                camera.CurrentSettingId,
                camera.CurrentExposure,
                camera.CurrentGain,
                camera.BestSettingId,
                camera.BestExposure,
                camera.BestGain,
                camera.BestScore,
                camera.BestDetectedLedCount,
                camera.BestUsableForCalibration,
                camera.FramesScored,
                camera.LastApplySucceeded,
                camera.Reason,
            }).ToArray(),
        },
        composite = new
        {
            video = presentation.VideoFeeds.Select(feed => new
            {
                feed.SourceId,
                feed.DisplayName,
                feed.Enabled,
                feed.Available,
                feed.SampleCount,
                Included = presentation.IncludesVideo(feed.SourceId),
                feed.Solo,
                feed.Opacity,
                feed.Layer,
            }).ToArray(),
            audio = presentation.AudioFeeds.Select(feed => new
            {
                feed.SourceId,
                feed.DisplayName,
                feed.Muted,
                feed.Solo,
                feed.Gain,
            }).ToArray(),
            postprocess = presentation.Postprocess,
        },
        publication = new
        {
            MimirObsPublicationConfigurations.NativeProgram.Id,
            MimirObsPublicationConfigurations.NativeProgram.VideoKind,
            MimirObsPublicationConfigurations.NativeProgram.VideoSourceName,
            MimirObsPublicationConfigurations.NativeProgram.AudioKind,
            MimirObsPublicationConfigurations.NativeProgram.TargetPresentationDelaySeconds,
            stems = MimirObsPublicationConfigurations.NativeProgram.AudioStems.Select(stem => new
            {
                stem.Id,
                stem.DisplayName,
                stem.ChannelCount,
                stem.UserMixable,
                stem.CarriesTimingWitness,
                stem.Notes,
            }).ToArray(),
        },
    };
    }

    private static object SourceHealthSnapshot(
        MimirStreamSourceFactory factory,
        IEnumerable<MimirRollingStreamBuffer> buffers,
        IReadOnlyList<object> sourceErrors,
        DateTimeOffset startedAt)
    {
        var descriptor = factory.Descriptor;
        var diagnostics = factory.Diagnostics;
        var matchingBuffers = buffers
            .Where(buffer => string.Equals(buffer.Descriptor.SourceId, descriptor.SourceId, StringComparison.Ordinal))
            .ToArray();
        if (matchingBuffers.Length == 0 && descriptor.SourceId.Contains("asio", StringComparison.OrdinalIgnoreCase))
        {
            matchingBuffers = buffers
                .Where(buffer =>
                    buffer.Descriptor.Kind == descriptor.Kind &&
                    buffer.Descriptor.Origin == descriptor.Origin &&
                    buffer.Descriptor.SourceId.StartsWith("asio-ch", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var samples = matchingBuffers.Sum(static buffer => buffer.Count);
        var latest = matchingBuffers
            .Select(static buffer => buffer.Latest)
            .Where(static sample => sample.HasValue)
            .OrderByDescending(static sample => sample!.Value.TimestampNs)
            .FirstOrDefault();
        var errored = sourceErrors.Any(error =>
            TryReadAnonymousString(error, "SourceId", "sourceId") is { } id &&
            string.Equals(id, descriptor.SourceId, StringComparison.Ordinal));
        var elapsedMs = Math.Max(0.0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        var latestAgeMs = latest.HasValue
            ? Math.Round(Math.Max(0.0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000.0 - latest.Value.TimestampNs) / 1_000_000.0), 1)
            : Math.Round(elapsedMs, 1);
        var status = errored
            ? "create-error"
            : samples > 0 && latestAgeMs > 2_000.0
                ? "stalled"
                : samples > 0
                    ? "live"
                    : descriptor.Origin == MimirStreamOrigin.Network
                        ? "waiting-network"
                        : "waiting-device-samples";

        return new
        {
            descriptor.SourceId,
            Kind = descriptor.Kind.ToString(),
            Origin = descriptor.Origin.ToString(),
            descriptor.Label,
            configured = diagnostics is null
                ? null
                : new
                {
                    diagnostics.Adapter,
                    diagnostics.Command,
                    diagnostics.PathNeedle,
                    diagnostics.Width,
                    diagnostics.Height,
                    diagnostics.InputFormat,
                    diagnostics.OutputFormat,
                    diagnostics.PixelFormat,
                    diagnostics.MinimumFramesPerSecond,
                    diagnostics.FramesPerSecond,
                    diagnostics.SampleRate,
                    diagnostics.Channels,
                    diagnostics.QueueDepth,
                    acceptSourceIds = diagnostics.AcceptSourceIds,
                },
            status,
            reason = status switch
            {
                "create-error" => "source creation failed; see sourceErrors",
                "stalled" => "source emitted samples earlier but latest sample is stale",
                "waiting-network" => "network source listener is configured but no producer samples have arrived",
                "waiting-device-samples" when descriptor.Kind == MimirStreamKind.Video && IsKsCamera(diagnostics) =>
                    KsCameraWaitingReason(diagnostics),
                "waiting-device-samples" => "local device source is configured but has not emitted a sample",
                _ => "samples arriving",
            },
            buffers = matchingBuffers.Length,
            samples,
            latestTimestampNs = latest?.TimestampNs ?? 0L,
            latestAgeMs,
            latestByteLength = latest?.ByteLength ?? 0,
        };
    }

    private static bool IsKsCamera(MimirStreamSourceDiagnostics? diagnostics) =>
        string.Equals(diagnostics?.Adapter, "ks-camera", StringComparison.OrdinalIgnoreCase);

    private static string KsCameraWaitingReason(MimirStreamSourceDiagnostics? diagnostics) =>
        diagnostics is null
            ? "KS camera opened but no frames have arrived; verify device ownership/driver"
            : $"KS camera opened but no frames have arrived; verify device ownership/driver for {diagnostics.Width}x{diagnostics.Height} {diagnostics.InputFormat}";

    private static string? TryReadAnonymousString(object value, params string[] names)
    {
        var type = value.GetType();
        foreach (var name in names)
        {
            if (type.GetProperty(name)?.GetValue(value) is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static object BufferSnapshot(MimirRollingStreamBuffer buffer) => new
    {
        buffer.Descriptor.SourceId,
        Kind = buffer.Descriptor.Kind.ToString(),
        Origin = buffer.Descriptor.Origin.ToString(),
        buffer.Descriptor.Label,
        buffer.Descriptor.EffectiveClockDomainId,
        buffer.Count,
        buffer.EdgeNs,
        buffer.OldestSampleTimestampNs,
        buffer.WindowStartNs,
        hasLatest = buffer.Latest.HasValue,
        latest = buffer.Latest.HasValue ? SampleSnapshot(buffer.Latest.Value) : null,
    };

    private static object SliceSnapshot(MimirSynchronizedStreamSlice slice) => new
    {
        slice.SourceId,
        Kind = slice.Kind.ToString(),
        Origin = slice.Origin.ToString(),
        Status = slice.Status.ToString(),
        slice.SourceTimestampNs,
        slice.CanonicalStartNs,
        slice.CanonicalEndNs,
        slice.PresentationTimeNs,
        slice.TimingOffsetNs,
        slice.DistanceFromPresentationNs,
        slice.TimingConfidence,
        slice.TimingEvidenceKind,
        sample = slice.Sample.HasValue ? SampleSnapshot(slice.Sample.Value) : null,
    };

    private static object SampleSnapshot(MimirStreamSample sample) => new
    {
        sample.SourceId,
        Kind = sample.Kind.ToString(),
        Origin = sample.Origin.ToString(),
        sample.TimestampNs,
        sample.ArrivalNs,
        sample.Sequence,
        sample.PayloadHandle,
        sample.ByteLength,
        video = sample.VideoFrame is null ? null : new
        {
            sample.VideoFrame.Width,
            sample.VideoFrame.Height,
            PixelFormat = sample.VideoFrame.PixelFormat.ToString(),
            sample.VideoFrame.StrideBytes,
            sample.VideoFrame.DeviceTimestampNs,
            sample.VideoFrame.NativeHandle,
            sample.VideoFrame.NativeHandleKind,
            sample.VideoFrame.ResourceKey,
            sample.VideoFrame.ProducerFenceHandle,
            sample.VideoFrame.ProducerFenceValue,
            sample.VideoFrame.UnavoidableCopyCount,
        },
        audio = sample.AudioBlock is null ? null : new
        {
            sample.AudioBlock.SampleRate,
            sample.AudioBlock.Channels,
            SampleFormat = sample.AudioBlock.SampleFormat.ToString(),
            sample.AudioBlock.FrameCount,
            sample.AudioBlock.DeviceTimestampNs,
            sample.AudioBlock.NativeHandle,
            sample.AudioBlock.NativeHandleKind,
        },
    };
}
