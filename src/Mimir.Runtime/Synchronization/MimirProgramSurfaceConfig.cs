using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

[CultDocument("mimir.program_surface_config", "mimir.program_surface_config.v1")]
[MessagePackObject]
public sealed record MimirProgramSurfaceConfigDocument(
    [property: Key(0)]
    [property: CultName]
    string SurfaceId,
    [property: Key(1)] string UpdatedAtUtc,
    [property: Key(2)] string Description,
    [property: Key(3)] string SelectedSourceId,
    [property: Key(4)] MimirProgramSurfaceLayerConfig[] Layers);

[MessagePackObject]
public sealed record MimirProgramSurfaceLayerConfig(
    [property: Key(0)] string SourceId,
    [property: Key(1)] string DisplayName,
    [property: Key(2)] bool Visible,
    [property: Key(3)] bool Locked,
    [property: Key(4)] int Layer,
    [property: Key(5)] float CenterX,
    [property: Key(6)] float CenterY,
    [property: Key(7)] float Width,
    [property: Key(8)] float Height,
    [property: Key(9)] float RotationRadians);

public static class MimirProgramSurfaceConfigStore
{
    public const string DefaultSurfaceId = "mimir.program.default";
    public const string DefaultConfigPath = "config/mimir-program-surface.cc";

    public static MimirProgramSurfaceConfigDocument LoadDefault(string? configuredPath)
    {
        var path = ResolvePath(configuredPath);
        if (!File.Exists(path))
        {
            return CreateDefault();
        }

        try
        {
            using var cache = CultCacheMessagePack.OpenAsync(path, new CultCacheOpenOptions
            {
                PullOnOpen = true
            }).GetAwaiter().GetResult();
            return cache.GetByName<MimirProgramSurfaceConfigDocument>(DefaultSurfaceId) ?? CreateDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mimir program surface config ignored: {path} could not be read ({ex.Message}).");
            return CreateDefault();
        }
    }

    public static MimirProgramSurfaceConfigDocument CreateDefault() =>
        new(
            DefaultSurfaceId,
            "2026-06-01T00:00:00Z",
            "Default Mimir program surface: Raven desktop full-frame with Kiyo Pro picture-in-picture.",
            "kiyo-pro-rgb",
            [
                new MimirProgramSurfaceLayerConfig(
                    "raven-display",
                    "Raven Desktop",
                    Visible: true,
                    Locked: false,
                    Layer: 0,
                    CenterX: 0.5f,
                    CenterY: 0.5f,
                    Width: 1.0f,
                    Height: 1.0f,
                    RotationRadians: 0.0f),
                new MimirProgramSurfaceLayerConfig(
                    "kiyo-pro-rgb",
                    "Kiyo Pro",
                    Visible: true,
                    Locked: false,
                    Layer: 1,
                    CenterX: 0.165f,
                    CenterY: 0.165f,
                    Width: 0.26f,
                    Height: 0.26f,
                    RotationRadians: 0.0f),
            ]);

    public static string ResolvePath(string? configuredPath)
    {
        var path = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : Environment.GetEnvironmentVariable("MIMIR_PROGRAM_SURFACE_CONFIG");
        path = string.IsNullOrWhiteSpace(path)
            ? DefaultConfigPath
            : path;
        return Path.GetFullPath(path);
    }
}
