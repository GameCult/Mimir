using Aquarium.Engine;
using Aquarium.Engine.Audio;
using Aquarium.Engine.Input;
using Aquarium.Engine.Render;
using Aquarium.Engine.Ui;
using Aquarium.LocalCast;
using Mimir.Runtime.Synchronization;

namespace Mimir.Runtime;

public sealed class MimirRuntime : IAquariumRuntime
{
    private const string DefaultAudioSyncReference = "loopback-scarlett-speakers";
    private const float AudioSyncUpdateIntervalSeconds = 0.5f;
    private readonly LocalCastRuntime visualRuntime;
    private readonly MimirSynchronizationHub synchronization;
    private readonly AquariumUiDocument ui;
    private readonly float telemetryIntervalSeconds;
    private int lastPollCount;
    private float runtimeSeconds;
    private float nextAudioSyncSeconds = AudioSyncUpdateIntervalSeconds;
    private float nextTelemetrySeconds;
    private ulong calibrationSegmentIndex;
    private IReadOnlyList<MimirAudioSynchronizationReport> lastAudioSynchronizationReports = [];

    public MimirRuntime(AquariumRuntimeOptions options)
        : this(options, MimirRuntimeConfiguration.Load())
    {
    }

    public MimirRuntime(AquariumRuntimeOptions options, MimirRuntimeConfiguration configuration)
        : this(options, configuration.Settings, configuration.Sources)
    {
    }

    public MimirRuntime(AquariumRuntimeOptions options, MimirSynchronizationSettings settings)
        : this(options, settings, [])
    {
    }

    public MimirRuntime(
        AquariumRuntimeOptions options,
        MimirSynchronizationSettings settings,
        IEnumerable<IMimirStreamSource> streamSources)
    {
        Options = options;
        visualRuntime = new LocalCastRuntime(options);
        synchronization = new MimirSynchronizationHub(settings);
        telemetryIntervalSeconds = ParseTelemetryIntervalSeconds();
        nextTelemetrySeconds = telemetryIntervalSeconds;
        foreach (var source in streamSources)
        {
            synchronization.AddSource(source);
        }

        ui = CreateUi();
    }

    public AquariumRuntimeOptions Options { get; }

    public AquariumFrame Frame => visualRuntime.Frame;

    public GraphicsSettings GraphicsSettings
    {
        get => visualRuntime.GraphicsSettings;
        set => visualRuntime.GraphicsSettings = value;
    }

    public AquariumRenderPlan RenderPlan => visualRuntime.RenderPlan;

    public AquariumUiDocument Ui => ui;

    public AquariumAudioDocument Audio => visualRuntime.Audio;

    public AquariumSynthDocument Synth => visualRuntime.Synth;

    public void RegisterStreamSource(IMimirStreamSource source)
    {
        synchronization.AddSource(source);
    }

    public void Start()
    {
        Console.WriteLine($"Mimir runtime sync buffers: {synchronization.Summary()} @ {synchronization.Settings.BufferDuration.TotalSeconds:0.###}s");
        visualRuntime.Start();
    }

    public void Update(float deltaSeconds, InputState input)
    {
        runtimeSeconds += Math.Max(deltaSeconds, 0.0f);
        QueueCalibrationTimeline();
        lastPollCount = synchronization.PollSources();
        UpdateAudioSynchronization();
        EmitTelemetry();
        visualRuntime.Update(deltaSeconds, input);
    }

    public AquariumFrame ComposeFrame(AquariumFrame frame, AquariumFrameInput input)
    {
        return visualRuntime.ComposeFrame(frame, input);
    }

    public void FlushState()
    {
        visualRuntime.FlushState();
    }

    public void Dispose()
    {
        synchronization.Dispose();
        visualRuntime.Dispose();
    }

    private AquariumUiDocument CreateUi()
    {
        return new AquariumUiDocument()
            .Panel("Mimir Sync", 18.0f, 82.0f, 390.0f, panel =>
            {
                panel.Section("Rolling Buffers");
                panel.Readout("Window", () => $"{synchronization.Settings.BufferDuration.TotalSeconds:0.###}s");
                panel.Readout("Streams", synchronization.Summary);
                panel.Readout("Sources", () => $"{synchronization.SourceCount}");
                panel.Readout("Last poll", () => $"{lastPollCount} samples");
                panel.Readout("Ingested", () => $"{synchronization.IngestedSamples}");
                panel.Readout("Buffer details", DescribeBuffers);
                panel.Readout("Audio sync", DescribeAudioSync);
                panel.Readout("Audio sync state", DescribeAudioSyncState);
                panel.Readout("Aligned audio", DescribeAlignedAudio);
                panel.Readout("Chirplet reference", DescribeChirpletReference);
            });
    }

    private void QueueCalibrationTimeline()
    {
        var queuedUntilSeconds = calibrationSegmentIndex * MimirChirpletTimeline.SegmentSeconds;
        var targetSeconds = runtimeSeconds + (float)MimirChirpletTimeline.QueueLeadSeconds;
        while (queuedUntilSeconds < targetSeconds)
        {
            visualRuntime.Audio.EnqueuePcm16Base64(
                MimirChirpletTimeline.Default.RenderSegmentPcm16Base64(calibrationSegmentIndex),
                MimirChirpletTimeline.SampleRate,
                channels: 1,
                gain: 1.0f);
            calibrationSegmentIndex++;
            queuedUntilSeconds = calibrationSegmentIndex * MimirChirpletTimeline.SegmentSeconds;
        }
    }

    private string DescribeChirpletReference()
    {
        var queuedUntilSeconds = calibrationSegmentIndex * MimirChirpletTimeline.SegmentSeconds;
        return $"{DefaultAudioSyncReference} continuous timeline {MimirChirpletTimeline.SegmentSeconds:0.00}s segments queued to {queuedUntilSeconds:0.00}s";
    }

    private string DescribeBuffers()
    {
        return string.Join(" | ", synchronization.Buffers.Buffers.Select(DescribeBuffer));
    }

    private void UpdateAudioSynchronization()
    {
        if (runtimeSeconds < nextAudioSyncSeconds)
        {
            return;
        }

        lastAudioSynchronizationReports = synchronization.AnalyzeAudioSynchronization(DefaultAudioSyncReference);
        nextAudioSyncSeconds += AudioSyncUpdateIntervalSeconds;
    }

    private void EmitTelemetry()
    {
        if (telemetryIntervalSeconds <= 0.0f || runtimeSeconds < nextTelemetrySeconds)
        {
            return;
        }

        var loopback = synchronization.Buffers.Buffers.FirstOrDefault(buffer =>
            string.Equals(buffer.Descriptor.SourceId, DefaultAudioSyncReference, StringComparison.Ordinal));
        var states = synchronization.AudioSynchronizationStates;
        Console.WriteLine(
            $"mimir-sync-telemetry t={runtimeSeconds:0.00}s loopbackCount={loopback?.Count ?? 0} loopbackEdgeNs={loopback?.EdgeNs ?? 0} reports={lastAudioSynchronizationReports.Count} states={states.Count} aligned={DescribeAlignedAudio()}");
        Console.WriteLine($"mimir-sync-buffers {DescribeAudioBuffers()}");
        foreach (var report in lastAudioSynchronizationReports.OrderBy(report => report.SourceId, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"mimir-sync-report {report.ReferenceSourceId}->{report.SourceId} delaySamples={report.FractionalDelaySamples:0.000} delayMs={report.DelayMilliseconds:0.000} confidence={report.Confidence:0.000}");
        }

        foreach (var state in states)
        {
            Console.WriteLine(
                $"mimir-sync-state {state.ReferenceSourceId}->{state.SourceId} delaySamples={state.SmoothedDelaySamples:0.000} delayMs={state.DelayMilliseconds:0.000} sroPpm={state.SamplingRateOffsetPpm:0.000} confidence={state.Confidence:0.000}");
        }

        nextTelemetrySeconds += telemetryIntervalSeconds;
    }

    private static float ParseTelemetryIntervalSeconds()
    {
        return float.TryParse(Environment.GetEnvironmentVariable("MIMIR_SYNC_TELEMETRY_SECONDS"), out var seconds)
            ? Math.Clamp(seconds, 0.0f, 60.0f)
            : 0.0f;
    }

    private string DescribeAudioBuffers()
    {
        return string.Join(" | ", synchronization.Buffers.Buffers
            .Where(buffer => buffer.Descriptor.Kind == MimirStreamKind.Audio)
            .OrderBy(buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal)
            .Select(buffer => $"{buffer.Descriptor.SourceId}:{buffer.Count}@{buffer.EdgeNs}"));
    }

    private static string DescribeBuffer(MimirRollingStreamBuffer buffer)
    {
        var latest = buffer.Latest;
        if (latest?.VideoFrame is { } frame)
        {
            return $"{buffer.Descriptor.SourceId}: {buffer.Count} {frame.Width}x{frame.Height} {frame.PixelFormat} bytes {latest.Value.ByteLength} edge {buffer.EdgeNs}";
        }

        if (latest?.AudioBlock is { } block)
        {
            return $"{buffer.Descriptor.SourceId}: {buffer.Count} {block.Channels}ch {block.SampleRate}Hz {block.SampleFormat} frames {block.FrameCount} bytes {latest.Value.ByteLength} edge {buffer.EdgeNs}";
        }

        return $"{buffer.Descriptor.SourceId}: {buffer.Count} edge {buffer.EdgeNs}";
    }

    private string DescribeAudioSync()
    {
        var reports = synchronization.AnalyzeAudioSynchronization(DefaultAudioSyncReference);
        return reports.Count == 0
            ? "no payload windows"
            : string.Join(" | ", reports.Select(report => $"{report.SourceId}: {report.FractionalDelaySamples:0.0} samples {report.DelayMilliseconds:0.00}ms c={report.Confidence:0.00}"));
    }

    private string DescribeAlignedAudio()
    {
        var frame = synchronization.BuildAlignedAudioFrame(DefaultAudioSyncReference);
        return frame == null
            ? "no aligned frame"
            : $"{frame.Channels.Count}ch {frame.SampleRate}Hz {frame.FrameCount} frames";
    }

    private string DescribeAudioSyncState()
    {
        var states = synchronization.AudioSynchronizationStates;
        return states.Count == 0
            ? "no sync state"
            : string.Join(" | ", states.Select(state => $"{state.SourceId}: {state.SmoothedDelaySamples:0.0} samples sro={state.SamplingRateOffsetPpm:0.0}ppm c={state.Confidence:0.00}"));
    }
}
