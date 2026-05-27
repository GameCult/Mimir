using System.Runtime.InteropServices;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirKsVideoCaptureDriverOptions(
    string NativeLibraryPath,
    string SourceId,
    string PathNeedle,
    int Width,
    int Height,
    string FourCc,
    double MinimumFramesPerSecond,
    int QueueDepth = 8);

public sealed unsafe class MimirKsVideoCaptureDriver : IMimirVideoCaptureDriver, IMimirFensalirTextureLeaseReceiver
{
    private readonly Native native;
    private readonly nint capture;
    private readonly byte[] scratch;
    private readonly string sourceId;
    private readonly string fourCc;
    private MimirFensalirTextureLeaseClient? textureLeaseClient;
    private string resourceKey = "";
    private bool disposed;

    public MimirKsVideoCaptureDriver(MimirKsVideoCaptureDriverOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.NativeLibraryPath))
        {
            throw new ArgumentException("KS camera capture requires a native library path.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.SourceId))
        {
            throw new ArgumentException("KS camera capture requires a source id.", nameof(options));
        }

        if (IsCompressed(options.FourCc))
        {
            throw new NotSupportedException("KS camera capture driver only accepts uncompressed frame formats. Use a GPU decode producer for MJPG/H264.");
        }

        native = Native.Load(options.NativeLibraryPath);
        sourceId = options.SourceId;
        fourCc = options.FourCc.Trim();
        capture = native.Create(
            options.PathNeedle,
            options.Width,
            options.Height,
            fourCc,
            options.MinimumFramesPerSecond,
            Math.Clamp(options.QueueDepth, 1, 32));
        if (capture == 0)
        {
            throw new InvalidOperationException($"KS camera capture could not open {options.PathNeedle} {options.Width}x{options.Height} {fourCc}.");
        }

        scratch = new byte[checked(options.Width * options.Height * 4)];
        if (!native.Start(capture))
        {
            throw new InvalidOperationException("KS camera capture could not start.");
        }
    }

    public string DriverName => "ks-uvc-direct";

    public void AttachTextureLeaseClient(MimirFensalirTextureLeaseClient? client)
    {
        textureLeaseClient = client;
        resourceKey = "";
    }

    public bool TryCapture(out MimirVideoFrameDescriptor frame, out ReadOnlyMemory<byte> data)
    {
        fixed (byte* destination = scratch)
        {
            Span<byte> fourCcBytes = stackalloc byte[16];
            var byteLength = native.Read(
                capture,
                out var width,
                out var height,
                out var stride,
                fourCcBytes,
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

            var pixelFormat = PixelFormatFromFourCc(FourCcFromBytes(fourCcBytes));
            EnsureLease(width, height, pixelFormat, stride, timestampNs, sequence);
            frame = new MimirVideoFrameDescriptor(
                width,
                height,
                pixelFormat,
                stride,
                timestampNs,
                ResourceKey: resourceKey,
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

    private void EnsureLease(int width, int height, MimirVideoPixelFormat pixelFormat, int stride, long timestampNs, ulong version)
    {
        if (!string.IsNullOrWhiteSpace(resourceKey) || textureLeaseClient == null)
        {
            return;
        }

        if (textureLeaseClient.TryLeaseTexture2D(new MimirFensalirTextureLeaseRequest(
            sourceId,
            width,
            height,
            pixelFormat,
            stride,
            timestampNs,
            version), out var lease))
        {
            resourceKey = lease.Frame.ResourceKey;
        }
    }

    private static MimirVideoPixelFormat PixelFormatFromFourCc(string value)
    {
        return value.Trim().ToUpperInvariant() switch
        {
            "YUY2" or "YUYV" => MimirVideoPixelFormat.Yuy2,
            "GREY" or "Y800" => MimirVideoPixelFormat.Gray8,
            "RG8" or "R8G8" => MimirVideoPixelFormat.Rg8,
            "BA81" or "BGGR" or "GBRG" or "GRBG" or "RGGB" => MimirVideoPixelFormat.Bayer8,
            _ => MimirVideoPixelFormat.Unknown,
        };
    }

    private static bool IsCompressed(string value)
    {
        return value.Trim().ToUpperInvariant() is "MJPG" or "H264";
    }

    private static string FourCcFromBytes(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return System.Text.Encoding.ASCII.GetString(bytes[..length]);
    }

    private sealed class Native : IDisposable
    {
        private readonly nint library;
        private readonly delegate* unmanaged[Stdcall]<byte*, int, int, byte*, double, int, nint> create;
        private readonly delegate* unmanaged[Stdcall]<nint, int> start;
        private readonly delegate* unmanaged[Stdcall]<nint, out int, out int, out int, byte*, int, out long, out ulong, byte*, int, int> read;
        private readonly delegate* unmanaged[Stdcall]<nint, void> destroy;

        private Native(nint library)
        {
            this.library = library;
            create = (delegate* unmanaged[Stdcall]<byte*, int, int, byte*, double, int, nint>)NativeLibrary.GetExport(library, "mimir_ks_create");
            start = (delegate* unmanaged[Stdcall]<nint, int>)NativeLibrary.GetExport(library, "mimir_ks_start");
            read = (delegate* unmanaged[Stdcall]<nint, out int, out int, out int, byte*, int, out long, out ulong, byte*, int, int>)NativeLibrary.GetExport(library, "mimir_ks_read");
            destroy = (delegate* unmanaged[Stdcall]<nint, void>)NativeLibrary.GetExport(library, "mimir_ks_destroy");
        }

        public static Native Load(string path) => new(NativeLibrary.Load(path));

        public nint Create(string pathNeedle, int width, int height, string subtype, double minFps, int queueDepth)
        {
            var pathNeedleBytes = System.Text.Encoding.UTF8.GetBytes(pathNeedle + "\0");
            var subtypeBytes = System.Text.Encoding.UTF8.GetBytes(subtype + "\0");
            fixed (byte* pathNeedlePointer = pathNeedleBytes)
            fixed (byte* subtypePointer = subtypeBytes)
            {
                return create(pathNeedlePointer, width, height, subtypePointer, minFps, queueDepth);
            }
        }

        public bool Start(nint capture) => start(capture) != 0;

        public int Read(
            nint capture,
            out int width,
            out int height,
            out int stride,
            Span<byte> fourCc,
            out long timestampNs,
            out ulong sequence,
            byte* destination,
            int destinationBytes)
        {
            fixed (byte* fourCcPointer = fourCc)
            {
                return read(capture, out width, out height, out stride, fourCcPointer, fourCc.Length, out timestampNs, out sequence, destination, destinationBytes);
            }
        }

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
