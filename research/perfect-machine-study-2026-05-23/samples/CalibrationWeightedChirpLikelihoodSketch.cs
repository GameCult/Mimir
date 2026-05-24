// Sketch only. This shows where meatspace calibration should enter the active
// decoder: before code-valid path selection, as likelihood shaping.

namespace Mimir.Research.Samples;

public sealed class CalibrationWeightedChirpLikelihoodSketch
{
    private readonly PathCalibration calibration;

    public CalibrationWeightedChirpLikelihoodSketch(PathCalibration calibration)
    {
        this.calibration = calibration;
    }

    public SymbolLikelihood[] ScoreFrame(FrameBinEnergy frame)
    {
        var result = new SymbolLikelihood[calibration.Symbols.Length];
        for (var symbolId = 0; symbolId < calibration.Symbols.Length; symbolId++)
        {
            var symbol = calibration.Symbols[symbolId];
            var observed = frame.EnergyByBin[symbolId];
            var reliability = symbol.Reliability;
            var confusionPenalty = ConfusionPenalty(symbolId, frame);
            var phaseWeight = PhaseWeight(symbol, frame.PhaseByBin[symbolId]);
            var delayCorrection = symbol.GroupDelaySamples;

            var logLikelihood =
                Math.Log(Math.Max(observed, 1.0e-12)) * reliability +
                Math.Log(Math.Max(phaseWeight, 1.0e-6)) -
                confusionPenalty;

            result[symbolId] = new SymbolLikelihood(
                symbolId,
                logLikelihood,
                delayCorrection,
                reliability,
                observed);
        }

        return result
            .OrderByDescending(score => score.LogLikelihood)
            .Take(calibration.MaxCandidatesPerFrame)
            .ToArray();
    }

    private double ConfusionPenalty(int expectedSymbol, FrameBinEnergy frame)
    {
        var penalty = 0.0;
        foreach (var alias in calibration.Confusions.Where(c => c.ExpectedSymbol == expectedSymbol))
        {
            var aliasEnergy = frame.EnergyByBin[alias.ObservedSymbol];
            penalty += alias.Probability * Math.Log1p(aliasEnergy);
        }

        return penalty;
    }

    private static double PhaseWeight(SymbolCalibration symbol, double observedPhase)
    {
        if (symbol.PhaseCoherence <= 0.0)
        {
            return 1.0;
        }

        var distance = AngularDistance(observedPhase, symbol.MeanPhaseRadians);
        return 1.0 - symbol.PhaseCoherence * Math.Min(1.0, distance / Math.PI);
    }

    private static double AngularDistance(double a, double b)
    {
        var d = Math.Abs(a - b) % (Math.PI * 2.0);
        return d > Math.PI ? Math.PI * 2.0 - d : d;
    }
}

public sealed record FrameBinEnergy(
    double[] EnergyByBin,
    double[] PhaseByBin,
    double InitialOffsetSamples);

public sealed record PathCalibration(
    SymbolCalibration[] Symbols,
    Confusion[] Confusions,
    int MaxCandidatesPerFrame);

public sealed record SymbolCalibration(
    int SymbolId,
    double Reliability,
    double MeanEnergy,
    double MeanPhaseRadians,
    double PhaseCoherence,
    double GroupDelaySamples);

public sealed record Confusion(
    int ExpectedSymbol,
    int ObservedSymbol,
    double Probability);

public sealed record SymbolLikelihood(
    int SymbolId,
    double LogLikelihood,
    double GroupDelayCorrectionSamples,
    double Reliability,
    double ObservedEnergy);

