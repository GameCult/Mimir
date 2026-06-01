using System.Net.WebSockets;
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
var presentation = new MimirPresentationControlState();
var exposureController = new MimirCameraExposureController(new MimirCameraExposureControlOptions(
    options.VisualCalibrationEnabled,
    options.VisualExpectedLedCount,
    options.VisualMinimumLuma,
    options.VisualSettingSeconds,
    options.VisualResweepSeconds));
Console.Error.WriteLine($"Mimir Well publishing to {options.PublishUrl}");
Console.Error.WriteLine($"Mimir Well sources={runtimeConfig.SourceFactories.Count} buffers={hub.Buffers.Buffers.Count}");

while (!stopping.IsCancellationRequested)
{
    hub.PollSources(options.MaxSamplesPerSource);
    var now = DateTimeOffset.UtcNow;
    if (now >= nextSync)
    {
        if (!string.IsNullOrWhiteSpace(runtimeConfig.Settings.Audio.ReferenceSourceId))
        {
            hub.AnalyzeAudioSynchronizationStep(
                runtimeConfig.Settings.Audio.ReferenceSourceId,
                runtimeConfig.Settings.Audio.Mode,
                options.SyncCandidatesPerStep);
            hub.AnalyzeComplexContourSynchronizationStep(
                runtimeConfig.Settings.Audio.ReferenceSourceId,
                (now - startedAt).TotalSeconds,
                options.SyncCandidatesPerStep);
        }

        hub.UpdateBioacousticProbeSchedule(NowNs());
        nextSync = now + TimeSpan.FromMilliseconds(options.SyncIntervalMs);
    }

    if (now >= nextVisualCalibration)
    {
        exposureController.Update(now, hub.Buffers.Buffers, hub.CameraExposureGainActuators);
        nextVisualCalibration = now + TimeSpan.FromMilliseconds(options.VisualCalibrationIntervalMs);
    }

    if (now >= nextPublish)
    {
        presentation.SyncFromBuffers(hub.Buffers.Buffers);
        var frame = BuildFrameOrEmpty(hub, TimeSpan.FromMilliseconds(options.PresentationDelayMs));
        var snapshot = MimirWellSnapshot.Build(
            options,
            runtimeConfig,
            hub,
            presentation,
            frame,
            exposureController.Statuses,
            sourceErrors,
            ++sequence,
            startedAt);
        await publisher.PublishAsync(snapshot, stopping.Token).ConfigureAwait(false);
        if (options.CapturePagesEnabled && now >= nextCapture)
        {
            var capturePage = MimirWellCapturePage.Build(
                options,
                presentation,
                frame,
                hub.Buffers.Buffers,
                ++captureSequence,
                startedAt);
            await publisher.PublishAsync(capturePage, stopping.Token).ConfigureAwait(false);
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

static MimirSynchronizedBufferFrame BuildFrameOrEmpty(MimirSynchronizationHub hub, TimeSpan presentationDelay)
{
    try
    {
        return hub.BuildSynchronizedBufferFrame(presentationDelay);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"mimir-well synchronized-frame degraded: {ex.Message}");
        return new MimirSynchronizedBufferFrame(0, 0, 0, presentationDelay, []);
    }
    catch (OverflowException ex)
    {
        Console.Error.WriteLine($"mimir-well synchronized-frame degraded: {ex.Message}");
        return new MimirSynchronizedBufferFrame(0, 0, 0, presentationDelay, []);
    }
}

internal sealed record MimirWellOptions(
    Uri PublishUrl,
    string NodeId,
    double Seconds,
    int PollMs,
    int PublishIntervalMs,
    int SyncIntervalMs,
    int PresentationDelayMs,
    int MaxSamplesPerSource,
    int SyncCandidatesPerStep,
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
        ParseInt(args, "--max-samples-per-source", 4),
        ParseInt(args, "--sync-candidates", 1),
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

    public async Task ConnectAsync(CancellationToken stopping)
    {
        await socket.ConnectAsync(url, stopping).ConfigureAwait(false);
    }

    public async Task PublishAsync(object document, CancellationToken stopping)
    {
        var json = JsonSerializer.Serialize(document);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, stopping).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
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
    }
}

internal static class MimirWellCapturePage
{
    public static object Build(
        MimirWellOptions options,
        MimirPresentationControlState presentation,
        MimirSynchronizedBufferFrame frame,
        IEnumerable<MimirRollingStreamBuffer> buffers,
        long captureSequence,
        DateTimeOffset startedAt) => new
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
            body = CaptureBody(options, sample),
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
            body = CaptureBody(options, sample),
        };
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

    private static object CaptureBody(MimirWellOptions options, MimirStreamSample sample)
    {
        if (!options.CaptureInlineBodies)
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

internal static class MimirWellSnapshot
{
    public static object Build(
        MimirWellOptions options,
        MimirRuntimeConfiguration runtimeConfig,
        MimirSynchronizationHub hub,
        MimirPresentationControlState presentation,
        MimirSynchronizedBufferFrame frame,
        IReadOnlyList<MimirCameraExposureControlStatus> visualCalibration,
        IReadOnlyList<object> sourceErrors,
        long sequence,
        DateTimeOffset startedAt) => new
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
        buffers = hub.Buffers.Buffers.Select(BufferSnapshot).ToArray(),
        synchronizedFrame = new
        {
            frame.PresentationTimeNs,
            frame.WindowStartNs,
            frame.WindowEndNs,
            PresentationDelayMs = frame.PresentationDelay.TotalMilliseconds,
            frame.IsComplete,
            slices = frame.Slices.Select(SliceSnapshot).ToArray(),
        },
        audioSync = new
        {
            referenceSourceId = runtimeConfig.Settings.Audio.ReferenceSourceId,
            mode = runtimeConfig.Settings.Audio.Mode.ToString(),
            complexContour = hub.ComplexContourRuntimeEnabled,
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

    private static object BufferSnapshot(MimirRollingStreamBuffer buffer) => new
    {
        buffer.Descriptor.SourceId,
        Kind = buffer.Descriptor.Kind.ToString(),
        Origin = buffer.Descriptor.Origin.ToString(),
        buffer.Descriptor.Label,
        buffer.Descriptor.EffectiveClockDomainId,
        buffer.Count,
        buffer.EdgeNs,
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
