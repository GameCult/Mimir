using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirAsioStreamSourceOptions(
    string NativeLibraryPath,
    string DriverClsid,
    int SampleRate,
    IReadOnlyList<string> SourceIds);

[SupportedOSPlatform("windows")]
public sealed class MimirAsioStreamSource : IMimirMultiplexedStreamSource
{
    private const string DefaultClsid = "{AC4D0455-50D7-4498-B3CD-9A41D130B759}";
    private const int MaxQueuedBlocks = 256;
    private readonly ConcurrentQueue<MimirStreamSample> samples = new();
    private readonly ManualResetEventSlim started = new();
    private readonly Thread captureThread;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string[] sourceIds;
    private int sampleRate;
    private string? startError;
    private bool disposed;

    public MimirAsioStreamSource(
        MimirStreamDescriptor descriptor,
        MimirAsioStreamSourceOptions options)
    {
        Descriptor = descriptor;
        Options = options;
        sourceIds = options.SourceIds.Count > 0
            ? options.SourceIds.ToArray()
            : Enumerable.Range(0, 8).Select(index => $"asio-ch{index}").ToArray();
        captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "Mimir Focusrite ASIO capture",
        };
        captureThread.SetApartmentState(ApartmentState.STA);
        captureThread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("Timed out starting Focusrite ASIO capture source");
        }

        if (startError != null)
        {
            throw new InvalidOperationException(startError);
        }
    }

    public MimirStreamDescriptor Descriptor { get; }

    public MimirAsioStreamSourceOptions Options { get; }

    public bool ExposesDescriptorBuffer => false;

    public int LogicalStreamCount => Math.Max(1, sourceIds.Length);

    public bool TryRead(out MimirStreamSample sample)
    {
        sample = default;
        ObjectDisposedException.ThrowIf(disposed, this);
        return samples.TryDequeue(out sample);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        captureThread.Join(TimeSpan.FromSeconds(2));
        cancellation.Dispose();
        started.Dispose();
    }

    private void CaptureLoop()
    {
        nint handle = 0;
        try
        {
            Native.Load(Options.NativeLibraryPath);
            handle = Native.Create(
                string.IsNullOrWhiteSpace(Options.DriverClsid) ? DefaultClsid : Options.DriverClsid,
                Options.SampleRate,
                out sampleRate,
                out _,
                out var maxFrames);
            if (handle == 0)
            {
                startError = "Could not create Focusrite ASIO capture source";
                started.Set();
                return;
            }

            if (!Native.Start(handle))
            {
                startError = "Could not start Focusrite ASIO capture source";
                started.Set();
                return;
            }

            started.Set();
            var sampleBuffer = new float[Math.Max(1, maxFrames)];
            while (!cancellation.IsCancellationRequested)
            {
                if (!Native.Read(
                        handle,
                        out var channel,
                        out var timestampNs,
                        out var sequence,
                        out var frameCount,
                        sampleBuffer,
                        sampleBuffer.Length))
                {
                    Thread.Sleep(1);
                    continue;
                }

                if (channel < 0)
                {
                    continue;
                }

                var sourceId = channel < sourceIds.Length ? sourceIds[channel] : $"asio-ch{channel}";
                var bytes = new byte[frameCount * sizeof(float)];
                var arrivalNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
                var durationNs = checked((long)Math.Ceiling(frameCount * 1_000_000_000.0 / Math.Max(1, sampleRate)));
                var startNs = Math.Max(0, arrivalNs - durationNs);
                MemoryMarshal.AsBytes(sampleBuffer.AsSpan(0, frameCount)).CopyTo(bytes);
                samples.Enqueue(new MimirStreamSample(
                    sourceId,
                    MimirStreamKind.Audio,
                    Descriptor.Origin,
                    startNs,
                    arrivalNs,
                    sequence,
                    0,
                    bytes.Length,
                    bytes,
                    AudioBlock: new MimirAudioBlockDescriptor(
                        sampleRate,
                        1,
                        MimirAudioSampleFormat.Float32,
                        frameCount,
                        timestampNs)));

                while (samples.Count > MaxQueuedBlocks && samples.TryDequeue(out _))
                {
                }
            }
        }
        catch (Exception ex)
        {
            startError = ex.Message;
            started.Set();
        }
        finally
        {
            Native.Destroy(handle);
        }
    }

    private static unsafe class Native
    {
        private static nint library;
        private static delegate* unmanaged[Stdcall]<byte*, double, out int, out int, out int, nint> create;
        private static delegate* unmanaged[Stdcall]<nint, int> start;
        private static delegate* unmanaged[Stdcall]<nint, out int, out long, out ulong, out int, float*, int, int> read;
        private static delegate* unmanaged[Stdcall]<nint, void> destroy;

        public static void Load(string path)
        {
            if (library != 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "mimir_asio_capture.dll");
            }

            library = NativeLibrary.Load(path);
            create = (delegate* unmanaged[Stdcall]<byte*, double, out int, out int, out int, nint>)NativeLibrary.GetExport(library, "mimir_asio_create");
            start = (delegate* unmanaged[Stdcall]<nint, int>)NativeLibrary.GetExport(library, "mimir_asio_start");
            read = (delegate* unmanaged[Stdcall]<nint, out int, out long, out ulong, out int, float*, int, int>)NativeLibrary.GetExport(library, "mimir_asio_read");
            destroy = (delegate* unmanaged[Stdcall]<nint, void>)NativeLibrary.GetExport(library, "mimir_asio_destroy");
        }

        public static nint Create(string clsid, int requestedSampleRate, out int sampleRate, out int inputCount, out int preferredBufferSize)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(clsid + "\0");
            fixed (byte* clsidPtr = bytes)
            {
                return create(clsidPtr, requestedSampleRate, out sampleRate, out inputCount, out preferredBufferSize);
            }
        }

        public static bool Start(nint handle) => start(handle) != 0;

        public static bool Read(
            nint handle,
            out int channel,
            out long timestampNs,
            out ulong sequence,
            out int frameCount,
            float[] samples,
            int maxFrames)
        {
            fixed (float* samplePtr = samples)
            {
                return read(handle, out channel, out timestampNs, out sequence, out frameCount, samplePtr, maxFrames) != 0;
            }
        }

        public static void Destroy(nint handle)
        {
            if (handle != 0)
            {
                destroy(handle);
            }
        }
    }
}
