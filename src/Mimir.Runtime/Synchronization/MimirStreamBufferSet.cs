namespace Mimir.Runtime.Synchronization;

public sealed class MimirStreamBufferSet
{
    private readonly Dictionary<string, MimirRollingStreamBuffer> buffers = new(StringComparer.Ordinal);

    public MimirStreamBufferSet(TimeSpan defaultDuration)
    {
        DefaultDuration = defaultDuration > TimeSpan.Zero ? defaultDuration : TimeSpan.FromSeconds(5);
    }

    public TimeSpan DefaultDuration { get; }

    public IReadOnlyCollection<MimirRollingStreamBuffer> Buffers => buffers.Values;

    public MimirRollingStreamBuffer EnsureBuffer(MimirStreamDescriptor descriptor)
    {
        if (!buffers.TryGetValue(descriptor.BufferKey, out var buffer))
        {
            buffer = new MimirRollingStreamBuffer(descriptor, DefaultDuration);
            buffers.Add(descriptor.BufferKey, buffer);
        }

        return buffer;
    }

    public void Append(MimirStreamSample sample)
    {
        EnsureBuffer(new MimirStreamDescriptor(sample.SourceId, sample.Kind, sample.Origin)).Append(sample);
    }
}
