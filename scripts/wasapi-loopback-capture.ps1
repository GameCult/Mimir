param(
    [string]$Output = "-",
    [double]$Seconds = 0,
    [int]$SampleRate = 48000,
    [int]$Channels = 2
)

$ErrorActionPreference = "Stop"

$source = @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LocalCastBridge {
    public static class WasapiLoopbackCapture {
        const int eRender = 0;
        const int eConsole = 0;
        const int CLSCTX_ALL = 23;
        const uint AUDCLNT_SHAREMODE_SHARED = 0;
        const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEXTENSIBLE {
            public WAVEFORMATEX Format;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IAudioClient audioClient);
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient {
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
        interface IAudioCaptureClient {
            [PreserveSig]
            int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig]
            int ReleaseBuffer(uint frames);
            [PreserveSig]
            int GetNextPacketSize(out uint frames);
        }

        public static void Capture(string outputPath, double seconds, int targetRate, int targetChannels) {
            Stream output = outputPath == "-" ? Console.OpenStandardOutput() : new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            try {
                IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
                IMMDevice device;
                Check(enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out device), "GetDefaultAudioEndpoint");
                IAudioClient audioClient;
                Guid audioClientId = IID_IAudioClient;
                Check(device.Activate(ref audioClientId, CLSCTX_ALL, IntPtr.Zero, out audioClient), "Activate IAudioClient");
                IntPtr mixFormatPtr;
                Check(audioClient.GetMixFormat(out mixFormatPtr), "GetMixFormat");
                WAVEFORMATEX fmt = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormatPtr);
                Console.Error.WriteLine("MixFormat tag={0} channels={1} rate={2} bits={3} blockAlign={4} cbSize={5}", fmt.wFormatTag, fmt.nChannels, fmt.nSamplesPerSec, fmt.wBitsPerSample, fmt.nBlockAlign, fmt.cbSize);
                IntPtr activeFormatPtr = mixFormatPtr;
                IntPtr fallbackFormatPtr = IntPtr.Zero;
                int init = audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 0, 0, activeFormatPtr, IntPtr.Zero);
                if (init < 0) {
                    fallbackFormatPtr = MakeWaveFormatPcm((ushort)targetChannels, (uint)targetRate, 16);
                    activeFormatPtr = fallbackFormatPtr;
                    fmt = Marshal.PtrToStructure<WAVEFORMATEX>(activeFormatPtr);
                    Console.Error.WriteLine("RetryFormat tag={0} channels={1} rate={2} bits={3} blockAlign={4} cbSize={5}", fmt.wFormatTag, fmt.nChannels, fmt.nSamplesPerSec, fmt.wBitsPerSample, fmt.nBlockAlign, fmt.cbSize);
                    init = audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 0, 0, activeFormatPtr, IntPtr.Zero);
                }
                Check(init, "Initialize loopback");
                IAudioCaptureClient captureClient;
                Guid captureClientId = IID_IAudioCaptureClient;
                Check(audioClient.GetService(ref captureClientId, out captureClient), "GetService IAudioCaptureClient");
                Check(audioClient.Start(), "Start");
                DateTime end = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
                byte[] pcm = new byte[8192];
                try {
                    while (DateTime.UtcNow < end) {
                        uint packetFrames;
                        Check(captureClient.GetNextPacketSize(out packetFrames), "GetNextPacketSize");
                        if (packetFrames == 0) {
                            Thread.Sleep(5);
                            continue;
                        }
                        IntPtr data;
                        uint frames;
                        uint flags;
                        ulong devicePosition;
                        ulong qpcPosition;
                        Check(captureClient.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition), "GetBuffer");
                        try {
                            int sourceChannels = Math.Max(1, (int)fmt.nChannels);
                            int sourceBytesPerFrame = (int)fmt.nBlockAlign;
                            int sourceBits = (int)fmt.wBitsPerSample;
                            int sourceBytesPerSample = Math.Max(1, sourceBits / 8);
                            int frameBytes = targetChannels * 4;
                            int needed = checked((int)frames * frameBytes);
                            if (pcm.Length < needed) pcm = new byte[needed];
                            int dst = 0;
                            for (int frame = 0; frame < frames; frame++) {
                                for (int ch = 0; ch < targetChannels; ch++) {
                                    float sample = 0f;
                                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0) {
                                        int srcCh = Math.Min(ch, sourceChannels - 1);
                                        IntPtr src = IntPtr.Add(data, frame * sourceBytesPerFrame + srcCh * sourceBytesPerSample);
                                        sample = ReadSample(src, sourceBits, fmt.wFormatTag, mixFormatPtr);
                                    }
                                    byte[] bytes = BitConverter.GetBytes(sample);
                                    Buffer.BlockCopy(bytes, 0, pcm, dst, 4);
                                    dst += 4;
                                }
                            }
                            output.Write(pcm, 0, needed);
                            output.Flush();
                        }
                        finally {
                            Check(captureClient.ReleaseBuffer(frames), "ReleaseBuffer");
                        }
                    }
                }
                finally {
                    audioClient.Stop();
                    Marshal.FreeCoTaskMem(mixFormatPtr);
                    if (fallbackFormatPtr != IntPtr.Zero) Marshal.FreeHGlobal(fallbackFormatPtr);
                    Marshal.ReleaseComObject(captureClient);
                    Marshal.ReleaseComObject(audioClient);
                    Marshal.ReleaseComObject(device);
                    Marshal.ReleaseComObject(enumerator);
                }
            }
            finally {
                if (outputPath != "-") output.Dispose();
            }
        }

        static void Check(int hr, string stage) {
            if (hr < 0) {
                Exception inner = Marshal.GetExceptionForHR(hr);
                throw new InvalidOperationException(stage + " failed: 0x" + hr.ToString("X8") + " " + (inner == null ? "" : inner.Message), inner);
            }
        }

        static IntPtr MakeWaveFormatPcm(ushort channels, uint sampleRate, ushort bits) {
            WAVEFORMATEX fmt = new WAVEFORMATEX();
            fmt.wFormatTag = 1;
            fmt.nChannels = channels;
            fmt.nSamplesPerSec = sampleRate;
            fmt.wBitsPerSample = bits;
            fmt.nBlockAlign = (ushort)(channels * bits / 8);
            fmt.nAvgBytesPerSec = sampleRate * fmt.nBlockAlign;
            fmt.cbSize = 0;
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEFORMATEX)));
            Marshal.StructureToPtr(fmt, ptr, false);
            return ptr;
        }

        static float ReadSample(IntPtr source, int bits, ushort tag, IntPtr mixFormatPtr) {
            bool isFloat = tag == 3;
            if (tag == 65534) {
                WAVEFORMATEXTENSIBLE ext = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(mixFormatPtr);
                isFloat = ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            }
            if (isFloat && bits == 32) {
                return Math.Max(-1f, Math.Min(1f, (float)Marshal.PtrToStructure(source, typeof(float))));
            }
            if (bits == 16) {
                return Marshal.ReadInt16(source) / 32768f;
            }
            if (bits == 24) {
                int b0 = Marshal.ReadByte(source, 0);
                int b1 = Marshal.ReadByte(source, 1);
                int b2 = Marshal.ReadByte(source, 2);
                int value = b0 | (b1 << 8) | (b2 << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000);
                return Math.Max(-1f, Math.Min(1f, value / 8388608f));
            }
            if (bits == 32) {
                return Math.Max(-1f, Math.Min(1f, Marshal.ReadInt32(source) / 2147483648f));
            }
            return 0f;
        }
    }
}
"@

Add-Type -TypeDefinition $source
[LocalCastBridge.WasapiLoopbackCapture]::Capture($Output, $Seconds, $SampleRate, $Channels)
