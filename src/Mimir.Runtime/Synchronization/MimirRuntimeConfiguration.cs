using System.Text.Json;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirRuntimeConfiguration
{
    public MimirSynchronizationSettings Settings { get; init; } = new();

    public IReadOnlyList<IMimirStreamSource> Sources { get; init; } = [];

    public static MimirRuntimeConfiguration Load()
    {
        var path = ResolveConfigPath();
        if (path == null)
        {
            return new MimirRuntimeConfiguration
            {
                Settings = MimirSynchronizationSettings.FromEnvironment(),
            };
        }

        var model = JsonSerializer.Deserialize<MimirRuntimeConfigFile>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new MimirRuntimeConfigFile();

        var bufferDuration = model.BufferSeconds > 0.0
            ? TimeSpan.FromSeconds(Math.Clamp(model.BufferSeconds, 0.25, 60.0))
            : MimirSynchronizationSettings.FromEnvironment().BufferDuration;
        var audio = (model.AudioSync?.ToSettings(new MimirAudioSynchronizationSettings()) ?? new MimirAudioSynchronizationSettings())
            .WithEnvironmentOverrides();

        var sourceModels = model.Streams
            .Where(stream => stream.Enabled)
            .ToArray();
        var streams = sourceModels
            .SelectMany(ToDescriptors)
            .ToArray();
        var sources = sourceModels
            .Select(stream => TryCreateSource(stream, Path.GetDirectoryName(path)))
            .Where(source => source != null)
            .Cast<IMimirStreamSource>()
            .ToArray();

        return new MimirRuntimeConfiguration
        {
            Settings = new MimirSynchronizationSettings
            {
                BufferDuration = bufferDuration,
                Audio = audio,
                Streams = streams,
            },
            Sources = sources,
        };
    }

    private static string? ResolveConfigPath()
    {
        var configured = Environment.GetEnvironmentVariable("MIMIR_RUNTIME_CONFIG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var current = AppContext.BaseDirectory;
        for (var index = 0; index < 8; index++)
        {
            var candidate = Path.Combine(current, "config", "mimir-runtime.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        return null;
    }

    private static MimirStreamDescriptor ToDescriptor(MimirStreamConfig stream)
    {
        return new MimirStreamDescriptor(
            stream.SourceId,
            ParseKind(stream.Kind),
            ParseOrigin(stream.Origin),
            stream.Enabled);
    }

    private static IEnumerable<MimirStreamDescriptor> ToDescriptors(MimirStreamConfig stream)
    {
        if (stream.AcceptSourceIds.Length == 0)
        {
            yield return ToDescriptor(stream);
            yield break;
        }

        foreach (var sourceId in stream.AcceptSourceIds.Where(sourceId => !string.IsNullOrWhiteSpace(sourceId)))
        {
            yield return new MimirStreamDescriptor(
                sourceId,
                ParseKind(stream.Kind),
                ParseOrigin(stream.Origin),
                stream.Enabled);
        }
    }

    private static IMimirStreamSource? TryCreateSource(MimirStreamConfig stream, string? configDirectory)
    {
        if (string.Equals(stream.Adapter, "native", StringComparison.OrdinalIgnoreCase))
        {
            return new MimirNativeIngestStreamSource(ToDescriptor(stream));
        }

        if (string.Equals(stream.Adapter, "frame-events", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stream.Adapter, "json-lines", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(stream.Command))
            {
                return null;
            }

            return new MimirFrameEventProcessStreamSource(
                ToDescriptor(stream),
                new MimirFrameEventProcessStreamSourceOptions(
                    ResolveCommand(stream.Command, configDirectory),
                    stream.Arguments,
                    stream.AcceptSourceIds.Length > 0
                        ? new HashSet<string>(stream.AcceptSourceIds, StringComparer.Ordinal)
                        : null));
        }

        if (!string.Equals(stream.Adapter, "process", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(stream.Command))
        {
            return null;
        }

        return new MimirProcessStreamSource(
            ToDescriptor(stream),
            new MimirProcessStreamSourceOptions(
                ResolveCommand(stream.Command, configDirectory),
                stream.Arguments,
                Math.Max(1024, stream.ChunkBytes)));
    }

    private static string ResolveCommand(string command, string? configDirectory)
    {
        if (string.IsNullOrWhiteSpace(command)
            || Path.IsPathRooted(command)
            || string.IsNullOrWhiteSpace(configDirectory))
        {
            return command;
        }

        return Path.GetFullPath(Path.Combine(configDirectory, command));
    }

    private static MimirStreamKind ParseKind(string value)
    {
        return Enum.TryParse<MimirStreamKind>(value, ignoreCase: true, out var kind)
            ? kind
            : throw new InvalidOperationException($"Unknown Mimir stream kind: {value}");
    }

    private static MimirStreamOrigin ParseOrigin(string value)
    {
        return Enum.TryParse<MimirStreamOrigin>(value, ignoreCase: true, out var origin)
            ? origin
            : throw new InvalidOperationException($"Unknown Mimir stream origin: {value}");
    }
}

public sealed class MimirRuntimeConfigFile
{
    public double BufferSeconds { get; set; } = 5.0;

    public MimirAudioSyncConfig? AudioSync { get; set; }

    public List<MimirStreamConfig> Streams { get; set; } = [];
}

public sealed class MimirAudioSyncConfig
{
    public string Mode { get; set; } = "";

    public string ReferenceSourceId { get; set; } = "";

    public float CalibrationGain { get; set; } = float.NaN;

    public float WatermarkGain { get; set; } = float.NaN;

    public MimirAudioSynchronizationSettings ToSettings(MimirAudioSynchronizationSettings fallback)
    {
        return new MimirAudioSynchronizationSettings
        {
            Mode = MimirAudioSynchronizationSettings.ParseMode(Mode, fallback.Mode),
            ReferenceSourceId = string.IsNullOrWhiteSpace(ReferenceSourceId)
                ? fallback.ReferenceSourceId
                : ReferenceSourceId.Trim(),
            CalibrationGain = float.IsFinite(CalibrationGain)
                ? Math.Clamp(CalibrationGain, 0.0f, 4.0f)
                : fallback.CalibrationGain,
            WatermarkGain = float.IsFinite(WatermarkGain)
                ? Math.Clamp(WatermarkGain, 0.0f, 0.25f)
                : fallback.WatermarkGain,
        };
    }
}

public sealed class MimirStreamConfig
{
    public string SourceId { get; set; } = "";

    public string Kind { get; set; } = "Video";

    public string Origin { get; set; } = "LocalDevice";

    public bool Enabled { get; set; } = true;

    public string Adapter { get; set; } = "process";

    public string Command { get; set; } = "";

    public string[] Arguments { get; set; } = [];

    public string[] AcceptSourceIds { get; set; } = [];

    public int ChunkBytes { get; set; } = 65_536;
}
