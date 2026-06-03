using System.Text.Json;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirRuntimeConfiguration
{
    public MimirSynchronizationSettings Settings { get; init; } = new();

    public IReadOnlyList<MimirStreamSourceFactory> SourceFactories { get; init; } = [];

    public IReadOnlyList<IMimirStreamSource> CreateSources()
    {
        return SourceFactories
            .Select(factory => factory.Create())
            .Where(source => source != null)
            .Cast<IMimirStreamSource>()
            .ToArray();
    }

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
        var configDirectory = Path.GetDirectoryName(path);
        var audio = (model.AudioSync?.ToSettings(new MimirAudioSynchronizationSettings(), configDirectory) ?? new MimirAudioSynchronizationSettings())
            .WithEnvironmentOverrides();

        var sourceModels = model.Streams
            .Where(stream => stream.Enabled)
            .ToArray();
        var streams = sourceModels
            .SelectMany(ToDescriptors)
            .ToArray();
        var sourceFactories = sourceModels
            .Select(stream => TryCreateSourceFactory(stream, configDirectory))
            .Where(factory => factory != null)
            .Cast<MimirStreamSourceFactory>()
            .ToArray();

        return new MimirRuntimeConfiguration
        {
            Settings = new MimirSynchronizationSettings
            {
                BufferDuration = bufferDuration,
                Audio = audio,
                Streams = streams,
            },
            SourceFactories = sourceFactories,
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
            stream.Enabled,
            stream.DisplayName,
            stream.ClockDomainId);
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
                stream.Enabled,
                stream.DisplayNameForSource(sourceId),
                stream.ClockDomainId);
        }
    }

    private static MimirStreamSourceFactory? TryCreateSourceFactory(MimirStreamConfig stream, string? configDirectory)
    {
        var descriptor = ToDescriptor(stream);
        var diagnostics = ToDiagnostics(stream, configDirectory);
        if (string.Equals(stream.Adapter, "native", StringComparison.OrdinalIgnoreCase))
        {
            return new MimirStreamSourceFactory(descriptor, () => new MimirNativeIngestStreamSource(descriptor), diagnostics);
        }

        if (string.Equals(stream.Adapter, "asio", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The Mimir ASIO stream source requires Windows.");
            }

#pragma warning disable CA1416
            return new MimirStreamSourceFactory(descriptor, () => new MimirAsioStreamSource(
                descriptor,
                new MimirAsioStreamSourceOptions(
                    ResolveCommand(stream.Command, configDirectory),
                    stream.DriverClsid,
                    stream.SampleRate,
                    stream.AcceptSourceIds)),
                diagnostics);
#pragma warning restore CA1416
        }

        if (string.Equals(stream.Adapter, "ks-camera", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stream.Adapter, "uvc-direct", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The Mimir KS camera source requires Windows.");
            }

            return new MimirStreamSourceFactory(descriptor, () => new MimirVideoCaptureDriverSource(
                descriptor,
                new MimirKsVideoCaptureDriver(new MimirKsVideoCaptureDriverOptions(
                    ResolveCommand(stream.Command, configDirectory),
                    stream.SourceId,
                    stream.PathNeedle,
                    stream.Width,
                    stream.Height,
                    string.IsNullOrWhiteSpace(stream.InputFormat) ? stream.PixelFormat : stream.InputFormat,
                    stream.MinimumFramesPerSecond,
                    stream.QueueDepth,
                    ParsePixelFormat(stream.PixelFormat))),
                UtcNowNs),
                diagnostics);
        }

        if (string.Equals(stream.Adapter, "ps3eye", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stream.Adapter, "ps3eye-direct", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The Mimir PS3 Eye source requires Windows.");
            }

            return new MimirStreamSourceFactory(descriptor, () => new MimirVideoCaptureDriverSource(
                descriptor,
                new MimirPs3EyeVideoCaptureDriver(new MimirPs3EyeVideoCaptureDriverOptions(
                    ResolveCommand(stream.Command, configDirectory),
                    stream.SourceId,
                    stream.CameraIndex,
                    stream.Width,
                    stream.Height,
                    stream.FramesPerSecond)),
                UtcNowNs),
                diagnostics);
        }

        if (string.Equals(stream.Adapter, "mf-gpu", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stream.Adapter, "media-foundation-gpu", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The Mimir Media Foundation GPU source requires Windows.");
            }

            return new MimirStreamSourceFactory(descriptor, () => new MimirVideoCaptureDriverSource(
                descriptor,
                new MimirMediaFoundationGpuVideoCaptureDriver(new MimirMediaFoundationGpuVideoCaptureDriverOptions(
                    ResolveCommand(stream.Command, configDirectory),
                    stream.SourceId,
                    stream.PathNeedle,
                    stream.Width,
                    stream.Height,
                    stream.InputFormat,
                    stream.OutputFormat,
                    stream.MinimumFramesPerSecond)),
                UtcNowNs),
                diagnostics);
        }

        if (string.Equals(stream.Adapter, "frame-events", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stream.Adapter, "json-lines", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(stream.Command))
            {
                return null;
            }

            return new MimirStreamSourceFactory(descriptor, () => new MimirFrameEventProcessStreamSource(
                descriptor,
                new MimirFrameEventProcessStreamSourceOptions(
                    ResolveCommand(stream.Command, configDirectory),
                    stream.Arguments,
                    stream.AcceptSourceIds.Length > 0
                        ? new HashSet<string>(stream.AcceptSourceIds, StringComparer.Ordinal)
                        : null)),
                diagnostics);
        }

        if (string.Equals(stream.Adapter, "ffmpeg-rawvideo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stream.Adapter, "rawvideo-process", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(stream.Command))
            {
                return null;
            }

            return new MimirStreamSourceFactory(descriptor, () => new MimirFfmpegRawVideoStreamSource(
                descriptor,
                new MimirFfmpegRawVideoStreamSourceOptions(
                    ResolveCommand(stream.Command, configDirectory),
                    stream.Arguments,
                    stream.Width,
                    stream.Height,
                    ParsePixelFormat(stream.PixelFormat),
                    stream.FrameBytes,
                    stream.StrideBytes)),
                diagnostics);
        }

        if (string.Equals(stream.Adapter, "ffmpeg-pcmaudio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stream.Adapter, "pcmaudio-process", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(stream.Command))
            {
                return null;
            }

            return new MimirStreamSourceFactory(descriptor, () => new MimirFfmpegPcmAudioStreamSource(
                descriptor,
                new MimirFfmpegPcmAudioStreamSourceOptions(
                    ResolveCommand(stream.Command, configDirectory),
                    stream.Arguments,
                    stream.SampleRate,
                    stream.Channels,
                    ParseAudioSampleFormat(stream.SampleFormat),
                    stream.BlockFrames)),
                diagnostics);
        }

        if (!string.Equals(stream.Adapter, "process", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(stream.Command))
        {
            return null;
        }

        return new MimirStreamSourceFactory(descriptor, () => new MimirProcessStreamSource(
            descriptor,
            new MimirProcessStreamSourceOptions(
                ResolveCommand(stream.Command, configDirectory),
                stream.Arguments,
                Math.Max(1024, stream.ChunkBytes))),
            diagnostics);
    }

    private static MimirStreamSourceDiagnostics ToDiagnostics(MimirStreamConfig stream, string? configDirectory) =>
        new(
            stream.Adapter,
            ResolveCommand(stream.Command, configDirectory),
            stream.PathNeedle,
            stream.Width,
            stream.Height,
            stream.InputFormat,
            stream.OutputFormat,
            stream.PixelFormat,
            stream.MinimumFramesPerSecond,
            stream.FramesPerSecond,
            stream.SampleRate,
            stream.Channels,
            stream.QueueDepth,
            stream.AcceptSourceIds);

    private static string ResolveCommand(string command, string? configDirectory)
    {
        if (string.IsNullOrWhiteSpace(command)
            || (!command.Contains(Path.DirectorySeparatorChar) && !command.Contains(Path.AltDirectorySeparatorChar))
            || Path.IsPathRooted(command)
            || string.IsNullOrWhiteSpace(configDirectory))
        {
            return command;
        }

        return Path.GetFullPath(Path.Combine(configDirectory, command));
    }

    private static long UtcNowNs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

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

    private static MimirVideoPixelFormat ParsePixelFormat(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? MimirVideoPixelFormat.Unknown
            : value.Trim().ToUpperInvariant() switch
            {
                "BAYER" or "BAYER8" => MimirVideoPixelFormat.Bayer8,
                "GRAY8" or "Y8" => MimirVideoPixelFormat.Gray8,
                "R8" => MimirVideoPixelFormat.R8,
                "RG8" => MimirVideoPixelFormat.Rg8,
                "YUY2" => MimirVideoPixelFormat.Yuy2,
                "MJPG" or "MJPEG" => MimirVideoPixelFormat.Mjpg,
                "H264" => MimirVideoPixelFormat.H264,
                "NV12" => MimirVideoPixelFormat.Nv12,
                "BGRA8" or "BGRA" => MimirVideoPixelFormat.Bgra8,
                "LEAP_STEREO_IR" or "LEAPSTEREOIR" => MimirVideoPixelFormat.LeapStereoIr,
                _ => MimirVideoPixelFormat.Unknown,
            };
    }

    private static MimirAudioSampleFormat ParseAudioSampleFormat(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? MimirAudioSampleFormat.Unknown
            : value.Trim().ToUpperInvariant() switch
            {
                "FLOAT32" or "F32" or "IEEE_FLOAT" => MimirAudioSampleFormat.Float32,
                "INT16" or "PCM16" or "S16" => MimirAudioSampleFormat.Int16,
                "INT24" or "PCM24" or "S24" => MimirAudioSampleFormat.Int24,
                "INT32" or "PCM32" or "S32" => MimirAudioSampleFormat.Int32,
                _ => MimirAudioSampleFormat.Unknown,
            };
    }
}

public sealed record MimirStreamSourceFactory(
    MimirStreamDescriptor Descriptor,
    Func<IMimirStreamSource?> Create,
    MimirStreamSourceDiagnostics? Diagnostics = null);

public sealed record MimirStreamSourceDiagnostics(
    string Adapter,
    string Command,
    string PathNeedle,
    int Width,
    int Height,
    string InputFormat,
    string OutputFormat,
    string PixelFormat,
    double MinimumFramesPerSecond,
    int FramesPerSecond,
    int SampleRate,
    int Channels,
    int QueueDepth,
    IReadOnlyList<string> AcceptSourceIds);

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

    public string CalibrationModelPath { get; set; } = "";

    public string ComplexContourChannelModelPath { get; set; } = "";

    public string BioacousticWitnessProfileId { get; set; } = "";

    public bool EnableComplexContourRuntime { get; set; }

    public MimirAudioSynchronizationSettings ToSettings(MimirAudioSynchronizationSettings fallback, string? configDirectory = null)
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
            CalibrationModelPath = string.IsNullOrWhiteSpace(CalibrationModelPath)
                ? fallback.CalibrationModelPath
                : ResolveConfigPath(CalibrationModelPath, configDirectory),
            ComplexContourChannelModelPath = string.IsNullOrWhiteSpace(ComplexContourChannelModelPath)
                ? fallback.ComplexContourChannelModelPath
                : ResolveConfigPath(ComplexContourChannelModelPath, configDirectory),
            BioacousticWitnessProfileId = string.IsNullOrWhiteSpace(BioacousticWitnessProfileId)
                ? fallback.BioacousticWitnessProfileId
                : BioacousticWitnessProfileId.Trim(),
            EnableComplexContourRuntime = EnableComplexContourRuntime || fallback.EnableComplexContourRuntime,
            };
    }

    private static long StopwatchTicksToNs(long ticks)
    {
        return checked((long)(ticks * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency)));
    }

    private static string ResolveConfigPath(string path, string? configDirectory)
    {
        var trimmed = path.Trim();
        return Path.IsPathRooted(trimmed) || string.IsNullOrWhiteSpace(configDirectory)
            ? trimmed
            : Path.GetFullPath(Path.Combine(configDirectory, trimmed));
    }
}

public sealed class MimirStreamConfig
{
    public string SourceId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string ClockDomainId { get; set; } = "";

    public string Kind { get; set; } = "Video";

    public string Origin { get; set; } = "LocalDevice";

    public bool Enabled { get; set; } = true;

    public string Adapter { get; set; } = "process";

    public string Command { get; set; } = "";

    public string[] Arguments { get; set; } = [];

    public string[] AcceptSourceIds { get; set; } = [];

    public Dictionary<string, string> SourceLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int ChunkBytes { get; set; } = 65_536;

    public int SampleRate { get; set; } = 192_000;

    public int Channels { get; set; } = 2;

    public string SampleFormat { get; set; } = "Float32";

    public int BlockFrames { get; set; } = 960;

    public string DriverClsid { get; set; } = "";

    public string PathNeedle { get; set; } = "";

    public int Width { get; set; }

    public int Height { get; set; }

    public string PixelFormat { get; set; } = "";

    public int FrameBytes { get; set; }

    public int StrideBytes { get; set; }

    public string InputFormat { get; set; } = "";

    public string OutputFormat { get; set; } = "Nv12";

    public double MinimumFramesPerSecond { get; set; }

    public int QueueDepth { get; set; } = 8;

    public int CameraIndex { get; set; }

    public int FramesPerSecond { get; set; }

    public string DisplayNameForSource(string sourceId)
    {
        if (SourceLabels.TryGetValue(sourceId, out var label) && !string.IsNullOrWhiteSpace(label))
        {
            return label.Trim();
        }

        return sourceId;
    }
}
