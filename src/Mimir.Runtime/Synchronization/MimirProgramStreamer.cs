using System.Diagnostics;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirProgramStreamer : IDisposable
{
    private readonly string repositoryRoot;
    private Process? process;
    private string status = "idle";
    private DateTimeOffset? startedAt;

    public MimirProgramStreamer(string? repositoryRoot = null)
    {
        this.repositoryRoot = string.IsNullOrWhiteSpace(repositoryRoot)
            ? ResolveRepositoryRoot()
            : Path.GetFullPath(repositoryRoot);
    }

    public bool IsStreaming => process is { HasExited: false };

    public string TargetUrl => Environment.GetEnvironmentVariable("MIMIR_STREAM_TARGET")
        ?? "rtmp://127.0.0.1:11935/live/mimir";

    public string Describe()
    {
        Refresh();
        if (IsStreaming)
        {
            var elapsed = startedAt == null
                ? TimeSpan.Zero
                : DateTimeOffset.Now - startedAt.Value;
            return $"live {elapsed.TotalSeconds:0}s pid={process!.Id}";
        }

        return status;
    }

    public bool Start()
    {
        Refresh();
        if (IsStreaming)
        {
            return false;
        }

        var projectPath = Path.Combine(repositoryRoot, "src", "Mimir.Broadcast", "Mimir.Broadcast.csproj");
        if (!File.Exists(projectPath))
        {
            status = "broadcast project missing";
            return false;
        }

        var inputPath = Environment.GetEnvironmentVariable("MIMIR_STREAM_INPUT");
        var allowTestPattern = IsTruthy(Environment.GetEnvironmentVariable("MIMIR_STREAM_ALLOW_TEST_PATTERN"));
        if (string.IsNullOrWhiteSpace(inputPath) && !allowTestPattern)
        {
            status = "stream input missing: set MIMIR_STREAM_INPUT";
            return false;
        }

        var logRoot = Path.Combine(repositoryRoot, "artifacts", "runtime", "stream-proof");
        Directory.CreateDirectory(logRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repositoryRoot,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(TargetUrl);
        if (!string.IsNullOrWhiteSpace(inputPath))
        {
            startInfo.ArgumentList.Add("--no-test-pattern");
            startInfo.ArgumentList.Add("--input");
            startInfo.ArgumentList.Add(Path.GetFullPath(Path.IsPathRooted(inputPath) ? inputPath : Path.Combine(repositoryRoot, inputPath)));
        }

        process = Process.Start(startInfo);
        if (process == null)
        {
            status = "stream launch failed";
            return false;
        }

        _ = DrainAsync(process.StandardOutput, Path.Combine(logRoot, "mimir-app-stream.out.log"));
        _ = DrainAsync(process.StandardError, Path.Combine(logRoot, "mimir-app-stream.err.log"));
        startedAt = DateTimeOffset.Now;
        status = $"live pid={process.Id}";
        return true;
    }

    public void Stop()
    {
        Refresh();
        if (!IsStreaming)
        {
            return;
        }

        try
        {
            process!.Kill(entireProcessTree: true);
            status = "stream stopped";
        }
        catch (Exception ex)
        {
            status = $"stream stop failed: {ex.Message}";
        }
        finally
        {
            process?.Dispose();
            process = null;
            startedAt = null;
        }
    }

    public void Refresh()
    {
        if (process == null || !process.HasExited)
        {
            return;
        }

        var exitCode = process.ExitCode;
        process.Dispose();
        process = null;
        startedAt = null;
        status = exitCode == 0
            ? "stream ended"
            : $"stream failed exit={exitCode}";
    }

    public void Dispose()
    {
        Stop();
    }

    private static async Task DrainAsync(StreamReader reader, string path)
    {
        await using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "scripts")) &&
                File.Exists(Path.Combine(directory.FullName, "src", "Mimir.Runtime", "Mimir.Runtime.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
