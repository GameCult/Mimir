using Aquarium.Engine.Render;

namespace Mimir.Runtime.Synchronization;

public readonly record struct MimirFensalirTextureLeaseRequest(
    string SourceId,
    int Width,
    int Height,
    MimirVideoPixelFormat PixelFormat,
    int StrideBytes,
    long DeviceTimestampNs,
    ulong Version,
    AquariumFieldShaderAccess ProducerAccess = AquariumFieldShaderAccess.ShaderResource);

public readonly record struct MimirFensalirTextureLease(
    MimirVideoFrameDescriptor Frame,
    IntPtr NativeHandle,
    IntPtr ProducerFenceHandle,
    AquariumFieldResourceDeclaration Declaration)
{
    public bool IsValid => NativeHandle != IntPtr.Zero && Frame.NativeHandle != 0 && !string.IsNullOrWhiteSpace(Frame.ResourceKey);
}

public sealed class MimirFensalirTextureLeaseClient(IAquariumFieldResourceBroker broker)
{
    public bool TryLeaseTexture2D(MimirFensalirTextureLeaseRequest request, out MimirFensalirTextureLease lease)
    {
        lease = default;
        if (string.IsNullOrWhiteSpace(request.SourceId) ||
            request.Width <= 0 ||
            request.Height <= 0 ||
            request.StrideBytes <= 0)
        {
            return false;
        }

        var resourceKey = ResourceKeyForSource(request.SourceId);
        var engineLease = broker.LeaseTexture2D(new AquariumTexture2DLeaseRequest(
            resourceKey,
            request.Width,
            request.Height,
            FormatForPixelFormat(request.PixelFormat),
            request.ProducerAccess,
            request.Version));
        if (!engineLease.IsValid || engineLease.NativeHandle == IntPtr.Zero)
        {
            return false;
        }

        var frame = new MimirVideoFrameDescriptor(
            request.Width,
            request.Height,
            request.PixelFormat,
            request.StrideBytes,
            request.DeviceTimestampNs,
            NativeHandle: unchecked((ulong)engineLease.NativeHandle.ToInt64()),
            NativeHandleKind: engineLease.NativeHandleKind,
            ResourceKey: resourceKey,
            ProducerFenceValue: 0);
        lease = new MimirFensalirTextureLease(
            frame,
            engineLease.NativeHandle,
            engineLease.ProducerFenceHandle,
            engineLease.Declaration);
        return lease.IsValid;
    }

    public bool Commit(string resourceKey, ulong version, ulong producerFenceValue) =>
        broker.CommitLeaseVersion(resourceKey, version, producerFenceValue);

    public bool UploadCpuFrame(MimirVideoFrameDescriptor frame, ReadOnlyMemory<byte> data)
    {
        if (string.IsNullOrWhiteSpace(frame.ResourceKey) ||
            frame.Width <= 0 ||
            frame.Height <= 0 ||
            frame.StrideBytes <= 0 ||
            data.IsEmpty)
        {
            return false;
        }

        return broker.UploadTexture2D(new AquariumTexture2DUpload(
            frame.ResourceKey,
            frame.Width,
            frame.Height,
            FormatForPixelFormat(frame.PixelFormat),
            frame.StrideBytes,
            data,
            frame.ProducerFenceValue));
    }

    public static string ResourceKeyForSource(string sourceId)
    {
        var normalized = sourceId.Trim().ToLowerInvariant().Replace('\\', '/');
        return $"mimir:resource:camera:{normalized}:texture2d";
    }

    private static string FormatForPixelFormat(MimirVideoPixelFormat pixelFormat) =>
        pixelFormat switch
        {
            MimirVideoPixelFormat.Gray8 or MimirVideoPixelFormat.R8 => "R8Unorm",
            MimirVideoPixelFormat.Bayer8 => "R8Unorm",
            MimirVideoPixelFormat.Rg8 or MimirVideoPixelFormat.LeapStereoIr => "R8G8_UNorm",
            MimirVideoPixelFormat.Yuy2 => "YUY2",
            MimirVideoPixelFormat.Bgra8 => "Bgra8",
            MimirVideoPixelFormat.Nv12 => "Nv12",
            _ => "Rgba8Unorm",
        };
}
