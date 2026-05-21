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
        ReadOnlyMemory<byte> data = default)
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
            data));
    }

    public bool TryRead(out MimirStreamSample sample)
    {
        return samples.TryDequeue(out sample);
    }

    public void Dispose()
    {
    }
}
