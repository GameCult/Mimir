using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirChirpBinCalibrationModel(
    string ModelId,
    DateTimeOffset CreatedAt,
    int SampleRate,
    string ReferenceSourceId,
    MimirChirpBinCodebookPlan EmissionPlan,
    IReadOnlyList<MimirChirpBinPathCalibration> Paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public MimirChirpBinPathCalibration? PathFor(string sourceId) =>
        Paths.FirstOrDefault(path => string.Equals(path.SourceId, sourceId, StringComparison.Ordinal));

    public static MimirChirpBinCalibrationModel Load(string path) =>
        JsonSerializer.Deserialize<MimirChirpBinCalibrationModel>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"Could not read chirp-bin calibration model: {path}");

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static MimirChirpBinCalibrationModel FromDecodes(
        string referenceSourceId,
        int sampleRate,
        IReadOnlyDictionary<string, MimirChirpletStreamDecode> decodes,
        string outputSourceId = "main-speakers")
    {
        var paths = decodes
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => MimirChirpBinPathCalibration.FromDecode(outputSourceId, pair.Key, sampleRate, pair.Value))
            .ToArray();
        var emissionPlan = MimirChirpBinCodebookPlan.ForSharedEmission(paths);
        paths = paths.Select(path => path with { EmissionPlan = emissionPlan }).ToArray();
        return new MimirChirpBinCalibrationModel(
            $"chirp-bin-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}",
            DateTimeOffset.UtcNow,
            sampleRate,
            referenceSourceId,
            emissionPlan,
            paths);
    }
}

public sealed record MimirChirpBinPathCalibration(
    string OutputSourceId,
    string SourceId,
    int SampleRate,
    MimirChirpBinCalibrationProfile Profile,
    MimirChirpBinCodebookPlan EmissionPlan,
    MimirChirpBinCodebookPlan CodebookPlan,
    IReadOnlyList<MimirChirpBinSymbolCalibration> Symbols,
    IReadOnlyList<MimirChirpBinConfusionObservation> Confusion,
    IReadOnlyList<MimirChirpBinDelayHypothesis> DelayHypotheses)
{
    public double SymbolWeight(int symbolId)
    {
        var symbol = Symbols.FirstOrDefault(item => item.SymbolId == symbolId);
        return symbol == null ? 1.0 : Math.Clamp(symbol.Reliability, 0.05, 1.0);
    }

    public double GroupDelayCorrectionSamples(int symbolId)
    {
        var symbol = Symbols.FirstOrDefault(item => item.SymbolId == symbolId);
        return symbol?.MeanTimingResidualSamples ?? 0.0;
    }

    public double PhaseWeight(int symbolId, double observedPhaseRadians)
    {
        var symbol = Symbols.FirstOrDefault(item => item.SymbolId == symbolId);
        if (symbol == null || symbol.ObservationCount <= 0)
        {
            return 1.0;
        }

        var error = Math.Abs(AngularDistance(observedPhaseRadians, symbol.MeanPhaseRadians));
        return Math.Clamp(1.0 - error / Math.PI, 0.15, 1.0);
    }

    public static MimirChirpBinPathCalibration FromDecode(
        string outputSourceId,
        string sourceId,
        int sampleRate,
        MimirChirpletStreamDecode decode)
    {
        var profile = MimirChirpBinCalibrationProfile.FromDecode(sourceId, sampleRate, decode);
        var observations = MimirChirpBinTimeline.Default.CalibrationObservations(decode, sampleRate);
        var symbols = observations
            .GroupBy(observation => observation.ExpectedSymbolId)
            .Select(group =>
            {
                var items = group.ToArray();
                var meanConfidence = items.Average(item => item.Confidence);
                var meanResidual = items.Average(item => item.TimingResidualSamples);
                var residualSpread = items.Average(item => Math.Abs(item.TimingResidualSamples - meanResidual));
                var correct = items.Count(item => item.ExpectedSymbolId == item.ObservedSymbolId);
                var reliability = meanConfidence *
                    (correct / (double)items.Length) *
                    (1.0 / (1.0 + residualSpread / Math.Max(1.0, sampleRate * 0.00025)));
                var phase = CircularMean(items.Select(item => item.PhaseRadians));
                var phaseCoherence = PhaseCoherence(items.Select(item => item.PhaseRadians));
                return new MimirChirpBinSymbolCalibration(
                    group.Key,
                    reliability,
                    phaseCoherence,
                    items.Average(item => item.ExpectedCenterHz),
                    items.Average(item => item.ObservedCenterHz),
                    items.Average(item => item.ObservedEnergy),
                    meanResidual,
                    residualSpread,
                    phase,
                    items.Length);
            })
            .OrderBy(symbol => symbol.SymbolId)
            .ToArray();
        var hypotheses = BuildDelayHypotheses(decode);
        var plan = MimirChirpBinCodebookPlan.FromSymbols(symbols);
        return new MimirChirpBinPathCalibration(
            outputSourceId,
            sourceId,
            sampleRate,
            profile,
            plan,
            plan,
            symbols,
            observations,
            hypotheses);
    }

    private static IReadOnlyList<MimirChirpBinDelayHypothesis> BuildDelayHypotheses(MimirChirpletStreamDecode decode)
    {
        if (decode.ClockFit == null)
        {
            return [];
        }

        var liveHorizonSamples = decode.ClockFit.EffectiveSampleRate * 10.0;
        var hypotheses = decode.Anchors
            .GroupBy(anchor => Math.Round(anchor.SampleOffset - anchor.TimelineSeconds * decode.ClockFit.EffectiveSampleRate))
            .Select(group =>
            {
                var delay = group.Average(anchor => anchor.SampleOffset - anchor.TimelineSeconds * decode.ClockFit!.EffectiveSampleRate);
                var residual = group.Average(anchor => Math.Abs(anchor.SampleOffset - (delay + anchor.TimelineSeconds * decode.ClockFit!.EffectiveSampleRate)));
                return new MimirChirpBinDelayHypothesis(
                    delay,
                    DominantBinShift(group),
                    group.Count(),
                    group.Average(anchor => anchor.Confidence),
                    residual);
            })
            .Where(hypothesis => Math.Abs(hypothesis.DelaySamples) <= liveHorizonSamples)
            .OrderByDescending(hypothesis => hypothesis.Confidence * hypothesis.SupportCount)
            .Take(8)
            .ToArray();
        if (hypotheses.Length > 0)
        {
            return hypotheses;
        }

        return Math.Abs(decode.ClockFit.SourceOffsetSamples) <= liveHorizonSamples
            ? [new MimirChirpBinDelayHypothesis(decode.ClockFit.SourceOffsetSamples, DominantBinShift(decode.Anchors), decode.ClockFit.AnchorCount, decode.ClockFit.Confidence, decode.ClockFit.MeanAbsoluteErrorSamples)]
            : [];
    }

    private static int DominantBinShift(IEnumerable<MimirChirpletTimelineAnchor> anchors)
    {
        return anchors
            .Select(anchor =>
            {
                var expected = MimirChirpBinTimeline.Default.EventForIndex(anchor.EventIndex).SymbolId;
                var observed = anchor.Symbols.FirstOrDefault()?.SymbolId ?? expected;
                var shift = observed - expected;
                if (shift > MimirChirpBinTimeline.SymbolCount / 2)
                {
                    shift -= MimirChirpBinTimeline.SymbolCount;
                }
                else if (shift < -MimirChirpBinTimeline.SymbolCount / 2)
                {
                    shift += MimirChirpBinTimeline.SymbolCount;
                }

                return shift;
            })
            .GroupBy(shift => shift)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => Math.Abs(group.Key))
            .Select(group => group.Key)
            .DefaultIfEmpty(0)
            .First();
    }

    private static double CircularMean(IEnumerable<double> radians)
    {
        var sin = 0.0;
        var cos = 0.0;
        var count = 0;
        foreach (var value in radians)
        {
            sin += Math.Sin(value);
            cos += Math.Cos(value);
            count++;
        }

        return count == 0 ? 0.0 : Math.Atan2(sin / count, cos / count);
    }

    private static double PhaseCoherence(IEnumerable<double> radians)
    {
        var sin = 0.0;
        var cos = 0.0;
        var count = 0;
        foreach (var value in radians)
        {
            sin += Math.Sin(value);
            cos += Math.Cos(value);
            count++;
        }

        return count == 0 ? 0.0 : Math.Clamp(Math.Sqrt(sin * sin + cos * cos) / count, 0.0, 1.0);
    }

    private static double AngularDistance(double a, double b)
    {
        var delta = Math.Abs(a - b) % (Math.PI * 2.0);
        return delta > Math.PI ? Math.PI * 2.0 - delta : delta;
    }
}

public sealed record MimirChirpBinCodebookPlan(
    int ReliableSymbolCount,
    int RecommendedOrder,
    IReadOnlyList<int> ReliableSymbolIds)
{
    public bool IsAdaptive => ReliableSymbolIds.Count >= 2 && ReliableSymbolIds.Count < MimirChirpBinTimeline.SymbolCount;

    public static MimirChirpBinCodebookPlan FromSymbols(IReadOnlyList<MimirChirpBinSymbolCalibration> symbols)
    {
        var reliable = symbols
            .Where(symbol => symbol.Reliability >= 0.20)
            .OrderByDescending(symbol => symbol.Reliability)
            .ThenBy(symbol => symbol.SymbolId)
            .Select(symbol => symbol.SymbolId)
            .ToArray();
        var count = Math.Max(2, reliable.Length);
        var order = 3;
        while (Math.Pow(count, order) < 120_000.0 && order < 8)
        {
            order++;
        }

        return new MimirChirpBinCodebookPlan(reliable.Length, order, reliable);
    }

    public static MimirChirpBinCodebookPlan ForSharedEmission(IReadOnlyList<MimirChirpBinPathCalibration> paths)
    {
        var reliable = paths
            .SelectMany(path => path.CodebookPlan.ReliableSymbolIds)
            .Distinct()
            .Order()
            .ToArray();
        if (reliable.Length < 2)
        {
            return new MimirChirpBinCodebookPlan(MimirChirpBinTimeline.SymbolCount, MimirChirpBinTimeline.TimelineOrder, []);
        }

        var order = 3;
        while (Math.Pow(reliable.Length, order) < 120_000.0 && order < 8)
        {
            order++;
        }

        return new MimirChirpBinCodebookPlan(reliable.Length, order, reliable);
    }
}

public sealed record MimirChirpBinSymbolCalibration(
    int SymbolId,
    double Reliability,
    double PhaseCoherence,
    double ExpectedCenterHz,
    double ObservedCenterHz,
    double MeanEnergy,
    double MeanTimingResidualSamples,
    double TimingResidualSpreadSamples,
    double MeanPhaseRadians,
    int ObservationCount);

public sealed record MimirChirpBinConfusionObservation(
    ulong EventIndex,
    int ExpectedSymbolId,
    int ObservedSymbolId,
    double ExpectedCenterHz,
    double ObservedCenterHz,
    double ObservedEnergy,
    double TimingResidualSamples,
    double Confidence,
    double DelayHypothesisSamples,
    int BinShift,
    double PhaseRadians);

public sealed record MimirChirpBinDelayHypothesis(
    double DelaySamples,
    int BinShift,
    int SupportCount,
    double Confidence,
    double MeanResidualSamples);
