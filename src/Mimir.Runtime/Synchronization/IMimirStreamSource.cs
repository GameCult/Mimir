namespace Mimir.Runtime.Synchronization;

public interface IMimirStreamSource : IDisposable
{
    MimirStreamDescriptor Descriptor { get; }

    bool ExposesDescriptorBuffer => true;

    bool TryRead(out MimirStreamSample sample);
}

public interface IMimirMultiplexedStreamSource : IMimirStreamSource
{
    int LogicalStreamCount { get; }
}
