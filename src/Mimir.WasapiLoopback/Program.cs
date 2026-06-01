using System.Runtime.InteropServices;

namespace Mimir.WasapiLoopback;

internal static class Program
{
    private const int ERender = 0;
    private const int EConsole = 0;
    private const int EMultimedia = 1;
    private const int ECommunications = 2;
    private const int ClsctxAll = 23;
    private const uint DeviceStateActive = 0x00000001;
    private const uint AudclntSharemodeShared = 0;
    private const uint AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntBufferflagsSilent = 0x2;
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid PcmSubformat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubformat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly PropertyKey DeviceFriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.ListDevices)
            {
                ListRenderDevices();
                return 0;
            }

            if (options.Mode == RunMode.PlayF32Mono)
            {
                PlayF32Mono(options);
            }
            else
            {
                Capture(options);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Capture(Options options)
    {
        var outputIsStdout = options.OutputPath is "-" or "stdout";
        Stream output = outputIsStdout
            ? Console.OpenStandardOutput()
            : new FileStream(options.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        using var outputLifetime = outputIsStdout ? null : output;
        IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        var device = ResolveRenderDevice(enumerator, options.Role, options.Device);

        var audioClientId = IidAudioClient;
        Check(device.Activate(ref audioClientId, ClsctxAll, IntPtr.Zero, out var audioClient), "Activate IAudioClient");
        Check(audioClient.GetMixFormat(out var mixFormatPtr), "GetMixFormat");

        var activeFormatPtr = mixFormatPtr;
        var fmt = Marshal.PtrToStructure<WaveFormatEx>(activeFormatPtr);
        var sampleFormat = SampleFormat.From(activeFormatPtr);
        LogFormat("MixFormat", fmt, sampleFormat);

        var init = audioClient.Initialize(AudclntSharemodeShared, AudclntStreamflagsLoopback, 0, 0, activeFormatPtr, IntPtr.Zero);
        Check(init, "Initialize loopback");

        var captureClientId = IidAudioCaptureClient;
        Check(audioClient.GetService(ref captureClientId, out var captureService), "GetService IAudioCaptureClient");
        var captureClient = (IAudioCaptureClient)captureService;
        Check(audioClient.Start(), "Start");

        try
        {
            Pump(options, output, captureClient, fmt, sampleFormat);
        }
        finally
        {
            audioClient.Stop();
            Marshal.FreeCoTaskMem(mixFormatPtr);
            Marshal.ReleaseComObject(captureClient);
            Marshal.ReleaseComObject(audioClient);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static void PlayF32Mono(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            throw new ArgumentException("--input is required for --play-f32-mono");
        }

        var inputBytes = File.ReadAllBytes(options.InputPath);
        if (inputBytes.Length < sizeof(float))
        {
            throw new InvalidOperationException("Input file contains no Float32 samples: " + options.InputPath);
        }

        var inputSamples = new float[inputBytes.Length / sizeof(float)];
        Buffer.BlockCopy(inputBytes, 0, inputSamples, 0, inputSamples.Length * sizeof(float));

        IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        var device = ResolveRenderDevice(enumerator, options.Role, options.Device);
        var audioClientId = IidAudioClient;
        Check(device.Activate(ref audioClientId, ClsctxAll, IntPtr.Zero, out var audioClient), "Activate IAudioClient");
        Check(audioClient.GetMixFormat(out var mixFormatPtr), "GetMixFormat");

        var fmt = Marshal.PtrToStructure<WaveFormatEx>(mixFormatPtr);
        var sampleFormat = SampleFormat.From(mixFormatPtr);
        LogFormat("RenderMixFormat", fmt, sampleFormat);

        var hnsBuffer = Math.Max(100_000L, (long)Math.Round(10_000_000.0 * Math.Min(0.25, Math.Max(0.02, options.RenderBufferSeconds))));
        Check(audioClient.Initialize(AudclntSharemodeShared, 0, hnsBuffer, 0, mixFormatPtr, IntPtr.Zero), "Initialize render");
        Check(audioClient.GetBufferSize(out var bufferFrames), "GetBufferSize");

        var renderClientId = IidAudioRenderClient;
        Check(audioClient.GetService(ref renderClientId, out var renderService), "GetService IAudioRenderClient");
        var renderClient = (IAudioRenderClient)renderService;
        Check(audioClient.Start(), "Start");

        try
        {
            PumpRender(options, inputSamples, audioClient, renderClient, fmt, sampleFormat, bufferFrames);
        }
        finally
        {
            audioClient.Stop();
            Marshal.FreeCoTaskMem(mixFormatPtr);
            Marshal.ReleaseComObject(renderClient);
            Marshal.ReleaseComObject(audioClient);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static void PumpRender(
        Options options,
        float[] inputSamples,
        IAudioClient audioClient,
        IAudioRenderClient renderClient,
        WaveFormatEx fmt,
        SampleFormat sampleFormat,
        uint bufferFrames)
    {
        var renderChannels = Math.Max(1, (int)fmt.Channels);
        var renderRate = Math.Max(1, (int)fmt.SamplesPerSec);
        var bytesPerFrame = fmt.BlockAlign;
        var bytesPerSample = Math.Max(1, sampleFormat.ContainerBits / 8);
        var sourceFramesPerRenderFrame = options.SampleRate / (double)renderRate;
        var sourceFrame = 0.0;
        var framesWritten = 0L;
        var peak = inputSamples.Select(Math.Abs).DefaultIfEmpty(0f).Max();
        var gain = Math.Clamp(options.Gain, 0.0, 8.0);
        var nextMeter = DateTime.UtcNow.AddSeconds(1);

        while (sourceFrame < inputSamples.Length)
        {
            Check(audioClient.GetCurrentPadding(out var padding), "GetCurrentPadding");
            var available = bufferFrames > padding ? bufferFrames - padding : 0;
            if (available == 0)
            {
                Thread.Sleep(2);
                continue;
            }

            var frames = (uint)Math.Min(available, Math.Ceiling((inputSamples.Length - sourceFrame) / sourceFramesPerRenderFrame));
            if (frames == 0)
            {
                break;
            }

            Check(renderClient.GetBuffer(frames, out var data), "Render GetBuffer");
            try
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    var sample = (float)Math.Clamp(ReadInterpolated(inputSamples, sourceFrame) * gain, -1.0, 1.0);
                    for (var ch = 0; ch < renderChannels; ch++)
                    {
                        var target = IntPtr.Add(data, frame * bytesPerFrame + ch * bytesPerSample);
                        WriteSample(target, sample, sampleFormat);
                    }

                    sourceFrame += sourceFramesPerRenderFrame;
                }
            }
            finally
            {
                Check(renderClient.ReleaseBuffer(frames, 0), "Render ReleaseBuffer");
            }

            framesWritten += frames;
            if (DateTime.UtcNow >= nextMeter)
            {
                Console.Error.WriteLine("RenderMeter frames={0} source={1:0}/{2} peak={3:0.000000} gain={4:0.000}", framesWritten, sourceFrame, inputSamples.Length, peak, gain);
                nextMeter = DateTime.UtcNow.AddSeconds(1);
            }
        }

        var drainMs = Math.Max(40, (int)Math.Round(options.RenderDrainSeconds * 1000.0));
        Thread.Sleep(drainMs);
        Console.Error.WriteLine("RenderComplete frames={0} seconds={1:0.000} inputSamples={2} inputRate={3}", framesWritten, framesWritten / (double)renderRate, inputSamples.Length, options.SampleRate);
    }

    private static void Pump(
        Options options,
        Stream output,
        IAudioCaptureClient captureClient,
        WaveFormatEx fmt,
        SampleFormat sampleFormat)
    {
        var targetChannels = Math.Max(1, options.Channels);
        var sourceChannels = Math.Max(1, (int)fmt.Channels);
        var sourceRate = Math.Max(1, (int)fmt.SamplesPerSec);
        var sourceFramesPerTargetFrame = sourceRate / (double)Math.Max(1, options.SampleRate);
        var sourceBytesPerFrame = fmt.BlockAlign;
        var sourceBytesPerSample = Math.Max(1, sampleFormat.ContainerBits / 8);
        var frameBytes = targetChannels * sizeof(float);
        var end = options.Seconds > 0 ? DateTime.UtcNow.AddSeconds(options.Seconds) : DateTime.MaxValue;
        var pcm = new byte[8192];
        var previousFrame = new float[targetChannels];
        var left = new float[targetChannels];
        var right = new float[targetChannels];
        var hasPreviousFrame = false;
        var sourceFramesSeen = 0L;
        var nextOutputSourceFrame = 0.0;
        var nextMeter = DateTime.UtcNow.AddSeconds(1);
        var samplesSeen = 0L;
        var peak = 0f;
        var clipped = 0L;

        while (DateTime.UtcNow < end)
        {
            Check(captureClient.GetNextPacketSize(out var packetFrames), "GetNextPacketSize");
            if (packetFrames == 0)
            {
                Thread.Sleep(2);
                continue;
            }

            Check(captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out _), "GetBuffer");
            try
            {
                var packetStart = sourceFramesSeen;
                var packetEnd = packetStart + frames;
                var outputFrames = 0;
                var cursor = nextOutputSourceFrame;
                while (cursor < packetEnd)
                {
                    outputFrames++;
                    cursor += sourceFramesPerTargetFrame;
                }

                var needed = checked(outputFrames * frameBytes);
                if (pcm.Length < needed)
                {
                    pcm = new byte[needed];
                }

                var dst = 0;
                while (nextOutputSourceFrame < packetEnd)
                {
                    var leftAbsolute = (long)Math.Floor(nextOutputSourceFrame);
                    var rightAbsolute = Math.Min(packetEnd - 1, leftAbsolute + 1);
                    var fraction = nextOutputSourceFrame - leftAbsolute;
                    ReadFrame(leftAbsolute, packetStart, data, flags, sourceChannels, sourceBytesPerFrame, sourceBytesPerSample, sampleFormat, previousFrame, hasPreviousFrame, left);
                    ReadFrame(rightAbsolute, packetStart, data, flags, sourceChannels, sourceBytesPerFrame, sourceBytesPerSample, sampleFormat, previousFrame, hasPreviousFrame, right);

                    for (var ch = 0; ch < targetChannels; ch++)
                    {
                        var sample = (float)(left[ch] + (right[ch] - left[ch]) * fraction);
                        var abs = Math.Abs(sample);
                        peak = Math.Max(peak, abs);
                        if (abs > 1f)
                        {
                            clipped++;
                        }

                        sample = Math.Clamp(sample, -1f, 1f);
                        BitConverter.TryWriteBytes(pcm.AsSpan(dst, sizeof(float)), sample);
                        dst += sizeof(float);
                        samplesSeen++;
                    }

                    nextOutputSourceFrame += sourceFramesPerTargetFrame;
                }

                if (frames > 0)
                {
                    ReadFrame(packetEnd - 1, packetStart, data, flags, sourceChannels, sourceBytesPerFrame, sourceBytesPerSample, sampleFormat, previousFrame, false, previousFrame);
                    hasPreviousFrame = true;
                }

                output.Write(pcm, 0, needed);
                output.Flush();
                sourceFramesSeen = packetEnd;

                if (DateTime.UtcNow >= nextMeter)
                {
                    Console.Error.WriteLine("LoopbackMeter samples={0} peak={1:0.000000} clipCandidates={2}", samplesSeen, peak, clipped);
                    nextMeter = DateTime.UtcNow.AddSeconds(1);
                }
            }
            finally
            {
                Check(captureClient.ReleaseBuffer(frames), "ReleaseBuffer");
            }
        }
    }

    private static void ReadFrame(
        long absoluteFrame,
        long packetStart,
        IntPtr data,
        uint flags,
        int sourceChannels,
        int sourceBytesPerFrame,
        int sourceBytesPerSample,
        SampleFormat format,
        float[] previousFrame,
        bool hasPreviousFrame,
        float[] target)
    {
        if (absoluteFrame < packetStart)
        {
            for (var ch = 0; ch < target.Length; ch++)
            {
                target[ch] = hasPreviousFrame ? previousFrame[ch] : 0f;
            }

            return;
        }

        var packetFrame = checked((int)(absoluteFrame - packetStart));
        for (var ch = 0; ch < target.Length; ch++)
        {
            var sample = 0f;
            if ((flags & AudclntBufferflagsSilent) == 0)
            {
                var srcCh = Math.Min(ch, sourceChannels - 1);
                var src = IntPtr.Add(data, packetFrame * sourceBytesPerFrame + srcCh * sourceBytesPerSample);
                sample = ReadSample(src, format);
            }

            target[ch] = sample;
        }
    }

    private static float ReadSample(IntPtr source, SampleFormat format)
    {
        if (format.Kind == SampleKind.IeeeFloat && format.ContainerBits == 32)
        {
            return Marshal.PtrToStructure<float>(source);
        }

        if (format.Kind != SampleKind.Pcm)
        {
            return 0f;
        }

        return format.ContainerBits switch
        {
            16 => Marshal.ReadInt16(source) / 32768f,
            24 => ReadPacked24(source) / 8388608f,
            32 => ReadPcm32(source, format.ValidBits),
            _ => 0f
        };
    }

    private static float ReadPcm32(IntPtr source, int validBits)
    {
        var value = Marshal.ReadInt32(source);
        if (validBits is > 0 and < 32)
        {
            var shifted = value >> (32 - validBits);
            return Math.Clamp(shifted / (float)(1 << (validBits - 1)), -1f, 1f);
        }

        return Math.Clamp(value / 2147483648f, -1f, 1f);
    }

    private static int ReadPacked24(IntPtr source)
    {
        var b0 = Marshal.ReadByte(source, 0);
        var b1 = Marshal.ReadByte(source, 1);
        var b2 = Marshal.ReadByte(source, 2);
        var value = b0 | (b1 << 8) | (b2 << 16);
        if ((value & 0x800000) != 0)
        {
            value |= unchecked((int)0xff000000);
        }

        return value;
    }

    private static float ReadInterpolated(IReadOnlyList<float> samples, double frame)
    {
        if (samples.Count == 0)
        {
            return 0f;
        }

        if (frame <= 0.0)
        {
            return samples[0];
        }

        var left = (int)Math.Floor(frame);
        if (left >= samples.Count - 1)
        {
            return samples[^1];
        }

        var fraction = frame - left;
        return (float)(samples[left] + (samples[left + 1] - samples[left]) * fraction);
    }

    private static void WriteSample(IntPtr target, float sample, SampleFormat format)
    {
        if (format.Kind == SampleKind.IeeeFloat && format.ContainerBits == 32)
        {
            Marshal.StructureToPtr(sample, target, false);
            return;
        }

        if (format.Kind != SampleKind.Pcm)
        {
            return;
        }

        sample = Math.Clamp(sample, -1f, 1f);
        switch (format.ContainerBits)
        {
            case 16:
                Marshal.WriteInt16(target, (short)Math.Clamp(Math.Round(sample * 32767.0), short.MinValue, short.MaxValue));
                break;
            case 24:
                WritePacked24(target, (int)Math.Clamp(Math.Round(sample * 8388607.0), -8388608.0, 8388607.0));
                break;
            case 32:
                var bits = format.ValidBits is > 0 and < 32 ? format.ValidBits : 32;
                var max = Math.Pow(2.0, bits - 1) - 1.0;
                var value = (int)Math.Clamp(Math.Round(sample * max), -max - 1.0, max);
                if (bits < 32)
                {
                    value <<= 32 - bits;
                }

                Marshal.WriteInt32(target, value);
                break;
        }
    }

    private static void WritePacked24(IntPtr target, int value)
    {
        Marshal.WriteByte(target, 0, (byte)(value & 0xff));
        Marshal.WriteByte(target, 1, (byte)((value >> 8) & 0xff));
        Marshal.WriteByte(target, 2, (byte)((value >> 16) & 0xff));
    }

    private static void LogFormat(string label, WaveFormatEx fmt, SampleFormat sampleFormat)
    {
        Console.Error.WriteLine(
            "{0} tag={1} channels={2} rate={3} bits={4} blockAlign={5} cbSize={6} kind={7} validBits={8} subFormat={9}",
            label,
            fmt.FormatTag,
            fmt.Channels,
            fmt.SamplesPerSec,
            fmt.BitsPerSample,
            fmt.BlockAlign,
            fmt.Size,
            sampleFormat.Kind,
            sampleFormat.ValidBits,
            sampleFormat.SubFormat);
    }

    private static void Check(int hr, string stage)
    {
        if (hr >= 0)
        {
            return;
        }

        var inner = Marshal.GetExceptionForHR(hr);
        throw new InvalidOperationException(stage + " failed: 0x" + hr.ToString("X8") + " " + inner?.Message, inner);
    }

    private static int RoleFromName(string roleName)
    {
        return roleName.ToLowerInvariant() switch
        {
            "multimedia" => EMultimedia,
            "communications" => ECommunications,
            _ => EConsole
        };
    }

    private static void ListRenderDevices()
    {
        IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        Check(enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out var devices), "EnumAudioEndpoints");
        Check(devices.GetCount(out var count), "GetCount");
        for (uint index = 0; index < count; index++)
        {
            Check(devices.Item(index, out var device), "Item");
            try
            {
                Console.WriteLine("{0}: {1}", index, DeviceFriendlyName(device));
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }

        Marshal.ReleaseComObject(devices);
        Marshal.ReleaseComObject(enumerator);
    }

    private static IMMDevice ResolveRenderDevice(IMMDeviceEnumerator enumerator, string role, string deviceSubstring)
    {
        if (string.IsNullOrWhiteSpace(deviceSubstring))
        {
            Check(enumerator.GetDefaultAudioEndpoint(ERender, RoleFromName(role), out var defaultDevice), "GetDefaultAudioEndpoint");
            Console.Error.WriteLine("SelectedRenderDevice default role={0} name=\"{1}\"", role, DeviceFriendlyName(defaultDevice));
            return defaultDevice;
        }

        Check(enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out var devices), "EnumAudioEndpoints");
        Check(devices.GetCount(out var count), "GetCount");
        IMMDevice? selected = null;
        var selectedName = "";
        try
        {
            for (uint index = 0; index < count; index++)
            {
                Check(devices.Item(index, out var device), "Item");
                var name = DeviceFriendlyName(device);
                if (name.Contains(deviceSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    selected = device;
                    selectedName = name;
                    break;
                }

                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(devices);
        }

        if (selected == null)
        {
            throw new InvalidOperationException("No active render endpoint matched --device \"" + deviceSubstring + "\"");
        }

        Console.Error.WriteLine("SelectedRenderDevice match=\"{0}\" name=\"{1}\"", deviceSubstring, selectedName);
        return selected;
    }

    private static string DeviceFriendlyName(IMMDevice device)
    {
        Check(device.OpenPropertyStore(0, out var store), "OpenPropertyStore");
        try
        {
            var key = DeviceFriendlyNameKey;
            Check(store.GetValue(ref key, out var value), "GetValue FriendlyName");
            try
            {
                return value.ValueType == 31 && value.Pointer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.Pointer) ?? ""
                    : "";
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private sealed record Options(
        RunMode Mode,
        string InputPath,
        string OutputPath,
        double Seconds,
        int SampleRate,
        int Channels,
        string Role,
        string Device,
        double Gain,
        double RenderBufferSeconds,
        double RenderDrainSeconds,
        bool ListDevices)
    {
        public static Options Parse(string[] args)
        {
            var mode = RunMode.Capture;
            var input = "";
            var output = "stdout";
            var seconds = 0.0;
            var sampleRate = 48000;
            var channels = 2;
            var role = "Console";
            var device = "";
            var gain = 1.0;
            var renderBufferSeconds = 0.06;
            var renderDrainSeconds = 0.20;
            var listDevices = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--list-devices":
                        listDevices = true;
                        break;
                    case "--play-f32-mono":
                        mode = RunMode.PlayF32Mono;
                        break;
                    case "--input":
                        input = ReadValue(args, ref i);
                        break;
                    case "--output":
                        output = ReadValue(args, ref i);
                        break;
                    case "--seconds":
                        seconds = double.Parse(ReadValue(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--sample-rate":
                        sampleRate = int.Parse(ReadValue(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--channels":
                        channels = int.Parse(ReadValue(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--role":
                        role = ReadValue(args, ref i);
                        break;
                    case "--device":
                        device = ReadValue(args, ref i);
                        break;
                    case "--gain":
                        gain = double.Parse(ReadValue(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--render-buffer-seconds":
                        renderBufferSeconds = double.Parse(ReadValue(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--render-drain-seconds":
                        renderDrainSeconds = double.Parse(ReadValue(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--help":
                    case "-h":
                        Console.Error.WriteLine("Usage: Mimir.WasapiLoopback [--list-devices] [--device Realtek] [--output stdout --sample-rate 48000 --channels 2 --seconds 10] [--play-f32-mono --input probe.raw --sample-rate 48000 --gain 1]");
                        Environment.Exit(0);
                        break;
                    default:
                        throw new ArgumentException("Unknown argument: " + args[i]);
                }
            }

            return new Options(mode, input, output, seconds, sampleRate, channels, role, device, gain, renderBufferSeconds, renderDrainSeconds, listDevices);
        }

        private static string ReadValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException("Missing value for " + args[index]);
            }

            index++;
            return args[index];
        }
    }

    private enum RunMode
    {
        Capture,
        PlayF32Mono
    }

    private readonly struct SampleFormat
    {
        public SampleFormat(SampleKind kind, int containerBits, int validBits, Guid subFormat)
        {
            Kind = kind;
            ContainerBits = containerBits;
            ValidBits = validBits;
            SubFormat = subFormat;
        }

        public SampleKind Kind { get; }
        public int ContainerBits { get; }
        public int ValidBits { get; }
        public Guid SubFormat { get; }

        public static SampleFormat From(IntPtr formatPtr)
        {
            var fmt = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
            var kind = fmt.FormatTag switch
            {
                1 => SampleKind.Pcm,
                3 => SampleKind.IeeeFloat,
                _ => SampleKind.Unknown
            };
            var validBits = fmt.BitsPerSample;
            var subFormat = Guid.Empty;

            if (fmt.FormatTag == 65534 && fmt.Size >= 22)
            {
                var ext = Marshal.PtrToStructure<WaveFormatExtensible>(formatPtr);
                subFormat = ext.SubFormat;
                kind = subFormat == FloatSubformat
                    ? SampleKind.IeeeFloat
                    : subFormat == PcmSubformat
                        ? SampleKind.Pcm
                        : SampleKind.Unknown;

                if (ext.ValidBitsPerSample > 0 && ext.ValidBitsPerSample <= fmt.BitsPerSample)
                {
                    validBits = ext.ValidBitsPerSample;
                }
            }

            return new SampleFormat(kind, fmt.BitsPerSample, validBits, subFormat);
        }
    }

    private enum SampleKind
    {
        Unknown,
        Pcm,
        IeeeFloat
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IAudioClient audioClient);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IPropertyStore properties);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(uint shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferSize);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint currentPadding);

        [PreserveSig]
        int IsFormatSupported(uint shareMode, IntPtr pFormat, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint frames);

        [PreserveSig]
        int GetNextPacketSize(out uint frames);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig]
        int GetBuffer(uint frames, out IntPtr data);

        [PreserveSig]
        int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort ValueType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public int Int32Value;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);
}
