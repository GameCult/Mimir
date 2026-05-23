namespace Mimir.Runtime.Synchronization;

public sealed record MimirAudioSynchronizationState(
    string ReferenceSourceId,
    string SourceId,
    int SampleRate,
    double LatestDelaySamples,
    double SmoothedDelaySamples,
    double DelayMilliseconds,
    double SamplingRateOffsetPpm,
    double Confidence,
    IReadOnlyList<MimirChirpletBandResponse> BandResponses,
    long UpdatedAtNs,
    ulong ReferenceSequence,
    ulong SourceSequence)
{
    public double DelayMicroseconds => SmoothedDelaySamples * 1_000_000.0 / SampleRate;
}

public sealed class MimirAudioSynchronizationStateTracker
{
    private const double MinimumUpdateConfidence = 0.05;
    private readonly Dictionary<string, MimirAudioSynchronizationState> states = new(StringComparer.Ordinal);

    public IReadOnlyList<MimirAudioSynchronizationState> States =>
        states.Values.OrderBy(state => state.SourceId, StringComparer.Ordinal).ToArray();

    public void Update(IEnumerable<MimirAudioSynchronizationReport> reports)
    {
        foreach (var report in reports)
        {
            if (report.Confidence < MinimumUpdateConfidence)
            {
                continue;
            }

            if (!states.TryGetValue(report.SourceId, out var previous))
            {
                states[report.SourceId] = NewState(report, report.FractionalDelaySamples, 0.0);
                continue;
            }

            if (previous.ReferenceSequence == report.ReferenceSequence &&
                previous.SourceSequence == report.SourceSequence)
            {
                continue;
            }

            var alpha = Math.Clamp(report.Confidence * 0.35, 0.04, 0.30);
            var smoothedDelay = previous.SmoothedDelaySamples +
                (report.FractionalDelaySamples - previous.SmoothedDelaySamples) * alpha;
            var measuredSro = EstimateSroPpm(previous, smoothedDelay, report);
            var sroAlpha = Math.Clamp(report.Confidence * 0.20, 0.02, 0.18);
            var smoothedSro = previous.SamplingRateOffsetPpm +
                (measuredSro - previous.SamplingRateOffsetPpm) * sroAlpha;
            states[report.SourceId] = NewState(report, smoothedDelay, smoothedSro);
        }
    }

    public void Remove(string sourceId)
    {
        states.Remove(sourceId);
    }

    private static MimirAudioSynchronizationState NewState(
        MimirAudioSynchronizationReport report,
        double smoothedDelaySamples,
        double samplingRateOffsetPpm) =>
        new(
            report.ReferenceSourceId,
            report.SourceId,
            report.SampleRate,
            report.FractionalDelaySamples,
            smoothedDelaySamples,
            smoothedDelaySamples * 1000.0 / report.SampleRate,
            samplingRateOffsetPpm,
            report.Confidence,
            report.BandResponses,
            report.AnalysisTimestampNs,
            report.ReferenceSequence,
            report.SourceSequence);

    private static double EstimateSroPpm(
        MimirAudioSynchronizationState previous,
        double smoothedDelaySamples,
        MimirAudioSynchronizationReport report)
    {
        var dtSeconds = (report.AnalysisTimestampNs - previous.UpdatedAtNs) / 1_000_000_000.0;
        if (dtSeconds <= 0.050)
        {
            return previous.SamplingRateOffsetPpm;
        }

        var delayDeltaSamples = smoothedDelaySamples - previous.SmoothedDelaySamples;
        return delayDeltaSamples / (dtSeconds * report.SampleRate) * 1_000_000.0;
    }
}
