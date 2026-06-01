using System.Runtime.InteropServices;

namespace Mimir.WasapiLoopback;

internal static class Program
{
    private const int ERender = 0;
    private const int EConsole = 0;
    private const int EMultimedia = 1;
    private const int ECommunications = 2;
    private const int ClsctxAll = 23;
    private const uint AudclntSharemodeShared = 0;
    private const uint AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntBufferflagsSilent = 0x2;
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid PcmSubformat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubformat = new("00000003-0000-0010-8000-00aa00389b71");

    public static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            Capture(options);
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
        Check(enumerator.GetDefaultAudioEndpoint(ERender, RoleFromName(options.Role), out var device), "GetDefaultAudioEndpoint");

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
        Check(audioClient.GetService(ref captureClientId, out var captureClient), "GetService IAudioCaptureClient");
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

    private sealed record Options(string OutputPath, double Seconds, int SampleRate, int Channels, string Role)
    {
        public static Options Parse(string[] args)
        {
            var output = "stdout";
            var seconds = 0.0;
            var sampleRate = 48000;
            var channels = 2;
            var role = "Console";

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
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
                    case "--help":
                    case "-h":
                        Console.Error.WriteLine("Usage: Mimir.WasapiLoopback --output stdout --sample-rate 48000 --channels 2 [--seconds 10] [--role Console]");
                        Environment.Exit(0);
                        break;
                    default:
                        throw new ArgumentException("Unknown argument: " + args[i]);
                }
            }

            return new Options(output, seconds, sampleRate, channels, role);
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
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IAudioClient audioClient);
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
        int GetService(ref Guid iid, out IAudioCaptureClient captureClient);
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
}
