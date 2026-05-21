namespace Mimir.Runtime.Synchronization;

public enum MimirVideoPixelFormat
{
    Unknown,
    Gray8,
    R8,
    Rg8,
    Bgra8,
    Nv12,
    LeapStereoIr,
}

public sealed record MimirVideoFrameDescriptor(
    int Width,
    int Height,
    MimirVideoPixelFormat PixelFormat,
    int StrideBytes,
    long DeviceTimestampNs,
    ulong NativeHandle = 0,
    string NativeHandleKind = "");
