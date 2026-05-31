using System.Runtime.InteropServices;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirPs3EyeVideoCaptureDriverOptions(
    string NativeLibraryPath,
    string SourceId,
    int CameraIndex,
    int Width,
    int Height,
    int FramesPerSecond);

public sealed unsafe class MimirPs3EyeVideoCaptureDriver : IMimirVideoCaptureDriver, IMimirFensalirTextureLeaseReceiver
{
    private readonly Native native;
    private readonly nint capture;
    private readonly byte[] scratch;
    private readonly string sourceId;
    private MimirFensalirTextureLeaseClient? textureLeaseClient;
    private string resourceKey = "";
    private ulong nativeHandle;
    private string nativeHandleKind = "";
    private ulong producerFenceHandle;
    private bool disposed;

    public MimirPs3EyeVideoCaptureDriver(MimirPs3EyeVideoCaptureDriverOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.NativeLibraryPath))
        {
            throw new ArgumentException("PS3 Eye capture requires a native library path.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.SourceId))
        {
            throw new ArgumentException("PS3 Eye capture requires a source id.", nameof(options));
        }

        native = Native.Load(options.NativeLibraryPath);
        sourceId = options.SourceId;
        capture = native.Create(options.CameraIndex, options.Width, options.Height, options.FramesPerSecond);
        if (capture == 0)
        {
            throw new InvalidOperationException($"PS3 Eye capture could not open camera {options.CameraIndex} at {options.Width}x{options.Height}@{options.FramesPerSecond}.");
        }

        scratch = new byte[checked(options.Width * options.Height)];
        if (!native.Start(capture))
        {
            throw new InvalidOperationException("PS3 Eye capture could not start.");
        }
    }

    public string DriverName => "ps3eye-winusb-direct";

    public void AttachTextureLeaseClient(MimirFensalirTextureLeaseClient? client)
    {
        textureLeaseClient = client;
        resourceKey = "";
        nativeHandle = 0;
        nativeHandleKind = "";
        producerFenceHandle = 0;
    }

    public bool TryCapture(out MimirVideoFrameDescriptor frame, out ReadOnlyMemory<byte> data)
    {
        fixed (byte* destination = scratch)
        {
            var byteLength = native.Read(
                capture,
                out var width,
                out var height,
                out var stride,
                out var timestampNs,
                out var sequence,
                destination,
                scratch.Length);
            if (byteLength <= 0)
            {
                frame = default!;
                data = default;
                return false;
            }

            EnsureLease(width, height, stride, timestampNs, sequence);
            frame = new MimirVideoFrameDescriptor(
                width,
                height,
                MimirVideoPixelFormat.Bayer8,
                stride,
                timestampNs,
                NativeHandle: nativeHandle,
                NativeHandleKind: nativeHandleKind,
                ResourceKey: resourceKey,
                ProducerFenceHandle: producerFenceHandle,
                ProducerFenceValue: sequence);
            data = string.IsNullOrWhiteSpace(resourceKey)
                ? scratch.AsMemory(0, byteLength).ToArray()
                : scratch.AsMemory(0, byteLength);
            return true;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        native.Destroy(capture);
        native.Dispose();
    }

    private void EnsureLease(int width, int height, int stride, long timestampNs, ulong version)
    {
        if (!string.IsNullOrWhiteSpace(resourceKey) || textureLeaseClient == null)
        {
            return;
        }

        if (textureLeaseClient.TryLeaseTexture2D(new MimirFensalirTextureLeaseRequest(
            sourceId,
            width,
            height,
            MimirVideoPixelFormat.Bayer8,
            stride,
            timestampNs,
            version), out var lease))
        {
            resourceKey = lease.Frame.ResourceKey;
            nativeHandle = lease.Frame.NativeHandle;
            nativeHandleKind = lease.Frame.NativeHandleKind;
            producerFenceHandle = unchecked((ulong)lease.ProducerFenceHandle.ToInt64());
        }
    }

    private sealed class Native : IDisposable
    {
        private readonly nint library;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, nint> create;
        private readonly delegate* unmanaged[Stdcall]<nint, int> start;
        private readonly delegate* unmanaged[Stdcall]<nint, out int, out int, out int, out long, out ulong, byte*, int, int> read;
        private readonly delegate* unmanaged[Stdcall]<nint, void> destroy;

        private Native(nint library)
        {
            this.library = library;
            create = (delegate* unmanaged[Stdcall]<int, int, int, int, nint>)NativeLibrary.GetExport(library, "mimir_ps3eye_create");
            start = (delegate* unmanaged[Stdcall]<nint, int>)NativeLibrary.GetExport(library, "mimir_ps3eye_start");
            read = (delegate* unmanaged[Stdcall]<nint, out int, out int, out int, out long, out ulong, byte*, int, int>)NativeLibrary.GetExport(library, "mimir_ps3eye_read");
            destroy = (delegate* unmanaged[Stdcall]<nint, void>)NativeLibrary.GetExport(library, "mimir_ps3eye_destroy");
        }

        public static Native Load(string path) => new(NativeLibrary.Load(path));

        public nint Create(int cameraIndex, int width, int height, int framesPerSecond) =>
            create(cameraIndex, width, height, framesPerSecond);

        public bool Start(nint capture) => start(capture) != 0;

        public int Read(
            nint capture,
            out int width,
            out int height,
            out int stride,
            out long timestampNs,
            out ulong sequence,
            byte* destination,
            int destinationBytes) =>
            read(capture, out width, out height, out stride, out timestampNs, out sequence, destination, destinationBytes);

        public void Destroy(nint capture)
        {
            if (capture != 0)
            {
                destroy(capture);
            }
        }

        public void Dispose()
        {
            NativeLibrary.Free(library);
        }
    }
}
