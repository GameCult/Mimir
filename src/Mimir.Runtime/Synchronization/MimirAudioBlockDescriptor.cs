namespace Mimir.Runtime.Synchronization;

public enum MimirAudioSampleFormat
{
    Unknown,
    Float32,
    Int16,
    Int24,
    Int32,
}

public sealed record MimirAudioBlockDescriptor(
    int SampleRate,
    int Channels,
    MimirAudioSampleFormat SampleFormat,
    int FrameCount,
    long DeviceTimestampNs,
    ulong NativeHandle = 0,
    string NativeHandleKind = "");
