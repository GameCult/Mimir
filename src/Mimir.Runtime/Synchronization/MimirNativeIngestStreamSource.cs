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
        MimirVideoFrameDescriptor? videoFrame = null,
        MimirTrackingObservation? trackingObservation = null)
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
            videoFrame,
            TrackingObservation: trackingObservation));
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
            frame.DeviceTimestampNs,
            arrivalNs,
            frame.NativeHandle,
            data.Length,
            data,
            frame);
    }

    public void PushTrackingObservation(MimirTrackingObservation observation)
    {
        if (Descriptor.Kind != MimirStreamKind.Tracking)
        {
            throw new InvalidOperationException("Tracking observations can only be pushed into a tracking stream source.");
        }

        if (!string.Equals(observation.StreamId, Descriptor.SourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Tracking observation stream id does not match this stream source.", nameof(observation));
        }

        Push(
            observation.SourceTimestampNs,
            observation.ArrivalTimestampNs,
            payloadHandle: 0,
            trackingObservation: observation);
    }

    public bool TryRead(out MimirStreamSample sample)
    {
        return samples.TryDequeue(out sample);
    }

    public void Dispose()
    {
    }
}
