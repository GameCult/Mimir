using System.Buffers.Binary;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirAudioSynchronizationAnalyzer
{
    private const int MaxWindowSamples = 4_800;
    private const int MaxLagSamples = 2_400;

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

        var referenceSamples = ExtractMonoWindow(reference, out var referenceBlock);
        if (referenceSamples.Length == 0 || referenceBlock == null)
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

            var candidateSamples = ExtractMonoWindow(buffer, out var candidateBlock);
            if (candidateSamples.Length == 0 || candidateBlock == null)
            {
                continue;
            }

            if (candidateBlock.SampleRate != referenceBlock.SampleRate ||
                candidateBlock.SampleFormat != MimirAudioSampleFormat.Float32 ||
                referenceBlock.SampleFormat != MimirAudioSampleFormat.Float32)
            {
                continue;
            }

            var compared = Math.Min(referenceSamples.Length, candidateSamples.Length);
            if (compared < 256)
            {
                continue;
            }

            var maxLag = Math.Min(MaxLagSamples, compared / 2);
            var (delaySamples, confidence) = EstimateDelay(referenceSamples.AsSpan(^compared..), candidateSamples.AsSpan(^compared..), maxLag);
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

    private static float[] ExtractMonoWindow(MimirRollingStreamBuffer buffer, out MimirAudioBlockDescriptor? latestBlock)
    {
        latestBlock = buffer.Latest?.AudioBlock;
        if (latestBlock == null || latestBlock.SampleFormat != MimirAudioSampleFormat.Float32)
        {
            return [];
        }

        var samples = new List<float>(MaxWindowSamples);
        foreach (var sample in buffer.Snapshot().Where(sample => sample.AudioBlock != null && !sample.Data.IsEmpty).Reverse())
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
