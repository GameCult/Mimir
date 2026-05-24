namespace Mimir.Runtime.Synchronization;

public enum MimirBioacousticContestantKind
{
    CurrentBirdcall,
    RedpollTrill,
    RobinWarble,
    ThrushLadder,
    ThornbillZigZag,
    NightingaleCascade,
    AquaSynthFormantWeaver,
    CanaryPacketTrill,
    FinchBurstPacket
}

public sealed record MimirBioacousticContestantProfile(
    string Id,
    string Description,
    MimirBioacousticContestantKind Kind,
    int SyllableCount,
    double MotifDurationSeconds,
    double EventSpacingSeconds,
    double LowestRootHz,
    double HighestRootHz,
    double Gain,
    int PayloadBitsPerEvent,
    string BeautyNotes);

public static class MimirBioacousticContestants
{
    public const double FirstEventSeconds = 0.08;
    public const int WordCount = 128;
    public const int SpeakerCount = 2;
    public const int SymbolCount = WordCount * SpeakerCount;

    public static MimirBioacousticContestantProfile CurrentBirdcall { get; } = new(
        "current-birdcall",
        "The current four-syllable formant word.",
        MimirBioacousticContestantKind.CurrentBirdcall,
        SyllableCount: 4,
        MotifDurationSeconds: 0.118,
        EventSpacingSeconds: 0.160,
        LowestRootHz: 2_600.0,
        HighestRootHz: 9_600.0,
        Gain: 0.030,
        PayloadBitsPerEvent: 4,
        "Musical enough, but still sounds like a lab bird with a clipboard.");

    public static MimirBioacousticContestantProfile RedpollTrill { get; } = new(
        "redpoll-trill",
        "Fast repeated chips with tiny frequency steps and strong onset timing.",
        MimirBioacousticContestantKind.RedpollTrill,
        SyllableCount: 7,
        MotifDurationSeconds: 0.126,
        EventSpacingSeconds: 0.165,
        LowestRootHz: 3_200.0,
        HighestRootHz: 10_800.0,
        Gain: 0.025,
        PayloadBitsPerEvent: 5,
        "Pretty, bright, and temporally sharp; risks weak identity if the trills blur together.");

    public static MimirBioacousticContestantProfile RobinWarble { get; } = new(
        "robin-warble",
        "Irregular soft phrases with curved syllables and wider formant motion.",
        MimirBioacousticContestantKind.RobinWarble,
        SyllableCount: 6,
        MotifDurationSeconds: 0.142,
        EventSpacingSeconds: 0.185,
        LowestRootHz: 2_200.0,
        HighestRootHz: 8_600.0,
        Gain: 0.024,
        PayloadBitsPerEvent: 6,
        "Most plausible room sound; slower lock and softer onsets are the price.");

    public static MimirBioacousticContestantProfile ThrushLadder { get; } = new(
        "thrush-ladder",
        "Repeated interval ladders: two crisp notes, a shifted reply, then a flourish.",
        MimirBioacousticContestantKind.ThrushLadder,
        SyllableCount: 5,
        MotifDurationSeconds: 0.132,
        EventSpacingSeconds: 0.170,
        LowestRootHz: 2_400.0,
        HighestRootHz: 9_200.0,
        Gain: 0.027,
        PayloadBitsPerEvent: 6,
        "Good identity geometry: repetition plus interval ratios. Less subtle.");

    public static MimirBioacousticContestantProfile ThornbillZigZag { get; } = new(
        "thornbill-zigzag",
        "Tiny high-band zig-zag syllables inspired by thornbill chatter.",
        MimirBioacousticContestantKind.ThornbillZigZag,
        SyllableCount: 8,
        MotifDurationSeconds: 0.112,
        EventSpacingSeconds: 0.155,
        LowestRootHz: 4_200.0,
        HighestRootHz: 13_200.0,
        Gain: 0.021,
        PayloadBitsPerEvent: 7,
        "Excellent frequency fingerprint if the path keeps high band alive; fragile on dull mics.");

    public static MimirBioacousticContestantProfile NightingaleCascade { get; } = new(
        "nightingale-cascade",
        "Dense rising and falling cascades with phrase-level frequency punctuation.",
        MimirBioacousticContestantKind.NightingaleCascade,
        SyllableCount: 9,
        MotifDurationSeconds: 0.150,
        EventSpacingSeconds: 0.195,
        LowestRootHz: 1_800.0,
        HighestRootHz: 11_200.0,
        Gain: 0.023,
        PayloadBitsPerEvent: 8,
        "The prettiest contestant; expensive and easiest to confuse under heavy smearing.");

    public static MimirBioacousticContestantProfile AquaSynthFormantWeaver { get; } = new(
        "aquasynth-formant-weaver",
        "AquaSynth-inspired formant/FM word: two timing syllables, payload ornaments, and a final parity flourish.",
        MimirBioacousticContestantKind.AquaSynthFormantWeaver,
        SyllableCount: 7,
        MotifDurationSeconds: 0.136,
        EventSpacingSeconds: 0.168,
        LowestRootHz: 2_050.0,
        HighestRootHz: 9_800.0,
        Gain: 0.026,
        PayloadBitsPerEvent: 8,
        "Less literal bird imitation, more compact singing modem: readable envelopes, formants, FM motion, and bit-bearing ornaments.");

    public static MimirBioacousticContestantProfile CanaryPacketTrill { get; } = new(
        "canary-packet-trill",
        "Short bright packet song: two hard timing chips plus four payload-colored formant notes.",
        MimirBioacousticContestantKind.CanaryPacketTrill,
        SyllableCount: 6,
        MotifDurationSeconds: 0.096,
        EventSpacingSeconds: 0.125,
        LowestRootHz: 2_800.0,
        HighestRootHz: 10_600.0,
        Gain: 0.024,
        PayloadBitsPerEvent: 2,
        "A higher-throughput singing modem candidate: compact, bright, and less apologetic about being a packet.");

    public static MimirBioacousticContestantProfile FinchBurstPacket { get; } = new(
        "finch-burst-packet",
        "Very short five-syllable burst packet with hard onset chips and payload-bent middle notes.",
        MimirBioacousticContestantKind.FinchBurstPacket,
        SyllableCount: 5,
        MotifDurationSeconds: 0.078,
        EventSpacingSeconds: 0.105,
        LowestRootHz: 3_050.0,
        HighestRootHz: 11_400.0,
        Gain: 0.023,
        PayloadBitsPerEvent: 8,
        "Throughput brute made musical enough to tolerate: fewer syllables, sharper anchors, and more packets per second.");

    public static IReadOnlyList<MimirBioacousticContestantProfile> BuiltIn { get; } =
    [
        CurrentBirdcall,
        RedpollTrill,
        RobinWarble,
        ThrushLadder,
        ThornbillZigZag,
        NightingaleCascade,
        AquaSynthFormantWeaver,
        CanaryPacketTrill,
        FinchBurstPacket
    ];
}

public sealed class MimirBioacousticContestantRenderer(MimirBioacousticContestantProfile profile)
{
    public MimirBioacousticContestantProfile Profile { get; } = profile;

    public double EventStartSeconds(ulong eventIndex) =>
        MimirBioacousticContestants.FirstEventSeconds + eventIndex * Profile.EventSpacingSeconds;

    public float[] RenderEventMonoFloat(ulong eventIndex, int sampleRate)
    {
        return RenderEventMonoFloat(eventIndex, sampleRate, PayloadSymbolForEvent(eventIndex));
    }

    public float[] RenderEventMonoFloat(ulong eventIndex, int sampleRate, int payloadSymbol)
    {
        var samples = new float[Math.Max(1, (int)Math.Round(Profile.MotifDurationSeconds * sampleRate))];
        AddEvent(samples, sampleRate, eventIndex, payloadSymbol, EventStartSeconds(eventIndex), EventStartSeconds(eventIndex));
        return samples;
    }

    public float[] RenderSequenceMonoFloat(double seconds, int sampleRate)
    {
        var samples = new float[Math.Max(1, (int)Math.Round(seconds * sampleRate))];
        var lastEvent = Math.Max(0, (int)Math.Ceiling((seconds - MimirBioacousticContestants.FirstEventSeconds) / Profile.EventSpacingSeconds) + 2);
        for (var eventIndex = 0; eventIndex <= lastEvent; eventIndex++)
        {
            var payload = PayloadSymbolForEvent((ulong)eventIndex);
            AddEvent(samples, sampleRate, (ulong)eventIndex, payload, EventStartSeconds((ulong)eventIndex), 0.0);
        }

        return samples;
    }

    public int ExpectedEventCount(double seconds) =>
        Math.Max(0, (int)Math.Floor((seconds - MimirBioacousticContestants.FirstEventSeconds - Profile.MotifDurationSeconds) / Profile.EventSpacingSeconds) + 1);

    public IReadOnlySet<ulong> ExpectedEvents(double seconds) =>
        Enumerable.Range(0, ExpectedEventCount(seconds)).Select(index => (ulong)index).ToHashSet();

    public int PayloadSymbolForEvent(ulong eventIndex)
    {
        var mask = (1 << Math.Clamp(Profile.PayloadBitsPerEvent, 0, 15)) - 1;
        return mask == 0 ? 0 : (int)(Mix((int)(eventIndex & 0x7fff), (uint)Profile.Kind + 0xC0DEu) & (uint)mask);
    }

    private void AddEvent(float[] output, int sampleRate, ulong eventIndex, int payload, double eventStartSeconds, double bufferStartSeconds)
    {
        var symbolId = SymbolForEvent(eventIndex);
        var root = RootForSymbol(symbolId);
        var rng = Mix(symbolId ^ (payload << 9), (uint)Profile.Kind);
        for (var syllable = 0; syllable < Profile.SyllableCount; syllable++)
        {
            var contour = SyllableContour(symbolId, payload, syllable, root, rng);
            var start = eventStartSeconds - bufferStartSeconds + contour.StartSeconds;
            AddSyllable(output, sampleRate, start, contour.DurationSeconds, contour.StartHz, contour.EndHz, Profile.Gain * contour.Weight);
        }
    }

    private (double StartSeconds, double DurationSeconds, double StartHz, double EndHz, double Weight) SyllableContour(
        int symbolId,
        int payload,
        int syllable,
        double root,
        uint rng)
    {
        var jitter = (((rng >> (syllable % 12)) & 7) - 3) * 0.0013;
        var phase = ((symbolId * 17 + syllable * 31) & 255) / 255.0;
        var payloadPhase = ((payload * 29 + syllable * 47) & 255) / 255.0;
        var payloadStep = ((payload >> (syllable % Math.Max(1, Profile.PayloadBitsPerEvent))) & 7) - 3;
        var payloadPair = (payload >> ((syllable * 2) % Math.Max(1, Profile.PayloadBitsPerEvent))) & 3;
        var payloadCross = (payload >> ((syllable * 2 + 1) % Math.Max(1, Profile.PayloadBitsPerEvent))) & 3;
        var payloadTone = payload & 3;
        var payloadBand = (payload >> 2) & 1;
        return Profile.Kind switch
        {
            MimirBioacousticContestantKind.RedpollTrill => (
                StartSeconds: syllable * Profile.MotifDurationSeconds / (Profile.SyllableCount + 0.7) + jitter,
                DurationSeconds: Profile.MotifDurationSeconds * 0.095,
                StartHz: root * (1.0 + 0.018 * syllable + 0.025 * Math.Sin(phase * Math.Tau)),
                EndHz: root * (1.0 + 0.030 * syllable + 0.018 * Math.Cos(phase * Math.Tau)),
                Weight: syllable % 2 == 0 ? 0.95 : 0.72),
            MimirBioacousticContestantKind.RobinWarble => (
                StartSeconds: syllable * Profile.MotifDurationSeconds / (Profile.SyllableCount + 0.35) + jitter * 1.4,
                DurationSeconds: Profile.MotifDurationSeconds * (0.115 + 0.015 * ((symbolId + syllable) % 3)),
                StartHz: root * Ratio(0.72 + 0.08 * ((symbolId >> (syllable % 5)) & 3)),
                EndHz: root * Ratio(1.05 + 0.10 * ((symbolId + syllable * 3) & 3)),
                Weight: 0.62 + 0.30 * Math.Sin((phase + 0.2) * Math.Tau) * Math.Sin((phase + 0.2) * Math.Tau)),
            MimirBioacousticContestantKind.ThrushLadder => (
                StartSeconds: syllable * Profile.MotifDurationSeconds / (Profile.SyllableCount + 0.5),
                DurationSeconds: Profile.MotifDurationSeconds * 0.13,
                StartHz: root * Ratio(0.75 + 0.16 * ((syllable + (symbolId & 3)) % 5)),
                EndHz: root * Ratio(0.78 + 0.16 * ((syllable + 1 + ((symbolId >> 2) & 3)) % 5)),
                Weight: syllable is 0 or 2 ? 1.0 : 0.70),
            MimirBioacousticContestantKind.ThornbillZigZag => (
                StartSeconds: syllable * Profile.MotifDurationSeconds / (Profile.SyllableCount + 0.2) + jitter * 0.6,
                DurationSeconds: Profile.MotifDurationSeconds * 0.075,
                StartHz: root * (syllable % 2 == 0 ? 0.88 : 1.18) * (1.0 + 0.010 * (symbolId & 7)),
                EndHz: root * (syllable % 2 == 0 ? 1.22 : 0.92) * (1.0 + 0.012 * ((symbolId >> 3) & 7)),
                Weight: 0.75),
            MimirBioacousticContestantKind.NightingaleCascade => (
                StartSeconds: syllable * Profile.MotifDurationSeconds / (Profile.SyllableCount + 0.55) + jitter,
                DurationSeconds: Profile.MotifDurationSeconds * 0.095,
                StartHz: root * Ratio(0.62 + 0.09 * ((symbolId + syllable) % 8)),
                EndHz: root * Ratio(1.20 - 0.07 * ((symbolId / 3 + syllable) % 7)),
                Weight: syllable is 2 or 5 or 8 ? 0.95 : 0.58),
            MimirBioacousticContestantKind.AquaSynthFormantWeaver => (
                StartSeconds: syllable * Profile.MotifDurationSeconds / (Profile.SyllableCount + 0.45) +
                    0.0022 * Math.Sin((payloadPhase + syllable * 0.11) * Math.Tau),
                DurationSeconds: Profile.MotifDurationSeconds * (0.088 + 0.018 * ((payload + syllable) & 3)),
                StartHz: root * Ratio(-2.5 + 0.72 * syllable + payloadStep * 0.21 + 0.36 * Math.Sin(payloadPhase * Math.Tau)),
                EndHz: root * Ratio(-1.4 + 0.58 * ((syllable + 2) % Profile.SyllableCount) - payloadStep * 0.18 + 0.42 * Math.Cos(payloadPhase * Math.Tau)),
                Weight: syllable is 0 or 3 or 6 ? 0.94 : 0.56 + 0.22 * Math.Sin(payloadPhase * Math.Tau) * Math.Sin(payloadPhase * Math.Tau)),
            MimirBioacousticContestantKind.CanaryPacketTrill => (
                StartSeconds: syllable * Profile.MotifDurationSeconds / (Profile.SyllableCount + 0.75) +
                    (syllable < 2 ? 0.0 : 0.0012 * (payloadTone - 1.5)),
                DurationSeconds: Profile.MotifDurationSeconds * (syllable < 2 ? 0.070 : 0.082 + 0.006 * payloadTone),
                StartHz: root * Ratio((syllable < 2 ? -1.0 + syllable * 1.85 : -0.55 + syllable * 0.52) + payloadTone * 0.72),
                EndHz: root * Ratio((syllable < 2 ? 1.25 + syllable * 1.35 : 0.10 + syllable * 0.19) + payloadTone * 0.58),
                Weight: syllable < 2 ? 1.0 : 0.62 + 0.10 * payloadTone),
            MimirBioacousticContestantKind.FinchBurstPacket => (
                StartSeconds: syllable * Profile.MotifDurationSeconds / (Profile.SyllableCount + 0.65) +
                    (syllable is 0 or 1 ? 0.0 : 0.0011 * Math.Sin((payloadPhase + 0.17) * Math.Tau)),
                DurationSeconds: Profile.MotifDurationSeconds * (syllable is 0 or 1 ? 0.064 : 0.078 + 0.008 * ((payload >> syllable) & 1)),
                StartHz: root * Ratio(-0.70 + syllable * 0.92 + payloadStep * 0.20 + 0.18 * Math.Sin(payloadPhase * Math.Tau)),
                EndHz: root * Ratio(0.95 + syllable * 0.44 - payloadStep * 0.14 + 0.22 * Math.Cos(payloadPhase * Math.Tau)),
                Weight: syllable is 0 or 1 ? 1.0 : 0.70 + 0.16 * (((payload >> (syllable + 2)) & 1) == 0 ? 0.0 : 1.0)),
            _ => (
                StartSeconds: syllable * Profile.MotifDurationSeconds / (Profile.SyllableCount + 0.6) + jitter,
                DurationSeconds: Profile.MotifDurationSeconds * 0.11,
                StartHz: root * Ratio(0.80 + 0.11 * ((symbolId + syllable) & 3)),
                EndHz: root * Ratio(0.92 + 0.10 * (((symbolId >> 2) + syllable * 2) & 3)),
                Weight: syllable is 0 or 3 ? 0.92 : 0.68)
        };
    }

    private static int SymbolForEvent(ulong eventIndex) =>
        (int)((eventIndex * 73UL + eventIndex / 5UL * 19UL) % MimirBioacousticContestants.SymbolCount);

    private double RootForSymbol(int symbolId)
    {
        var t = symbolId / (double)(MimirBioacousticContestants.SymbolCount - 1);
        var logMin = Math.Log(Profile.LowestRootHz);
        var logMax = Math.Log(Profile.HighestRootHz);
        return Math.Exp(logMin + (logMax - logMin) * t);
    }

    private static double Ratio(double semitones) => Math.Pow(2.0, semitones / 12.0);

    private static uint Mix(int symbolId, uint salt)
    {
        var value = (uint)symbolId * 0x9E3779B9u + salt * 0x85EBCA6Bu;
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        return value;
    }

    private static void AddSyllable(
        float[] output,
        int sampleRate,
        double startSeconds,
        double durationSeconds,
        double startHz,
        double endHz,
        double gain)
    {
        var startSample = Math.Max(0, (int)Math.Floor(startSeconds * sampleRate));
        var endSample = Math.Min(output.Length, (int)Math.Ceiling((startSeconds + durationSeconds) * sampleRate));
        if (endSample <= startSample)
        {
            return;
        }

        var phase = 0.0;
        var previousFrequency = startHz;
        for (var sample = startSample; sample < endSample; sample++)
        {
            var t = (sample / (double)sampleRate - startSeconds) / durationSeconds;
            var curved = t * t * (3.0 - 2.0 * t);
            var frequency = startHz + (endHz - startHz) * curved;
            phase += Math.Tau * (previousFrequency + frequency) * 0.5 / sampleRate;
            previousFrequency = frequency;
            var envelope = Math.Sin(Math.PI * Math.Clamp(t, 0.0, 1.0));
            var harmonic = 0.28 * Math.Sin(phase * 2.0 + 0.25) + 0.12 * Math.Sin(phase * 3.0 + 0.7);
            output[sample] += (float)(gain * envelope * (Math.Sin(phase) + harmonic));
        }
    }
}
