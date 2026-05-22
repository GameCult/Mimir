namespace Mimir.Runtime.Synchronization;

public sealed record MimirChirpletTone(
    double StartSeconds,
    double DurationSeconds,
    double StartHz,
    double EndHz,
    double Gain = 1.0)
{
    public double CenterHz => (StartHz + EndHz) * 0.5;
}

public sealed record MimirChirpletBandResponse(
    double CenterHz,
    double Energy);

public sealed class MimirChirpletTimeline
{
    public const int SampleRate = 48_000;
    public const double SegmentSeconds = 0.5;
    public const double QueueLeadSeconds = 1.0;
    private const double FirstEventSeconds = 0.08;
    private const double EventStrideSeconds = 0.118;
    private const double MaxEventDurationSeconds = 0.110;
    private const double Gain = 0.095;

    private static readonly MimirChirpletTone[] AnalysisAtoms =
    [
        new(0.0, 0.050, 6_900.0, 7_500.0, 0.70),
        new(0.0, 0.055, 8_000.0, 8_900.0, 0.75),
        new(0.0, 0.060, 9_600.0, 8_800.0, 0.75),
        new(0.0, 0.060, 10_600.0, 11_700.0, 0.85),
        new(0.0, 0.065, 12_400.0, 13_500.0, 0.90),
        new(0.0, 0.070, 14_400.0, 15_800.0, 0.80),
    ];
    private static readonly double[] HarmonicRatios =
    [
        1.0,
        9.0 / 8.0,
        5.0 / 4.0,
        4.0 / 3.0,
        3.0 / 2.0,
        5.0 / 3.0,
        15.0 / 8.0,
        2.0,
        9.0 / 4.0,
        5.0 / 2.0,
    ];

    private readonly IReadOnlyList<float[]> atomKernels;

    private MimirChirpletTimeline()
    {
        atomKernels = AnalysisAtoms.Select(atom => RenderToneKernel(atom, SampleRate)).ToArray();
    }

    public static MimirChirpletTimeline Default { get; } = new();

    public float[] RenderSegmentMonoFloat(ulong segmentIndex)
    {
        var segmentStartSeconds = segmentIndex * SegmentSeconds;
        var samples = new float[(int)Math.Round(SegmentSeconds * SampleRate)];
        foreach (var tone in TonesOverlapping(segmentStartSeconds, SegmentSeconds))
        {
            AddTone(samples, SampleRate, tone, segmentStartSeconds);
        }

        var peak = samples.Select(Math.Abs).DefaultIfEmpty(0.0f).Max();
        if (peak <= 1.0e-9f)
        {
            return samples;
        }

        var scale = (float)(Gain / peak);
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] *= scale;
        }

        return samples;
    }

    public string RenderSegmentPcm16Base64(ulong segmentIndex)
    {
        var samples = RenderSegmentMonoFloat(segmentIndex);
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = (short)Math.Round(Math.Clamp(samples[index], -1.0f, 1.0f) * short.MaxValue);
            bytes[index * sizeof(short)] = (byte)(sample & 0xff);
            bytes[index * sizeof(short) + 1] = (byte)((sample >> 8) & 0xff);
        }

        return Convert.ToBase64String(bytes);
    }

    public float[] BuildTimelineEnergyTrace(ReadOnlySpan<float> samples, int sampleRate, int hopSamples)
    {
        var traces = new List<float[]>();
        for (var index = 0; index < AnalysisAtoms.Length; index++)
        {
            var atom = AnalysisAtoms[index];
            var kernel = sampleRate == SampleRate ? atomKernels[index] : RenderToneKernel(atom, sampleRate);
            var trace = BuildMatchedEnergyTrace(samples, kernel, hopSamples);
            if (trace.Length > 0)
            {
                traces.Add(trace);
            }
        }

        if (traces.Count == 0)
        {
            return [];
        }

        var length = traces.Min(trace => trace.Length);
        var output = new float[length];
        for (var frame = 0; frame < length; frame++)
        {
            var best = 0.0f;
            foreach (var trace in traces)
            {
                best = Math.Max(best, trace[frame]);
            }

            output[frame] = best;
        }

        return ContrastNormalize(output);
    }

    public IReadOnlyList<MimirChirpletBandResponse> EstimateBandResponse(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        var responses = new List<MimirChirpletBandResponse>(AnalysisAtoms.Length);
        for (var index = 0; index < AnalysisAtoms.Length; index++)
        {
            var atom = AnalysisAtoms[index];
            var kernel = sampleRate == SampleRate ? atomKernels[index] : RenderToneKernel(atom, sampleRate);
            var energy = MaxMatchedEnergy(samples, kernel, Math.Max(1, sampleRate / 1_000));
            responses.Add(new MimirChirpletBandResponse(atom.CenterHz, energy));
        }

        return responses;
    }

    private static IEnumerable<MimirChirpletTone> TonesOverlapping(double startSeconds, double durationSeconds)
    {
        var endSeconds = startSeconds + durationSeconds;
        var firstIndex = Math.Max(0, (long)Math.Floor((startSeconds - FirstEventSeconds - MaxEventDurationSeconds) / EventStrideSeconds) - 2);
        var lastIndex = Math.Max(firstIndex, (long)Math.Ceiling((endSeconds - FirstEventSeconds) / EventStrideSeconds) + 2);
        for (var eventIndex = firstIndex; eventIndex <= lastIndex; eventIndex++)
        {
            var tone = ToneForEvent((ulong)eventIndex);
            if (tone.StartSeconds < endSeconds && tone.StartSeconds + tone.DurationSeconds > startSeconds)
            {
                yield return tone;
            }
        }
    }

    private static MimirChirpletTone ToneForEvent(ulong eventIndex)
    {
        var seed = Mix(eventIndex);
        var startJitter = Unit(seed) * 0.034 - 0.017;
        var duration = 0.045 + Unit(seed >> 10) * 0.052;
        var octaveJitter = Unit(seed >> 20) * 0.028 - 0.014;
        var glideScale = 0.72 + Unit(seed >> 31) * 0.66;
        var gain = 0.72 + Unit(seed >> 42) * 0.34;
        var descending = ((seed >> 57) & 1UL) != 0;
        var degree = (int)((seed >> 58) % 10);
        var center = Math.Clamp(6_800.0 * HarmonicRatios[degree] * (1.0 + octaveJitter), 6_500.0, 16_300.0);
        var halfWidth = Math.Clamp(center * 0.035 * glideScale, 220.0, 820.0);
        var low = Math.Clamp(center - halfWidth, 6_300.0, 16_500.0);
        var high = Math.Clamp(center + halfWidth, 6_300.0, 16_500.0);
        var start = FirstEventSeconds + eventIndex * EventStrideSeconds + startJitter;
        return descending
            ? new MimirChirpletTone(start, duration, high, low, gain)
            : new MimirChirpletTone(start, duration, low, high, gain);
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static double Unit(ulong value) =>
        (value & ((1UL << 53) - 1)) / (double)(1UL << 53);

    private static float[] RenderToneKernel(MimirChirpletTone tone, int sampleRate)
    {
        var frameCount = Math.Max(1, (int)Math.Ceiling(tone.DurationSeconds * sampleRate));
        var samples = new float[frameCount];
        AddTone(samples, sampleRate, tone with { StartSeconds = 0.0 }, segmentStartSeconds: 0.0);
        return samples;
    }

    private static void AddTone(float[] samples, int sampleRate, MimirChirpletTone tone, double segmentStartSeconds)
    {
        var relativeStartSeconds = tone.StartSeconds - segmentStartSeconds;
        var startFrame = (int)Math.Round(relativeStartSeconds * sampleRate);
        var frameCount = Math.Max(1, (int)Math.Round(tone.DurationSeconds * sampleRate));
        for (var frame = 0; frame < frameCount; frame++)
        {
            var outputFrame = startFrame + frame;
            if (outputFrame < 0 || outputFrame >= samples.Length)
            {
                continue;
            }

            var normalized = frameCount <= 1 ? 1.0 : frame / (double)(frameCount - 1);
            var t = frame / (double)sampleRate;
            var envelope = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * normalized);
            var phase = 2.0 * Math.PI * (tone.StartHz * t + 0.5 * (tone.EndHz - tone.StartHz) * t * normalized);
            samples[outputFrame] += (float)(Math.Sin(phase) * envelope * tone.Gain);
        }
    }

    private static float[] BuildMatchedEnergyTrace(ReadOnlySpan<float> samples, ReadOnlySpan<float> kernel, int hopSamples)
    {
        if (samples.Length < kernel.Length || kernel.Length == 0)
        {
            return [];
        }

        var output = new float[1 + (samples.Length - kernel.Length) / hopSamples];
        for (var frame = 0; frame < output.Length; frame++)
        {
            var offset = frame * hopSamples;
            output[frame] = (float)MatchedEnergy(samples.Slice(offset, kernel.Length), kernel);
        }

        return output;
    }

    private static double MatchedEnergy(ReadOnlySpan<float> samples, ReadOnlySpan<float> kernel)
    {
        var count = Math.Min(samples.Length, kernel.Length);
        if (count == 0)
        {
            return 0.0;
        }

        var dot = 0.0;
        var sampleEnergy = 0.0;
        var kernelEnergy = 0.0;
        for (var index = 0; index < count; index++)
        {
            var sample = samples[index];
            var basis = kernel[index];
            dot += sample * basis;
            sampleEnergy += sample * sample;
            kernelEnergy += basis * basis;
        }

        var denominator = Math.Sqrt(sampleEnergy * kernelEnergy);
        return denominator > 1.0e-12 ? Math.Abs(dot) / denominator : 0.0;
    }

    private static double MaxMatchedEnergy(ReadOnlySpan<float> samples, ReadOnlySpan<float> kernel, int hopSamples)
    {
        if (samples.Length < kernel.Length || kernel.Length == 0)
        {
            return 0.0;
        }

        var best = 0.0;
        for (var offset = 0; offset <= samples.Length - kernel.Length; offset += hopSamples)
        {
            best = Math.Max(best, MatchedEnergy(samples.Slice(offset, kernel.Length), kernel));
        }

        return best;
    }

    private static float[] ContrastNormalize(float[] samples)
    {
        if (samples.Length == 0)
        {
            return samples;
        }

        var mean = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            mean += samples[index];
        }

        mean /= samples.Length;
        var variance = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            var centered = samples[index] - mean;
            variance += centered * centered;
        }

        var deviation = Math.Sqrt(variance / samples.Length);
        if (deviation <= 1.0e-12)
        {
            return samples;
        }

        var output = new float[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            var z = (samples[index] - mean) / deviation;
            output[index] = z > 0.0 ? (float)(z * z) : 0.0f;
        }

        return output;
    }
}
