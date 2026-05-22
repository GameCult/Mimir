namespace Mimir.Runtime.Synchronization;

public sealed record MimirAudioSynchronizationDecodeTrace(
    string ReferenceSourceId,
    string SourceId,
    int SampleRate,
    int ComparedSamples,
    int ReferenceFrames,
    int ReferenceAnchors,
    double ReferenceClockConfidence,
    int CandidateFrames,
    int CandidateAnchors,
    double CandidateClockConfidence,
    int MatchedEvents,
    double Confidence,
    string Status);
