namespace Mimir.Runtime.Synchronization;

public sealed class MimirSynchronizationSettings
{
    public TimeSpan BufferDuration { get; init; } = TimeSpan.FromSeconds(5);

    public MimirAudioSynchronizationSettings Audio { get; init; } = new();

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
            Audio = MimirAudioSynchronizationSettings.FromEnvironment(),
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

public sealed class MimirAudioSynchronizationSettings
{
    public const string DefaultReferenceSourceId = "loopback-scarlett-speakers";
    public const float DefaultCalibrationGain = 2.0f;
    public const float DefaultWatermarkGain = 0.035f;

    public MimirAudioSyncMode Mode { get; init; } = MimirAudioSyncMode.Hybrid;

    public string ReferenceSourceId { get; init; } = DefaultReferenceSourceId;

    public float CalibrationGain { get; init; } = DefaultCalibrationGain;

    public float WatermarkGain { get; init; } = DefaultWatermarkGain;

    public string CalibrationModelPath { get; init; } = "";

    public static MimirAudioSynchronizationSettings FromEnvironment()
    {
        return new MimirAudioSynchronizationSettings().WithEnvironmentOverrides();
    }

    public MimirAudioSynchronizationSettings WithEnvironmentOverrides()
    {
        return new MimirAudioSynchronizationSettings
        {
            Mode = ParseMode(Environment.GetEnvironmentVariable("MIMIR_AUDIO_SYNC_MODE"), Mode),
            ReferenceSourceId = ReadReferenceSourceId(ReferenceSourceId),
            CalibrationGain = ReadCalibrationGain(CalibrationGain),
            WatermarkGain = ReadWatermarkGain(WatermarkGain),
            CalibrationModelPath = ReadCalibrationModelPath(CalibrationModelPath),
        };
    }

    public static MimirAudioSyncMode ParseMode(string? value, MimirAudioSyncMode fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "chirp-only" or "chirponly" or "chirp" or "active" => MimirAudioSyncMode.ChirpOnly,
            "passive" or "passive-only" or "passiveonly" or "no-chirp" or "nochirp" => MimirAudioSyncMode.Passive,
            "hybrid" => MimirAudioSyncMode.Hybrid,
            _ => fallback,
        };
    }

    private static string ReadReferenceSourceId(string fallback)
    {
        var sourceId = Environment.GetEnvironmentVariable("MIMIR_AUDIO_SYNC_REFERENCE");
        return string.IsNullOrWhiteSpace(sourceId)
            ? fallback
            : sourceId.Trim();
    }

    private static float ReadCalibrationGain(float fallback)
    {
        return float.TryParse(Environment.GetEnvironmentVariable("MIMIR_CHIRPLET_GAIN"), out var gain)
            ? Math.Clamp(gain, 0.0f, 4.0f)
            : fallback;
    }

    private static float ReadWatermarkGain(float fallback)
    {
        return float.TryParse(Environment.GetEnvironmentVariable("MIMIR_WATERMARK_GAIN"), out var gain)
            ? Math.Clamp(gain, 0.0f, 0.25f)
            : fallback;
    }

    private static string ReadCalibrationModelPath(string fallback)
    {
        var path = Environment.GetEnvironmentVariable("MIMIR_CHIRP_BIN_CALIBRATION");
        return string.IsNullOrWhiteSpace(path) ? fallback : path.Trim();
    }
}

public enum MimirAudioSyncMode
{
    ChirpOnly,
    Passive,
    Hybrid,
}
