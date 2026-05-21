namespace Mimir.Runtime.Synchronization;

public sealed class MimirSynchronizationSettings
{
    public TimeSpan BufferDuration { get; init; } = TimeSpan.FromSeconds(5);

    public IReadOnlyList<MimirStreamDescriptor> Streams { get; init; } = [];

    public static MimirSynchronizationSettings FromEnvironment()
    {
        var duration = ReadDuration();
        var streams = new List<MimirStreamDescriptor>();
        AddStreams(streams, Environment.GetEnvironmentVariable("MIMIR_LOCAL_VIDEO_STREAMS"), MimirStreamKind.Video, MimirStreamOrigin.LocalDevice);
        AddStreams(streams, Environment.GetEnvironmentVariable("MIMIR_LOCAL_AUDIO_STREAMS"), MimirStreamKind.Audio, MimirStreamOrigin.LocalDevice);
        AddStreams(streams, Environment.GetEnvironmentVariable("MIMIR_NETWORK_VIDEO_STREAMS"), MimirStreamKind.Video, MimirStreamOrigin.Network);
        AddStreams(streams, Environment.GetEnvironmentVariable("MIMIR_NETWORK_AUDIO_STREAMS"), MimirStreamKind.Audio, MimirStreamOrigin.Network);

        return new MimirSynchronizationSettings
        {
            BufferDuration = duration,
            Streams = streams,
        };
    }

    private static TimeSpan ReadDuration()
    {
        var raw = Environment.GetEnvironmentVariable("MIMIR_SYNC_BUFFER_SECONDS")
            ?? Environment.GetEnvironmentVariable("MIMIR_RESERVOIR_SECONDS");
        return double.TryParse(raw, out var seconds) && double.IsFinite(seconds) && seconds > 0.0
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0.25, 60.0))
            : TimeSpan.FromSeconds(5);
    }

    private static void AddStreams(
        ICollection<MimirStreamDescriptor> streams,
        string? raw,
        MimirStreamKind kind,
        MimirStreamOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        foreach (var sourceId in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            streams.Add(new MimirStreamDescriptor(sourceId, kind, origin));
        }
    }
}
