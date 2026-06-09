using System.Text.Json;

namespace Mimir.Runtime.Synchronization;

public static class MimirObsSceneImporter
{
    public static MimirProgramSceneDocument ImportFile(
        string path,
        string? sceneName = null,
        DateTimeOffset? updatedAt = null)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return Import(document.RootElement, sceneName, updatedAt);
    }

    public static MimirProgramSceneDocument Import(
        JsonElement root,
        string? sceneName = null,
        DateTimeOffset? updatedAt = null)
    {
        var sources = root.GetProperty("sources").EnumerateArray().ToArray();
        var sourceByName = sources
            .Where(source => source.TryGetProperty("name", out _))
            .ToDictionary(source => source.GetProperty("name").GetString() ?? "", StringComparer.Ordinal);

        var selectedSceneName = !string.IsNullOrWhiteSpace(sceneName)
            ? sceneName
            : root.TryGetProperty("current_program_scene", out var currentProgram)
                ? currentProgram.GetString()
                : root.TryGetProperty("current_scene", out var currentScene)
                    ? currentScene.GetString()
                    : null;
        if (string.IsNullOrWhiteSpace(selectedSceneName))
        {
            selectedSceneName = "Scene";
        }

        if (!sourceByName.TryGetValue(selectedSceneName, out var sceneSource))
        {
            throw new InvalidOperationException($"OBS scene '{selectedSceneName}' was not found.");
        }

        var settings = sceneSource.GetProperty("settings");
        var itemElements = settings.GetProperty("items").EnumerateArray().ToArray();
        var layers = new List<MimirProgramSceneLayer>(itemElements.Length);
        for (var index = 0; index < itemElements.Length; index++)
        {
            var item = itemElements[index];
            var name = item.GetProperty("name").GetString() ?? $"layer-{index}";
            sourceByName.TryGetValue(name, out var source);
            var sourceKind = SourceKind(source);
            var crop = CombineCrop(ItemCrop(item), FilterCrop(source));
            var chromaKey = FilterChromaKey(source);
            var (x, y, width, height) = Bounds(item);
            layers.Add(new MimirProgramSceneLayer(
                LayerId: LayerId(name, index),
                SourceRef: SourceRef(name, sourceKind),
                SourceKind: sourceKind,
                Visible: ReadBool(item, "visible", fallback: true),
                X: x,
                Y: y,
                Width: width,
                Height: height,
                ZIndex: index,
                Crop: crop,
                ChromaKey: chromaKey));
        }

        var resolution = root.TryGetProperty("resolution", out var resolutionElement)
            ? resolutionElement
            : default;
        return new MimirProgramSceneDocument(
            SceneId: SceneId(selectedSceneName),
            UpdatedAtUtc: (updatedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            CanvasWidth: ReadInt(resolution, "x", 1920),
            CanvasHeight: ReadInt(resolution, "y", 1080),
            Owner: "Mimir",
            Layers: layers.ToArray());
    }

    private static string SourceKind(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Undefined)
        {
            return "unknown";
        }

        return (source.GetProperty("id").GetString() ?? "") switch
        {
            "monitor_capture" => "monitor",
            "window_capture" => "window",
            "dshow_input" => "camera",
            "browser_source" => "browser",
            "ffmpeg_source" => "network-monitor",
            "mimir_program_texture_source" => "mimir-program-texture",
            "muninn_stream_source" => "muninn-stream",
            _ => "unknown"
        };
    }

    private static string SourceRef(string name, string sourceKind)
    {
        var normalized = LayerId(name, 0);
        if (name.Contains("raven", StringComparison.OrdinalIgnoreCase))
        {
            return sourceKind.Contains("audio", StringComparison.OrdinalIgnoreCase)
                ? "muninn:raven:audio:realtek-loopback"
                : "muninn:raven:monitor:primary";
        }

        if (sourceKind == "monitor")
        {
            return name.Contains("bigscreen", StringComparison.OrdinalIgnoreCase)
                ? "muninn:starfire:monitor:bigscreen"
                : "muninn:starfire:monitor:primary";
        }

        if (sourceKind == "window")
        {
            return $"muninn:starfire:window:{normalized}";
        }

        if (sourceKind == "camera")
        {
            return $"muninn:starfire:camera:{normalized}";
        }

        if (sourceKind == "mimir-program-texture")
        {
            return "mimir:generated:program-texture";
        }

        if (sourceKind == "browser")
        {
            return $"mimir:browser:{normalized}";
        }

        if (sourceKind == "muninn-stream")
        {
            return $"muninn:starfire:stream:{normalized}";
        }

        return $"mimir:unknown:{normalized}";
    }

    private static (double X, double Y, double Width, double Height) Bounds(JsonElement item)
    {
        var pos = item.TryGetProperty("pos", out var posElement) ? posElement : default;
        var scale = item.TryGetProperty("scale", out var scaleElement) ? scaleElement : default;
        var scaleRef = item.TryGetProperty("scale_ref", out var scaleRefElement) ? scaleRefElement : default;
        var bounds = item.TryGetProperty("bounds", out var boundsElement) ? boundsElement : default;
        var width = ReadDouble(bounds, "x", 0.0);
        var height = ReadDouble(bounds, "y", 0.0);
        var scaledWidth = ReadDouble(scaleRef, "x", 0.0) * Math.Abs(ReadDouble(scale, "x", 1.0));
        var scaledHeight = ReadDouble(scaleRef, "y", 0.0) * Math.Abs(ReadDouble(scale, "y", 1.0));
        if (scaledWidth > width)
        {
            width = scaledWidth;
        }

        if (scaledHeight > height)
        {
            height = scaledHeight;
        }

        return (
            ReadDouble(pos, "x", 0.0),
            ReadDouble(pos, "y", 0.0),
            width,
            height);
    }

    private static MimirProgramCrop ItemCrop(JsonElement item) =>
        new(
            ReadDouble(item, "crop_left", 0.0),
            ReadDouble(item, "crop_top", 0.0),
            ReadDouble(item, "crop_right", 0.0),
            ReadDouble(item, "crop_bottom", 0.0));

    private static MimirProgramCrop FilterCrop(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Undefined || !source.TryGetProperty("filters", out var filters))
        {
            return new MimirProgramCrop(0, 0, 0, 0);
        }

        foreach (var filter in filters.EnumerateArray())
        {
            if ((filter.GetProperty("id").GetString() ?? "") != "crop_filter")
            {
                continue;
            }

            var settings = filter.GetProperty("settings");
            return new MimirProgramCrop(
                ReadDouble(settings, "left", 0.0),
                ReadDouble(settings, "top", 0.0),
                ReadDouble(settings, "right", 0.0),
                ReadDouble(settings, "bottom", 0.0));
        }

        return new MimirProgramCrop(0, 0, 0, 0);
    }

    private static MimirProgramChromaKey? FilterChromaKey(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Undefined || !source.TryGetProperty("filters", out var filters))
        {
            return null;
        }

        MimirProgramChromaKey? strongest = null;
        foreach (var filter in filters.EnumerateArray())
        {
            if ((filter.GetProperty("id").GetString() ?? "") != "chroma_key_filter")
            {
                continue;
            }

            var settings = filter.GetProperty("settings");
            strongest = new MimirProgramChromaKey(
                ReadUInt(settings, "key_color", 0),
                ReadDouble(settings, "similarity", 0.0),
                ReadDouble(settings, "smoothness", 0.0),
                ReadDouble(settings, "spill", 0.0));
        }

        return strongest;
    }

    private static MimirProgramCrop CombineCrop(MimirProgramCrop item, MimirProgramCrop filter) =>
        new(
            item.Left + filter.Left,
            item.Top + filter.Top,
            item.Right + filter.Right,
            item.Bottom + filter.Bottom);

    private static string SceneId(string name) =>
        $"mimir-scene-{LayerId(name, 0)}";

    private static string LayerId(string name, int fallback)
    {
        var chars = name
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var id = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(id) ? $"layer-{fallback}" : id;
    }

    private static bool ReadBool(JsonElement element, string property, bool fallback) =>
        element.ValueKind != JsonValueKind.Undefined &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int ReadInt(JsonElement element, string property, int fallback) =>
        element.ValueKind != JsonValueKind.Undefined &&
        element.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static double ReadDouble(JsonElement element, string property, double fallback) =>
        element.ValueKind != JsonValueKind.Undefined &&
        element.TryGetProperty(property, out var value) &&
        value.TryGetDouble(out var parsed)
            ? parsed
            : fallback;

    private static uint ReadUInt(JsonElement element, string property, uint fallback)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !element.TryGetProperty(property, out var value))
        {
            return fallback;
        }

        return value.TryGetUInt32(out var parsed)
            ? parsed
            : value.TryGetInt64(out var signed)
                ? unchecked((uint)signed)
                : fallback;
    }
}
