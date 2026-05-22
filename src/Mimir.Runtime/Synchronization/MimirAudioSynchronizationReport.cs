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
    int ComparedSamples,
    ulong ReferenceSequence,
    ulong SourceSequence);
