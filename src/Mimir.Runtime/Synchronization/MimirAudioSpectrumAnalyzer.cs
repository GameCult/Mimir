using System.Buffers.Binary;
using System.Numerics;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirAudioSpectrumBin(double FrequencyHz, double Decibels, double Magnitude);

public sealed record MimirAudioSpectrumSnapshot(
    string SourceId,
    int SampleRate,
    int FftSize,
    int WindowSamples,
    double Rms,
    double Peak,
    double NoiseFloorDb,
    IReadOnlyList<MimirAudioSpectrumBin> Peaks,
    IReadOnlyList<double> BandDecibels,
    long EdgeNs);

public sealed class MimirAudioSpectrumAnalyzer
{
    private const int MinimumFftSize = 1024;
    private const int MaximumFftSize = 32768;
    private readonly int fftSize;
    private readonly int displayBandCount;
    private readonly Complex[] spectrum;

    public MimirAudioSpectrumAnalyzer(int fftSize = 8192, int displayBandCount = 48)
    {
        this.fftSize = Math.Clamp(NextPowerOfTwo(Math.Max(MinimumFftSize, fftSize)), MinimumFftSize, MaximumFftSize);
        this.displayBandCount = Math.Clamp(displayBandCount, 16, 96);
        spectrum = new Complex[this.fftSize];
    }

    public IReadOnlyList<MimirAudioSpectrumSnapshot> Analyze(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        string referenceSourceId,
        int maxNonReferenceSources = 3)
    {
        var audioBuffers = buffers
            .Where(buffer => buffer.Descriptor.Kind == MimirStreamKind.Audio && buffer.Latest?.AudioBlock != null)
            .OrderBy(buffer => string.Equals(buffer.Descriptor.SourceId, referenceSourceId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal)
            .ToArray();
        if (audioBuffers.Length == 0)
        {
            return [];
        }

        var snapshots = new List<MimirAudioSpectrumSnapshot>();
        var nonReferenceCount = 0;
        foreach (var buffer in audioBuffers)
        {
            var isReference = string.Equals(buffer.Descriptor.SourceId, referenceSourceId, StringComparison.Ordinal);
            if (!isReference && nonReferenceCount++ >= maxNonReferenceSources)
            {
                continue;
            }

            var snapshot = AnalyzeBuffer(buffer);
            if (snapshot != null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    public MimirAudioSpectrumSnapshot? AnalyzeSamples(
        string sourceId,
        ReadOnlySpan<float> samples,
        int sampleRate)
    {
        if (samples.Length < MinimumFftSize / 2 || sampleRate <= 0)
        {
            return null;
        }

        Array.Clear(spectrum);
        var written = Math.Min(samples.Length, fftSize);
        var window = samples[^written..];
        for (var index = 0; index < written; index++)
        {
            spectrum[index] = new Complex(window[index], 0.0);
        }

        return AnalyzePreparedSpectrum(sourceId, sampleRate, written, edgeNs: 0);
    }

    private MimirAudioSpectrumSnapshot? AnalyzeBuffer(MimirRollingStreamBuffer buffer)
    {
        var latestBlock = buffer.Latest?.AudioBlock;
        if (latestBlock == null || latestBlock.Channels <= 0 || latestBlock.SampleRate <= 0)
        {
            return null;
        }

        Array.Clear(spectrum);
        var written = FillLatestMonoWindow(buffer, latestBlock, spectrum);
        if (written < MinimumFftSize / 2)
        {
            return null;
        }

        return AnalyzePreparedSpectrum(buffer.Descriptor.SourceId, latestBlock.SampleRate, written, buffer.EdgeNs);
    }

    private MimirAudioSpectrumSnapshot AnalyzePreparedSpectrum(
        string sourceId,
        int sampleRate,
        int written,
        long edgeNs)
    {
        var rms = 0.0;
        var peak = 0.0;
        for (var index = 0; index < written; index++)
        {
            var sample = spectrum[index].Real;
            rms += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        rms = Math.Sqrt(rms / Math.Max(1, written));
        ApplyHannWindow(spectrum, written);
        FastFourierTransform(spectrum, inverse: false);

        var magnitudes = new double[fftSize / 2];
        for (var index = 1; index < magnitudes.Length; index++)
        {
            magnitudes[index] = spectrum[index].Magnitude / Math.Max(1, written);
        }

        var bands = BuildLogBands(magnitudes, sampleRate);
        var peaks = FindPeaks(magnitudes, sampleRate, count: 6);
        var floor = bands.Count == 0 ? -120.0 : bands.OrderBy(value => value).ElementAt(Math.Clamp(bands.Count / 4, 0, bands.Count - 1));
        return new MimirAudioSpectrumSnapshot(
            sourceId,
            sampleRate,
            fftSize,
            written,
            rms,
            peak,
            floor,
            peaks,
            bands,
            edgeNs);
    }

    private int FillLatestMonoWindow(
        MimirRollingStreamBuffer buffer,
        MimirAudioBlockDescriptor latestBlock,
        Complex[] destination)
    {
        var write = destination.Length;
        foreach (var sample in buffer.Snapshot()
                     .Where(sample => sample.AudioBlock != null && !sample.Data.IsEmpty)
                     .Reverse())
        {
            var block = sample.AudioBlock!;
            if (!IsSupportedPcmFormat(block.SampleFormat) || block.Channels <= 0 || block.SampleRate != latestBlock.SampleRate)
            {
                continue;
            }

            var bytesPerSample = BytesPerSample(block.SampleFormat);
            var frameCount = sample.Data.Length / (bytesPerSample * block.Channels);
            var data = sample.Data.Span;
            for (var frame = frameCount - 1; frame >= 0 && write > 0; frame--)
            {
                var offset = frame * block.Channels * bytesPerSample;
                destination[--write] = new Complex(ReadPcmSample(data.Slice(offset, bytesPerSample), block.SampleFormat), 0.0);
            }

            if (write == 0)
            {
                break;
            }
        }

        var written = destination.Length - write;
        if (write > 0 && written > 0)
        {
            Array.Copy(destination, write, destination, 0, written);
            Array.Clear(destination, written, destination.Length - written);
        }

        return written;
    }

    private IReadOnlyList<double> BuildLogBands(IReadOnlyList<double> magnitudes, int sampleRate)
    {
        var bands = new double[displayBandCount];
        var nyquist = sampleRate * 0.5;
        var minHz = 40.0;
        var maxHz = Math.Max(minHz * 2.0, nyquist);
        for (var band = 0; band < bands.Length; band++)
        {
            var start = band / (double)bands.Length;
            var end = (band + 1) / (double)bands.Length;
            var lowHz = minHz * Math.Pow(maxHz / minHz, start);
            var highHz = minHz * Math.Pow(maxHz / minHz, end);
            var firstBin = Math.Clamp((int)Math.Floor(lowHz * fftSize / sampleRate), 1, magnitudes.Count - 1);
            var lastBin = Math.Clamp((int)Math.Ceiling(highHz * fftSize / sampleRate), firstBin, magnitudes.Count - 1);
            var sum = 0.0;
            var count = 0;
            for (var bin = firstBin; bin <= lastBin; bin++)
            {
                sum += magnitudes[bin] * magnitudes[bin];
                count++;
            }

            bands[band] = ToDecibels(Math.Sqrt(sum / Math.Max(1, count)));
        }

        return bands;
    }

    private IReadOnlyList<MimirAudioSpectrumBin> FindPeaks(IReadOnlyList<double> magnitudes, int sampleRate, int count)
    {
        var peaks = new List<MimirAudioSpectrumBin>();
        var minBin = Math.Max(1, (int)Math.Round(20.0 * fftSize / sampleRate));
        var maxBin = Math.Min(magnitudes.Count - 2, (int)Math.Round(Math.Min(sampleRate * 0.48, 24000.0) * fftSize / sampleRate));
        for (var bin = minBin; bin <= maxBin; bin++)
        {
            var magnitude = magnitudes[bin];
            if (magnitude <= magnitudes[bin - 1] || magnitude < magnitudes[bin + 1])
            {
                continue;
            }

            peaks.Add(new MimirAudioSpectrumBin(bin * sampleRate / (double)fftSize, ToDecibels(magnitude), magnitude));
        }

        return peaks
            .OrderByDescending(peak => peak.Magnitude)
            .Take(count)
            .OrderBy(peak => peak.FrequencyHz)
            .ToArray();
    }

    private static void ApplyHannWindow(Complex[] values, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / Math.Max(1, count - 1));
            values[index] *= window;
        }
    }

    private static bool IsSupportedPcmFormat(MimirAudioSampleFormat sampleFormat) =>
        sampleFormat is MimirAudioSampleFormat.Float32 or
            MimirAudioSampleFormat.Int16 or
            MimirAudioSampleFormat.Int24 or
            MimirAudioSampleFormat.Int32;

    private static int BytesPerSample(MimirAudioSampleFormat sampleFormat) =>
        sampleFormat switch
        {
            MimirAudioSampleFormat.Float32 => sizeof(float),
            MimirAudioSampleFormat.Int16 => sizeof(short),
            MimirAudioSampleFormat.Int24 => 3,
            MimirAudioSampleFormat.Int32 => sizeof(int),
            _ => 0,
        };

    private static float ReadPcmSample(ReadOnlySpan<byte> data, MimirAudioSampleFormat sampleFormat) =>
        sampleFormat switch
        {
            MimirAudioSampleFormat.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data)),
            MimirAudioSampleFormat.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data) / 32768.0f,
            MimirAudioSampleFormat.Int24 => ReadInt24LittleEndian(data) / 8388608.0f,
            MimirAudioSampleFormat.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648.0f,
            _ => 0.0f,
        };

    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> data)
    {
        var value = data[0] | (data[1] << 8) | (data[2] << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xff000000) : value;
    }

    private static double ToDecibels(double value) => 20.0 * Math.Log10(Math.Max(value, 1.0e-12));

    private static int NextPowerOfTwo(int value)
    {
        var power = 1;
        while (power < value)
        {
            power <<= 1;
        }

        return power;
    }

    private static void FastFourierTransform(Complex[] values, bool inverse)
    {
        var n = values.Length;
        for (int index = 1, swap = 0; index < n; index++)
        {
            var bit = n >> 1;
            for (; (swap & bit) != 0; bit >>= 1)
            {
                swap ^= bit;
            }

            swap ^= bit;
            if (index < swap)
            {
                (values[index], values[swap]) = (values[swap], values[index]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2.0 * Math.PI / length * (inverse ? 1.0 : -1.0);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += length)
            {
                var rotation = Complex.One;
                var half = length >> 1;
                for (var offset = 0; offset < half; offset++)
                {
                    var even = values[start + offset];
                    var odd = values[start + offset + half] * rotation;
                    values[start + offset] = even + odd;
                    values[start + offset + half] = even - odd;
                    rotation *= step;
                }
            }
        }

        if (!inverse)
        {
            return;
        }

        for (var index = 0; index < n; index++)
        {
            values[index] /= n;
        }
    }
}
