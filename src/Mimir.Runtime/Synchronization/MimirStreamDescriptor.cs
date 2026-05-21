namespace Mimir.Runtime.Synchronization;

public sealed record MimirStreamDescriptor(
    string SourceId,
    MimirStreamKind Kind,
    MimirStreamOrigin Origin,
    bool Enabled = true)
{
    public string BufferKey => $"{Kind}:{Origin}:{SourceId}";
}
