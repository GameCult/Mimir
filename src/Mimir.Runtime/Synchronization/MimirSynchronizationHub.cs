namespace Mimir.Runtime.Synchronization;

public sealed class MimirSynchronizationHub : IDisposable
{
    private readonly List<IMimirStreamSource> sources = [];
    private readonly MimirAudioSynchronizationAnalyzer audioSynchronization = new();
    private readonly MimirAudioSynchronizationStateTracker audioSynchronizationState = new();
    private ulong ingestedSamples;

    public MimirSynchronizationHub(MimirSynchronizationSettings settings)
    {
        Settings = settings;
        Buffers = new MimirStreamBufferSet(settings.BufferDuration);
        foreach (var stream in settings.Streams.Where(stream => stream.Enabled))
        {
            Buffers.EnsureBuffer(stream);
        }
    }

    public MimirSynchronizationSettings Settings { get; }

    public MimirStreamBufferSet Buffers { get; }

    public ulong IngestedSamples => ingestedSamples;

    public int SourceCount => sources.Count;

    public IReadOnlyList<MimirAudioSynchronizationState> AudioSynchronizationStates =>
        audioSynchronizationState.States;

    public void AddSource(IMimirStreamSource source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        sources.Add(source);
        if (source.ExposesDescriptorBuffer)
        {
            Buffers.EnsureBuffer(source.Descriptor);
        }
    }

    public int PollSources(int maxSamplesPerSource = 256)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var consumed = 0;
        foreach (var source in sources)
        {
            for (var index = 0; index < maxSamplesPerSource && source.TryRead(out var sample); index++)
            {
                Buffers.Append(sample);
                consumed++;
                ingestedSamples++;
            }
        }

        return consumed;
    }

    public string Summary()
    {
        var audio = Buffers.Buffers.Count(buffer => buffer.Descriptor.Kind == MimirStreamKind.Audio);
        var video = Buffers.Buffers.Count(buffer => buffer.Descriptor.Kind == MimirStreamKind.Video);
        return $"{video} video / {audio} audio buffers";
    }

    public IReadOnlyList<MimirAudioSynchronizationReport> AnalyzeAudioSynchronization(
        string referenceSourceId,
        double approximateTimelineSeconds = double.PositiveInfinity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var reports = audioSynchronization.Analyze(Buffers.Buffers, referenceSourceId, approximateTimelineSeconds);
        audioSynchronizationState.Update(reports);
        return reports;
    }

    public MimirAlignedAudioFrame? BuildAlignedAudioFrame(
        string referenceSourceId,
        int frameCount = 4_800,
        double minimumConfidence = 0.10)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return audioSynchronization.BuildAlignedFrame(
            Buffers.Buffers,
            referenceSourceId,
            frameCount,
            minimumConfidence);
    }

    private bool disposed;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var source in sources)
        {
            source.Dispose();
        }

        sources.Clear();
        disposed = true;
    }
}
