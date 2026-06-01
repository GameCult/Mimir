namespace Mimir.Runtime.Synchronization;

public sealed class MimirVideoCaptureDriverSource : IMimirStreamSource, IMimirFensalirTextureLeaseReceiver, IMimirCameraExposureGainActuator
{
    private readonly IMimirVideoCaptureDriver driver;
    private readonly MimirNativeIngestStreamSource nativeSource;
    private MimirFensalirTextureLeaseClient? textureLeaseClient;

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

    public string SourceId => Descriptor.SourceId;

    public string ExposureControlKind =>
        driver is MimirKsVideoCaptureDriver ? "ks-exposure-gain" : "fixed-control";

    public bool SupportsExposureGain => driver is MimirKsVideoCaptureDriver;

    public Func<long> ReadArrivalNs { get; }

    public int LastUploadedCopyCount { get; private set; }

    public int LastUploadedByteLength { get; private set; }

    public void AttachTextureLeaseClient(MimirFensalirTextureLeaseClient? client)
    {
        textureLeaseClient = client;
        if (driver is IMimirFensalirTextureLeaseReceiver receiver)
        {
            receiver.AttachTextureLeaseClient(client);
        }
    }

    public bool TrySetExposureGain(int? exposure, int? gain) =>
        driver is MimirKsVideoCaptureDriver ksDriver &&
        ksDriver.TrySetExposureGain(exposure, gain);

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

        if (!data.IsEmpty && !string.IsNullOrWhiteSpace(frame.ResourceKey))
        {
            if (textureLeaseClient?.UploadCpuFrame(frame, data) == true)
            {
                frame = frame with { UnavoidableCopyCount = frame.UnavoidableCopyCount + 1 };
                LastUploadedCopyCount = frame.UnavoidableCopyCount;
                LastUploadedByteLength = data.Length;
                data = default;
            }
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
