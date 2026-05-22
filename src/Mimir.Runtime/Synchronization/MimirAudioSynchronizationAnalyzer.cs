using System.Buffers.Binary;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirAudioSynchronizationAnalyzer
{
    private const int MaxWindowSamples = 48_000 * 5;
    private const int MaxLagSamples = 2_400;
    private const int ChirpletHopSamples = 64;
    private const double ChirpletDurationSeconds = 0.08;
    private const double ChirpletStartHz = 9_000.0;
    private const double ChirpletEndHz = 16_000.0;

    public IReadOnlyList<MimirAudioSynchronizationReport> Analyze(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        string referenceSourceId)
    {
        var audioBuffers = buffers
            .Where(buffer => buffer.Descriptor.Kind == MimirStreamKind.Audio)
            .ToArray();
        var reference = audioBuffers.FirstOrDefault(buffer =>
            string.Equals(buffer.Descriptor.SourceId, referenceSourceId, StringComparison.Ordinal));
        if (reference == null)
        {
            return [];
        }

        var referenceLatest = reference.Latest;
        if (referenceLatest?.AudioBlock == null)
        {
            return [];
        }

        var reports = new List<MimirAudioSynchronizationReport>();
        foreach (var buffer in audioBuffers)
        {
            if (ReferenceEquals(buffer, reference))
            {
                continue;
            }

            var commonEndNs = Math.Min(referenceLatest.Value.TimestampNs, buffer.Latest?.TimestampNs ?? 0);
            var referenceSamples = ExtractMonoWindow(reference, out var referenceBlock, commonEndNs);
            var candidateSamples = ExtractMonoWindow(buffer, out var candidateBlock, commonEndNs);
            if (candidateSamples.Length == 0 || candidateBlock == null)
            {
                continue;
            }

            if (referenceSamples.Length == 0 || referenceBlock == null)
            {
                continue;
            }

            if (candidateBlock.SampleFormat != MimirAudioSampleFormat.Float32 ||
                referenceBlock.SampleFormat != MimirAudioSampleFormat.Float32)
            {
                continue;
            }

            candidateSamples = ResampleToRate(candidateSamples, candidateBlock.SampleRate, referenceBlock.SampleRate);

            var compared = Math.Min(referenceSamples.Length, candidateSamples.Length);
            if (compared < 256)
            {
                continue;
            }

            var referenceSync = BuildChirpletEnergyTrace(referenceSamples.AsSpan(^compared..), referenceBlock.SampleRate);
            var candidateSync = BuildChirpletEnergyTrace(candidateSamples.AsSpan(^compared..), referenceBlock.SampleRate);
            if (referenceSync.Length < 16 || candidateSync.Length < 16)
            {
                continue;
            }

            var maxLag = Math.Min(MaxLagSamples / ChirpletHopSamples, Math.Min(referenceSync.Length, candidateSync.Length) / 2);
            var (delaySamples, confidence) = EstimateDelay(referenceSync, candidateSync, maxLag);
            delaySamples *= ChirpletHopSamples;
            reports.Add(new MimirAudioSynchronizationReport(
                reference.Descriptor.SourceId,
                buffer.Descriptor.SourceId,
                referenceBlock.SampleRate,
                delaySamples,
                delaySamples * 1000.0 / referenceBlock.SampleRate,
                confidence,
                compared,
                reference.Latest?.Sequence ?? 0,
                buffer.Latest?.Sequence ?? 0));
        }

        return reports;
    }

    public MimirAlignedAudioFrame? BuildAlignedFrame(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        string referenceSourceId,
        int frameCount = 4_800,
        double minimumConfidence = 0.10)
    {
        var audioBuffers = buffers
            .Where(buffer => buffer.Descriptor.Kind == MimirStreamKind.Audio)
            .ToDictionary(buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal);
        if (!audioBuffers.TryGetValue(referenceSourceId, out var reference))
        {
            return null;
        }

        var referenceSamples = ExtractMonoWindow(reference, out var referenceBlock);
        if (referenceSamples.Length < frameCount || referenceBlock == null)
        {
            return null;
        }

        var reports = Analyze(audioBuffers.Values, referenceSourceId)
            .Where(report => report.Confidence >= minimumConfidence)
            .ToDictionary(report => report.SourceId, StringComparer.Ordinal);
        var alignedReports = reports.Values
            .OrderBy(report => report.SourceId, StringComparer.Ordinal)
            .ToArray();
        var alignedBuffers = alignedReports
            .Select(report => audioBuffers.TryGetValue(report.SourceId, out var buffer) ? buffer : null)
            .Where(buffer => buffer != null)
            .Cast<MimirRollingStreamBuffer>()
            .ToArray();
        var commonEndNs = new[] { reference }
            .Concat(alignedBuffers)
            .Select(buffer => buffer.Latest?.TimestampNs ?? 0)
            .Where(timestamp => timestamp > 0)
            .DefaultIfEmpty(0)
            .Min();
        if (commonEndNs <= 0)
        {
            return null;
        }

        referenceSamples = ExtractMonoWindow(reference, out referenceBlock, commonEndNs);
        if (referenceSamples.Length < frameCount || referenceBlock == null)
        {
            return null;
        }

        var maxPositiveDelay = Math.Max(0, alignedReports.Select(report => report.DelaySamples).DefaultIfEmpty(0).Max());
        var channels = new List<MimirAlignedAudioChannel>
        {
            new(referenceSourceId, 0, 1.0),
        };
        var samples = new List<float[]>
        {
            TailEndingBefore(referenceSamples, frameCount, maxPositiveDelay),
        };

        foreach (var report in alignedReports)
        {
            if (!audioBuffers.TryGetValue(report.SourceId, out var buffer))
            {
                continue;
            }

            var candidateSamples = ExtractMonoWindow(buffer, out var candidateBlock, commonEndNs);
            if (candidateBlock == null ||
                candidateBlock.SampleFormat != MimirAudioSampleFormat.Float32)
            {
                continue;
            }

            candidateSamples = ResampleToRate(candidateSamples, candidateBlock.SampleRate, referenceBlock.SampleRate);
            var trimSamples = maxPositiveDelay - report.DelaySamples;
            if (trimSamples < 0 || candidateSamples.Length < frameCount + trimSamples)
            {
                continue;
            }

            samples.Add(TailEndingBefore(candidateSamples, frameCount, trimSamples));
            channels.Add(new MimirAlignedAudioChannel(report.SourceId, report.DelaySamples, report.Confidence));
        }

        return new MimirAlignedAudioFrame(
            referenceSourceId,
            referenceBlock.SampleRate,
            frameCount,
            channels,
            samples.ToArray());
    }

    private static float[] ExtractMonoWindow(
        MimirRollingStreamBuffer buffer,
        out MimirAudioBlockDescriptor? latestBlock,
        long endNs = long.MaxValue)
    {
        latestBlock = buffer.Latest?.AudioBlock;
        if (latestBlock == null || latestBlock.SampleFormat != MimirAudioSampleFormat.Float32)
        {
            return [];
        }

        var samples = new List<float>(MaxWindowSamples);
        foreach (var sample in buffer.Snapshot()
                     .Where(sample => sample.AudioBlock != null && !sample.Data.IsEmpty && sample.TimestampNs <= endNs)
                     .Reverse())
        {
            var block = sample.AudioBlock!;
            if (block.SampleFormat != MimirAudioSampleFormat.Float32 || block.Channels <= 0)
            {
                continue;
            }

            var mono = ExtractFirstChannel(sample.Data.Span, block.Channels);
            for (var index = mono.Length - 1; index >= 0 && samples.Count < MaxWindowSamples; index--)
            {
                samples.Add(mono[index]);
            }

            if (samples.Count >= MaxWindowSamples)
            {
                break;
            }
        }

        samples.Reverse();
        return samples.ToArray();
    }

    private static float[] ExtractFirstChannel(ReadOnlySpan<byte> data, int channels)
    {
        const int bytesPerSample = sizeof(float);
        var frameCount = data.Length / (bytesPerSample * channels);
        var samples = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * channels * bytesPerSample;
            var bits = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, bytesPerSample));
            samples[frame] = BitConverter.Int32BitsToSingle(bits);
        }

        return samples;
    }

    private static float[] TailEndingBefore(float[] samples, int frameCount, int trimSamples)
    {
        var output = new float[frameCount];
        var endExclusive = samples.Length - trimSamples;
        var start = endExclusive - frameCount;
        if (start < 0)
        {
            return output;
        }

        Array.Copy(samples, start, output, 0, frameCount);
        return output;
    }

    private static float[] ResampleToRate(float[] samples, int sourceRate, int targetRate)
    {
        if (samples.Length == 0 || sourceRate == targetRate)
        {
            return samples;
        }

        var outputLength = Math.Max(1, (int)Math.Round(samples.Length * (double)targetRate / sourceRate));
        var output = new float[outputLength];
        var scale = sourceRate / (double)targetRate;
        for (var index = 0; index < output.Length; index++)
        {
            var sourcePosition = index * scale;
            var left = (int)Math.Floor(sourcePosition);
            if (left >= samples.Length - 1)
            {
                output[index] = samples[^1];
                continue;
            }

            var fraction = sourcePosition - left;
            output[index] = (float)(samples[left] + (samples[left + 1] - samples[left]) * fraction);
        }

        return output;
    }

    private static float[] BuildChirpletEnergyTrace(ReadOnlySpan<float> samples, int sampleRate)
    {
        var kernelLength = Math.Max(8, (int)Math.Round(sampleRate * ChirpletDurationSeconds));
        if (samples.Length < kernelLength)
        {
            return [];
        }

        var output = new float[1 + (samples.Length - kernelLength) / ChirpletHopSamples];
        for (var frame = 0; frame < output.Length; frame++)
        {
            var offset = frame * ChirpletHopSamples;
            var real = 0.0;
            var imag = 0.0;
            var energy = 0.0;
            for (var index = 0; index < kernelLength; index++)
            {
                var normalized = kernelLength <= 1 ? 1.0 : index / (double)(kernelLength - 1);
                var time = index / (double)sampleRate;
                var envelope = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * normalized);
                var phase = 2.0 * Math.PI * (ChirpletStartHz * time + 0.5 * (ChirpletEndHz - ChirpletStartHz) * time * normalized);
                var sample = samples[offset + index] * envelope;
                real += sample * Math.Cos(phase);
                imag -= sample * Math.Sin(phase);
                energy += sample * sample;
            }

            var magnitude = Math.Sqrt(real * real + imag * imag);
            output[frame] = energy > 1.0e-12 ? (float)(magnitude / Math.Sqrt(energy)) : 0.0f;
        }

        return ContrastNormalize(output);
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

    private static (int DelaySamples, double Confidence) EstimateDelay(ReadOnlySpan<float> reference, ReadOnlySpan<float> candidate, int maxLag)
    {
        var bestLag = 0;
        var best = double.NegativeInfinity;
        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            var score = NormalizedCorrelation(reference, candidate, lag);
            if (score > best)
            {
                best = score;
                bestLag = lag;
            }
        }

        return (bestLag, Math.Max(0.0, Math.Min(1.0, best)));
    }

    private static double NormalizedCorrelation(ReadOnlySpan<float> reference, ReadOnlySpan<float> candidate, int lag)
    {
        var startReference = lag < 0 ? -lag : 0;
        var startCandidate = lag > 0 ? lag : 0;
        var count = Math.Min(reference.Length - startReference, candidate.Length - startCandidate);
        if (count < 128)
        {
            return double.NegativeInfinity;
        }

        var sumReference = 0.0;
        var sumCandidate = 0.0;
        for (var index = 0; index < count; index++)
        {
            sumReference += reference[startReference + index];
            sumCandidate += candidate[startCandidate + index];
        }

        var meanReference = sumReference / count;
        var meanCandidate = sumCandidate / count;
        var cross = 0.0;
        var energyReference = 0.0;
        var energyCandidate = 0.0;
        for (var index = 0; index < count; index++)
        {
            var a = reference[startReference + index] - meanReference;
            var b = candidate[startCandidate + index] - meanCandidate;
            cross += a * b;
            energyReference += a * a;
            energyCandidate += b * b;
        }

        var denominator = Math.Sqrt(energyReference * energyCandidate);
        return denominator > 1.0e-12 ? cross / denominator : double.NegativeInfinity;
    }
}
