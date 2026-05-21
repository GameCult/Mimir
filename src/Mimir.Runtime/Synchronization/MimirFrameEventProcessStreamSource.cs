using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirFrameEventProcessStreamSourceOptions(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlySet<string>? AcceptedSourceIds = null);

public sealed class MimirFrameEventProcessStreamSource : IMimirStreamSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentQueue<MimirStreamSample> samples = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Process process;
    private readonly Task stdoutTask;
    private readonly Task stderrTask;
    private ulong fallbackSequence;
    private bool disposed;

    public MimirFrameEventProcessStreamSource(
        MimirStreamDescriptor descriptor,
        MimirFrameEventProcessStreamSourceOptions options)
    {
        Descriptor = descriptor;
        Options = options;
        process = StartProcess(options);
        stdoutTask = Task.Run(() => ReadStdout(cancellation.Token));
        stderrTask = Task.Run(() => DrainStderr(cancellation.Token));
    }

    public MimirStreamDescriptor Descriptor { get; }

    public MimirFrameEventProcessStreamSourceOptions Options { get; }

    public bool ExposesDescriptorBuffer => Options.AcceptedSourceIds is not { Count: > 0 };

    public bool TryRead(out MimirStreamSample sample)
    {
        return samples.TryDequeue(out sample);
    }

    private static Process StartProcess(MimirFrameEventProcessStreamSourceOptions options)
    {
        var startInfo = new ProcessStartInfo(options.Command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start frame-event source process: {options.Command}");
    }

    private async Task ReadStdout(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            if (!line.TrimStart().StartsWith('{'))
            {
                continue;
            }

            if (TryParseFrameEvent(line, out var sample))
            {
                samples.Enqueue(sample);
            }
        }
    }

    private bool TryParseFrameEvent(string line, out MimirStreamSample sample)
    {
        sample = default;

        MimirFrameEvent? frameEvent;
        try
        {
            frameEvent = JsonSerializer.Deserialize<MimirFrameEvent>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (frameEvent == null || !frameEvent.IsVideoFrame)
        {
            return false;
        }

        var sourceId = string.IsNullOrWhiteSpace(frameEvent.SourceId)
            ? Descriptor.SourceId
            : frameEvent.SourceId;
        if (!AcceptsSourceId(sourceId))
        {
            return false;
        }

        var timestampNs = frameEvent.TimestampNs > 0
            ? frameEvent.TimestampNs
            : StopwatchTicksToNs(Stopwatch.GetTimestamp());
        var arrivalNs = StopwatchTicksToNs(Stopwatch.GetTimestamp());
        var byteLength = Math.Max(0, frameEvent.ByteLength);
        var strideBytes = frameEvent.StrideBytes > 0
            ? frameEvent.StrideBytes
            : InferStride(frameEvent.Width, frameEvent.PixelFormat, byteLength, frameEvent.Height);
        var sequence = frameEvent.Sequence ?? fallbackSequence++;
        var payloadHandle = frameEvent.NativeHandle;
        var videoFrame = new MimirVideoFrameDescriptor(
            frameEvent.Width,
            frameEvent.Height,
            ParsePixelFormat(frameEvent.PixelFormat),
            strideBytes,
            timestampNs,
            payloadHandle,
            frameEvent.NativeHandleKind ?? "");

        sample = new MimirStreamSample(
            sourceId,
            Descriptor.Kind,
            Descriptor.Origin,
            timestampNs,
            arrivalNs,
            sequence,
            payloadHandle,
            byteLength,
            default,
            videoFrame);
        return true;
    }

    private bool AcceptsSourceId(string sourceId)
    {
        if (Options.AcceptedSourceIds is { Count: > 0 } acceptedSourceIds)
        {
            return acceptedSourceIds.Contains(sourceId);
        }

        return string.Equals(sourceId, Descriptor.SourceId, StringComparison.Ordinal);
    }

    private async Task DrainStderr(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(token).ConfigureAwait(false);
            if (line == null)
            {
                break;
            }
        }
    }

    private static MimirVideoPixelFormat ParsePixelFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return MimirVideoPixelFormat.Unknown;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "BAYER" or "BAYER8" => MimirVideoPixelFormat.Bayer8,
            "GRAY8" or "Y8" => MimirVideoPixelFormat.Gray8,
            "R8" => MimirVideoPixelFormat.R8,
            "RG8" => MimirVideoPixelFormat.Rg8,
            "YUY2" => MimirVideoPixelFormat.Yuy2,
            "MJPG" or "MJPEG" => MimirVideoPixelFormat.Mjpg,
            "H264" => MimirVideoPixelFormat.H264,
            "NV12" => MimirVideoPixelFormat.Nv12,
            "BGRA8" => MimirVideoPixelFormat.Bgra8,
            "LEAP_STEREO_IR" or "LEAPSTEREOIR" => MimirVideoPixelFormat.LeapStereoIr,
            _ => MimirVideoPixelFormat.Unknown,
        };
    }

    private static int InferStride(int width, string? pixelFormat, int byteLength, int height)
    {
        if (width <= 0)
        {
            return 0;
        }

        if (height > 0 && byteLength > 0 && byteLength % height == 0)
        {
            return byteLength / height;
        }

        return (pixelFormat ?? "").Trim().ToUpperInvariant() switch
        {
            "BAYER" or "BAYER8" or "GRAY8" or "Y8" or "R8" => width,
            "RG8" or "YUY2" or "LEAP_STEREO_IR" or "LEAPSTEREOIR" => checked(width * 2),
            "BGRA8" => checked(width * 4),
            _ => 0,
        };
    }

    private static long StopwatchTicksToNs(long ticks)
    {
        return checked((long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency)));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        process.Dispose();
        cancellation.Dispose();
    }

    private sealed class MimirFrameEvent
    {
        public string Type { get; set; } = "";

        public string SourceId { get; set; } = "";

        public long TimestampNs { get; set; }

        public ulong? Sequence { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public string PixelFormat { get; set; } = "";

        public int StrideBytes { get; set; }

        public int ByteLength { get; set; }

        public ulong NativeHandle { get; set; }

        public string? NativeHandleKind { get; set; }

        public bool IsVideoFrame =>
            string.Equals(Type, "video-frame", StringComparison.OrdinalIgnoreCase) &&
            Width > 0 &&
            Height > 0;
    }
}
