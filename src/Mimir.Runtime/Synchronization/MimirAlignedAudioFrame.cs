namespace Mimir.Runtime.Synchronization;

public sealed record MimirAlignedAudioChannel(
    string SourceId,
    int DelaySamples,
    double FractionalDelaySamples,
    double Confidence);

public sealed record MimirAlignedAudioFrame(
    string ReferenceSourceId,
    int SampleRate,
    int FrameCount,
    IReadOnlyList<MimirAlignedAudioChannel> Channels,
    float[][] Samples);
