using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirFfmpegRawVideoStreamSourceOptions(
    string Command,
    IReadOnlyList<string> Arguments,
    int Width,
    int Height,
    MimirVideoPixelFormat PixelFormat,
    int FrameBytes = 0,
    int StrideBytes = 0);

public sealed class MimirFfmpegRawVideoStreamSource : IMimirStreamSource
{
    private readonly ConcurrentQueue<MimirStreamSample> samples = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Process process;
    private readonly Task stdoutTask;
    private readonly Task stderrTask;
    private readonly int frameBytes;
    private readonly int strideBytes;
    private ulong sequence;
    private bool disposed;

    public MimirFfmpegRawVideoStreamSource(
        MimirStreamDescriptor descriptor,
        MimirFfmpegRawVideoStreamSourceOptions options)
    {
        if (descriptor.Kind != MimirStreamKind.Video)
        {
            throw new ArgumentException("Raw FFmpeg video sources require a video descriptor.", nameof(descriptor));
        }

        if (options.Width <= 0 || options.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Raw FFmpeg video sources require positive width and height.");
        }

        Descriptor = descriptor;
        Options = options;
        strideBytes = options.StrideBytes > 0 ? options.StrideBytes : InferStride(options.Width, options.PixelFormat);
        frameBytes = options.FrameBytes > 0 ? options.FrameBytes : InferFrameBytes(options.Width, options.Height, options.PixelFormat, strideBytes);
        if (strideBytes <= 0 || frameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Could not infer raw video frame size.");
        }

        process = StartProcess(options);
        stdoutTask = Task.Run(() => ReadStdout(cancellation.Token));
        stderrTask = Task.Run(() => DrainStderr(cancellation.Token));
    }

    public MimirStreamDescriptor Descriptor { get; }

    public MimirFfmpegRawVideoStreamSourceOptions Options { get; }

    public bool TryRead(out MimirStreamSample sample)
    {
        return samples.TryDequeue(out sample);
    }

    private static Process StartProcess(MimirFfmpegRawVideoStreamSourceOptions options)
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
            ?? throw new InvalidOperationException($"Could not start FFmpeg raw video source process: {options.Command}");
    }

    private async Task ReadStdout(CancellationToken token)
    {
        var stream = process.StandardOutput.BaseStream;
        while (!token.IsCancellationRequested)
        {
            var buffer = new byte[frameBytes];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                offset += read;
            }

            var timestampNs = StopwatchTicksToNs(Stopwatch.GetTimestamp());
            var videoFrame = new MimirVideoFrameDescriptor(
                Options.Width,
                Options.Height,
                Options.PixelFormat,
                strideBytes,
                timestampNs);
            samples.Enqueue(new MimirStreamSample(
                Descriptor.SourceId,
                Descriptor.Kind,
                Descriptor.Origin,
                timestampNs,
                timestampNs,
                sequence++,
                0,
                buffer.Length,
                buffer,
                videoFrame));
        }
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

    private static int InferStride(int width, MimirVideoPixelFormat pixelFormat)
    {
        return pixelFormat switch
        {
            MimirVideoPixelFormat.Bgra8 => checked(width * 4),
            MimirVideoPixelFormat.Nv12 => width,
            MimirVideoPixelFormat.R8 or MimirVideoPixelFormat.Gray8 or MimirVideoPixelFormat.Bayer8 => width,
            MimirVideoPixelFormat.Rg8 or MimirVideoPixelFormat.Yuy2 or MimirVideoPixelFormat.LeapStereoIr => checked(width * 2),
            _ => 0,
        };
    }

    private static int InferFrameBytes(int width, int height, MimirVideoPixelFormat pixelFormat, int strideBytes)
    {
        return pixelFormat switch
        {
            MimirVideoPixelFormat.Nv12 => checked(width * height * 3 / 2),
            _ => checked(strideBytes * height),
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
}
