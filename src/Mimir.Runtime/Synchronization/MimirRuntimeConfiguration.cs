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

        var sourceModels = model.Streams
            .Where(stream => stream.Enabled)
            .ToArray();
        var streams = sourceModels
            .Select(ToDescriptor)
            .ToArray();
        var sources = sourceModels
            .Select(TryCreateSource)
            .Where(source => source != null)
            .Cast<IMimirStreamSource>()
            .ToArray();

        return new MimirRuntimeConfiguration
        {
            Settings = new MimirSynchronizationSettings
            {
                BufferDuration = bufferDuration,
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

    private static IMimirStreamSource? TryCreateSource(MimirStreamConfig stream)
    {
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
                stream.Command,
                stream.Arguments,
                Math.Max(1024, stream.ChunkBytes)));
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

    public List<MimirStreamConfig> Streams { get; set; } = [];
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

    public int ChunkBytes { get; set; } = 65_536;
}
