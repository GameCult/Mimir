namespace Mimir.Runtime.Synchronization;

public sealed record MimirChirpletSymbolObservation(
    int SymbolId,
    double SampleOffset,
    double Energy);

public sealed record MimirChirpletTimelineAnchor(
    ulong EventIndex,
    double TimelineSeconds,
    double SampleOffset,
    double Confidence,
    IReadOnlyList<MimirChirpletSymbolObservation> Symbols);

public sealed record MimirChirpletClockFit(
    double SourceOffsetSamples,
    double EffectiveSampleRate,
    double Confidence,
    int AnchorCount,
    double MeanAbsoluteErrorSamples)
{
    public double SampleForTimelineSeconds(double timelineSeconds) =>
        SourceOffsetSamples + timelineSeconds * EffectiveSampleRate;
}

public sealed record MimirChirpletStreamDecode(
    IReadOnlyList<MimirChirpletSymbolObservation> Symbols,
    IReadOnlyList<MimirChirpletTimelineAnchor> Anchors,
    MimirChirpletClockFit? ClockFit);
