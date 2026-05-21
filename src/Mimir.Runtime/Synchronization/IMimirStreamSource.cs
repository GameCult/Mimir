namespace Mimir.Runtime.Synchronization;

public interface IMimirStreamSource : IDisposable
{
    MimirStreamDescriptor Descriptor { get; }

    bool TryRead(out MimirStreamSample sample);
}
