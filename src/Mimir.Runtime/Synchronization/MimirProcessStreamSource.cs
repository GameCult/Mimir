using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirProcessStreamSourceOptions(
    string Command,
    IReadOnlyList<string> Arguments,
    int ChunkBytes = 65_536);

public sealed class MimirProcessStreamSource : IMimirStreamSource
{
    private readonly ConcurrentQueue<MimirStreamSample> samples = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Process process;
    private readonly Task stdoutTask;
    private readonly Task stderrTask;
    private ulong sequence;
    private bool disposed;

    public MimirProcessStreamSource(
        MimirStreamDescriptor descriptor,
        MimirProcessStreamSourceOptions options)
    {
        Descriptor = descriptor;
        Options = options;
        process = StartProcess(options);
        stdoutTask = Task.Run(() => ReadStdout(options.ChunkBytes, cancellation.Token));
        stderrTask = Task.Run(() => DrainStderr(cancellation.Token));
    }

    public MimirStreamDescriptor Descriptor { get; }

    public MimirProcessStreamSourceOptions Options { get; }

    public bool TryRead(out MimirStreamSample sample)
    {
        return samples.TryDequeue(out sample);
    }

    private Process StartProcess(MimirProcessStreamSourceOptions options)
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
            ?? throw new InvalidOperationException($"Could not start stream source process: {options.Command}");
    }

    private async Task ReadStdout(int chunkBytes, CancellationToken token)
    {
        var stream = process.StandardOutput.BaseStream;
        while (!token.IsCancellationRequested)
        {
            var buffer = new byte[chunkBytes];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            if (read != buffer.Length)
            {
                Array.Resize(ref buffer, read);
            }

            var now = Stopwatch.GetTimestamp();
            var timestampNs = StopwatchTicksToNs(now);
            samples.Enqueue(new MimirStreamSample(
                Descriptor.SourceId,
                Descriptor.Kind,
                Descriptor.Origin,
                timestampNs,
                timestampNs,
                sequence++,
                0,
                buffer.Length,
                buffer));
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
        GC.SuppressFinalize(stdoutTask);
        GC.SuppressFinalize(stderrTask);
    }
}
