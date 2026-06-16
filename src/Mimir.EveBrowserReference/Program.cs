using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Networking;
using MessagePack;

var port = ParseInt(args, "--port", 8891);
var bind = ParseString(args, "--bind", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_BIND") ?? "0.0.0.0");
var directory = ParseString(args, "--directory", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_DIRECTORY") ?? Directory.GetCurrentDirectory());
var cultCacheWitnessPath = ParseString(
    args,
    "--cultcache-witness",
    Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_CULTCACHE_WITNESS") ?? DefaultPaths.CultCacheWitnessPath);
var idunnRudpHealth = ParseString(args, "--idunn-rudp-health", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_IDUNN_RUDP_HEALTH") ?? "");
var idunnDaemon = ParseString(args, "--idunn-daemon", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_IDUNN_DAEMON") ?? "nightwing-eve-browser-reference");
var idunnHealthContract = ParseString(args, "--idunn-health-contract", Environment.GetEnvironmentVariable("MIMIR_EVE_BROWSER_REFERENCE_IDUNN_HEALTH_CONTRACT") ?? "nightwing.cultnet-rudp-browser-reference-health");

using var verseRuntime = EveBrowserReferenceVerseRuntime.Create(cultCacheWitnessPath);
using var server = new EveBrowserReferenceServer(
    IPAddress.Parse(bind),
    port,
    Path.GetFullPath(directory),
    verseRuntime,
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

internal static class DefaultPaths
{
    public static string CultCacheWitnessPath =>
        OperatingSystem.IsWindows()
            ? @"E:\Projects\Mimir\state\eve-browser-reference.service.cc"
            : "/var/lib/gamecult/eve-browser-reference/cultcache/eve-browser-reference.service.cc";
}

internal sealed class EveBrowserReferenceServer(
    IPAddress bindAddress,
    int port,
    string documentRoot,
    EveBrowserReferenceVerseRuntime verseRuntime,
    IdunnRudpHealthOptions idunnRudpHealth) : IDisposable
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
    private DateTimeOffset lastWitnessPublishAttemptAt = DateTimeOffset.MinValue;

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
        Console.WriteLine($"Mimir Eve browser reference witness store publishing to {verseRuntime.WitnessPath}");
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
            var observedAt = DateTimeOffset.UtcNow;
            PublishWitnessIfDue(observedAt);
            QueueIdunnRudpHealthIfDue(observedAt);
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
                cultCacheWitness = verseRuntime.GetWitnessStatus(),
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

    private void PublishWitnessIfDue(DateTimeOffset observedAt)
    {
        if (lastWitnessPublishAttemptAt != DateTimeOffset.MinValue &&
            observedAt - lastWitnessPublishAttemptAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        lastWitnessPublishAttemptAt = observedAt;
        verseRuntime.Publish(
            CreateDaemonHealthRecord(observedAt),
            documentRoot,
            port,
            observedAt);
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

        var health = CreateDaemonHealthRecord(observedAt);
        var observedAtText = health.ObservedAt;

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

    private IdunnDaemonHealthRecord CreateDaemonHealthRecord(DateTimeOffset observedAt)
    {
        var observedAtText = observedAt.ToString("O", CultureInfo.InvariantCulture);
        return new IdunnDaemonHealthRecord
        {
            DaemonId = idunnRudpHealth.DaemonId,
            State = "healthy",
            Detail = $"Nightwing Eve browser reference serving static lowering; root={documentRoot}; port={port}; witness={verseRuntime.WitnessPath}",
            ObservedAt = observedAtText,
            HealthContract = idunnRudpHealth.HealthContract,
            PublicationSource = "daemon-published",
            Transport = "cultnet.transport.rudp.v0",
        };
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

internal sealed record CultCacheWitnessStatus(
    string Path,
    string Status,
    string Error,
    string ObservedAtUtc,
    IReadOnlyList<string> PublishedSchemas);

internal sealed record IdunnRudpHealthStatus(
    string Endpoint,
    string Daemon,
    string Contract,
    string Status,
    string Error,
    bool InFlight,
    string ObservedAtUtc);

[CultDocument("idunn.daemon_health", "idunn.daemon_health.v1")]
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

internal sealed class EveBrowserReferenceVerseRuntime : IDisposable
{
    private static readonly CultRecordKey ManifestKey = new("nightwing-eve-browser-reference");
    private static readonly CultRecordKey StaticSurfaceKey = new("nightwing-eve-browser-reference");
    private static readonly CultRecordKey CommandBoundaryKey = new("nightwing-eve-browser-reference");
    private static readonly CultRecordKey TransportProfileKey = new("nightwing-eve-browser-reference");
    private static readonly string[] PublishedSchemas =
    [
        "mimir.eve_browser_reference_manifest.v1",
        "mimir.eve_browser_reference_static_surface.v1",
        "mimir.eve_browser_reference_command_boundary.v1",
        "mimir.eve_browser_reference_transport_profile.v1",
        "idunn.daemon_health.v1",
    ];

    private readonly CultDocumentRegistry registry = new();
    private readonly string witnessPath;
    private readonly object publishLock = new();
    private string witnessStatus = "starting";
    private string witnessError = string.Empty;
    private string witnessObservedAtUtc = string.Empty;

    private EveBrowserReferenceVerseRuntime(string witnessPath)
    {
        this.witnessPath = Path.GetFullPath(witnessPath);
    }

    public string WitnessPath => witnessPath;

    public static EveBrowserReferenceVerseRuntime Create(string witnessPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(witnessPath)) ?? ".");
        return new EveBrowserReferenceVerseRuntime(witnessPath);
    }

    public CultCacheWitnessStatus GetWitnessStatus()
    {
        lock (publishLock)
        {
            return new CultCacheWitnessStatus(
                witnessPath,
                witnessStatus,
                witnessError,
                witnessObservedAtUtc,
                PublishedSchemas);
        }
    }

    public void Publish(
        IdunnDaemonHealthRecord healthRecord,
        string documentRoot,
        int port,
        DateTimeOffset observedAt)
    {
        var observedAtUtc = observedAt.ToString("O", CultureInfo.InvariantCulture);

        try
        {
            lock (publishLock)
            {
                var records = new List<CultPersistedRecord>();
                var schemas = new Dictionary<string, CultSchemaCatalogEntry>(StringComparer.Ordinal);
                var assets = EnumerateRelativeFiles(documentRoot);
                var fixtures = assets
                    .Where(path => path.StartsWith("fixtures/", StringComparison.Ordinal))
                    .ToArray();

                AddRecord(records, schemas, ToManifestDocument(documentRoot, port, assets), ManifestKey, observedAtUtc);
                AddRecord(records, schemas, ToStaticSurfaceDocument(documentRoot, assets, fixtures, observedAtUtc), StaticSurfaceKey, observedAtUtc);
                AddRecord(records, schemas, ToCommandBoundaryDocument(observedAtUtc), CommandBoundaryKey, observedAtUtc);
                AddRecord(records, schemas, ToTransportProfileDocument(observedAtUtc), TransportProfileKey, observedAtUtc);
                AddRecord(records, schemas, healthRecord, new CultRecordKey(healthRecord.DaemonId), observedAtUtc);

                WriteSnapshot(new CultPersistedStoreSnapshot
                {
                    FormatVersion = "cultcache.store.v1",
                    SchemaCatalog = schemas.Values.OrderBy(entry => entry.SchemaName, StringComparer.Ordinal).ToArray(),
                    Records = records.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray(),
                });
                witnessStatus = "published";
                witnessError = string.Empty;
                witnessObservedAtUtc = observedAtUtc;
            }
        }
        catch (Exception error)
        {
            lock (publishLock)
            {
                witnessStatus = "publish-error";
                witnessError = error.Message;
                witnessObservedAtUtc = observedAtUtc;
            }
        }
    }

    public void Dispose()
    {
    }

    private void AddRecord<T>(
        List<CultPersistedRecord> records,
        Dictionary<string, CultSchemaCatalogEntry> schemas,
        T document,
        CultRecordKey key,
        string observedAtUtc) where T : class
    {
        var descriptor = registry.GetRequired<T>();
        var payload = descriptor.GeneratedPayloadSerializer != null
            ? descriptor.GeneratedPayloadSerializer(document)
            : MessagePackSerializer.Serialize(typeof(T), document, CultNetSchemaMessageSerialization.Options);
        schemas[descriptor.SchemaId] = descriptor.ToCatalogEntry();
        records.Add(new CultPersistedRecord
        {
            Key = key.Value,
            SchemaId = descriptor.SchemaId,
            StoredAt = observedAtUtc,
            Payload = payload,
        });
    }

    private void WriteSnapshot(CultPersistedStoreSnapshot snapshot)
    {
        var payload = CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot);
        var directory = Path.GetDirectoryName(witnessPath) ?? ".";
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(witnessPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(true);
            }

            if (File.Exists(witnessPath))
            {
                File.Replace(tempPath, witnessPath, null);
            }
            else
            {
                File.Move(tempPath, witnessPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static EveBrowserReferenceManifestRecord ToManifestDocument(
        string documentRoot,
        int port,
        IReadOnlyList<string> assets) =>
        new()
        {
            DaemonId = "nightwing-eve-browser-reference",
            SiteTitle = "Nightwing Eve Browser Reference",
            DocumentRoot = documentRoot,
            EntryPoint = "index.html",
            Port = port,
            Assets = assets.ToArray(),
            SurfaceTransport = "static-http-lowering",
        };

    private static EveBrowserReferenceStaticSurfaceRecord ToStaticSurfaceDocument(
        string documentRoot,
        IReadOnlyList<string> assets,
        IReadOnlyList<string> fixtures,
        string observedAtUtc) =>
        new()
        {
            DaemonId = "nightwing-eve-browser-reference",
            UpdatedAtUtc = observedAtUtc,
            DocumentRoot = documentRoot,
            EntryPoint = "index.html",
            Assets = assets.ToArray(),
            FixtureDocuments = fixtures.ToArray(),
            CurrentCutLine = "Static browser lowering assets are published into the daemon-owned witness store so Nightwing and Odin can reason about the lowering body without treating the HTTP service probe as lifecycle truth.",
        };

    private static EveBrowserReferenceCommandBoundaryRecord ToCommandBoundaryDocument(string observedAtUtc) =>
        new()
        {
            DaemonId = "nightwing-eve-browser-reference",
            UpdatedAtUtc = observedAtUtc,
            Mode = "static-read-only-lowering",
            WritesAccepted = false,
            OperatorInputAuthority = "served browser assets only; no write path is accepted by this daemon",
            LifecycleAuthority = "idunn.local-command.restart + compatibility.systemd.nightwing-eve-browser-reference.service",
            AcceptedCommands = [],
            RejectedCommands =
            [
                "surface-mutation",
                "provider-catalog-ownership",
                "daemon-health-override",
                "service-restart",
            ],
        };

    private EveBrowserReferenceTransportProfileRecord ToTransportProfileDocument(string observedAtUtc) =>
        new()
        {
            DaemonId = "nightwing-eve-browser-reference",
            UpdatedAtUtc = observedAtUtc,
            CurrentState = "partial-rudp-health-and-provider-store-live",
            InputTransport = "compatibility.http-static-lowering",
            OutputTransport = "daemon-owned-cultcache-boundary-store + daemon-published-rudp-health",
            HealthContract = "nightwing.cultnet-rudp-browser-reference-health",
            IdunnRudpHealth = "idunn.health-published-separately",
            WitnessPath = witnessPath,
            CurrentCutLine = "The Nightwing Eve browser reference runtime now owns its manifest, static surface witness, command boundary, and transport profile in a daemon-owned CultCache store; systemd and HTTP remain compatibility witnesses while projections learn to consume the typed store directly.",
        };

    private static string[] EnumerateRelativeFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }
}

[CultDocument("mimir.eve_browser_reference_manifest", "mimir.eve_browser_reference_manifest.v1")]
[MessagePackObject(AllowPrivate = true)]
internal sealed class EveBrowserReferenceManifestRecord
{
    [Key(0)]
    [CultName]
    public string DaemonId { get; set; } = string.Empty;

    [Key(1)] public string SiteTitle { get; set; } = string.Empty;
    [Key(2)] public string DocumentRoot { get; set; } = string.Empty;
    [Key(3)] public string EntryPoint { get; set; } = string.Empty;
    [Key(4)] public int Port { get; set; }
    [Key(5)] public string[] Assets { get; set; } = [];
    [Key(6)] public string SurfaceTransport { get; set; } = string.Empty;
}

[CultDocument("mimir.eve_browser_reference_static_surface", "mimir.eve_browser_reference_static_surface.v1")]
[MessagePackObject(AllowPrivate = true)]
internal sealed class EveBrowserReferenceStaticSurfaceRecord
{
    [Key(0)]
    [CultName]
    public string DaemonId { get; set; } = string.Empty;

    [Key(1)] public string UpdatedAtUtc { get; set; } = string.Empty;
    [Key(2)] public string DocumentRoot { get; set; } = string.Empty;
    [Key(3)] public string EntryPoint { get; set; } = string.Empty;
    [Key(4)] public string[] Assets { get; set; } = [];
    [Key(5)] public string[] FixtureDocuments { get; set; } = [];
    [Key(6)] public string CurrentCutLine { get; set; } = string.Empty;
}

[CultDocument("mimir.eve_browser_reference_command_boundary", "mimir.eve_browser_reference_command_boundary.v1")]
[MessagePackObject(AllowPrivate = true)]
internal sealed class EveBrowserReferenceCommandBoundaryRecord
{
    [Key(0)]
    [CultName]
    public string DaemonId { get; set; } = string.Empty;

    [Key(1)] public string UpdatedAtUtc { get; set; } = string.Empty;
    [Key(2)] public string Mode { get; set; } = string.Empty;
    [Key(3)] public bool WritesAccepted { get; set; }
    [Key(4)] public string OperatorInputAuthority { get; set; } = string.Empty;
    [Key(5)] public string LifecycleAuthority { get; set; } = string.Empty;
    [Key(6)] public string[] AcceptedCommands { get; set; } = [];
    [Key(7)] public string[] RejectedCommands { get; set; } = [];
}

[CultDocument("mimir.eve_browser_reference_transport_profile", "mimir.eve_browser_reference_transport_profile.v1")]
[MessagePackObject(AllowPrivate = true)]
internal sealed class EveBrowserReferenceTransportProfileRecord
{
    [Key(0)]
    [CultName]
    public string DaemonId { get; set; } = string.Empty;

    [Key(1)] public string UpdatedAtUtc { get; set; } = string.Empty;
    [Key(2)] public string CurrentState { get; set; } = string.Empty;
    [Key(3)] public string InputTransport { get; set; } = string.Empty;
    [Key(4)] public string OutputTransport { get; set; } = string.Empty;
    [Key(5)] public string HealthContract { get; set; } = string.Empty;
    [Key(6)] public string IdunnRudpHealth { get; set; } = string.Empty;
    [Key(7)] public string WitnessPath { get; set; } = string.Empty;
    [Key(8)] public string CurrentCutLine { get; set; } = string.Empty;
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
