namespace Mimir.Runtime.Synchronization;

public sealed record MimirChirpBinCalibrationBand(
    double CenterHz,
    double MeanEnergy,
    double PeakEnergy,
    double RelativeGain,
    int ObservationCount,
    bool Usable);

public sealed record MimirChirpBinCalibrationProfile(
    string SourceId,
    int SampleRate,
    int FrameCount,
    int AnchorCount,
    double ClockConfidence,
    double MeanAnchorErrorSamples,
    IReadOnlyList<MimirChirpBinCalibrationBand> Bands)
{
    private const double UsableRelativeGain = 0.20;
    private const double UsableMeanEnergy = 0.015;

    public int UsableBandCount => Bands.Count(band => band.Usable);

    public IReadOnlyList<MimirChirpBinCalibrationBand> StrongestBands(int count) =>
        Bands
            .OrderByDescending(band => band.MeanEnergy)
            .ThenBy(band => band.CenterHz)
            .Take(Math.Max(0, count))
            .ToArray();

    public static MimirChirpBinCalibrationProfile FromDecode(
        string sourceId,
        int sampleRate,
        MimirChirpletStreamDecode decode)
    {
        var grouped = decode.Frames
            .SelectMany(frame => frame.BestCandidate.BandResponses ?? [])
            .GroupBy(response => response.CenterHz)
            .Select(group => new
            {
                CenterHz = group.Key,
                MeanEnergy = group.Average(response => response.Energy),
                PeakEnergy = group.Max(response => response.Energy),
                ObservationCount = group.Count(),
            })
            .OrderBy(band => band.CenterHz)
            .ToArray();
        var strongestMean = grouped.Length == 0
            ? 0.0
            : grouped.Max(band => band.MeanEnergy);
        var bands = grouped
            .Select(band =>
            {
                var relativeGain = strongestMean <= 1.0e-12
                    ? 0.0
                    : band.MeanEnergy / strongestMean;
                return new MimirChirpBinCalibrationBand(
                    band.CenterHz,
                    band.MeanEnergy,
                    band.PeakEnergy,
                    relativeGain,
                    band.ObservationCount,
                    relativeGain >= UsableRelativeGain && band.MeanEnergy >= UsableMeanEnergy);
            })
            .ToArray();

        return new MimirChirpBinCalibrationProfile(
            sourceId,
            sampleRate,
            decode.Frames.Count,
            decode.Anchors.Count,
            decode.ClockFit?.Confidence ?? 0.0,
            decode.ClockFit?.MeanAbsoluteErrorSamples ?? double.NaN,
            bands);
    }
}
