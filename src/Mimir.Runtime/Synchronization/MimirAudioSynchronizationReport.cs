namespace Mimir.Runtime.Synchronization;

public sealed record MimirAudioSynchronizationReport(
    string ReferenceSourceId,
    string SourceId,
    int SampleRate,
    int DelaySamples,
    double FractionalDelaySamples,
    double DelayMilliseconds,
    double Confidence,
    IReadOnlyList<MimirChirpletBandResponse> BandResponses,
    long AnalysisTimestampNs,
    int ComparedSamples,
    ulong ReferenceSequence,
    ulong SourceSequence,
    int TimelineMatchedEvents = 0,
    double TimelineConfidence = 0.0);
