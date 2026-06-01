namespace Mimir.Runtime.Synchronization;

public sealed record MimirCalibratedAudioCompositeOptions(
    double MinimumConfidence = 0.05,
    double TargetBandEnergy = 0.70,
    double MinimumBandEnergy = 0.01,
    double MinimumBandGain = 0.20,
    double MaximumBandGain = 4.00,
    double NoiseFloorRms = 1.0e-5);

public sealed record MimirCalibratedAudioCompositeSource(
    string SourceId,
    int SampleRate,
    float[] Samples,
    MimirAudioSynchronizationState? SynchronizationState,
    IReadOnlyList<MimirChirpletBandResponse> BandResponses,
    double NoiseRms = 0.0);

public sealed record MimirCalibratedAudioCompositeSourceReport(
    string SourceId,
    double AppliedDelaySamples,
    double AppliedGain,
    double Weight,
    int CorrectedBandCount,
    double ResponseFlatnessBefore,
    double ResponseFlatnessAfter,
    double Confidence,
    double NoiseRms);

public sealed record MimirCalibratedAudioCompositeResult(
    int SampleRate,
    float[] Samples,
    IReadOnlyList<MimirCalibratedAudioCompositeSourceReport> SourceReports,
    double CompositeRms,
    double SourceMeanRms,
    double ResponseFlatnessBefore,
    double ResponseFlatnessAfter);

public sealed class MimirCalibratedAudioCompositeBuilder(MimirCalibratedAudioCompositeOptions? options = null)
{
    private readonly MimirCalibratedAudioCompositeOptions options = options ?? new();

    public MimirCalibratedAudioCompositeResult Build(IReadOnlyList<MimirCalibratedAudioCompositeSource> sources)
    {
        var accepted = sources
            .Where(source => source.Samples.Length > 0)
            .ToArray();
        if (accepted.Length == 0)
        {
            return new MimirCalibratedAudioCompositeResult(0, [], [], 0.0, 0.0, 0.0, 0.0);
        }

        var sampleRate = accepted[0].SampleRate;
        if (accepted.Any(source => source.SampleRate != sampleRate))
        {
            throw new ArgumentException("Calibrated audio composite requires one sample rate. Resampling belongs to the native DSP actuator.");
        }

        var frameCount = accepted.Min(source => source.Samples.Length);
        var output = new float[frameCount];
        var reports = new List<MimirCalibratedAudioCompositeSourceReport>(accepted.Length);
        var totalWeight = 0.0;
        var sourceRmsTotal = 0.0;
        var beforeFlatnessTotal = 0.0;
        var afterFlatnessTotal = 0.0;

        foreach (var source in accepted)
        {
            var confidence = source.SynchronizationState?.Confidence ?? 1.0;
            if (confidence < options.MinimumConfidence)
            {
                continue;
            }

            var delaySamples = source.SynchronizationState?.SmoothedDelaySamples ?? 0.0;
            var aligned = ApplyFractionalDelay(source.Samples, -delaySamples, frameCount);
            var beforeFlatness = ResponseFlatness(source.BandResponses);
            var corrected = ApplySparseBandEqualization(aligned, sampleRate, source.BandResponses, out var correctedBands);
            var afterResponses = EstimateCorrectedResponses(source.BandResponses);
            var afterFlatness = ResponseFlatness(afterResponses);
            var appliedGain = OverallGain(source.BandResponses);
            if (Math.Abs(appliedGain - 1.0) > 1.0e-6)
            {
                for (var index = 0; index < corrected.Length; index++)
                {
                    corrected[index] *= (float)appliedGain;
                }
            }

            var noise = Math.Max(options.NoiseFloorRms, source.NoiseRms);
            var responseWeight = Math.Clamp(afterFlatness <= 0.0 ? 1.0 : afterFlatness, 0.10, 1.0);
            var weight = confidence * responseWeight / (noise * noise);
            totalWeight += weight;
            sourceRmsTotal += RootMeanSquare(source.Samples.AsSpan(0, frameCount));
            beforeFlatnessTotal += beforeFlatness;
            afterFlatnessTotal += afterFlatness;
            for (var index = 0; index < frameCount; index++)
            {
                output[index] += (float)(corrected[index] * weight);
            }

            reports.Add(new MimirCalibratedAudioCompositeSourceReport(
                source.SourceId,
                delaySamples,
                appliedGain,
                weight,
                correctedBands,
                beforeFlatness,
                afterFlatness,
                confidence,
                source.NoiseRms));
        }

        if (totalWeight > 0.0)
        {
            for (var index = 0; index < output.Length; index++)
            {
                output[index] = (float)Math.Clamp(output[index] / totalWeight, -1.0, 1.0);
            }
        }

        var reportCount = Math.Max(1, reports.Count);
        return new MimirCalibratedAudioCompositeResult(
            sampleRate,
            output,
            reports,
            RootMeanSquare(output),
            sourceRmsTotal / reportCount,
            beforeFlatnessTotal / reportCount,
            afterFlatnessTotal / reportCount);
    }

    private float[] ApplySparseBandEqualization(
        ReadOnlySpan<float> samples,
        int sampleRate,
        IReadOnlyList<MimirChirpletBandResponse> responses,
        out int correctedBands)
    {
        var output = samples.ToArray();
        correctedBands = 0;
        foreach (var response in responses)
        {
            if (response.CenterHz <= 0.0 ||
                response.CenterHz >= sampleRate * 0.48 ||
                response.Energy < options.MinimumBandEnergy)
            {
                continue;
            }

            var gain = Math.Clamp(
                Math.Sqrt(options.TargetBandEnergy / Math.Max(options.MinimumBandEnergy, response.Energy)),
                options.MinimumBandGain,
                options.MaximumBandGain);
            if (Math.Abs(gain - 1.0) < 0.02)
            {
                continue;
            }

            AddBandGainDelta(output, samples, sampleRate, response.CenterHz, gain - 1.0);
            correctedBands++;
        }

        return output;
    }

    private static void AddBandGainDelta(float[] output, ReadOnlySpan<float> samples, int sampleRate, double centerHz, double gainDelta)
    {
        var sine = 0.0;
        var cosine = 0.0;
        var omega = 2.0 * Math.PI * centerHz / sampleRate;
        for (var index = 0; index < samples.Length; index++)
        {
            var phase = omega * index;
            sine += samples[index] * Math.Sin(phase);
            cosine += samples[index] * Math.Cos(phase);
        }

        var scale = 2.0 / Math.Max(1, samples.Length);
        sine *= scale;
        cosine *= scale;
        for (var index = 0; index < output.Length; index++)
        {
            var phase = omega * index;
            var component = sine * Math.Sin(phase) + cosine * Math.Cos(phase);
            output[index] = (float)Math.Clamp(output[index] + component * gainDelta, -4.0, 4.0);
        }
    }

    private double OverallGain(IReadOnlyList<MimirChirpletBandResponse> responses)
    {
        var usable = responses
            .Where(response => response.Energy >= options.MinimumBandEnergy)
            .Select(response => response.Energy)
            .Order()
            .ToArray();
        if (usable.Length == 0)
        {
            return 1.0;
        }

        var median = usable[usable.Length / 2];
        return Math.Clamp(
            Math.Sqrt(options.TargetBandEnergy / Math.Max(options.MinimumBandEnergy, median)),
            options.MinimumBandGain,
            options.MaximumBandGain);
    }

    private IReadOnlyList<MimirChirpletBandResponse> EstimateCorrectedResponses(IReadOnlyList<MimirChirpletBandResponse> responses) =>
        responses
            .Select(response =>
            {
                if (response.Energy < options.MinimumBandEnergy)
                {
                    return response;
                }

                var gain = Math.Clamp(
                    Math.Sqrt(options.TargetBandEnergy / Math.Max(options.MinimumBandEnergy, response.Energy)),
                    options.MinimumBandGain,
                    options.MaximumBandGain);
                return response with { Energy = response.Energy * gain * gain };
            })
            .ToArray();

    private static float[] ApplyFractionalDelay(ReadOnlySpan<float> source, double delaySamples, int frameCount)
    {
        var delayed = new float[frameCount];
        for (var index = 0; index < delayed.Length; index++)
        {
            var sourcePosition = index - delaySamples;
            if (sourcePosition < 0.0 || sourcePosition >= source.Length - 1)
            {
                continue;
            }

            var left = (int)Math.Floor(sourcePosition);
            var fraction = sourcePosition - left;
            delayed[index] = (float)(source[left] + (source[left + 1] - source[left]) * fraction);
        }

        return delayed;
    }

    private static double RootMeanSquare(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            sum += samples[index] * samples[index];
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private static double ResponseFlatness(IReadOnlyList<MimirChirpletBandResponse> responses)
    {
        var energies = responses
            .Where(response => response.Energy > 1.0e-9)
            .Select(response => Math.Log(response.Energy))
            .ToArray();
        if (energies.Length <= 1)
        {
            return energies.Length;
        }

        var mean = energies.Average();
        var variance = energies.Sum(value => (value - mean) * (value - mean)) / energies.Length;
        return 1.0 / (1.0 + Math.Sqrt(variance));
    }
}
