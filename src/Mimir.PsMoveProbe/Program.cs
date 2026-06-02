using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

const ushort SonyVid = 0x054C;
const ushort PsMovePid = 0x03D5;

var readReports = args.Any(arg => string.Equals(arg, "--read", StringComparison.OrdinalIgnoreCase));
var pairHost = TryGetOption(args, "--pair-host");
var rgb = TryGetOption(args, "--rgb");
var pulseMs = TryGetIntOption(args, "--pulse-ms", 0);
var pulseCount = TryGetIntOption(args, "--pulse-count", 1);
var intervalMs = TryGetIntOption(args, "--interval-ms", 500);
var eventLog = TryGetOption(args, "--event-log");
var pairedChirpTrain = args.Any(arg => string.Equals(arg, "--paired-chirp-train", StringComparison.OrdinalIgnoreCase));
var outputAudio = TryGetOption(args, "--output-audio");
var eventCount = TryGetIntOption(args, "--event-count", pulseCount);
var startDelayMs = TryGetIntOption(args, "--start-delay-ms", 150);
var chirpMs = TryGetIntOption(args, "--chirp-ms", Math.Max(1, pulseMs));
var sampleRate = TryGetIntOption(args, "--sample-rate", 48_000);
var startHz = TryGetDoubleOption(args, "--start-hz", 2400.0);
var endHz = TryGetDoubleOption(args, "--end-hz", 7200.0);
var gain = TryGetDoubleOption(args, "--gain", 0.12);
var renderDevice = TryGetOption(args, "--render-device");
var wasapiExe = TryGetOption(args, "--wasapi-exe");
var noAudioEmit = args.Any(arg => string.Equals(arg, "--no-audio-emit", StringComparison.OrdinalIgnoreCase));
var showAll = args.Any(arg => string.Equals(arg, "--all", StringComparison.OrdinalIgnoreCase));
if (showAll)
{
    foreach (var path in WindowsHid.EnumeratePaths())
    {
        Console.WriteLine(path);
    }

    return 0;
}

var devices = WindowsHid.Enumerate()
    .Where(device => device.VendorId == SonyVid && device.ProductId == PsMovePid)
    .OrderBy(device => device.Path, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine("Mimir PS Move USB probe");
Console.WriteLine($"target vid=0x{SonyVid:X4} pid=0x{PsMovePid:X4}");
Console.WriteLine($"collections={devices.Count}");
Console.WriteLine();

if (devices.Count == 0)
{
    Console.WriteLine("No PS Move HID collections are visible. Check the USB cable and Windows Device Manager.");
    return 1;
}

if (pairedChirpTrain)
{
    var move = devices.FirstOrDefault(static device =>
        device.Caps.OutputReportByteLength > 0 &&
        device.Path.IndexOf("&col01#", StringComparison.OrdinalIgnoreCase) >= 0);
    if (move == null)
    {
        Console.WriteLine("No PS Move col01 output collection is available for paired chirp train.");
        return 1;
    }

    var audioPath = string.IsNullOrWhiteSpace(outputAudio)
        ? Path.Combine("artifacts", "runtime", $"starfire-usb-move-chirp-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.f32")
        : outputAudio;
    if (string.IsNullOrWhiteSpace(rgb) || !WindowsHid.TryParseRgb(rgb, out var r, out var g, out var b))
    {
        Console.WriteLine("Paired chirp train requires --rgb #rrggbb or --rgb r,g,b.");
        return 1;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(audioPath)) ?? ".");
    if (!string.IsNullOrWhiteSpace(eventLog))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(eventLog)) ?? ".");
    }

    var events = PairedChirpTrain.RenderAudio(
        audioPath,
        Math.Max(1, eventCount),
        TimeSpan.FromMilliseconds(Math.Max(0, startDelayMs)),
        TimeSpan.FromMilliseconds(Math.Max(1, intervalMs)),
        TimeSpan.FromMilliseconds(Math.Max(1, chirpMs)),
        sampleRate,
        startHz,
        endHz,
        gain);
    PairedChirpTrain.AppendScheduleEvents(eventLog, events, audioPath, sampleRate, gain);
    var audioTask = noAudioEmit
        ? Task.FromResult("skipped: --no-audio-emit")
        : PairedChirpTrain.EmitAudioAsync(audioPath, sampleRate, gain, renderDevice, wasapiExe, eventLog);
    var ledTask = WindowsHid.TryWriteScheduledLedTrainAsync(
        move,
        events,
        r,
        g,
        b,
        TimeSpan.FromMilliseconds(Math.Max(1, pulseMs)),
        eventLog);
    var result = await ledTask.ConfigureAwait(false);
    var audioResult = await audioTask.ConfigureAwait(false);
    Console.WriteLine(
        $"pairedChirpTrain audio={audioPath} events={events.Count} sampleRate={sampleRate} startDelayMs={startDelayMs} chirpMs={chirpMs} intervalMs={intervalMs} led={result} audio={audioResult}");
    return result.StartsWith("ok", StringComparison.OrdinalIgnoreCase) &&
        (audioResult.StartsWith("ok", StringComparison.OrdinalIgnoreCase) || audioResult.StartsWith("skipped", StringComparison.OrdinalIgnoreCase))
        ? 0
        : 1;
}

foreach (var device in devices)
{
    PrintDevice(device);

    if (!string.IsNullOrWhiteSpace(pairHost))
    {
        var result = WindowsHid.TryPairHost(device, pairHost);
        Console.WriteLine($"  pairHost={result}");
    }

    if (readReports)
    {
        var result = await WindowsHid.TryReadInputReportAsync(device, TimeSpan.FromMilliseconds(350));
        Console.WriteLine($"  read={result}");
    }

    if (!string.IsNullOrWhiteSpace(rgb))
    {
        var result = await WindowsHid.TryWriteLedPulseTrainAsync(
            device,
            rgb,
            Math.Max(1, pulseCount),
            TimeSpan.FromMilliseconds(Math.Max(0, pulseMs)),
            TimeSpan.FromMilliseconds(Math.Max(1, intervalMs)),
            eventLog);
        Console.WriteLine($"  led={result}");
    }

    Console.WriteLine();
}

return 0;

static void PrintDevice(HidDeviceInfo device)
{
    Console.WriteLine(device.Path);
    Console.WriteLine($"  vid=0x{device.VendorId:X4} pid=0x{device.ProductId:X4}");
    Console.WriteLine($"  manufacturer={device.Manufacturer ?? "(unreported)"}");
    Console.WriteLine($"  product={device.Product ?? "(unreported)"}");
    Console.WriteLine($"  serial={device.SerialNumber ?? "(unreported)"}");
    Console.WriteLine($"  usagePage=0x{device.Caps.UsagePage:X4} usage=0x{device.Caps.Usage:X4}");
    Console.WriteLine($"  reports input={device.Caps.InputReportByteLength} output={device.Caps.OutputReportByteLength} feature={device.Caps.FeatureReportByteLength}");
    Console.WriteLine($"  buttons in/out/feature={device.Caps.NumberInputButtonCaps}/{device.Caps.NumberOutputButtonCaps}/{device.Caps.NumberFeatureButtonCaps}");
    Console.WriteLine($"  values in/out/feature={device.Caps.NumberInputValueCaps}/{device.Caps.NumberOutputValueCaps}/{device.Caps.NumberFeatureValueCaps}");
}

static string? TryGetOption(string[] values, string name)
{
    for (var index = 0; index < values.Length; index++)
    {
        if (string.Equals(values[index], name, StringComparison.OrdinalIgnoreCase) && index + 1 < values.Length)
        {
            return values[index + 1];
        }

        if (values[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            return values[index][(name.Length + 1)..];
        }
    }

    return null;
}

static int TryGetIntOption(string[] values, string name, int fallback) =>
    int.TryParse(TryGetOption(values, name), out var value) ? value : fallback;

static double TryGetDoubleOption(string[] values, string name, double fallback) =>
    double.TryParse(TryGetOption(values, name), out var value) ? value : fallback;

internal sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    string? Manufacturer,
    string? Product,
    string? SerialNumber,
    HidpCaps Caps);

internal sealed record PairedChirpEvent(
    string EventId,
    int Index,
    double OffsetSeconds,
    int AudioStartSample,
    int AudioEndSample,
    double StartHz,
    double EndHz);

internal static class PairedChirpTrain
{
    public static IReadOnlyList<PairedChirpEvent> RenderAudio(
        string path,
        int count,
        TimeSpan startDelay,
        TimeSpan interval,
        TimeSpan chirpDuration,
        int sampleRate,
        double startHz,
        double endHz,
        double gain)
    {
        var events = new List<PairedChirpEvent>(count);
        var intervalSeconds = Math.Max(0.001, interval.TotalSeconds);
        var startDelaySeconds = Math.Max(0.0, startDelay.TotalSeconds);
        var chirpSeconds = Math.Max(0.001, chirpDuration.TotalSeconds);
        var chirpSamples = Math.Max(1, (int)Math.Round(chirpSeconds * sampleRate));
        var totalSamples = Math.Max(
            chirpSamples,
            (int)Math.Ceiling((startDelaySeconds + (count - 1) * intervalSeconds + chirpSeconds + 0.05) * sampleRate));
        var samples = new float[totalSamples];
        var runId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (var index = 0; index < count; index++)
        {
            var offsetSeconds = startDelaySeconds + index * intervalSeconds;
            var startSample = Math.Min(samples.Length - 1, (int)Math.Round(offsetSeconds * sampleRate));
            var endSample = Math.Min(samples.Length, startSample + chirpSamples);
            events.Add(new PairedChirpEvent(
                $"starfire-usb-move-chirp:{runId}:{index}",
                index,
                offsetSeconds,
                startSample,
                endSample,
                startHz,
                endHz));

            for (var sample = startSample; sample < endSample; sample++)
            {
                var local = sample - startSample;
                var normalized = chirpSamples <= 1 ? 0.0 : local / (double)(chirpSamples - 1);
                var frequency = startHz + (endHz - startHz) * normalized;
                var phase = 2.0 * Math.PI * (startHz * local / sampleRate +
                    0.5 * (endHz - startHz) * local * local / Math.Max(1.0, chirpSamples) / sampleRate);
                var envelope = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * normalized);
                samples[sample] += (float)Math.Clamp(Math.Sin(phase) * envelope * gain, -1.0, 1.0);
                _ = frequency; // keep the linear-frequency intent explicit for receipts.
            }
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        stream.Write(bytes);
        return events;
    }

    public static void AppendScheduleEvents(
        string? eventLogPath,
        IReadOnlyList<PairedChirpEvent> events,
        string audioPath,
        int sampleRate,
        double gain)
    {
        if (string.IsNullOrWhiteSpace(eventLogPath))
        {
            return;
        }

        foreach (var item in events)
        {
            var line = System.Text.Json.JsonSerializer.Serialize(new
            {
                document = "mimir.psmove_usb_audio_visual_pulse_event.v1",
                invariant = "visual pulse and audio chirp share one event id and one planned offset",
                item.EventId,
                item.Index,
                item.OffsetSeconds,
                item.AudioStartSample,
                item.AudioEndSample,
                item.StartHz,
                item.EndHz,
                phase = "schedule",
                audioPath,
                sampleRate,
                gain,
                result = "planned"
            });
            File.AppendAllText(eventLogPath, line + Environment.NewLine);
        }
    }

    public static async Task<string> EmitAudioAsync(
        string audioPath,
        int sampleRate,
        double gain,
        string? renderDevice,
        string? wasapiExe,
        string? eventLogPath)
    {
        var resolvedExe = ResolveWasapiExecutable(wasapiExe);
        var args = new List<string>
        {
            "--play-f32-mono",
            "--input",
            Path.GetFullPath(audioPath),
            "--sample-rate",
            sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--gain",
            gain.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--render-buffer-seconds",
            "0.02",
            "--render-drain-seconds",
            "0.08"
        };
        if (!string.IsNullOrWhiteSpace(renderDevice))
        {
            args.Add("--device");
            args.Add(renderDevice);
        }

        var startedNs = WindowsHid.NowNs();
        AppendRenderEvent(eventLogPath, "render-start", audioPath, sampleRate, gain, startedNs, startedNs, "starting");
        using var process = new Process();
        process.StartInfo.FileName = resolvedExe;
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        try
        {
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var completedNs = WindowsHid.NowNs();
            var stderr = await stderrTask.ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var result = process.ExitCode == 0
                ? $"ok renderExit=0 pid={process.Id}"
                : $"render failed exit={process.ExitCode} pid={process.Id}";
            AppendRenderEvent(eventLogPath, "render-complete", audioPath, sampleRate, gain, startedNs, completedNs, result, stdout, stderr);
            return result;
        }
        catch (Exception ex)
        {
            var completedNs = WindowsHid.NowNs();
            var result = "render failed: " + ex.Message;
            AppendRenderEvent(eventLogPath, "render-complete", audioPath, sampleRate, gain, startedNs, completedNs, result);
            return result;
        }
    }

    private static string ResolveWasapiExecutable(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "Mimir.WasapiLoopback",
                "bin",
                "Debug",
                "net10.0-windows",
                "Mimir.WasapiLoopback.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return "Mimir.WasapiLoopback.exe";
    }

    private static void AppendRenderEvent(
        string? eventLogPath,
        string phase,
        string audioPath,
        int sampleRate,
        double gain,
        long startedNs,
        long completedNs,
        string result,
        string? stdout = null,
        string? stderr = null)
    {
        if (string.IsNullOrWhiteSpace(eventLogPath))
        {
            return;
        }

        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            document = "mimir.psmove_usb_audio_visual_pulse_event.v1",
            invariant = "visual pulse and audio chirp share one event id and one planned offset",
            phase,
            audioPath,
            sampleRate,
            gain,
            startedNs,
            completedNs,
            result,
            stdout,
            stderr
        });
        File.AppendAllText(eventLogPath, line + Environment.NewLine);
    }
}

internal static class WindowsHid
{
    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceinterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint ErrorIoPending = 997;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const int HidpStatusSuccess = 0x00110000;
    private const byte GetBtAddressReport = 0x04;
    private const byte SetBtAddressReport = 0x05;
    private const byte LedReport = 0x06;
    private const int BtAddressGetSize = 16;
    private const int BtAddressSetSize = 23;

    public static IEnumerable<HidDeviceInfo> Enumerate()
    {
        foreach (var path in EnumeratePaths())
        {
            if (TryProbe(path, out var device))
            {
                yield return device;
            }
        }
    }

    public static IEnumerable<string> EnumeratePaths()
    {
        HidD_GetHidGuid(out var hidGuid);

        var deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceinterface);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");
        }

        try
        {
            var interfaceData = new SpDeviceInterfaceData
            {
                CbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
            };

            for (uint index = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData); index++)
            {
                yield return GetDevicePath(deviceInfoSet, interfaceData);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    public static async Task<string> TryReadInputReportAsync(HidDeviceInfo device, TimeSpan timeout)
    {
        if (device.Caps.InputReportByteLength == 0)
        {
            return "skipped: no input report";
        }

        using var handle = CreateFile(
            device.Path,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return $"open failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        await Task.Yield();
        var buffer = new byte[device.Caps.InputReportByteLength];
        var readEvent = CreateEvent(IntPtr.Zero, true, false, null);

        if (readEvent == IntPtr.Zero)
        {
            return $"CreateEvent failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        var overlapped = new NativeOverlapped
        {
            EventHandle = readEvent
        };

        try
        {
            var started = ReadFile(handle, buffer, (uint)buffer.Length, IntPtr.Zero, ref overlapped);
            if (!started)
            {
                var error = (uint)Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                {
                    return $"ReadFile failed: {new Win32Exception((int)error).Message}";
                }
            }

            var wait = WaitForSingleObject(readEvent, (uint)Math.Ceiling(timeout.TotalMilliseconds));
            if (wait == WaitTimeout)
            {
                CancelIoEx(handle, ref overlapped);
                return $"timeout after {timeout.TotalMilliseconds:0}ms";
            }

            if (wait != WaitObject0)
            {
                CancelIoEx(handle, ref overlapped);
                return $"wait failed: 0x{wait:X8}";
            }

            if (!GetOverlappedResult(handle, ref overlapped, out var read, false))
            {
                return $"GetOverlappedResult failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            }

            var shown = Convert.ToHexString(buffer.AsSpan(0, (int)Math.Min(read, 32)));
            return $"{read} bytes {shown}{(read > 32 ? "..." : string.Empty)}";
        }
        finally
        {
            CloseHandle(readEvent);
        }
    }

    public static string TryPairHost(HidDeviceInfo device, string hostAddress)
    {
        if (device.Caps.FeatureReportByteLength < BtAddressSetSize || device.Path.IndexOf("&col02#", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return "skipped: pairing uses the PS Move col02 feature-report collection";
        }

        if (!TryParseBluetoothAddress(hostAddress, out var host))
        {
            return $"invalid host address: {hostAddress}";
        }

        using var handle = CreateFile(
            device.Path,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return $"open failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        var before = ReadBluetoothAddresses(handle);
        var report = new byte[BtAddressSetSize];
        report[0] = SetBtAddressReport;
        host.CopyTo(report.AsSpan(1, 6));

        if (!HidD_SetFeature(handle, report, report.Length))
        {
            return $"HidD_SetFeature failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        var after = ReadBluetoothAddresses(handle);
        var controller = after.Controller ?? before.Controller ?? "(unknown)";
        var assigned = after.Host ?? FormatBluetoothAddress(host);
        return $"ok controller={controller} host={assigned}";
    }

    public static async Task<string> TryWriteLedPulseTrainAsync(
        HidDeviceInfo device,
        string rgb,
        int pulseCount,
        TimeSpan pulseDuration,
        TimeSpan interval,
        string? eventLogPath)
    {
        if (device.Caps.OutputReportByteLength == 0 || device.Path.IndexOf("&col01#", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return "skipped: LED output uses the PS Move col01 output-report collection";
        }

        if (!TryParseRgb(rgb, out var r, out var g, out var b))
        {
            return $"invalid rgb: {rgb}";
        }

        var written = 0;
        for (var index = 0; index < Math.Max(1, pulseCount); index++)
        {
            var eventId = $"starfire-usb-move:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{index}";
            var onStarted = NowNs();
            var on = TryWriteLed(device, r, g, b);
            var onCompleted = NowNs();
            AppendPulseEvent(eventLogPath, eventId, index, "on", r, g, b, onStarted, onCompleted, on);
            if (!on.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
            {
                return on;
            }

            if (pulseDuration > TimeSpan.Zero)
            {
                await Task.Delay(pulseDuration).ConfigureAwait(false);
            }

            var offStarted = NowNs();
            var off = TryWriteLed(device, 0, 0, 0);
            var offCompleted = NowNs();
            AppendPulseEvent(eventLogPath, eventId, index, "off", 0, 0, 0, offStarted, offCompleted, off);
            if (!off.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
            {
                return $"off failed after {written} pulses: {off}";
            }

            written++;
            if (index + 1 < pulseCount)
            {
                var rest = interval - pulseDuration;
                if (rest > TimeSpan.Zero)
                {
                    await Task.Delay(rest).ConfigureAwait(false);
                }
            }
        }

        return $"ok pulses={written} rgb=#{r:X2}{g:X2}{b:X2} pulseMs={pulseDuration.TotalMilliseconds:0} intervalMs={interval.TotalMilliseconds:0}";
    }

    public static async Task<string> TryWriteScheduledLedTrainAsync(
        HidDeviceInfo device,
        IReadOnlyList<PairedChirpEvent> events,
        byte r,
        byte g,
        byte b,
        TimeSpan pulseDuration,
        string? eventLogPath)
    {
        if (device.Caps.OutputReportByteLength == 0 || device.Path.IndexOf("&col01#", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return "skipped: LED output uses the PS Move col01 output-report collection";
        }

        TimeBeginPeriod(1);
        try
        {
            var started = NowNs();
            var stopwatch = Stopwatch.StartNew();
            var written = 0;
            foreach (var item in events)
            {
                WaitUntil(stopwatch, TimeSpan.FromSeconds(item.OffsetSeconds));

                var onStarted = NowNs();
                var on = TryWriteLed(device, r, g, b);
                var onCompleted = NowNs();
                AppendPairedPulseEvent(eventLogPath, item, "on", r, g, b, started, onStarted, onCompleted, on);
                if (!on.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
                {
                    return on;
                }

                if (pulseDuration > TimeSpan.Zero)
                {
                    WaitUntil(stopwatch, TimeSpan.FromSeconds(item.OffsetSeconds) + pulseDuration);
                }

                var offStarted = NowNs();
                var off = TryWriteLed(device, 0, 0, 0);
                var offCompleted = NowNs();
                AppendPairedPulseEvent(eventLogPath, item, "off", 0, 0, 0, started, offStarted, offCompleted, off);
                if (!off.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
                {
                    return $"off failed after {written} events: {off}";
                }

                written++;
            }

            return $"ok pairedEvents={written}";
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    private static void WaitUntil(Stopwatch stopwatch, TimeSpan due)
    {
        while (true)
        {
            var remaining = due - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining > TimeSpan.FromMilliseconds(8))
            {
                Thread.Sleep(Math.Max(1, (int)Math.Floor(remaining.TotalMilliseconds) - 4));
                continue;
            }

            if (remaining > TimeSpan.FromMilliseconds(1.5))
            {
                Thread.Sleep(0);
                continue;
            }

            Thread.SpinWait(256);
        }
    }

    private static string TryWriteLed(HidDeviceInfo device, byte r, byte g, byte b)
    {
        using var handle = CreateFile(
            device.Path,
            GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return $"open failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        var report = new byte[Math.Max(9, (int)device.Caps.OutputReportByteLength)];
        report[0] = LedReport;
        report[1] = 0;
        report[2] = r;
        report[3] = g;
        report[4] = b;
        return WriteFile(handle, report, (uint)report.Length, out var written, IntPtr.Zero)
            ? $"ok rgb=#{r:X2}{g:X2}{b:X2} bytes={written}"
            : $"WriteFile failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
    }

    public static bool TryParseRgb(string value, out byte r, out byte g, out byte b)
    {
        r = 0;
        g = 0;
        b = 0;
        var normalized = value.Trim().TrimStart('#');
        if (normalized.Length == 6)
        {
            return byte.TryParse(normalized.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r) &&
                byte.TryParse(normalized.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g) &&
                byte.TryParse(normalized.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
        }

        var parts = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 3 &&
            byte.TryParse(parts[0], out r) &&
            byte.TryParse(parts[1], out g) &&
            byte.TryParse(parts[2], out b);
    }

    public static long NowNs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

    private static void AppendPulseEvent(
        string? eventLogPath,
        string eventId,
        int index,
        string phase,
        byte r,
        byte g,
        byte b,
        long startedNs,
        long completedNs,
        string result)
    {
        if (string.IsNullOrWhiteSpace(eventLogPath))
        {
            return;
        }

        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            document = "mimir.psmove_usb_pulse_event.v1",
            eventId,
            index,
            phase,
            rgb = new[] { (int)r, (int)g, (int)b },
            startedNs,
            completedNs,
            result,
        });
        File.AppendAllText(eventLogPath, line + Environment.NewLine);
    }

    private static void AppendPairedPulseEvent(
        string? eventLogPath,
        PairedChirpEvent item,
        string phase,
        byte r,
        byte g,
        byte b,
        long trainStartNs,
        long startedNs,
        long completedNs,
        string result)
    {
        if (string.IsNullOrWhiteSpace(eventLogPath))
        {
            return;
        }

        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            document = "mimir.psmove_usb_audio_visual_pulse_event.v1",
            invariant = "visual pulse and audio chirp share one event id and one planned offset",
            item.EventId,
            item.Index,
            item.OffsetSeconds,
            item.AudioStartSample,
            item.AudioEndSample,
            item.StartHz,
            item.EndHz,
            phase,
            rgb = new[] { (int)r, (int)g, (int)b },
            trainStartNs,
            startedNs,
            completedNs,
            result,
        });
        File.AppendAllText(eventLogPath, line + Environment.NewLine);
    }

    private static (string? Controller, string? Host) ReadBluetoothAddresses(SafeFileHandle handle)
    {
        var report = new byte[BtAddressGetSize];
        report[0] = GetBtAddressReport;
        if (!HidD_GetFeature(handle, report, report.Length))
        {
            return (null, null);
        }

        return (FormatBluetoothAddress(report.AsSpan(1, 6)), FormatBluetoothAddress(report.AsSpan(10, 6)));
    }

    private static bool TryParseBluetoothAddress(string value, out byte[] bytes)
    {
        bytes = new byte[6];
        var parts = value.Split(':', '-');
        if (parts.Length != 6)
        {
            return false;
        }

        for (var index = 0; index < parts.Length; index++)
        {
            if (!byte.TryParse(parts[index], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            bytes[5 - index] = parsed;
        }

        return true;
    }

    private static string FormatBluetoothAddress(ReadOnlySpan<byte> littleEndianAddress)
    {
        return string.Join(
            ":",
            littleEndianAddress.ToArray().Reverse().Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string GetDevicePath(IntPtr deviceInfoSet, SpDeviceInterfaceData interfaceData)
    {
        SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
        if (requiredSize == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail size query failed.");
        }

        var detailData = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailData, requiredSize, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail failed.");
            }

            return Marshal.PtrToStringUni(IntPtr.Add(detailData, 4))
                ?? throw new InvalidOperationException("HID device path was empty.");
        }
        finally
        {
            Marshal.FreeHGlobal(detailData);
        }
    }

    private static bool TryProbe(string path, out HidDeviceInfo device)
    {
        device = default!;
        if (!TryExtractVidPid(path, out var vid, out var pid))
        {
            return false;
        }

        using var handle = CreateFile(
            path,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        if (!HidD_GetPreparsedData(handle, out var preparsedData))
        {
            return false;
        }

        try
        {
            var status = HidP_GetCaps(preparsedData, out var caps);
            if (status != HidpStatusSuccess)
            {
                return false;
            }

            device = new HidDeviceInfo(
                path,
                vid,
                pid,
                GetHidString(handle, HidD_GetManufacturerString),
                GetHidString(handle, HidD_GetProductString),
                GetHidString(handle, HidD_GetSerialNumberString),
                caps);
            return true;
        }
        finally
        {
            HidD_FreePreparsedData(preparsedData);
        }
    }

    private static bool TryExtractVidPid(string path, out ushort vid, out ushort pid)
    {
        vid = 0;
        pid = 0;
        var lower = path.ToLowerInvariant();
        return TryExtractHexAfter(lower, "vid_", out vid) && TryExtractHexAfter(lower, "pid_", out pid);
    }

    private static bool TryExtractHexAfter(string value, string marker, out ushort result)
    {
        result = 0;
        var index = value.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0 || index + marker.Length + 4 > value.Length)
        {
            return false;
        }

        return ushort.TryParse(
            value.AsSpan(index + marker.Length, 4),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);
    }

    private delegate bool HidStringGetter(SafeFileHandle hidDeviceObject, byte[] buffer, int bufferLength);

    private static string? GetHidString(SafeFileHandle handle, HidStringGetter getter)
    {
        var buffer = new byte[256];
        return getter(handle, buffer, buffer.Length)
            ? Marshal.PtrToStringUni(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0))?.TrimEnd('\0')
            : null;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle hidDeviceObject, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle hidDeviceObject, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(
        IntPtr eventAttributes,
        bool manualReset,
        bool initialState,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle file,
        byte[] buffer,
        uint numberOfBytesToRead,
        IntPtr numberOfBytesRead,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle file,
        byte[] buffer,
        uint numberOfBytesToWrite,
        out uint numberOfBytesWritten,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle file,
        ref NativeOverlapped overlapped,
        out uint numberOfBytesTransferred,
        bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(
        SafeFileHandle file,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped
    {
        public IntPtr InternalLow;
        public IntPtr InternalHigh;
        public uint OffsetLow;
        public uint OffsetHigh;
        public IntPtr EventHandle;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct HidpCaps
{
    public ushort Usage;
    public ushort UsagePage;
    public ushort InputReportByteLength;
    public ushort OutputReportByteLength;
    public ushort FeatureReportByteLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
    public ushort[] Reserved;

    public ushort NumberLinkCollectionNodes;
    public ushort NumberInputButtonCaps;
    public ushort NumberInputValueCaps;
    public ushort NumberInputDataIndices;
    public ushort NumberOutputButtonCaps;
    public ushort NumberOutputValueCaps;
    public ushort NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps;
    public ushort NumberFeatureValueCaps;
    public ushort NumberFeatureDataIndices;
}
