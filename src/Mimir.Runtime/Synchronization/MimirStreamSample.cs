namespace Mimir.Runtime.Synchronization;

public readonly record struct MimirStreamSample(
    string SourceId,
    MimirStreamKind Kind,
    MimirStreamOrigin Origin,
    long TimestampNs,
    long ArrivalNs,
    ulong Sequence,
    ulong PayloadHandle,
    int ByteLength = 0);
