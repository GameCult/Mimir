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
    private readonly LocalCastRuntime visualRuntime;
    private readonly MimirSynchronizationHub synchronization;
    private readonly AquariumUiDocument ui;
    private int lastPollCount;

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
                foreach (var buffer in synchronization.Buffers.Buffers)
                {
                    panel.Readout(
                        buffer.Descriptor.SourceId,
                        () => $"{buffer.Descriptor.Kind}/{buffer.Descriptor.Origin} {buffer.Count} samples edge {buffer.EdgeNs}");
                }
            });
    }
}
