namespace Mimir.Runtime.Synchronization;

public sealed record MimirStreamDescriptor(
    string SourceId,
    MimirStreamKind Kind,
    MimirStreamOrigin Origin,
    bool Enabled = true,
    string DisplayName = "")
{
    public string BufferKey => $"{Kind}:{Origin}:{SourceId}";

    public string Label => string.IsNullOrWhiteSpace(DisplayName)
        ? SourceId
        : DisplayName;
}
