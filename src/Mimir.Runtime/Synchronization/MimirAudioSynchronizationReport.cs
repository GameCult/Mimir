namespace Mimir.Runtime.Synchronization;

public sealed record MimirAudioSynchronizationReport(
    string ReferenceSourceId,
    string SourceId,
    int SampleRate,
    int DelaySamples,
    double DelayMilliseconds,
    double Confidence,
    int ComparedSamples,
    ulong ReferenceSequence,
    ulong SourceSequence);
