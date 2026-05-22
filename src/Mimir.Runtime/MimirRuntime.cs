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
    private static readonly MimirChirpletCalibrationPhrase CalibrationPhrase = MimirChirpletCalibrationPhrase.Default;
    private readonly LocalCastRuntime visualRuntime;
    private readonly MimirSynchronizationHub synchronization;
    private readonly AquariumUiDocument ui;
    private int lastPollCount;
    private float runtimeSeconds;
    private float nextCalibrationChirpSeconds = (float)CalibrationPhrase.FirstFireSeconds;
    private string? calibrationPhrasePcm16Base64;

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
        QueueCalibrationChirps();
        lastPollCount = synchronization.PollSources();
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
                panel.Readout("Chirplet reference", () => $"{DefaultAudioSyncReference} every {CalibrationPhrase.IntervalSeconds:0.0}s");
            });
    }

    private void QueueCalibrationChirps()
    {
        while (runtimeSeconds >= nextCalibrationChirpSeconds)
        {
            visualRuntime.Audio.EnqueuePcm16Base64(
                calibrationPhrasePcm16Base64 ??= CalibrationPhrase.RenderPcm16Base64(),
                CalibrationPhrase.SampleRate,
                channels: 1,
                gain: 1.0f);
            nextCalibrationChirpSeconds += (float)CalibrationPhrase.IntervalSeconds;
        }
    }

    private string DescribeBuffers()
    {
        return string.Join(" | ", synchronization.Buffers.Buffers.Select(DescribeBuffer));
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
