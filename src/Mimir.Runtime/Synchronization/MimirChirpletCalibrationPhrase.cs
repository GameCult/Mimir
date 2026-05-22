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

public sealed class MimirChirpletCalibrationPhrase
{
    private readonly float[] kernel;
    private readonly IReadOnlyList<float[]> toneKernels;

    public MimirChirpletCalibrationPhrase(
        int sampleRate,
        double intervalSeconds,
        double firstFireSeconds,
        double gain,
        IReadOnlyList<MimirChirpletTone> tones)
    {
        SampleRate = sampleRate;
        IntervalSeconds = intervalSeconds;
        FirstFireSeconds = firstFireSeconds;
        Gain = gain;
        Tones = tones;
        DurationSeconds = tones.Count == 0
            ? 0.0
            : tones.Max(tone => tone.StartSeconds + tone.DurationSeconds);
        kernel = RenderMonoFloat(sampleRate);
        toneKernels = tones.Select(tone => RenderToneKernel(tone, sampleRate, DurationSeconds)).ToArray();
    }

    public static MimirChirpletCalibrationPhrase Default { get; } = new(
        sampleRate: 48_000,
        intervalSeconds: 1.5,
        firstFireSeconds: 0.5,
        gain: 0.125,
        tones:
        [
            new(0.000, 0.055, 7_680.0, 8_320.0),
            new(0.085, 0.055, 9_600.0, 10_400.0),
            new(0.170, 0.055, 11_520.0, 12_480.0),
            new(0.255, 0.055, 15_360.0, 16_000.0),
        ]);

    public int SampleRate { get; }

    public double IntervalSeconds { get; }

    public double FirstFireSeconds { get; }

    public double Gain { get; }

    public double DurationSeconds { get; }

    public IReadOnlyList<MimirChirpletTone> Tones { get; }

    public string RenderPcm16Base64()
    {
        var samples = RenderMonoFloat(SampleRate);
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = (short)Math.Round(Math.Clamp(samples[index], -1.0f, 1.0f) * short.MaxValue);
            bytes[index * sizeof(short)] = (byte)(sample & 0xff);
            bytes[index * sizeof(short) + 1] = (byte)((sample >> 8) & 0xff);
        }

        return Convert.ToBase64String(bytes);
    }

    public float[] BuildEnergyTrace(ReadOnlySpan<float> samples, int sampleRate, int hopSamples)
    {
        var analysisKernel = sampleRate == SampleRate ? kernel : RenderMonoFloat(sampleRate);
        return BuildMatchedEnergyTrace(samples, analysisKernel, hopSamples);
    }

    public IReadOnlyList<MimirChirpletBandResponse> EstimateBandResponse(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0 || Tones.Count == 0)
        {
            return [];
        }

        var responses = new List<MimirChirpletBandResponse>(Tones.Count);
        for (var index = 0; index < Tones.Count; index++)
        {
            var tone = Tones[index];
            var toneKernel = sampleRate == SampleRate
                ? toneKernels[index]
                : RenderToneKernel(tone, sampleRate, DurationSeconds);
            var energy = MaxMatchedEnergy(samples, toneKernel, Math.Max(1, sampleRate / 1_000));
            responses.Add(new MimirChirpletBandResponse(tone.CenterHz, energy));
        }

        return responses;
    }

    private float[] RenderMonoFloat(int sampleRate)
    {
        var frameCount = Math.Max(1, (int)Math.Ceiling(DurationSeconds * sampleRate));
        var samples = new float[frameCount];
        foreach (var tone in Tones)
        {
            AddTone(samples, sampleRate, tone);
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

    private static float[] RenderToneKernel(MimirChirpletTone tone, int sampleRate, double phraseDurationSeconds)
    {
        var frameCount = Math.Max(1, (int)Math.Ceiling(phraseDurationSeconds * sampleRate));
        var samples = new float[frameCount];
        AddTone(samples, sampleRate, tone);
        return samples;
    }

    private static void AddTone(float[] samples, int sampleRate, MimirChirpletTone tone)
    {
        var startFrame = Math.Max(0, (int)Math.Round(tone.StartSeconds * sampleRate));
        var frameCount = Math.Max(1, (int)Math.Round(tone.DurationSeconds * sampleRate));
        for (var frame = 0; frame < frameCount && startFrame + frame < samples.Length; frame++)
        {
            var normalized = frameCount <= 1 ? 1.0 : frame / (double)(frameCount - 1);
            var t = frame / (double)sampleRate;
            var envelope = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * normalized);
            var phase = 2.0 * Math.PI * (tone.StartHz * t + 0.5 * (tone.EndHz - tone.StartHz) * t * normalized);
            samples[startFrame + frame] += (float)(Math.Sin(phase) * envelope * tone.Gain);
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

        return ContrastNormalize(output);
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
