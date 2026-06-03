using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirFfmpegPcmAudioStreamSourceOptions(
    string Command,
    IReadOnlyList<string> Arguments,
    int SampleRate,
    int Channels,
    MimirAudioSampleFormat SampleFormat,
    int BlockFrames = 960);

public sealed class MimirFfmpegPcmAudioStreamSource : IMimirStreamSource
{
    private readonly ConcurrentQueue<MimirStreamSample> samples = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Process process;
    private readonly Task stdoutTask;
    private readonly Task stderrTask;
    private readonly int blockBytes;
    private ulong sequence;
    private bool disposed;

    public MimirFfmpegPcmAudioStreamSource(
        MimirStreamDescriptor descriptor,
        MimirFfmpegPcmAudioStreamSourceOptions options)
    {
        if (descriptor.Kind != MimirStreamKind.Audio)
        {
            throw new ArgumentException("Raw FFmpeg PCM sources require an audio descriptor.", nameof(descriptor));
        }

        if (options.SampleRate <= 0 || options.Channels <= 0 || options.BlockFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Raw PCM audio sources require positive sample rate, channels, and block frame count.");
        }

        var bytesPerSample = BytesPerSample(options.SampleFormat);
        if (bytesPerSample <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Raw PCM audio sources require a known sample format.");
        }

        Descriptor = descriptor;
        Options = options;
        blockBytes = checked(options.BlockFrames * options.Channels * bytesPerSample);
        process = StartProcess(options);
        stdoutTask = Task.Run(() => ReadStdout(cancellation.Token));
        stderrTask = Task.Run(() => DrainStderr(cancellation.Token));
    }

    public MimirStreamDescriptor Descriptor { get; }

    public MimirFfmpegPcmAudioStreamSourceOptions Options { get; }

    public bool TryRead(out MimirStreamSample sample)
    {
        return samples.TryDequeue(out sample);
    }

    private static Process StartProcess(MimirFfmpegPcmAudioStreamSourceOptions options)
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
            ?? throw new InvalidOperationException($"Could not start FFmpeg raw PCM source process: {options.Command}");
    }

    private async Task ReadStdout(CancellationToken token)
    {
        var stream = process.StandardOutput.BaseStream;
        while (!token.IsCancellationRequested)
        {
            var buffer = new byte[blockBytes];
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

            var timestampNs = UtcNowNs();
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
                AudioBlock: new MimirAudioBlockDescriptor(
                    Options.SampleRate,
                    Options.Channels,
                    Options.SampleFormat,
                    Options.BlockFrames,
                    timestampNs)));
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

    private static int BytesPerSample(MimirAudioSampleFormat format) =>
        format switch
        {
            MimirAudioSampleFormat.Float32 or MimirAudioSampleFormat.Int32 => 4,
            MimirAudioSampleFormat.Int24 => 3,
            MimirAudioSampleFormat.Int16 => 2,
            _ => 0,
        };

    private static long UtcNowNs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

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
