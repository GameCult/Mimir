namespace Mimir.Runtime.Synchronization;

public sealed class MimirRollingStreamBuffer
{
    private readonly Queue<MimirStreamSample> samples = new();

    public MimirRollingStreamBuffer(MimirStreamDescriptor descriptor, TimeSpan duration)
    {
        Descriptor = descriptor;
        Duration = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(5);
    }

    public MimirStreamDescriptor Descriptor { get; }

    public TimeSpan Duration { get; }

    public long EdgeNs { get; private set; }

    public long WindowStartNs => Math.Max(0, EdgeNs - DurationNs);

    public long OldestSampleTimestampNs => samples.Count == 0 ? 0L : samples.Peek().TimestampNs;

    public int Count => samples.Count;

    public MimirStreamSample? Latest { get; private set; }

    private long DurationNs => checked((long)(Duration.TotalSeconds * 1_000_000_000.0));

    public void Append(MimirStreamSample sample)
    {
        if (!string.Equals(sample.SourceId, Descriptor.SourceId, StringComparison.Ordinal)
            || sample.Kind != Descriptor.Kind
            || sample.Origin != Descriptor.Origin)
        {
            throw new ArgumentException("Sample does not belong to this stream buffer.", nameof(sample));
        }

        EdgeNs = Math.Max(EdgeNs, sample.TimestampNs);
        samples.Enqueue(sample);
        Latest = sample;
        EvictExpired();
    }

    public IReadOnlyList<MimirStreamSample> Snapshot()
    {
        return samples.ToArray();
    }

    private void EvictExpired()
    {
        var windowStart = WindowStartNs;
        while (samples.Count > 0 && samples.Peek().TimestampNs < windowStart)
        {
            samples.Dequeue();
        }
    }
}
