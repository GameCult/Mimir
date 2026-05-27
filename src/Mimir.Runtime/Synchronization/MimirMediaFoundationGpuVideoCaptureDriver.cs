using System.Runtime.InteropServices;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirMediaFoundationGpuVideoCaptureDriverOptions(
    string NativeLibraryPath,
    string SourceId,
    string PathNeedle,
    int Width,
    int Height,
    string InputFormat,
    string OutputFormat = "Nv12",
    double MinimumFramesPerSecond = 0.0);

public sealed unsafe class MimirMediaFoundationGpuVideoCaptureDriver : IMimirVideoCaptureDriver
{
    private readonly Native native;
    private readonly nint capture;
    private readonly string sourceId;
    private bool disposed;

    public MimirMediaFoundationGpuVideoCaptureDriver(MimirMediaFoundationGpuVideoCaptureDriverOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.NativeLibraryPath))
        {
            throw new ArgumentException("Media Foundation GPU capture requires a native library path.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.SourceId))
        {
            throw new ArgumentException("Media Foundation GPU capture requires a source id.", nameof(options));
        }

        native = Native.Load(options.NativeLibraryPath);
        sourceId = options.SourceId;
        capture = native.Create(
            options.PathNeedle,
            options.Width,
            options.Height,
            options.InputFormat,
            options.OutputFormat,
            options.MinimumFramesPerSecond);
        if (capture == 0)
        {
            throw new InvalidOperationException($"Media Foundation GPU capture could not open {options.PathNeedle} {options.Width}x{options.Height} {options.InputFormat}->{options.OutputFormat}.");
        }
    }

    public string DriverName => "media-foundation-gpu-decode";

    public bool TryCapture(out MimirVideoFrameDescriptor frame, out ReadOnlyMemory<byte> data)
    {
        Span<byte> formatBytes = stackalloc byte[16];
        if (!native.Read(
                capture,
                out var sharedHandle,
                out var width,
                out var height,
                formatBytes,
                out var timestampNs,
                out var sequence) ||
            sharedHandle == 0)
        {
            frame = default!;
            data = default;
            return false;
        }

        var pixelFormat = PixelFormatFromText(FourCcFromBytes(formatBytes));
        frame = new MimirVideoFrameDescriptor(
            width,
            height,
            pixelFormat,
            StrideBytesFor(pixelFormat, width),
            timestampNs,
            NativeHandle: unchecked((ulong)sharedHandle),
            NativeHandleKind: "shared-d3d11-texture",
            ResourceKey: MimirFensalirTextureLeaseClient.ResourceKeyForSource(sourceId),
            ProducerFenceValue: sequence);
        data = default;
        return true;
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

    private static int StrideBytesFor(MimirVideoPixelFormat format, int width) =>
        format switch
        {
            MimirVideoPixelFormat.Nv12 => width,
            MimirVideoPixelFormat.Yuy2 => checked(width * 2),
            MimirVideoPixelFormat.Bgra8 => checked(width * 4),
            _ => Math.Max(1, width),
        };

    private static MimirVideoPixelFormat PixelFormatFromText(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "NV12" => MimirVideoPixelFormat.Nv12,
            "YUY2" => MimirVideoPixelFormat.Yuy2,
            "BGRA8" or "BGRA" or "RGB32" => MimirVideoPixelFormat.Bgra8,
            _ => MimirVideoPixelFormat.Unknown,
        };

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
        private readonly delegate* unmanaged[Stdcall]<byte*, int, int, byte*, byte*, double, nint> create;
        private readonly delegate* unmanaged[Stdcall]<nint, out nint, out int, out int, byte*, int, out long, out ulong, int> read;
        private readonly delegate* unmanaged[Stdcall]<nint, void> destroy;

        private Native(nint library)
        {
            this.library = library;
            create = (delegate* unmanaged[Stdcall]<byte*, int, int, byte*, byte*, double, nint>)NativeLibrary.GetExport(library, "mimir_mf_gpu_create");
            read = (delegate* unmanaged[Stdcall]<nint, out nint, out int, out int, byte*, int, out long, out ulong, int>)NativeLibrary.GetExport(library, "mimir_mf_gpu_read");
            destroy = (delegate* unmanaged[Stdcall]<nint, void>)NativeLibrary.GetExport(library, "mimir_mf_gpu_destroy");
        }

        public static Native Load(string path) => new(NativeLibrary.Load(path));

        public nint Create(string pathNeedle, int width, int height, string inputFormat, string outputFormat, double minFps)
        {
            var pathNeedleBytes = System.Text.Encoding.UTF8.GetBytes(pathNeedle + "\0");
            var inputBytes = System.Text.Encoding.UTF8.GetBytes(inputFormat + "\0");
            var outputBytes = System.Text.Encoding.UTF8.GetBytes(outputFormat + "\0");
            fixed (byte* pathNeedlePointer = pathNeedleBytes)
            fixed (byte* inputPointer = inputBytes)
            fixed (byte* outputPointer = outputBytes)
            {
                return create(pathNeedlePointer, width, height, inputPointer, outputPointer, minFps);
            }
        }

        public bool Read(
            nint capture,
            out nint sharedHandle,
            out int width,
            out int height,
            Span<byte> format,
            out long timestampNs,
            out ulong sequence)
        {
            fixed (byte* formatPointer = format)
            {
                return read(capture, out sharedHandle, out width, out height, formatPointer, format.Length, out timestampNs, out sequence) != 0;
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
