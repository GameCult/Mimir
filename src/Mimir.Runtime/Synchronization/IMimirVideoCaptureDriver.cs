namespace Mimir.Runtime.Synchronization;

public interface IMimirVideoCaptureDriver : IDisposable
{
    string DriverName { get; }

    bool TryCapture(out MimirVideoFrameDescriptor frame, out ReadOnlyMemory<byte> data);
}
