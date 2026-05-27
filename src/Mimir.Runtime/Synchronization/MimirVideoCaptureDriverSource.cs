namespace Mimir.Runtime.Synchronization;

public sealed class MimirVideoCaptureDriverSource : IMimirStreamSource, IMimirFensalirTextureLeaseReceiver
{
    private readonly IMimirVideoCaptureDriver driver;
    private readonly MimirNativeIngestStreamSource nativeSource;

    public MimirVideoCaptureDriverSource(
        MimirStreamDescriptor descriptor,
        IMimirVideoCaptureDriver driver,
        Func<long> readArrivalNs)
    {
        if (descriptor.Kind != MimirStreamKind.Video)
        {
            throw new ArgumentException("Driver video sources require a video stream descriptor.", nameof(descriptor));
        }

        Descriptor = descriptor;
        this.driver = driver;
        ReadArrivalNs = readArrivalNs;
        nativeSource = new MimirNativeIngestStreamSource(descriptor);
    }

    public MimirStreamDescriptor Descriptor { get; }

    public Func<long> ReadArrivalNs { get; }

    public void AttachTextureLeaseClient(MimirFensalirTextureLeaseClient? client)
    {
        if (driver is IMimirFensalirTextureLeaseReceiver receiver)
        {
            receiver.AttachTextureLeaseClient(client);
        }
    }

    public bool TryRead(out MimirStreamSample sample)
    {
        if (nativeSource.TryRead(out sample))
        {
            return true;
        }

        if (!driver.TryCapture(out var frame, out var data))
        {
            return false;
        }

        nativeSource.PushVideoFrame(frame, ReadArrivalNs(), data);
        return nativeSource.TryRead(out sample);
    }

    public void Dispose()
    {
        nativeSource.Dispose();
        driver.Dispose();
    }
}
