using System.Runtime.InteropServices;
using System.Text;

namespace Mimir.Runtime.Synchronization;

public enum MimirNativeReservoirSampleKind : uint
{
    CameraFrame = 0,
    CameraFeature = 1,
    SceneRay = 2,
    SurfaceClaim = 3,
    MaterialClaim = 4,
    AudioBlock = 5,
    PhaseClaim = 6,
    EventClaim = 7,
    RenderPacket = 8,
    MoveEvidence = 9
}

public enum MimirNativeMoveEvidenceKind : uint
{
    OpticalMarker = 1,
    ControllerState = 2
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct MimirNativeSampleHandle(
    ulong SensorIdHash,
    ulong TimestampNs,
    ulong ArrivalNs,
    ulong Sequence,
    ulong PayloadHandle,
    uint Flags,
    uint Reserved);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct MimirNativeMoveEvidenceSample(
    ulong WitnessIdHash,
    ulong ControllerIdHash,
    ulong SourceTimestampNs,
    ulong ArrivalNs,
    ulong Sequence,
    uint EvidenceKind,
    uint Flags,
    float ImageX,
    float ImageY,
    float RadiusPx,
    float Confidence,
    float AccelX,
    float AccelY,
    float AccelZ,
    float GyroX,
    float GyroY,
    float GyroZ,
    float Trigger,
    uint ButtonsMask,
    uint Reserved,
    float Battery01,
    uint Reserved1,
    uint Reserved2);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct MimirNativeMoveEvidenceBufferDescriptor(
    ulong SampleBufferHandle,
    uint SampleCount,
    uint SampleStrideBytes,
    ulong SourceTimeMinNs,
    ulong SourceTimeMaxNs,
    ulong CalibrationHash,
    ulong TrackingSpaceHash);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct MimirNativeRuntimeStatus(
    ulong EdgeNs,
    ulong WindowStartNs,
    UIntPtr TotalSampleCount,
    UIntPtr CameraFrameCount,
    UIntPtr CameraFeatureCount,
    UIntPtr SceneRayCount,
    UIntPtr SurfaceClaimCount,
    UIntPtr MaterialClaimCount,
    UIntPtr AudioBlockCount,
    UIntPtr PhaseClaimCount,
    UIntPtr EventClaimCount,
    UIntPtr RenderPacketCount,
    UIntPtr MoveEvidenceCount);

public sealed unsafe class MimirNativeReservoirRuntime : IDisposable
{
    public const int MoveEvidenceSampleStrideBytes = 112;

    private readonly nint runtime;
    private readonly List<PinnedMoveEvidenceBatch> batches = [];
    private bool disposed;

    public MimirNativeReservoirRuntime(string nativeLibraryPath, TimeSpan? window = null)
    {
        Native.Load(nativeLibraryPath);
        runtime = Native.RuntimeCreate((ulong)(window ?? TimeSpan.FromSeconds(5)).TotalNanoseconds());
        if (runtime == 0)
        {
            throw new InvalidOperationException("Native reservoir runtime creation failed.");
        }
    }

    public MimirNativeRuntimeStatus Status
    {
        get
        {
            ThrowIfDisposed();
            if (!Native.RuntimeStatus(runtime, out var status))
            {
                throw new InvalidOperationException("Native reservoir runtime status read failed.");
            }

            return status;
        }
    }

    public MimirNativeSampleHandle AdmitMoveEvidence(
        string producerSourceId,
        IReadOnlyList<MimirNativeMoveEvidenceSample> samples,
        string calibrationId,
        string trackingSpaceId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(producerSourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackingSpaceId);
        if (samples.Count == 0)
        {
            throw new ArgumentException("Move evidence batch must contain at least one sample.", nameof(samples));
        }

        var producer = Native.ProducerCreateForSource(
            MimirNativeReservoirSampleKind.MoveEvidence,
            producerSourceId);
        if (producer == 0)
        {
            throw new InvalidOperationException($"Native Move evidence producer creation failed for '{producerSourceId}'.");
        }

        try
        {
            var batch = new PinnedMoveEvidenceBatch(samples, calibrationId, trackingSpaceId);
            if (!Native.ProducerPushMoveEvidenceBuffer(
                producer,
                runtime,
                batch.SourceTimeMaxNs,
                batch.ArrivalMaxNs,
                batch.DescriptorPointer,
                out var handle))
            {
                batch.Dispose();
                throw new InvalidOperationException("Native Move evidence admission failed.");
            }

            batches.Add(batch);
            return handle;
        }
        finally
        {
            Native.ProducerDestroy(producer);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var batch in batches)
        {
            batch.Dispose();
        }

        batches.Clear();
        Native.RuntimeDestroy(runtime);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class PinnedMoveEvidenceBatch : IDisposable
    {
        private readonly GCHandle samplesHandle;
        private readonly GCHandle descriptorHandle;

        public PinnedMoveEvidenceBatch(
            IReadOnlyList<MimirNativeMoveEvidenceSample> samples,
            string calibrationId,
            string trackingSpaceId)
        {
            var sampleArray = samples.ToArray();
            SourceTimeMaxNs = sampleArray.Max(sample => sample.SourceTimestampNs);
            ArrivalMaxNs = sampleArray.Max(sample => sample.ArrivalNs);
            samplesHandle = GCHandle.Alloc(sampleArray, GCHandleType.Pinned);
            var descriptor = new[]
            {
                new MimirNativeMoveEvidenceBufferDescriptor(
                    (ulong)samplesHandle.AddrOfPinnedObject(),
                    (uint)sampleArray.Length,
                    MoveEvidenceSampleStrideBytes,
                    sampleArray.Min(sample => sample.SourceTimestampNs),
                    SourceTimeMaxNs,
                    Fnva64(calibrationId),
                    Fnva64(trackingSpaceId))
            };
            descriptorHandle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        }

        public ulong SourceTimeMaxNs { get; }

        public ulong ArrivalMaxNs { get; }

        public nint DescriptorPointer => descriptorHandle.AddrOfPinnedObject();

        public void Dispose()
        {
            if (descriptorHandle.IsAllocated)
            {
                descriptorHandle.Free();
            }

            if (samplesHandle.IsAllocated)
            {
                samplesHandle.Free();
            }
        }
    }

    private static ulong Fnva64(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        const ulong offset = 14_695_981_039_346_656_037;
        const ulong prime = 1_099_511_628_211;
        var hash = offset;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash == 0 ? 1 : hash;
    }

    private static class Native
    {
        private static nint library;
        private static delegate* unmanaged[Cdecl]<ulong, nint> runtimeCreate;
        private static delegate* unmanaged[Cdecl]<nint, void> runtimeDestroy;
        private static delegate* unmanaged[Cdecl]<nint, out MimirNativeRuntimeStatus, byte> runtimeStatus;
        private static delegate* unmanaged[Cdecl]<uint, byte*, nuint, ulong, nint> producerCreateForSource;
        private static delegate* unmanaged[Cdecl]<nint, void> producerDestroy;
        private static delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, nint, out MimirNativeSampleHandle, byte> producerPushMoveEvidenceBuffer;

        public static void Load(string path)
        {
            if (library != 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "localcast_reservoir.dll");
            }

            library = NativeLibrary.Load(path);
            runtimeCreate = (delegate* unmanaged[Cdecl]<ulong, nint>)NativeLibrary.GetExport(library, "localcast_runtime_create");
            runtimeDestroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "localcast_runtime_destroy");
            runtimeStatus = (delegate* unmanaged[Cdecl]<nint, out MimirNativeRuntimeStatus, byte>)NativeLibrary.GetExport(library, "localcast_runtime_status");
            producerCreateForSource = (delegate* unmanaged[Cdecl]<uint, byte*, nuint, ulong, nint>)NativeLibrary.GetExport(library, "localcast_producer_create_for_source");
            producerDestroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "localcast_producer_destroy");
            producerPushMoveEvidenceBuffer = (delegate* unmanaged[Cdecl]<nint, nint, ulong, ulong, nint, out MimirNativeSampleHandle, byte>)NativeLibrary.GetExport(library, "localcast_producer_push_move_evidence_buffer");
        }

        public static nint RuntimeCreate(ulong durationNs) => runtimeCreate(durationNs);

        public static void RuntimeDestroy(nint handle)
        {
            if (handle != 0)
            {
                runtimeDestroy(handle);
            }
        }

        public static bool RuntimeStatus(nint handle, out MimirNativeRuntimeStatus status) =>
            runtimeStatus(handle, out status) != 0;

        public static nint ProducerCreateForSource(MimirNativeReservoirSampleKind kind, string sourceId)
        {
            var bytes = Encoding.UTF8.GetBytes(sourceId);
            fixed (byte* ptr = bytes)
            {
                return producerCreateForSource((uint)kind, ptr, (nuint)bytes.Length, 0);
            }
        }

        public static void ProducerDestroy(nint handle)
        {
            if (handle != 0)
            {
                producerDestroy(handle);
            }
        }

        public static bool ProducerPushMoveEvidenceBuffer(
            nint producer,
            nint runtime,
            ulong timestampNs,
            ulong arrivalNs,
            nint descriptor,
            out MimirNativeSampleHandle handle) =>
            producerPushMoveEvidenceBuffer(producer, runtime, timestampNs, arrivalNs, descriptor, out handle) != 0;

    }
}

internal static class MimirTimeSpanExtensions
{
    public static long TotalNanoseconds(this TimeSpan value) =>
        checked((long)(value.TotalSeconds * 1_000_000_000.0));
}
