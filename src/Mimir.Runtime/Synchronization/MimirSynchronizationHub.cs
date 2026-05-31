namespace Mimir.Runtime.Synchronization;

public sealed class MimirSynchronizationHub : IDisposable
{
    private readonly List<IMimirStreamSource> sources = [];
    private readonly MimirAudioSynchronizationAnalyzer audioSynchronization = new();
    private readonly MimirComplexContourRuntimeAnalyzer? complexContourSynchronization;
    private readonly MimirAudioSynchronizationStateTracker audioSynchronizationState = new();
    private readonly Dictionary<string, MimirAudioSynchronizationReport> audioSynchronizationReports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MimirAudioSynchronizationReport> complexContourReports = new(StringComparer.Ordinal);
    private readonly MimirChirpBinCalibrationModel? chirpBinCalibrationModel;
    private MimirFensalirTextureLeaseClient? textureLeaseClient;
    private ulong ingestedSamples;
    private int nextAudioSynchronizationCandidate;

    public MimirSynchronizationHub(MimirSynchronizationSettings settings)
    {
        Settings = settings;
        Buffers = new MimirStreamBufferSet(settings.BufferDuration);
        chirpBinCalibrationModel = TryLoadCalibrationModel(settings.Audio.CalibrationModelPath);
        complexContourSynchronization = settings.Audio.EnableComplexContourRuntime
            ? new MimirComplexContourRuntimeAnalyzer(
                new MimirComplexContourRuntimeOptions(settings.Audio.BioacousticWitnessProfileId),
                TryLoadComplexContourChannelModel(settings.Audio.ComplexContourChannelModelPath))
            : null;
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

    public IReadOnlyList<MimirAudioSynchronizationReport> ComplexContourSynchronizationReports =>
        complexContourReports.Values.OrderBy(report => report.SourceId, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<MimirAudioSynchronizationDecodeTrace> AudioSynchronizationDecodeTraces =>
        audioSynchronization.LastDecodeTraces;

    public IReadOnlyList<MimirChirpBinCalibrationProfile> AudioChirpBinCalibrationProfiles =>
        audioSynchronization.LastCalibrationProfiles;

    public MimirChirpBinCodebookPlan? ChirpBinEmissionPlan =>
        chirpBinCalibrationModel?.EmissionPlan;

    public bool ComplexContourRuntimeEnabled => complexContourSynchronization != null;

    public MimirSynchronizedBufferFrame BuildSynchronizedBufferFrame(TimeSpan? presentationDelay = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var delay = presentationDelay ?? TimeSpan.FromTicks(Settings.BufferDuration.Ticks / 2);
        return new MimirSynchronizedBufferPlanner().BuildFrame(
            Buffers.Buffers,
            delay,
            MimirSynchronizedBufferPlanner.CorrectionsFromAudioStates(audioSynchronizationState.States, Buffers.Buffers));
    }

    public void AddSource(IMimirStreamSource source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        sources.Add(source);
        if (source.ExposesDescriptorBuffer)
        {
            Buffers.EnsureBuffer(source.Descriptor);
        }

        if (source is IMimirFensalirTextureLeaseReceiver receiver)
        {
            receiver.AttachTextureLeaseClient(textureLeaseClient);
        }
    }

    public void AttachTextureLeaseClient(MimirFensalirTextureLeaseClient? client)
    {
        textureLeaseClient = client;
        foreach (var source in sources)
        {
            if (source is IMimirFensalirTextureLeaseReceiver receiver)
            {
                receiver.AttachTextureLeaseClient(client);
            }
        }
    }

    public int PollSources(int maxSamplesPerSource = 8192)
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
        MimirAudioSyncMode mode = MimirAudioSyncMode.ChirpOnly)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var candidates = Buffers.Buffers
            .Where(buffer =>
                buffer.Descriptor.Kind == MimirStreamKind.Audio &&
                !string.Equals(buffer.Descriptor.SourceId, referenceSourceId, StringComparison.Ordinal) &&
                buffer.Latest?.AudioBlock != null)
            .Select(buffer => buffer.Descriptor.SourceId)
            .ToArray();
        var reports = audioSynchronization.Analyze(Buffers.Buffers, referenceSourceId, mode, calibrationModel: chirpBinCalibrationModel);
        StoreAudioSynchronizationReports(reports, candidates);
        return reports;
    }

    public IReadOnlyList<MimirAudioSynchronizationReport> AnalyzeAudioSynchronizationStep(
        string referenceSourceId,
        MimirAudioSyncMode mode = MimirAudioSyncMode.ChirpOnly,
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
            mode,
            selected,
            chirpBinCalibrationModel);
        StoreAudioSynchronizationReports(reports, selected);
        return reports;
    }

    public IReadOnlyList<MimirAudioSynchronizationReport> AnalyzeComplexContourSynchronizationStep(
        string referenceSourceId,
        double runtimeSeconds,
        int maxCandidates = 1)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (complexContourSynchronization == null)
        {
            return [];
        }

        var candidates = Buffers.Buffers
            .Where(buffer =>
                buffer.Descriptor.Kind == MimirStreamKind.Audio &&
                !string.Equals(buffer.Descriptor.SourceId, referenceSourceId, StringComparison.Ordinal) &&
                buffer.Latest?.AudioBlock is { SampleFormat: MimirAudioSampleFormat.Float32 })
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

        var predicted = audioSynchronizationState.States.ToDictionary(state => state.SourceId, StringComparer.Ordinal);
        var reports = complexContourSynchronization.Analyze(
            Buffers.Buffers,
            referenceSourceId,
            runtimeSeconds,
            predicted,
            selected);
        StoreComplexContourReports(reports, selected);
        if (reports.Count > 0)
        {
            audioSynchronizationState.Update(reports);
        }

        return reports;
    }

    private bool disposed;

    private static MimirChirpBinCalibrationModel? TryLoadCalibrationModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return MimirChirpBinCalibrationModel.Load(path);
    }

    private static MimirComplexContourChannelModelDocument? TryLoadComplexContourChannelModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return MimirComplexContourChannelModelDocument.Load(path);
    }

    private void StoreAudioSynchronizationReports(
        IReadOnlyList<MimirAudioSynchronizationReport> reports,
        IEnumerable<string> analyzedCandidateIds)
    {
        var reportIds = new HashSet<string>(reports.Select(report => report.SourceId), StringComparer.Ordinal);
        foreach (var sourceId in analyzedCandidateIds)
        {
            if (!reportIds.Contains(sourceId))
            {
                audioSynchronizationReports.Remove(sourceId);
            }
        }

        foreach (var report in reports)
        {
            audioSynchronizationReports[report.SourceId] = report;
        }

        audioSynchronizationState.Update(reports);
        foreach (var trace in audioSynchronization.LastDecodeTraces)
        {
            if (string.Equals(trace.Status, "passive-negative-lag", StringComparison.Ordinal))
            {
                audioSynchronizationReports.Remove(trace.SourceId);
                audioSynchronizationState.Remove(trace.SourceId);
            }
        }
    }

    private void StoreComplexContourReports(
        IReadOnlyList<MimirAudioSynchronizationReport> reports,
        IEnumerable<string> analyzedCandidateIds)
    {
        var reportIds = new HashSet<string>(reports.Select(report => report.SourceId), StringComparer.Ordinal);
        foreach (var sourceId in analyzedCandidateIds)
        {
            if (!reportIds.Contains(sourceId))
            {
                complexContourReports.Remove(sourceId);
            }
        }

        foreach (var report in reports)
        {
            complexContourReports[report.SourceId] = report;
            audioSynchronizationReports[report.SourceId] = report;
        }
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
