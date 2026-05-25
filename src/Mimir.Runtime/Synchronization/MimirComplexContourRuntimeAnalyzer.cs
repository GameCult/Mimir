using System.Buffers.Binary;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirComplexContourRuntimeOptions(
    string ProfileId = "canary-packet-trill",
    double ScheduleStartSeconds = 0.5,
    double SearchRadiusSeconds = 0.080,
    double ReferenceSearchRadiusSeconds = 0.120);

public sealed class MimirComplexContourRuntimeAnalyzer(
    MimirComplexContourRuntimeOptions? options = null,
    MimirComplexContourChannelModelDocument? channelModel = null)
{
    private const double MaxWindowSeconds = 1.25;
    private readonly MimirComplexContourRuntimeOptions options = options ?? new();
    private readonly MimirBioacousticContestantProfile profile = MimirBioacousticContestants.BuiltIn.FirstOrDefault(profile =>
        string.Equals(profile.Id, (options ?? new MimirComplexContourRuntimeOptions()).ProfileId, StringComparison.OrdinalIgnoreCase))
        ?? MimirBioacousticContestants.CanaryPacketTrill;
    private readonly MimirComplexContourChannelModelDocument? channelModel = channelModel;
    private readonly Dictionary<int, MimirComplexContourMatchedFilterBank> banks = [];

    public IReadOnlyList<MimirAudioSynchronizationReport> Analyze(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        string referenceSourceId,
        double runtimeSeconds,
        IReadOnlyDictionary<string, MimirAudioSynchronizationState>? predictedStates = null,
        IReadOnlySet<string>? candidateSourceIds = null)
    {
        var audioBuffers = buffers
            .Where(buffer => buffer.Descriptor.Kind == MimirStreamKind.Audio)
            .ToArray();
        var reference = audioBuffers.FirstOrDefault(buffer =>
            string.Equals(buffer.Descriptor.SourceId, referenceSourceId, StringComparison.Ordinal));
        if (reference == null || reference.Latest?.AudioBlock == null)
        {
            return [];
        }

        var referenceSamples = ExtractFloatMonoWindow(reference, out var referenceBlock);
        if (referenceSamples.Length == 0 || referenceBlock == null)
        {
            return [];
        }

        var reports = new List<MimirAudioSynchronizationReport>();
        foreach (var buffer in audioBuffers)
        {
            if (ReferenceEquals(buffer, reference) ||
                candidateSourceIds != null && !candidateSourceIds.Contains(buffer.Descriptor.SourceId))
            {
                continue;
            }

            var candidateSamples = ExtractFloatMonoWindow(buffer, out var candidateBlock);
            if (candidateSamples.Length == 0 || candidateBlock == null)
            {
                continue;
            }

            if (candidateBlock.SampleRate != referenceBlock.SampleRate)
            {
                continue;
            }

            var compared = Math.Min(referenceSamples.Length, candidateSamples.Length);
            if (compared < referenceBlock.SampleRate * profile.MotifDurationSeconds * 2.0)
            {
                continue;
            }

            var sampleRate = referenceBlock.SampleRate;
            var referenceWindow = referenceSamples.AsSpan(^compared..).ToArray();
            var candidateWindow = candidateSamples.AsSpan(^compared..).ToArray();
            var captureDurationSeconds = compared / (double)sampleRate;
            var windowStartSeconds = Math.Max(
                0.0,
                runtimeSeconds - options.ScheduleStartSeconds - captureDurationSeconds - options.ReferenceSearchRadiusSeconds);
            var windowEndSeconds = Math.Max(0.0, runtimeSeconds - options.ScheduleStartSeconds) + options.ReferenceSearchRadiusSeconds;
            var firstEvent = Math.Max(0, (long)Math.Floor((windowStartSeconds - MimirBioacousticContestants.FirstEventSeconds - profile.MotifDurationSeconds) / profile.EventSpacingSeconds) - 2);
            var lastEvent = Math.Max(firstEvent, (long)Math.Ceiling((windowEndSeconds - MimirBioacousticContestants.FirstEventSeconds) / profile.EventSpacingSeconds) + 2);
            var eventIndices = Enumerable.Range((int)firstEvent, (int)(lastEvent - firstEvent + 1))
                .Select(index => (ulong)index)
                .ToArray();
            if (eventIndices.Length == 0)
            {
                continue;
            }

            var predictedDelay = predictedStates != null &&
                predictedStates.TryGetValue(buffer.Descriptor.SourceId, out var state)
                    ? state.SmoothedDelaySamples
                    : 0.0;
            var sourceOffset = -windowStartSeconds * sampleRate;
            var bank = BankFor(sampleRate);
            var referenceHits = bank.AnalyzeEvents(
                referenceWindow,
                eventIndices,
                sourceOffset,
                Math.Max(8, (int)Math.Round(options.ReferenceSearchRadiusSeconds * sampleRate)));
            var candidateHits = bank.AnalyzeEvents(
                candidateWindow,
                eventIndices,
                sourceOffset + predictedDelay,
                Math.Max(16, (int)Math.Round(options.SearchRadiusSeconds * sampleRate)));
            var model = channelModel?
                .PathFor(referenceSourceId, buffer.Descriptor.SourceId, sampleRate)?
                .ToRuntimeModel();
            var estimate = new MimirDirectPathTracker(
                sampleRate,
                new MimirDirectPathTrackerOptions(
                    PredictionGateSamples: Math.Max(16.0, options.SearchRadiusSeconds * sampleRate),
                    ChannelModel: model)).Update(referenceHits, candidateHits, predictedDelay);
            if (estimate == null)
            {
                continue;
            }

            reports.Add(new MimirAudioSynchronizationReport(
                referenceSourceId,
                buffer.Descriptor.SourceId,
                sampleRate,
                (int)Math.Round(estimate.DelaySamples),
                estimate.DelaySamples,
                estimate.DelaySamples * 1000.0 / sampleRate,
                estimate.Confidence,
                estimate.BandObservations
                    .GroupBy(observation => Math.Round(observation.CenterHz / 250.0) * 250.0)
                    .Select(group => new MimirChirpletBandResponse(
                        group.Key,
                        group.Sum(observation => observation.Weight)))
                    .OrderBy(response => response.CenterHz)
                    .ToArray(),
                Math.Min(reference.Latest?.TimestampNs ?? 0, buffer.Latest?.TimestampNs ?? 0),
                compared,
                reference.Latest?.Sequence ?? 0,
                buffer.Latest?.Sequence ?? 0,
                estimate.DirectHitCount,
                estimate.Confidence,
                "complex-contour"));
        }

        return reports;
    }

    private MimirComplexContourMatchedFilterBank BankFor(int sampleRate)
    {
        if (!banks.TryGetValue(sampleRate, out var bank))
        {
            bank = new MimirComplexContourMatchedFilterBank(new MimirBioacousticContestantRenderer(profile), sampleRate);
            banks[sampleRate] = bank;
        }

        return bank;
    }

    private static float[] ExtractFloatMonoWindow(MimirRollingStreamBuffer buffer, out MimirAudioBlockDescriptor? latestBlock)
    {
        latestBlock = buffer.Latest?.AudioBlock;
        if (latestBlock == null)
        {
            return [];
        }

        var maxSamples = Math.Max(1, (int)Math.Ceiling(latestBlock.SampleRate * MaxWindowSeconds));
        var samples = new List<float>(maxSamples);
        foreach (var sample in buffer.Snapshot()
                     .Where(sample => sample.AudioBlock is { SampleFormat: MimirAudioSampleFormat.Float32 } && !sample.Data.IsEmpty)
                     .Reverse())
        {
            var block = sample.AudioBlock!;
            var mono = ExtractFirstFloatChannel(sample.Data.Span, block.Channels);
            for (var index = mono.Length - 1; index >= 0 && samples.Count < maxSamples; index--)
            {
                samples.Add(mono[index]);
            }

            if (samples.Count >= maxSamples)
            {
                break;
            }
        }

        samples.Reverse();
        return samples.ToArray();
    }

    private static float[] ExtractFirstFloatChannel(ReadOnlySpan<byte> data, int channels)
    {
        if (channels <= 0)
        {
            return [];
        }

        var frames = data.Length / (sizeof(float) * channels);
        var output = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * channels * sizeof(float);
            output[frame] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(float))));
        }

        return output;
    }
}
