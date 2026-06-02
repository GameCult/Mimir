using System.Collections.Concurrent;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirNativeIngestStreamSource : IMimirStreamSource
{
    private readonly ConcurrentQueue<MimirStreamSample> samples = new();
    private ulong sequence;

    public MimirNativeIngestStreamSource(MimirStreamDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public MimirStreamDescriptor Descriptor { get; }

    public void Push(
        long timestampNs,
        long arrivalNs,
        ulong payloadHandle,
        int byteLength = 0,
        ReadOnlyMemory<byte> data = default,
        MimirVideoFrameDescriptor? videoFrame = null)
    {
        samples.Enqueue(new MimirStreamSample(
            Descriptor.SourceId,
            Descriptor.Kind,
            Descriptor.Origin,
            timestampNs,
            arrivalNs,
            sequence++,
            payloadHandle,
            byteLength,
            data,
            videoFrame));
    }

    public void PushVideoFrame(
        MimirVideoFrameDescriptor frame,
        long arrivalNs,
        ReadOnlyMemory<byte> data = default)
    {
        if (Descriptor.Kind != MimirStreamKind.Video)
        {
            throw new InvalidOperationException("Video frames can only be pushed into a video stream source.");
        }

        Push(
            arrivalNs,
            arrivalNs,
            frame.NativeHandle,
            data.Length,
            data,
            frame);
    }

    public bool TryRead(out MimirStreamSample sample)
    {
        return samples.TryDequeue(out sample);
    }

    public void Dispose()
    {
    }
}
