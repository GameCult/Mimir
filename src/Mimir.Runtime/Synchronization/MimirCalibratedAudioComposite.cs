namespace Mimir.Runtime.Synchronization;

public sealed record MimirCalibratedAudioCompositeOptions(
    double MinimumConfidence = 0.05,
    double TargetBandEnergy = 0.70,
    double MinimumBandEnergy = 0.01,
    double MinimumBandGain = 0.20,
    double MaximumBandGain = 4.00,
    double NoiseFloorRms = 1.0e-5,
    double MinimumCoherence = 0.35,
    double IncoherentBandSuppression = 0.72);

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
    double NoiseRms,
    double AverageCoherence);

public sealed record MimirCalibratedAudioCompositeBandReport(
    double CenterHz,
    double Coherence,
    double Suppression,
    int SourceCount);

public sealed record MimirCalibratedAudioCompositeResult(
    int SampleRate,
    float[] Samples,
    IReadOnlyList<MimirCalibratedAudioCompositeSourceReport> SourceReports,
    IReadOnlyList<MimirCalibratedAudioCompositeBandReport> BandReports,
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
            return new MimirCalibratedAudioCompositeResult(0, [], [], [], 0.0, 0.0, 0.0, 0.0);
        }

        var sampleRate = accepted[0].SampleRate;
        if (accepted.Any(source => source.SampleRate != sampleRate))
        {
            throw new ArgumentException("Calibrated audio composite requires one sample rate. Resampling belongs to the native DSP actuator.");
        }

        var frameCount = accepted.Min(source => source.Samples.Length);
        var output = new float[frameCount];
        var reports = new List<MimirCalibratedAudioCompositeSourceReport>(accepted.Length);
        var prepared = new List<PreparedCompositeSource>(accepted.Length);
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
            sourceRmsTotal += RootMeanSquare(source.Samples.AsSpan(0, frameCount));
            beforeFlatnessTotal += beforeFlatness;
            afterFlatnessTotal += afterFlatness;
            prepared.Add(new PreparedCompositeSource(
                source.SourceId,
                corrected,
                weight,
                delaySamples,
                appliedGain,
                correctedBands,
                beforeFlatness,
                afterFlatness,
                confidence,
                source.NoiseRms,
                source.BandResponses));
        }

        var bandReports = ApplyCoherenceSuppression(prepared, sampleRate);
        foreach (var source in prepared)
        {
            totalWeight += source.Weight;
            for (var index = 0; index < frameCount; index++)
            {
                output[index] += (float)(source.Samples[index] * source.Weight);
            }

            reports.Add(new MimirCalibratedAudioCompositeSourceReport(
                source.SourceId,
                source.AppliedDelaySamples,
                source.AppliedGain,
                source.Weight,
                source.CorrectedBandCount,
                source.ResponseFlatnessBefore,
                source.ResponseFlatnessAfter,
                source.Confidence,
                source.NoiseRms,
                AverageSourceCoherence(source, bandReports)));
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
            bandReports,
            RootMeanSquare(output),
            sourceRmsTotal / reportCount,
            beforeFlatnessTotal / reportCount,
            afterFlatnessTotal / reportCount);
    }

    private IReadOnlyList<MimirCalibratedAudioCompositeBandReport> ApplyCoherenceSuppression(
        IReadOnlyList<PreparedCompositeSource> sources,
        int sampleRate)
    {
        if (sources.Count < 2)
        {
            return [];
        }

        var bandCenters = sources
            .SelectMany(source => source.BandResponses.Select(response => response.CenterHz))
            .Where(centerHz => centerHz > 0.0 && centerHz < sampleRate * 0.48)
            .DistinctBy(centerHz => Math.Round(12.0 * Math.Log2(centerHz / 110.0)))
            .Order()
            .ToArray();
        var reports = new List<MimirCalibratedAudioCompositeBandReport>(bandCenters.Length);
        foreach (var centerHz in bandCenters)
        {
            var measurements = sources
                .Select(source => new BandMeasurement(source, ToneCoefficient(source.Samples, sampleRate, centerHz)))
                .Where(measurement => measurement.Coefficient.Magnitude > 1.0e-9)
                .ToArray();
            if (measurements.Length < 2)
            {
                continue;
            }

            var coherence = PairwiseCoherence(measurements.Select(static measurement => measurement.Coefficient).ToArray());
            var suppression = Math.Clamp((options.MinimumCoherence - coherence) / Math.Max(1.0e-6, options.MinimumCoherence), 0.0, 1.0) *
                Math.Clamp(options.IncoherentBandSuppression, 0.0, 1.0);
            if (suppression > 0.0)
            {
                foreach (var measurement in measurements)
                {
                    AddBandGainDelta(measurement.Source.Samples, measurement.Source.Samples, sampleRate, centerHz, -suppression);
                }
            }

            reports.Add(new MimirCalibratedAudioCompositeBandReport(
                centerHz,
                coherence,
                suppression,
                measurements.Length));
        }

        return reports;
    }

    private static double AverageSourceCoherence(
        PreparedCompositeSource source,
        IReadOnlyList<MimirCalibratedAudioCompositeBandReport> reports)
    {
        var centers = source.BandResponses.Select(response => response.CenterHz).ToArray();
        var matched = reports
            .Where(report => centers.Any(center => BandsTooClose(center, report.CenterHz)))
            .Select(report => report.Coherence)
            .ToArray();
        return matched.Length == 0 ? 1.0 : matched.Average();
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

    private static ComplexToneCoefficient ToneCoefficient(ReadOnlySpan<float> samples, int sampleRate, double centerHz)
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
        return new ComplexToneCoefficient(cosine, sine);
    }

    private static double PairwiseCoherence(IReadOnlyList<ComplexToneCoefficient> coefficients)
    {
        var total = 0.0;
        var count = 0;
        for (var left = 0; left < coefficients.Count; left++)
        {
            for (var right = left + 1; right < coefficients.Count; right++)
            {
                var leftCoefficient = coefficients[left];
                var rightCoefficient = coefficients[right];
                var magnitude = leftCoefficient.Magnitude * rightCoefficient.Magnitude;
                if (magnitude <= 1.0e-12)
                {
                    continue;
                }

                var phaseAgreement = (leftCoefficient.Real * rightCoefficient.Real + leftCoefficient.Imaginary * rightCoefficient.Imaginary) / magnitude;
                var energyBalance = Math.Min(leftCoefficient.Magnitude, rightCoefficient.Magnitude) / Math.Max(leftCoefficient.Magnitude, rightCoefficient.Magnitude);
                total += Math.Clamp((phaseAgreement + 1.0) * 0.5, 0.0, 1.0) * Math.Sqrt(Math.Clamp(energyBalance, 0.0, 1.0));
                count++;
            }
        }

        return count == 0 ? 0.0 : total / count;
    }

    private static bool BandsTooClose(double leftHz, double rightHz)
    {
        if (leftHz <= 0.0 || rightHz <= 0.0)
        {
            return false;
        }

        return Math.Abs(Math.Log(leftHz / rightHz)) < 0.06;
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

    private sealed record PreparedCompositeSource(
        string SourceId,
        float[] Samples,
        double Weight,
        double AppliedDelaySamples,
        double AppliedGain,
        int CorrectedBandCount,
        double ResponseFlatnessBefore,
        double ResponseFlatnessAfter,
        double Confidence,
        double NoiseRms,
        IReadOnlyList<MimirChirpletBandResponse> BandResponses);

    private sealed record BandMeasurement(PreparedCompositeSource Source, ComplexToneCoefficient Coefficient);

    private readonly record struct ComplexToneCoefficient(double Real, double Imaginary)
    {
        public double Magnitude => Math.Sqrt(Real * Real + Imaginary * Imaginary);
    }
}
