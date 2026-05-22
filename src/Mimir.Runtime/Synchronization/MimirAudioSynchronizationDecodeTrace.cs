namespace Mimir.Runtime.Synchronization;

public sealed record MimirAudioSynchronizationDecodeTrace(
    string ReferenceSourceId,
    string SourceId,
    int SampleRate,
    int ComparedSamples,
    int ReferenceFrames,
    int ReferenceAnchors,
    double ReferenceClockConfidence,
    double ReferenceBestEnergy,
    int CandidateFrames,
    int CandidateAnchors,
    double CandidateClockConfidence,
    double CandidateBestEnergy,
    int MatchedEvents,
    double Confidence,
    string Status);
