using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var options = VerseRecorderOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);
var runId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
var runDirectory = Path.Combine(options.OutputDirectory, runId);
Directory.CreateDirectory(runDirectory);
var jsonlPath = Path.Combine(runDirectory, "observations.jsonl");
var sessionPath = Path.Combine(runDirectory, "session.json");

await File.WriteAllTextAsync(
    sessionPath,
    JsonSerializer.Serialize(
        new
        {
            kind = "mimir.verse_recorder_session.v1",
            runId,
            options.Url,
            startedAt = DateTimeOffset.Now.ToString("O"),
            options.Seconds,
            observations = jsonlPath,
        },
        new JsonSerializerOptions { WriteIndented = true }));

Console.Error.WriteLine($"Mimir Verse recorder subscribing to {options.Url}");
Console.Error.WriteLine($"Writing {jsonlPath}");

using var stopping = new CancellationTokenSource();
if (options.Seconds > 0)
{
    stopping.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
}

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

var count = 0L;
var lastMeter = DateTimeOffset.UtcNow;
await using var output = new StreamWriter(new FileStream(jsonlPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);

while (!stopping.IsCancellationRequested)
{
    using var socket = new ClientWebSocket();
    try
    {
        await socket.ConnectAsync(options.Url, stopping.Token);
        while (socket.State == WebSocketState.Open && !stopping.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(socket, stopping.Token);
            if (text == null)
            {
                break;
            }

            await output.WriteLineAsync(text);
            count++;
            if (DateTimeOffset.UtcNow - lastMeter >= TimeSpan.FromSeconds(options.MeterSeconds))
            {
                await output.FlushAsync();
                Console.Error.WriteLine($"verse-recorder observations={count} path={jsonlPath}");
                lastMeter = DateTimeOffset.UtcNow;
            }
        }
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"verse-recorder reconnect after {ex.GetType().Name}: {ex.Message}");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.ReconnectSeconds), stopping.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

await output.FlushAsync();
Console.Error.WriteLine($"verse-recorder complete observations={count} path={jsonlPath}");

static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken stopping)
{
    var buffer = new byte[64 * 1024];
    using var memory = new MemoryStream();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, stopping);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        memory.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
        {
            return Encoding.UTF8.GetString(memory.ToArray());
        }
    }
}

internal sealed record VerseRecorderOptions(
    Uri Url,
    string OutputDirectory,
    double Seconds,
    double ReconnectSeconds,
    double MeterSeconds)
{
    public static VerseRecorderOptions Parse(string[] args)
    {
        var defaultOutput = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "Mimir",
            "VerseCaptures");
        return new VerseRecorderOptions(
            new Uri(ParseString(args, "--url", "ws://127.0.0.1:8796/eve/periwinkle/subscribe")),
            ParseString(args, "--out-dir", defaultOutput),
            ParseDouble(args, "--seconds", 0.0),
            ParseDouble(args, "--reconnect-seconds", 2.0),
            ParseDouble(args, "--meter-seconds", 5.0));
    }

    private static string ParseString(IReadOnlyList<string> args, string name, string fallback)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return fallback;
    }

    private static double ParseDouble(IReadOnlyList<string> args, string name, double fallback)
    {
        return double.TryParse(ParseString(args, name, ""), out var value)
            ? value
            : fallback;
    }
}
