using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using Mimir.Runtime.Synchronization;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)) return await RunSelfTestAsync();

var bind = ParseEndpoint(Parse(args, "--bind", "0.0.0.0:11111"));
var streamId = Parse(args, "--stream", "iphone-xs-max-face");
var cachePath = Path.GetFullPath(Parse(args, "--ledger", Path.Combine("state", "mimir-face-observations.cc")));
var slots = Math.Clamp(int.Parse(Parse(args, "--slots", "3600")), 60, 216000);
Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
using var cache = await CultCacheMessagePack.OpenAsync(cachePath, new CultCacheOpenOptions { PullOnOpen = File.Exists(cachePath) });
using var udp = new UdpClient(bind);
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
var ledger = new MimirFaceObservationLedger(streamId);
long accepted = 0, rejected = 0;
Console.Error.WriteLine($"Mimir face receiver owns udp://{bind} -> {cachePath}; stream={streamId}; schema=mimir.face_tracking_observation.v1");

try
{
    while (!stopping.IsCancellationRequested)
    {
        var datagram = await udp.ReceiveAsync(stopping.Token);
        var arrivalNs = checked(Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks * 100L);
        if (!MimirLiveLinkFaceDecoder.TryDecode(datagram.Buffer, out var packet, out var error) ||
            !ledger.TryAdmit(packet!, arrivalNs, out var observation, out error))
        {
            rejected++;
            if (rejected <= 3 || rejected % 120 == 0) Console.Error.WriteLine($"rejected={rejected}: {error}");
            continue;
        }
        var admitted = observation!;
        await cache.UpsertAsync(admitted, new CultRecordHandle<MimirFaceTrackingObservation>(
            new CultRecordKey($"mimir-face:{streamId}:slot:{admitted.Sequence % (ulong)slots:D6}")));
        accepted++;
        if (accepted % 60 == 0) { await cache.FlushAsync(soft: true); Console.Error.WriteLine($"accepted={accepted} rejected={rejected} sourceFrame={admitted.SourceFrame}"); }
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
finally { await cache.FlushAsync(); }
return 0;

static string Parse(IReadOnlyList<string> args, string name, string fallback)
{
    for (var i = 0; i < args.Count - 1; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return fallback;
}
static IPEndPoint ParseEndpoint(string value)
{
    var split = value.LastIndexOf(':');
    if (split <= 0 || !int.TryParse(value[(split + 1)..], out var port)) throw new ArgumentException($"Invalid endpoint: {value}");
    var host = value[..split];
    return new IPEndPoint(host is "0.0.0.0" or "*" ? IPAddress.Any : IPAddress.Parse(host), port);
}

static async Task<int> RunSelfTestAsync()
{
    using var memory = new MemoryStream();
    using var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, leaveOpen: true);
    writer.Write(MimirLiveLinkFaceDecoder.SupportedVersion);
    WriteString(writer, "iphone11,6");
    WriteString(writer, "MimirFace");
    WriteUInt32(writer, 42); WriteUInt32(writer, 0); WriteUInt32(writer, 60); WriteUInt32(writer, 1);
    writer.Write((byte)MimirLiveLinkFaceDecoder.ChannelCount);
    for (var index = 0; index < MimirLiveLinkFaceDecoder.ChannelCount; index++) WriteSingle(writer, index == 17 ? 0.75f : 0f);
    var bytes = memory.ToArray();
    if (!MimirLiveLinkFaceDecoder.TryDecode(bytes, out var packet, out var error)) { Console.Error.WriteLine(error); return 1; }
    var decodedPacket = packet!;
    var ledger = new MimirFaceObservationLedger("iphone-xs-max-face");
    if (!ledger.TryAdmit(decodedPacket, 123456, out var observation, out error) || observation is null ||
        observation.ChannelNames.Length != 61 || observation.ChannelValues[17] != 0.75f || observation.SourceFrame != 42)
    { Console.Error.WriteLine(error.Length == 0 ? "normalized observation mismatch" : error); return 1; }
    var encoded = MessagePack.MessagePackSerializer.Serialize(observation);
    var decoded = MessagePack.MessagePackSerializer.Deserialize<MimirFaceTrackingObservation>(encoded);
    if (decoded.ObservationId != observation.ObservationId) { Console.Error.WriteLine("typed observation MessagePack roundtrip mismatch"); return 1; }
    bytes[^1] = 0x7f;
    bytes[^2] = 0xff;
    bytes[^3] = 0xff;
    bytes[^4] = 0xff;
    if (MimirLiveLinkFaceDecoder.TryDecode(bytes, out _, out _)) { Console.Error.WriteLine("non-finite channel was admitted"); return 1; }
    var stalePacket = decodedPacket with { Frame = 41 };
    if (ledger.TryAdmit(stalePacket, 123457, out _, out _)) { Console.Error.WriteLine("stale frame was admitted"); return 1; }
    var restartedPacket = decodedPacket with { Frame = 1 };
    if (!ledger.TryAdmit(restartedPacket, 1_100_123_458, out var restarted, out _) || restarted?.SourceEpoch != 1) { Console.Error.WriteLine("source restart epoch was not admitted"); return 1; }
    var testLedgerPath = Path.Combine(Path.GetTempPath(), $"mimir-face-self-test-{Guid.NewGuid():N}.cc");
    try
    {
        using (var cache = await CultCacheMessagePack.OpenAsync(testLedgerPath, new CultCacheOpenOptions { PullOnOpen = false }))
        {
            await cache.UpsertAsync(restarted!, new CultRecordHandle<MimirFaceTrackingObservation>(new CultRecordKey("mimir-face:self-test:slot:000000")));
            await cache.FlushAsync();
        }
        if (!File.Exists(testLedgerPath) || new FileInfo(testLedgerPath).Length == 0) { Console.Error.WriteLine("CultCache ledger was not persisted"); return 1; }
    }
    finally { if (File.Exists(testLedgerPath)) File.Delete(testLedgerPath); }
    Console.WriteLine("Mimir face receiver self-test passed: Live Link Face v6 decode, admission, typed observation roundtrip, malformed/stale rejection, restart epoch, CultCache persistence.");
    return 0;

    static void WriteString(BinaryWriter target, string value) { var bytes = System.Text.Encoding.UTF8.GetBytes(value); WriteUInt32(target, checked((uint)bytes.Length)); target.Write(bytes); }
    static void WriteUInt32(BinaryWriter target, uint value) { Span<byte> bytes = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value); target.Write(bytes); }
    static void WriteSingle(BinaryWriter target, float value) => WriteUInt32(target, unchecked((uint)BitConverter.SingleToInt32Bits(value)));
}
