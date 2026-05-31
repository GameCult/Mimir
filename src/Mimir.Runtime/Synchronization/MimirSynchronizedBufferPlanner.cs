namespace Mimir.Runtime.Synchronization;

public enum MimirSynchronizedSliceStatus
{
    Missing,
    Ready,
    HoldingPreviousSample,
    FutureSampleOnly,
}

public readonly record struct MimirSourceTimingCorrection(
    string SourceId,
    string ClockDomainId,
    long OffsetNs,
    double Confidence,
    string EvidenceKind)
{
    public long ToCanonicalNs(long sourceTimestampNs) => checked(sourceTimestampNs + OffsetNs);

    public bool AppliesTo(MimirStreamDescriptor descriptor) =>
        string.Equals(SourceId, descriptor.SourceId, StringComparison.Ordinal) ||
        (!string.IsNullOrWhiteSpace(ClockDomainId) &&
            string.Equals(ClockDomainId, descriptor.EffectiveClockDomainId, StringComparison.Ordinal));
}

public readonly record struct MimirSynchronizedStreamSlice(
    string SourceId,
    MimirStreamKind Kind,
    MimirStreamOrigin Origin,
    MimirSynchronizedSliceStatus Status,
    MimirStreamSample? Sample,
    long SourceTimestampNs,
    long CanonicalStartNs,
    long CanonicalEndNs,
    long PresentationTimeNs,
    long TimingOffsetNs,
    long DistanceFromPresentationNs,
    double TimingConfidence,
    string TimingEvidenceKind);

public sealed record MimirSynchronizedBufferFrame(
    long PresentationTimeNs,
    long WindowStartNs,
    long WindowEndNs,
    TimeSpan PresentationDelay,
    IReadOnlyList<MimirSynchronizedStreamSlice> Slices)
{
    public bool IsComplete => Slices.Count > 0 && Slices.All(static slice => slice.Status != MimirSynchronizedSliceStatus.Missing);

    public IEnumerable<MimirSynchronizedStreamSlice> VideoSlices =>
        Slices.Where(static slice => slice.Kind == MimirStreamKind.Video);

    public IEnumerable<MimirSynchronizedStreamSlice> AudioSlices =>
        Slices.Where(static slice => slice.Kind == MimirStreamKind.Audio);
}

public sealed class MimirSynchronizedBufferPlanner
{
    public static IReadOnlyList<MimirSourceTimingCorrection> CorrectionsFromAudioStates(
        IEnumerable<MimirAudioSynchronizationState> states,
        IEnumerable<MimirRollingStreamBuffer>? buffers = null)
    {
        var corrections = new List<MimirSourceTimingCorrection>();
        var references = new HashSet<string>(StringComparer.Ordinal);
        var descriptorsBySource = buffers?
            .Select(static buffer => buffer.Descriptor)
            .GroupBy(static descriptor => descriptor.SourceId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal)
            ?? new Dictionary<string, MimirStreamDescriptor>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            if (state.SampleRate <= 0)
            {
                continue;
            }

            if (references.Add(state.ReferenceSourceId))
            {
                corrections.Add(new MimirSourceTimingCorrection(
                    state.ReferenceSourceId,
                    "",
                    0,
                    1.0,
                    "reference"));
            }

            descriptorsBySource.TryGetValue(state.SourceId, out var descriptor);
            var clockDomainId = descriptor?.EffectiveClockDomainId ?? "";
            var offsetNs = checked((long)Math.Round(-state.SmoothedDelaySamples * 1_000_000_000.0 / state.SampleRate));
            corrections.Add(new MimirSourceTimingCorrection(
                state.SourceId,
                clockDomainId,
                offsetNs,
                Math.Clamp(state.Confidence, 0.0, 1.0),
                string.IsNullOrWhiteSpace(clockDomainId) ? "audio-sync" : "audio-sync-clock-domain"));
        }

        return corrections;
    }

    public MimirSynchronizedBufferFrame BuildFrame(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        TimeSpan presentationDelay,
        IEnumerable<MimirSourceTimingCorrection>? timingCorrections = null)
    {
        var activeBuffers = buffers
            .Where(static buffer => buffer.Latest.HasValue)
            .OrderBy(static buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal)
            .ToArray();
        if (activeBuffers.Length == 0)
        {
            return new MimirSynchronizedBufferFrame(0, 0, 0, presentationDelay, []);
        }

        var corrections = BuildCorrectionMap(timingCorrections);
        var windowStartNs = activeBuffers.Max(buffer => Correct(corrections, buffer.Descriptor, buffer.WindowStartNs));
        var windowEndNs = activeBuffers.Min(buffer =>
        {
            var latest = buffer.Latest!.Value;
            return SampleCanonicalEndNs(latest, CorrectionFor(corrections, buffer.Descriptor));
        });
        var delayNs = checked((long)Math.Round(Math.Max(0.0, presentationDelay.TotalSeconds) * 1_000_000_000.0));
        var presentationTimeNs = Math.Clamp(windowEndNs - delayNs, windowStartNs, windowEndNs);

        var slices = new List<MimirSynchronizedStreamSlice>(activeBuffers.Length);
        foreach (var buffer in activeBuffers)
        {
            slices.Add(SelectSlice(buffer, presentationTimeNs, CorrectionFor(corrections, buffer.Descriptor)));
        }

        return new MimirSynchronizedBufferFrame(
            presentationTimeNs,
            windowStartNs,
            windowEndNs,
            presentationDelay,
            slices);
    }

    private static Dictionary<string, MimirSourceTimingCorrection> BuildCorrectionMap(
        IEnumerable<MimirSourceTimingCorrection>? timingCorrections)
    {
        var map = new Dictionary<string, MimirSourceTimingCorrection>(StringComparer.Ordinal);
        if (timingCorrections == null)
        {
            return map;
        }

        foreach (var correction in timingCorrections)
        {
            if (!string.IsNullOrWhiteSpace(correction.SourceId))
            {
                map[$"source:{correction.SourceId}"] = correction;
            }

            if (!string.IsNullOrWhiteSpace(correction.ClockDomainId))
            {
                map[$"clock:{correction.ClockDomainId}"] = correction;
            }
        }

        return map;
    }

    private static MimirSourceTimingCorrection CorrectionFor(
        IReadOnlyDictionary<string, MimirSourceTimingCorrection> corrections,
        MimirStreamDescriptor descriptor)
    {
        if (corrections.TryGetValue($"source:{descriptor.SourceId}", out var sourceCorrection))
        {
            return sourceCorrection;
        }

        if (!string.IsNullOrWhiteSpace(descriptor.EffectiveClockDomainId) &&
            corrections.TryGetValue($"clock:{descriptor.EffectiveClockDomainId}", out var clockCorrection))
        {
            return clockCorrection;
        }

        return new MimirSourceTimingCorrection(descriptor.SourceId, descriptor.EffectiveClockDomainId, 0, 0.0, "");
    }

    private static long Correct(
        IReadOnlyDictionary<string, MimirSourceTimingCorrection> corrections,
        MimirStreamDescriptor descriptor,
        long timestampNs) =>
        CorrectionFor(corrections, descriptor).ToCanonicalNs(timestampNs);

    private static MimirSynchronizedStreamSlice SelectSlice(
        MimirRollingStreamBuffer buffer,
        long presentationTimeNs,
        MimirSourceTimingCorrection correction)
    {
        var samples = buffer.Snapshot();
        if (samples.Count == 0)
        {
            return new MimirSynchronizedStreamSlice(
                buffer.Descriptor.SourceId,
                buffer.Descriptor.Kind,
                buffer.Descriptor.Origin,
                MimirSynchronizedSliceStatus.Missing,
                null,
                0,
                0,
                0,
                presentationTimeNs,
                correction.OffsetNs,
                long.MaxValue,
                correction.Confidence,
                correction.EvidenceKind);
        }

        MimirStreamSample? best = null;
        MimirSynchronizedSliceStatus status = MimirSynchronizedSliceStatus.Missing;
        long bestDistance = long.MaxValue;
        foreach (var sample in samples)
        {
            var startNs = correction.ToCanonicalNs(sample.TimestampNs);
            var endNs = SampleCanonicalEndNs(sample, correction);
            if (presentationTimeNs >= startNs && presentationTimeNs < endNs)
            {
                best = sample;
                status = MimirSynchronizedSliceStatus.Ready;
                bestDistance = 0;
                break;
            }

            var distance = presentationTimeNs < startNs
                ? startNs - presentationTimeNs
                : presentationTimeNs - endNs;
            if (distance < bestDistance)
            {
                best = sample;
                bestDistance = distance;
                status = presentationTimeNs >= endNs
                    ? MimirSynchronizedSliceStatus.HoldingPreviousSample
                    : MimirSynchronizedSliceStatus.FutureSampleOnly;
            }
        }

        var selected = best!.Value;
        var selectedStartNs = correction.ToCanonicalNs(selected.TimestampNs);
        var selectedEndNs = SampleCanonicalEndNs(selected, correction);
        return new MimirSynchronizedStreamSlice(
            buffer.Descriptor.SourceId,
            buffer.Descriptor.Kind,
            buffer.Descriptor.Origin,
            status,
            selected,
            selected.TimestampNs,
            selectedStartNs,
            selectedEndNs,
            presentationTimeNs,
            correction.OffsetNs,
            bestDistance,
            correction.Confidence,
            correction.EvidenceKind);
    }

    private static long SampleCanonicalEndNs(MimirStreamSample sample, MimirSourceTimingCorrection correction)
    {
        var startNs = correction.ToCanonicalNs(sample.TimestampNs);
        var durationNs = SampleDurationNs(sample);
        return checked(startNs + Math.Max(1, durationNs));
    }

    private static long SampleDurationNs(MimirStreamSample sample)
    {
        if (sample.AudioBlock is { SampleRate: > 0, FrameCount: > 0 } block)
        {
            return checked((long)Math.Ceiling(block.FrameCount * 1_000_000_000.0 / block.SampleRate));
        }

        return 1;
    }
}
