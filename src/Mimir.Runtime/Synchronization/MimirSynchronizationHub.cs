namespace Mimir.Runtime.Synchronization;

public sealed class MimirSynchronizationHub : IDisposable
{
    private readonly List<IMimirStreamSource> sources = [];
    private readonly MimirAudioSynchronizationAnalyzer audioSynchronization = new();
    private readonly MimirAudioSynchronizationStateTracker audioSynchronizationState = new();
    private readonly Dictionary<string, MimirAudioSynchronizationReport> audioSynchronizationReports = new(StringComparer.Ordinal);
    private ulong ingestedSamples;
    private int nextAudioSynchronizationCandidate;

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

    public IReadOnlyList<MimirAudioSynchronizationReport> AudioSynchronizationReports =>
        audioSynchronizationReports.Values.OrderBy(report => report.SourceId, StringComparer.Ordinal).ToArray();

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
        string referenceSourceId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var reports = audioSynchronization.Analyze(Buffers.Buffers, referenceSourceId);
        StoreAudioSynchronizationReports(reports);
        return reports;
    }

    public IReadOnlyList<MimirAudioSynchronizationReport> AnalyzeAudioSynchronizationStep(
        string referenceSourceId,
        int maxCandidates = 1)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var candidates = Buffers.Buffers
            .Where(buffer =>
                buffer.Descriptor.Kind == MimirStreamKind.Audio &&
                !string.Equals(buffer.Descriptor.SourceId, referenceSourceId, StringComparison.Ordinal) &&
                buffer.Latest?.AudioBlock != null)
            .OrderBy(buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal)
            .Select(buffer => buffer.Descriptor.SourceId)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var selected = new HashSet<string>(StringComparer.Ordinal);
        for (var count = 0; count < Math.Min(maxCandidates, candidates.Length); count++)
        {
            selected.Add(candidates[nextAudioSynchronizationCandidate % candidates.Length]);
            nextAudioSynchronizationCandidate++;
        }

        var reports = audioSynchronization.Analyze(
            Buffers.Buffers,
            referenceSourceId,
            selected);
        StoreAudioSynchronizationReports(reports);
        return reports;
    }

    private bool disposed;

    private void StoreAudioSynchronizationReports(IReadOnlyList<MimirAudioSynchronizationReport> reports)
    {
        foreach (var report in reports)
        {
            audioSynchronizationReports[report.SourceId] = report;
        }

        audioSynchronizationState.Update(reports);
    }

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
