using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameCult.Networking;
using MessagePack;

var port = ParseInt(args, "--port", 8891);
var bind = ParseString(args, "--bind", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_BIND") ?? "0.0.0.0");
var directory = ParseString(args, "--directory", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_DIRECTORY") ?? Directory.GetCurrentDirectory());
var idunnRudpHealth = ParseString(args, "--idunn-rudp-health", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_IDUNN_RUDP_HEALTH") ?? "");
var idunnDaemon = ParseString(args, "--idunn-daemon", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_IDUNN_DAEMON") ?? "nightwing-eve-browser-reference");
var idunnHealthContract = ParseString(args, "--idunn-health-contract", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_IDUNN_HEALTH_CONTRACT") ?? "nightwing.cultnet-rudp-browser-reference-health");

using var server = new EveBrowserReferenceServer(
    IPAddress.Parse(bind),
    port,
    Path.GetFullPath(directory),
    new IdunnRudpHealthOptions(idunnRudpHealth, idunnDaemon, idunnHealthContract));
await server.RunAsync().ConfigureAwait(false);

static int ParseInt(IReadOnlyList<string> args, string name, int fallback)
{
    for (var index = 0; index < args.Count - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
    }

    return fallback;
}

static string ParseString(IReadOnlyList<string> args, string name, string fallback)
{
    for (var index = 0; index < args.Count - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return args[index + 1];
        }
    }

    return fallback;
}

internal sealed class EveBrowserReferenceServer(IPAddress bindAddress, int port, string documentRoot, IdunnRudpHealthOptions idunnRudpHealth) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TcpListener listener = new(bindAddress, port);
    private readonly CancellationTokenSource stopping = new();
    private readonly object idunnRudpPublishLock = new();
    private string idunnRudpPublishStatus = string.IsNullOrWhiteSpace(idunnRudpHealth.Endpoint) ? "disabled" : "idle";
    private string idunnRudpPublishError = "";
    private string idunnRudpPublishObservedAt = "";
    private bool idunnRudpPublishInFlight;
    private DateTimeOffset lastHealthPublishAttemptAt = DateTimeOffset.MinValue;

    public async Task RunAsync()
    {
        if (!Directory.Exists(documentRoot))
        {
            throw new DirectoryNotFoundException($"Eve browser reference document root does not exist: {documentRoot}");
        }

        listener.Start();
        Console.WriteLine($"Mimir Eve browser reference serving {documentRoot} on http://{bindAddress}:{port}/");
        Console.WriteLine(string.IsNullOrWhiteSpace(idunnRudpHealth.Endpoint)
            ? "Mimir Eve browser reference Idunn RUDP health publisher disabled."
            : $"Mimir Eve browser reference Idunn RUDP health publisher targeting {idunnRudpHealth.Endpoint} as {idunnRudpHealth.DaemonId}.");
        _ = Task.Run(PublishHealthLoopAsync);
        while (!stopping.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task PublishHealthLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stopping.IsCancellationRequested && await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
        {
            QueueIdunnRudpHealthIfDue(DateTimeOffset.UtcNow);
        }
    }

    private async Task HandleAsync(TcpClient tcpClient)
    {
        await using var stream = tcpClient.GetStream();
        var request = await ReadHttpRequestAsync(stream).ConfigureAwait(false);
        if (request.Path == "/health")
        {
            var health = JsonSerializer.Serialize(new
            {
                ok = true,
                documentRoot,
                port,
                transport = "static-http-lowering",
                idunnRudpHealth = GetIdunnRudpHealthStatus(),
            }, JsonOptions);
            await WriteHttpResponseAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(health)).ConfigureAwait(false);
            return;
        }

        var filePath = ResolveStaticFilePath(request.Path);
        if (filePath == null)
        {
            await WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(filePath, stopping.Token).ConfigureAwait(false);
        await WriteHttpResponseAsync(stream, "200 OK", ContentType(filePath), bytes).ConfigureAwait(false);
    }

    private IdunnRudpHealthStatus GetIdunnRudpHealthStatus()
    {
        lock (idunnRudpPublishLock)
        {
            return new IdunnRudpHealthStatus(
                idunnRudpHealth.Endpoint,
                idunnRudpHealth.DaemonId,
                idunnRudpHealth.HealthContract,
                idunnRudpPublishStatus,
                idunnRudpPublishError,
                idunnRudpPublishInFlight,
                idunnRudpPublishObservedAt);
        }
    }

    private void QueueIdunnRudpHealthIfDue(DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(idunnRudpHealth.Endpoint))
        {
            idunnRudpPublishStatus = "disabled";
            return;
        }

        if (lastHealthPublishAttemptAt != DateTimeOffset.MinValue &&
            observedAt - lastHealthPublishAttemptAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        var observedAtText = observedAt.ToString("O", CultureInfo.InvariantCulture);
        var health = new IdunnDaemonHealthRecord
        {
            DaemonId = idunnRudpHealth.DaemonId,
            State = "healthy",
            Detail = $"Nightwing Eve browser reference serving static lowering; root={documentRoot}; port={port}",
            ObservedAt = observedAtText,
            HealthContract = idunnRudpHealth.HealthContract,
            PublicationSource = "daemon-published",
            Transport = "cultnet.transport.rudp.v0",
        };

        lock (idunnRudpPublishLock)
        {
            if (idunnRudpPublishInFlight)
            {
                return;
            }

            idunnRudpPublishInFlight = true;
            lastHealthPublishAttemptAt = observedAt;
            idunnRudpPublishObservedAt = observedAtText;
            if (!string.Equals(idunnRudpPublishStatus, "published", StringComparison.Ordinal))
            {
                idunnRudpPublishStatus = "publishing";
                idunnRudpPublishError = "";
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                IdunnRudpHealthPublisher.Publish(idunnRudpHealth.Endpoint, health);
                lock (idunnRudpPublishLock)
                {
                    idunnRudpPublishStatus = "published";
                    idunnRudpPublishError = "";
                    idunnRudpPublishInFlight = false;
                }
            }
            catch (Exception error)
            {
                lock (idunnRudpPublishLock)
                {
                    if (!string.Equals(idunnRudpPublishStatus, "published", StringComparison.Ordinal))
                    {
                        idunnRudpPublishStatus = "publish-error";
                    }

                    idunnRudpPublishError = error.Message;
                    idunnRudpPublishInFlight = false;
                }
            }
        });
    }

    private string? ResolveStaticFilePath(string path)
    {
        var requestPath = path.Split('?', 2)[0];
        if (requestPath == "/")
        {
            requestPath = "/index.html";
        }

        var relativePath = Uri.UnescapeDataString(requestPath.TrimStart('/'))
            .Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(documentRoot, relativePath));
        if (!candidate.StartsWith(documentRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(candidate, documentRoot, StringComparison.Ordinal))
        {
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    private static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };

    private static async Task<HttpRequest> ReadHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var length = await stream.ReadAsync(buffer).ConfigureAwait(false);
        if (length == 0)
        {
            return new HttpRequest("/");
        }

        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var firstLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        var firstLine = firstLineEnd >= 0 ? text[..firstLineEnd] : text;
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new HttpRequest(parts.Length >= 2 ? parts[1] : "/");
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, string status, string contentType, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
        stopping.Dispose();
    }
}

internal sealed record HttpRequest(string Path);

internal sealed record IdunnRudpHealthOptions(string Endpoint, string DaemonId, string HealthContract);

internal sealed record IdunnRudpHealthStatus(
    string Endpoint,
    string Daemon,
    string Contract,
    string Status,
    string Error,
    bool InFlight,
    string ObservedAtUtc);

[MessagePackObject(AllowPrivate = true)]
internal sealed class IdunnDaemonHealthRecord
{
    [Key(0)] public string DaemonId { get; set; } = "nightwing-eve-browser-reference";
    [Key(1)] public string State { get; set; } = "active";
    [Key(2)] public string Detail { get; set; } = "";
    [Key(3)] public string ObservedAt { get; set; } = "";
    [Key(4)] public string HealthContract { get; set; } = "nightwing.cultnet-rudp-browser-reference-health";
    [Key(5)] public string PublicationSource { get; set; } = "daemon-published";
    [Key(6)] public string Transport { get; set; } = "cultnet.transport.rudp.v0";
}

internal static class IdunnRudpHealthPublisher
{
    private const uint ConnectionId = 0x1d0d0001;

    public static void Publish(string endpoint, IdunnDaemonHealthRecord health)
    {
        var remote = ParseEndpoint(endpoint);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.Blocking = false;
        socket.ReceiveTimeout = 100;
        socket.SendTimeout = 1000;
        using var transport = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
        {
            RuntimeId = "mimir-eve-browser-reference",
            Socket = socket,
            Mode = CultNetRudpSocketMode.Client,
            RemoteEndPoint = remote,
            ConnectionId = ConnectionId,
            InitialSequence = 1,
            ResendDelayMs = 100,
        });

        if (!transport.ConnectAndWait(Array.Empty<byte>(), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20)))
        {
            throw new TimeoutException($"timed out connecting Mimir Eve browser reference RUDP health publisher to {endpoint}");
        }

        var observedAt = string.IsNullOrWhiteSpace(health.ObservedAt)
            ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            : health.ObservedAt;
        health.ObservedAt = observedAt;
        var message = new CultNetDocumentPutRawMessage
        {
            MessageId = $"mimir-eve-browser-reference-health:{health.DaemonId}:{observedAt.Replace(':', '-')}",
            Document = new CultNetRawDocumentRecord
            {
                SchemaId = "idunn.daemon_health",
                RecordKey = health.DaemonId,
                StoredAt = observedAt,
                PayloadEncoding = "messagepack",
                Payload = MessagePackSerializer.Serialize(health, CultNetSchemaMessageSerialization.Options),
                SourceRuntimeId = "mimir-eve-browser-reference",
                SourceRole = "daemon-health-publisher",
                Tags = ["cultnet.transport.rudp.v0"],
            },
        };
        transport.SendSchema(CultNetSchemaMessageSerialization.Serialize(message));
    }

    private static IPEndPoint ParseEndpoint(string value)
    {
        var trimmed = value.Trim();
        var separator = trimmed.LastIndexOf(':');
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            throw new ArgumentException($"Idunn RUDP endpoint must be host:port, got '{value}'.", nameof(value));
        }

        var host = trimmed[..separator];
        var portText = trimmed[(separator + 1)..];
        if (!int.TryParse(portText, CultureInfo.InvariantCulture, out var port))
        {
            throw new ArgumentException($"Idunn RUDP endpoint port must be numeric, got '{value}'.", nameof(value));
        }

        var addresses = Dns.GetHostAddresses(host);
        var address = addresses.FirstOrDefault(static candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new ArgumentException($"Idunn RUDP endpoint host did not resolve to IPv4, got '{value}'.", nameof(value));
        return new IPEndPoint(address, port);
    }
}
