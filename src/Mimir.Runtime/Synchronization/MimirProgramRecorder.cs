using System.Diagnostics;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirProgramRecorder : IDisposable
{
    private const int DefaultDurationSeconds = 15;
    private readonly string repositoryRoot;
    private Process? process;
    private string status = "idle";
    private DateTimeOffset? startedAt;

    public MimirProgramRecorder(string? repositoryRoot = null)
    {
        this.repositoryRoot = string.IsNullOrWhiteSpace(repositoryRoot)
            ? ResolveRepositoryRoot()
            : Path.GetFullPath(repositoryRoot);
        LatestOutputPath = Path.Combine(this.repositoryRoot, "artifacts", "runtime", "mimir-app-recordings", "mimir-record-latest.mp4");
    }

    public bool IsRecording => process is { HasExited: false };

    public string LatestOutputPath { get; private set; }

    public string Describe()
    {
        Refresh();
        if (IsRecording)
        {
            var elapsed = startedAt == null
                ? TimeSpan.Zero
                : DateTimeOffset.Now - startedAt.Value;
            return $"recording {elapsed.TotalSeconds:0}s pid={process!.Id}";
        }

        return status;
    }

    public bool Start(int? durationSeconds = null, string? outputPath = null)
    {
        Refresh();
        if (IsRecording)
        {
            return false;
        }

        var scriptPath = Path.Combine(repositoryRoot, "scripts", "record-kiyo-dual-mic-composite.ps1");
        if (!File.Exists(scriptPath))
        {
            status = "record script missing";
            return false;
        }

        var duration = Math.Max(1, durationSeconds ?? ReadDurationSeconds());
        LatestOutputPath = Path.GetFullPath(string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(repositoryRoot, "artifacts", "runtime", "mimir-app-recordings", "mimir-record-latest.mp4")
            : Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(repositoryRoot, outputPath));
        var logRoot = Path.Combine(repositoryRoot, "artifacts", "runtime", "mimir-app-recordings");
        Directory.CreateDirectory(logRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(LatestOutputPath) ?? logRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repositoryRoot,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-DurationSeconds");
        startInfo.ArgumentList.Add(duration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-OutputPath");
        startInfo.ArgumentList.Add(LatestOutputPath);
        startInfo.ArgumentList.Add("-LogRoot");
        startInfo.ArgumentList.Add(logRoot);

        var stdoutPath = Path.Combine(logRoot, "mimir-app-record.out.log");
        var stderrPath = Path.Combine(logRoot, "mimir-app-record.err.log");
        process = Process.Start(startInfo);
        if (process == null)
        {
            status = "record launch failed";
            return false;
        }

        _ = DrainAsync(process.StandardOutput, stdoutPath);
        _ = DrainAsync(process.StandardError, stderrPath);
        startedAt = DateTimeOffset.Now;
        status = $"recording pid={process.Id}";
        return true;
    }

    public void Stop()
    {
        Refresh();
        if (!IsRecording)
        {
            return;
        }

        try
        {
            process!.Kill(entireProcessTree: true);
            status = "recording stopped";
        }
        catch (Exception ex)
        {
            status = $"stop failed: {ex.Message}";
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
            ? $"ready {LatestOutputPath}"
            : $"record failed exit={exitCode}";
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

    private static int ReadDurationSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("MIMIR_RECORD_SECONDS");
        return int.TryParse(raw, out var parsed)
            ? parsed
            : DefaultDurationSeconds;
    }

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
