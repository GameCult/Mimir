using System.Diagnostics;
using System.Text;

namespace Mimir.Broadcast;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = MimirBroadcastOptions.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(MimirBroadcastOptions.HelpText);
            return 0;
        }

        var plan = MimirBroadcastPlan.FromOptions(options);
        if (options.PrintCommand || options.DryRun)
        {
            Console.WriteLine(plan.CommandLine);
        }

        if (options.Smoke)
        {
            return RunSmoke(plan);
        }

        if (options.DryRun || options.PrintCommand)
        {
            return 0;
        }

        return await RunFfmpegAsync(options, plan).ConfigureAwait(false);
    }

    private static int RunSmoke(MimirBroadcastPlan plan)
    {
        var command = plan.CommandLine;
        var ok = command.Contains("h264_nvenc", StringComparison.Ordinal) &&
            command.Contains("-f flv", StringComparison.Ordinal) &&
            command.Contains("rtmp://127.0.0.1:11935/live/mimir", StringComparison.Ordinal) &&
            command.Contains("-g 60", StringComparison.Ordinal) &&
            command.Contains("-sc_threshold 0", StringComparison.Ordinal);
        Console.WriteLine($"mimir-broadcast-smoke nvenc={command.Contains("h264_nvenc", StringComparison.Ordinal)} rtmp={command.Contains("rtmp://", StringComparison.Ordinal)} keyframeCadence={command.Contains("-g 60", StringComparison.Ordinal)}");
        return ok ? 0 : 1;
    }

    private static async Task<int> RunFfmpegAsync(MimirBroadcastOptions options, MimirBroadcastPlan plan)
    {
        var start = new ProcessStartInfo
        {
            FileName = options.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in plan.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process == null)
        {
            Console.Error.WriteLine("Could not start ffmpeg.");
            return 1;
        }

        var stdout = PumpAsync(process.StandardOutput, Console.Out);
        var stderr = PumpAsync(process.StandardError, Console.Error);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, TextWriter writer)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            writer.WriteLine(line);
        }
    }
}

internal sealed record MimirBroadcastPlan(IReadOnlyList<string> Arguments)
{
    public string CommandLine => $"ffmpeg {string.Join(' ', Arguments.Select(Quote))}";

    public static MimirBroadcastPlan FromOptions(MimirBroadcastOptions options)
    {
        var args = new List<string>();
        if (options.Realtime)
        {
            args.Add("-re");
        }

        if (options.TestPattern)
        {
            args.AddRange(["-f", "lavfi", "-i", $"testsrc2=size={options.Width}x{options.Height}:rate={options.FrameRate}"]);
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=997:sample_rate={options.AudioSampleRate}"]);
        }
        else if (!string.IsNullOrWhiteSpace(options.InputPath))
        {
            args.AddRange(["-i", options.InputPath]);
        }
        else
        {
            throw new InvalidOperationException("Use --test-pattern or provide --input.");
        }

        var audioMap = options.TestPattern ? "1:a:0" : "0:a:0?";

        args.AddRange([
            "-map", "0:v:0",
            "-map", audioMap,
            "-c:v", "h264_nvenc",
            "-preset", options.NvencPreset,
            "-tune", "ll",
            "-rc", "cbr",
            "-b:v", $"{options.VideoBitrateKbps}k",
            "-maxrate", $"{options.VideoBitrateKbps}k",
            "-bufsize", $"{options.VideoBitrateKbps * 2}k",
            "-g", options.GopFrames.ToStringInvariant(),
            "-keyint_min", options.GopFrames.ToStringInvariant(),
            "-sc_threshold", "0",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", $"{options.AudioBitrateKbps}k",
            "-ar", options.AudioSampleRate.ToStringInvariant(),
        ]);

        if (options.DurationSeconds > 0)
        {
            args.AddRange(["-t", options.DurationSeconds.ToStringInvariant()]);
        }

        args.AddRange([
            "-f", "flv",
            options.TargetUrl,
        ]);
        return new MimirBroadcastPlan(args);
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.Any(static c => char.IsWhiteSpace(c) || c == '"')
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}

internal sealed record MimirBroadcastOptions
{
    public string FfmpegPath { get; init; } = "ffmpeg";

    public string TargetUrl { get; init; } = "rtmp://127.0.0.1:11935/live/mimir";

    public string InputPath { get; init; } = "";

    public bool TestPattern { get; init; } = true;

    public bool Realtime { get; init; } = true;

    public bool PrintCommand { get; init; }

    public bool DryRun { get; init; }

    public bool Smoke { get; init; }

    public bool ShowHelp { get; init; }

    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    public int FrameRate { get; init; } = 30;

    public int GopFrames { get; init; } = 60;

    public int VideoBitrateKbps { get; init; } = 4500;

    public int AudioSampleRate { get; init; } = 48_000;

    public int AudioBitrateKbps { get; init; } = 160;

    public string NvencPreset { get; init; } = "p4";

    public int DurationSeconds { get; init; }

    public static string HelpText => """
        Mimir.Broadcast

        Push a Starfire-encoded Mimir program stream to the Yggdrasil RTMP/HLS edge.
        The default command is a synthetic NVENC smoke source:

          dotnet run --project src/Mimir.Broadcast -- --print-command

        Options:
          --target <url>          RTMP target. Default: rtmp://127.0.0.1:11935/live/mimir
          --ffmpeg <path>         FFmpeg executable. Default: ffmpeg
          --input <path>          Input media path instead of the synthetic test pattern.
          --no-test-pattern       Require --input.
          --width <px>            Synthetic width. Default: 1280
          --height <px>           Synthetic height. Default: 720
          --fps <n>               Synthetic frame rate. Default: 30
          --video-kbps <n>        CBR video bitrate. Default: 4500
          --audio-kbps <n>        AAC bitrate. Default: 160
          --gop <frames>          Keyframe cadence. Default: 60
          --preset <name>         NVENC preset. Default: p4
          --duration-seconds <n>  Stop after this many seconds. Default: run until stopped.
          --print-command         Print the generated FFmpeg command.
          --dry-run               Print command and exit.
          --smoke                 Validate the generated command shape and exit.
        """;

    public static MimirBroadcastOptions Parse(IReadOnlyList<string> args)
    {
        var options = new MimirBroadcastOptions();
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            options = arg switch
            {
                "--help" or "-h" => options with { ShowHelp = true },
                "--print-command" => options with { PrintCommand = true },
                "--dry-run" => options with { DryRun = true, PrintCommand = true },
                "--smoke" => options with { Smoke = true },
                "--no-test-pattern" => options with { TestPattern = false },
                "--target" => options with { TargetUrl = RequiredValue(args, ref index, arg) },
                "--ffmpeg" => options with { FfmpegPath = RequiredValue(args, ref index, arg) },
                "--input" => options with { InputPath = RequiredValue(args, ref index, arg), TestPattern = false },
                "--width" => options with { Width = ParsePositiveInt(args, ref index, arg) },
                "--height" => options with { Height = ParsePositiveInt(args, ref index, arg) },
                "--fps" => options with { FrameRate = ParsePositiveInt(args, ref index, arg) },
                "--video-kbps" => options with { VideoBitrateKbps = ParsePositiveInt(args, ref index, arg) },
                "--audio-kbps" => options with { AudioBitrateKbps = ParsePositiveInt(args, ref index, arg) },
                "--gop" => options with { GopFrames = ParsePositiveInt(args, ref index, arg) },
                "--preset" => options with { NvencPreset = RequiredValue(args, ref index, arg) },
                "--duration-seconds" => options with { DurationSeconds = ParsePositiveInt(args, ref index, arg) },
                _ => throw new ArgumentException($"Unknown argument `{arg}`."),
            };
        }

        return options;
    }

    private static string RequiredValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static int ParsePositiveInt(IReadOnlyList<string> args, ref int index, string option)
    {
        var text = RequiredValue(args, ref index, option);
        if (!int.TryParse(text, out var value) || value <= 0)
        {
            throw new ArgumentException($"{option} requires a positive integer.");
        }

        return value;
    }
}

internal static class InvariantFormatting
{
    public static string ToStringInvariant(this int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
