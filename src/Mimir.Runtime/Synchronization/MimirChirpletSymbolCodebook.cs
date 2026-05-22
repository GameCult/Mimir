namespace Mimir.Runtime.Synchronization;

public sealed record MimirChirpletSymbolDefinition(
    int SymbolId,
    MimirChirpletTone Tone,
    double GapSeconds);

public sealed class MimirChirpletSymbolCodebook
{
    private const int CandidateBandCount = 8;
    private const int CandidateDurationCount = 5;
    private const int CandidateGlideCount = 4;
    private const int CandidateGapCount = 5;

    private readonly MimirChirpletSymbolDefinition[] symbols;

    private MimirChirpletSymbolCodebook()
    {
        symbols = BuildSymbols();
    }

    public static MimirChirpletSymbolCodebook Default { get; } = new();

    public IReadOnlyList<MimirChirpletSymbolDefinition> Symbols => symbols;

    public MimirChirpletSymbolDefinition this[int symbolId] => symbols[symbolId];

    private static MimirChirpletSymbolDefinition[] BuildSymbols()
    {
        var symbols = new MimirChirpletSymbolDefinition[MimirChirpletTimeline.SymbolCount];
        for (var symbol = 0; symbol < symbols.Length; symbol++)
        {
            var band = symbol % CandidateBandCount;
            var glide = symbol / CandidateBandCount;
            var duration = (band + 2 * glide) % CandidateDurationCount;
            var gap = (3 * band + glide) % CandidateGapCount;
            symbols[symbol] = new MimirChirpletSymbolDefinition(
                symbol,
                new MimirChirpletTone(
                    0.0,
                    DurationForClass(duration),
                    StartHzForBand(band),
                    EndHzFor(StartHzForBand(band), glide),
                    0.82),
                GapForClass(gap));
        }

        return symbols;
    }

    private static double StartHzForBand(int band) =>
        6_300.0 * Math.Pow(16_200.0 / 6_300.0, band / (double)(CandidateBandCount - 1));

    private static double DurationForClass(int durationClass) =>
        0.040 + durationClass * 0.0095;

    private static double GapForClass(int gapClass) =>
        gapClass switch
        {
            0 => 0.092,
            1 => 0.109,
            2 => 0.128,
            3 => 0.146,
            _ => 0.163,
        };

    private static double EndHzFor(double startHz, int glideClass)
    {
        var glideSemitones = glideClass switch
        {
            0 => -4.75,
            1 => -1.50,
            2 => 2.25,
            _ => 5.75,
        };
        return Math.Clamp(startHz * Math.Pow(2.0, glideSemitones / 12.0), 6_000.0, 17_200.0);
    }
}
