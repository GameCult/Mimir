namespace Mimir.Runtime.Synchronization;

public sealed record MimirLookupTablePreset(
    string Id,
    string DisplayName,
    string LutPath,
    float Exposure,
    float Contrast,
    float Saturation,
    float Temperature,
    float Tint,
    float BloomIntensity,
    float BloomVeilIntensity)
{
    public static IReadOnlyList<MimirLookupTablePreset> BuiltIn { get; } =
    [
        new(
            "neutral",
            "Neutral",
            "luts/neutral.cube",
            Exposure: 0.16f,
            Contrast: 1.00f,
            Saturation: 1.00f,
            Temperature: 0.00f,
            Tint: 0.00f,
            BloomIntensity: 0.072f,
            BloomVeilIntensity: 0.014f),
        new(
            "clean-rec709",
            "Clean Rec.709",
            "luts/clean-rec709.cube",
            Exposure: 0.18f,
            Contrast: 1.04f,
            Saturation: 0.98f,
            Temperature: 0.00f,
            Tint: 0.00f,
            BloomIntensity: 0.045f,
            BloomVeilIntensity: 0.006f),
        new(
            "soft-film",
            "Soft Film",
            "luts/soft-film.cube",
            Exposure: 0.15f,
            Contrast: 0.94f,
            Saturation: 1.08f,
            Temperature: 0.08f,
            Tint: 0.02f,
            BloomIntensity: 0.095f,
            BloomVeilIntensity: 0.022f),
        new(
            "cool-monitor",
            "Cool Monitor",
            "luts/cool-monitor.cube",
            Exposure: 0.17f,
            Contrast: 1.02f,
            Saturation: 0.92f,
            Temperature: -0.10f,
            Tint: 0.01f,
            BloomIntensity: 0.060f,
            BloomVeilIntensity: 0.010f),
        new(
            "warm-room",
            "Warm Room",
            "luts/warm-room.cube",
            Exposure: 0.14f,
            Contrast: 0.98f,
            Saturation: 1.06f,
            Temperature: 0.12f,
            Tint: -0.01f,
            BloomIntensity: 0.085f,
            BloomVeilIntensity: 0.018f),
    ];
}

public sealed class MimirVideoPresentationControl
{
    public MimirVideoPresentationControl(string sourceId, string displayName, int layer)
    {
        SourceId = sourceId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? sourceId : displayName;
        Layer = Math.Max(0, layer);
    }

    public string SourceId { get; }

    public string DisplayName { get; private set; }

    public bool Enabled { get; set; } = true;

    public bool Solo { get; set; }

    public float Opacity { get; set; } = 1.0f;

    public int Layer { get; set; }

    public void RefreshLabel(string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName;
        }
    }
}

public sealed class MimirAudioPresentationControl
{
    public MimirAudioPresentationControl(string sourceId, string displayName)
    {
        SourceId = sourceId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? sourceId : displayName;
    }

    public string SourceId { get; }

    public string DisplayName { get; private set; }

    public bool Muted { get; set; }

    public bool Solo { get; set; }

    public float Gain { get; set; } = 1.0f;

    public void RefreshLabel(string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName;
        }
    }
}

public sealed record MimirPostprocessControlSnapshot(
    string PresetId,
    string PresetName,
    string LutPath,
    float LutStrength,
    float Exposure,
    float Contrast,
    float Saturation,
    float Temperature,
    float Tint,
    float BloomIntensity,
    float BloomVeilIntensity);

public sealed class MimirPresentationControlState
{
    private readonly Dictionary<string, MimirVideoPresentationControl> video = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MimirAudioPresentationControl> audio = new(StringComparer.Ordinal);

    public IReadOnlyList<MimirLookupTablePreset> LutPresets => MimirLookupTablePreset.BuiltIn;

    public int SelectedVideoIndex { get; set; }

    public int SelectedAudioIndex { get; set; }

    public int SelectedLutPresetIndex { get; private set; }

    public float LutStrength { get; set; } = 1.0f;

    public MimirPostprocessControlSnapshot Postprocess { get; private set; } =
        SnapshotFor(MimirLookupTablePreset.BuiltIn[0], 1.0f);

    public IReadOnlyList<MimirVideoPresentationControl> VideoFeeds =>
        video.Values.OrderBy(feed => feed.Layer).ThenBy(feed => feed.SourceId, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<MimirAudioPresentationControl> AudioFeeds =>
        audio.Values.OrderBy(feed => feed.SourceId, StringComparer.Ordinal).ToArray();

    public MimirVideoPresentationControl? SelectedVideo =>
        VideoFeeds.Count == 0 ? null : VideoFeeds[Math.Clamp(SelectedVideoIndex, 0, VideoFeeds.Count - 1)];

    public MimirAudioPresentationControl? SelectedAudio =>
        AudioFeeds.Count == 0 ? null : AudioFeeds[Math.Clamp(SelectedAudioIndex, 0, AudioFeeds.Count - 1)];

    public void SyncFromBuffers(IEnumerable<MimirRollingStreamBuffer> buffers)
    {
        var videoIndex = video.Count;
        foreach (var buffer in buffers)
        {
            if (buffer.Descriptor.Kind == MimirStreamKind.Video)
            {
                if (!video.TryGetValue(buffer.Descriptor.SourceId, out var control))
                {
                    control = new MimirVideoPresentationControl(buffer.Descriptor.SourceId, buffer.Descriptor.Label, videoIndex++);
                    video.Add(buffer.Descriptor.SourceId, control);
                }

                control.RefreshLabel(buffer.Descriptor.Label);
                continue;
            }

            if (buffer.Descriptor.Kind == MimirStreamKind.Audio)
            {
                if (!audio.TryGetValue(buffer.Descriptor.SourceId, out var control))
                {
                    control = new MimirAudioPresentationControl(buffer.Descriptor.SourceId, buffer.Descriptor.Label);
                    audio.Add(buffer.Descriptor.SourceId, control);
                }

                control.RefreshLabel(buffer.Descriptor.Label);
            }
        }

        SelectedVideoIndex = ClampIndex(SelectedVideoIndex, VideoFeeds.Count);
        SelectedAudioIndex = ClampIndex(SelectedAudioIndex, AudioFeeds.Count);
    }

    public bool IncludesVideo(string sourceId)
    {
        if (!video.TryGetValue(sourceId, out var control))
        {
            return true;
        }

        var hasSolo = video.Values.Any(feed => feed.Solo);
        return hasSolo ? control.Solo : control.Enabled;
    }

    public float VideoOpacity(string sourceId) =>
        video.TryGetValue(sourceId, out var control)
            ? Math.Clamp(control.Opacity, 0.0f, 1.0f)
            : 1.0f;

    public float AudioGain(string sourceId)
    {
        if (!audio.TryGetValue(sourceId, out var control))
        {
            return 1.0f;
        }

        var hasSolo = audio.Values.Any(feed => feed.Solo);
        if (control.Muted || (hasSolo && !control.Solo))
        {
            return 0.0f;
        }

        return Math.Clamp(control.Gain, 0.0f, 2.0f);
    }

    public void MoveSelectedVideo(int delta)
    {
        var selected = SelectedVideo;
        if (selected == null)
        {
            return;
        }

        selected.Layer = Math.Clamp(selected.Layer + delta, 0, Math.Max(0, video.Count - 1));
        NormalizeVideoLayers();
        SelectedVideoIndex = VideoFeeds.ToList().FindIndex(feed => string.Equals(feed.SourceId, selected.SourceId, StringComparison.Ordinal));
    }

    public void SelectLutPreset(int index)
    {
        SelectedLutPresetIndex = ClampIndex(index, LutPresets.Count);
        Postprocess = SnapshotFor(LutPresets[SelectedLutPresetIndex], LutStrength);
    }

    public void RefreshPostprocess()
    {
        Postprocess = SnapshotFor(LutPresets[SelectedLutPresetIndex], LutStrength);
    }

    private void NormalizeVideoLayers()
    {
        var ordered = video.Values
            .OrderBy(feed => feed.Layer)
            .ThenBy(feed => feed.SourceId, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            ordered[index].Layer = index;
        }
    }

    private static int ClampIndex(int index, int count) =>
        count <= 0 ? 0 : Math.Clamp(index, 0, count - 1);

    private static MimirPostprocessControlSnapshot SnapshotFor(MimirLookupTablePreset preset, float strength) =>
        new(
            preset.Id,
            preset.DisplayName,
            preset.LutPath,
            Math.Clamp(strength, 0.0f, 1.0f),
            preset.Exposure,
            preset.Contrast,
            preset.Saturation,
            preset.Temperature,
            preset.Tint,
            preset.BloomIntensity,
            preset.BloomVeilIntensity);
}
