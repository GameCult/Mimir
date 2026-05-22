namespace Mimir.Runtime.Synchronization;

public sealed record MimirChirpletTone(
    double StartSeconds,
    double DurationSeconds,
    double StartHz,
    double EndHz,
    double Gain = 1.0)
{
    public double CenterHz => (StartHz + EndHz) * 0.5;
}

public sealed record MimirChirpletBandResponse(
    double CenterHz,
    double Energy);

public sealed record MimirChirpletTimelineEvent(
    ulong Index,
    int SymbolId,
    double StartSeconds,
    MimirChirpletTone Tone);

public sealed record MimirChirpletEventObservation(
    ulong EventIndex,
    int SymbolId,
    double TimelineSeconds,
    double SampleOffset,
    double Energy);

public sealed record MimirChirpletTimelinePlacement(
    double TimelineSecondsAtWindowStart,
    double Confidence,
    IReadOnlyList<MimirChirpletEventObservation> Observations);

public sealed class MimirChirpletTimeline
{
    public const int SampleRate = 48_000;
    public const double SegmentSeconds = 0.5;
    public const double QueueLeadSeconds = 1.0;
    public const int SymbolCount = 32;
    public const int TimelineOrder = 3;
    public static readonly int TimelinePeriod = (int)Math.Pow(SymbolCount, TimelineOrder);

    private const double FirstEventSeconds = 0.08;
    private const double MinEventGapSeconds = 0.088;
    private const double MaxEventGapSeconds = 0.162;
    private const double MaxEventDurationSeconds = 0.078;
    private const double Gain = 0.090;

    private static readonly int[] TimelineSymbols = RotateToDistinctOpening(BuildDeBruijn(SymbolCount, TimelineOrder));
    private static readonly Dictionary<int, int> TripleToIndex = BuildTripleIndex(TimelineSymbols);
    private static readonly double[] PeriodEventStarts = BuildPeriodEventStarts(TimelineSymbols);
    private static readonly double PeriodDurationSeconds = PeriodEventStarts[^1] + GapSecondsForSymbol(TimelineSymbols[^1]);
    private static readonly MimirChirpletTone[] SymbolTones = BuildSymbolTones();
    private static readonly MimirChirpletTone[] CoarseTones =
    [
        new(0.0, 0.048, 6_700.0, 7_200.0, 0.75),
        new(0.0, 0.052, 7_700.0, 8_400.0, 0.80),
        new(0.0, 0.056, 8_900.0, 9_800.0, 0.85),
        new(0.0, 0.060, 10_300.0, 11_400.0, 0.85),
        new(0.0, 0.064, 12_100.0, 13_500.0, 0.90),
        new(0.0, 0.068, 14_200.0, 15_900.0, 0.80),
    ];
    private readonly IReadOnlyList<float[]> symbolKernels;
    private readonly IReadOnlyList<float[]> coarseKernels;

    private MimirChirpletTimeline()
    {
        symbolKernels = SymbolTones.Select(symbol => RenderToneKernel(symbol, SampleRate)).ToArray();
        coarseKernels = CoarseTones.Select(tone => RenderToneKernel(tone, SampleRate)).ToArray();
    }

    public static MimirChirpletTimeline Default { get; } = new();

    public float[] RenderSegmentMonoFloat(ulong segmentIndex)
    {
        var segmentStartSeconds = segmentIndex * SegmentSeconds;
        var samples = new float[(int)Math.Round(SegmentSeconds * SampleRate)];
        foreach (var timelineEvent in EventsOverlapping(segmentStartSeconds, SegmentSeconds))
        {
            AddTone(samples, SampleRate, timelineEvent.Tone, segmentStartSeconds);
        }

        var peak = samples.Select(Math.Abs).DefaultIfEmpty(0.0f).Max();
        if (peak <= 1.0e-9f)
        {
            return samples;
        }

        var scale = (float)(Gain / peak);
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] *= scale;
        }

        return samples;
    }

    public string RenderSegmentPcm16Base64(ulong segmentIndex)
    {
        var samples = RenderSegmentMonoFloat(segmentIndex);
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = (short)Math.Round(Math.Clamp(samples[index], -1.0f, 1.0f) * short.MaxValue);
            bytes[index * sizeof(short)] = (byte)(sample & 0xff);
            bytes[index * sizeof(short) + 1] = (byte)((sample >> 8) & 0xff);
        }

        return Convert.ToBase64String(bytes);
    }

    public float[] BuildTimelineEnergyTrace(ReadOnlySpan<float> samples, int sampleRate, int hopSamples)
    {
        return ContrastNormalize(BuildEnvelopeEnergyTrace(samples, sampleRate, hopSamples));
    }

    public IReadOnlyList<MimirChirpletBandResponse> EstimateBandResponse(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        var responses = new List<MimirChirpletBandResponse>(SymbolTones.Length);
        for (var symbol = 0; symbol < SymbolTones.Length; symbol++)
        {
            var tone = SymbolTones[symbol];
            var kernel = sampleRate == SampleRate ? symbolKernels[symbol] : RenderToneKernel(tone, sampleRate);
            var energy = MaxMatchedEnergy(samples, kernel, Math.Max(1, sampleRate / 1_000));
            responses.Add(new MimirChirpletBandResponse(tone.CenterHz, energy));
        }

        return responses;
    }

    public MimirChirpletStreamDecode DecodeStreamWindow(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return new MimirChirpletStreamDecode([], [], null);
        }

        var symbols = DetectSymbolEvents(samples, sampleRate, Math.Max(1, sampleRate / 200))
            .Select(observation => new MimirChirpletSymbolObservation(
                observation.SymbolId,
                observation.SampleOffset,
                observation.Energy))
            .ToArray();
        var anchors = PlaceAllTripletAnchors(symbols, sampleRate);
        var clock = FitClock(anchors, sampleRate);
        return new MimirChirpletStreamDecode(symbols, anchors, clock);
    }

    public MimirChirpletTimelinePlacement DecodeReferenceWindow(
        ReadOnlySpan<float> samples,
        int sampleRate,
        double approximateWindowEndTimelineSeconds)
    {
        if (samples.Length == 0)
        {
            return new MimirChirpletTimelinePlacement(0.0, 0.0, []);
        }

        var observed = DetectSymbolEvents(samples, sampleRate, Math.Max(1, sampleRate / 200));
        var placement = PlaceObservedSymbols(observed, sampleRate);
        if (placement.Observations.Count >= 3 || !double.IsFinite(approximateWindowEndTimelineSeconds))
        {
            return placement;
        }

        var windowSeconds = samples.Length / (double)sampleRate;
        var approximateStartSeconds = Math.Max(0.0, approximateWindowEndTimelineSeconds - windowSeconds);
        var fallback = new List<MimirChirpletEventObservation>();
        foreach (var timelineEvent in EventsOverlapping(approximateStartSeconds - 0.25, windowSeconds + 0.5))
        {
            var predictedSample = (timelineEvent.StartSeconds - approximateStartSeconds) * sampleRate;
            var observation = ObserveEvent(samples, sampleRate, timelineEvent, predictedSample, searchRadiusSamples: Math.Max(8, sampleRate / 2));
            if (observation != null)
            {
                fallback.Add(observation);
            }
        }

        if (fallback.Count < 3)
        {
            return new MimirChirpletTimelinePlacement(approximateStartSeconds, 0.0, fallback);
        }

        return FitTimelinePlacement(fallback, sampleRate);
    }

    public IReadOnlyList<MimirChirpletEventObservation> ObserveKnownEvents(
        ReadOnlySpan<float> samples,
        int sampleRate,
        IEnumerable<MimirChirpletEventObservation> referenceObservations,
        double coarseDelaySamples)
    {
        var observations = new List<MimirChirpletEventObservation>();
        foreach (var reference in referenceObservations)
        {
            var timelineEvent = EventForIndex(reference.EventIndex);
            var predictedSample = reference.SampleOffset + coarseDelaySamples;
            var observation = ObserveEvent(samples, sampleRate, timelineEvent, predictedSample, searchRadiusSamples: Math.Max(16, sampleRate / 10));
            if (observation != null)
            {
                observations.Add(observation);
            }
        }

        return observations;
    }

    public MimirChirpletTimelineEvent EventForIndex(ulong eventIndex)
    {
        var symbolId = SymbolForEvent(eventIndex);
        var tone = SymbolTones[symbolId] with { StartSeconds = EventStartSeconds(eventIndex) };
        return new MimirChirpletTimelineEvent(eventIndex, symbolId, tone.StartSeconds, tone);
    }

    public IReadOnlyList<MimirChirpletTimelineEvent> EventsOverlapping(double startSeconds, double durationSeconds)
    {
        var endSeconds = startSeconds + durationSeconds;
        var firstIndex = Math.Max(0, (long)Math.Floor((startSeconds - FirstEventSeconds - MaxEventDurationSeconds) / MaxEventGapSeconds) - 2);
        var lastIndex = Math.Max(firstIndex, (long)Math.Ceiling((endSeconds - FirstEventSeconds) / MinEventGapSeconds) + 2);
        var events = new List<MimirChirpletTimelineEvent>((int)(lastIndex - firstIndex + 1));
        for (var eventIndex = firstIndex; eventIndex <= lastIndex; eventIndex++)
        {
            var timelineEvent = EventForIndex((ulong)eventIndex);
            if (timelineEvent.StartSeconds < endSeconds &&
                timelineEvent.StartSeconds + timelineEvent.Tone.DurationSeconds > startSeconds)
            {
                events.Add(timelineEvent);
            }
        }

        return events;
    }

    private IReadOnlyList<MimirChirpletEventObservation> DetectSymbolEvents(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int hopSamples)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        var coarse = ContrastNormalize(BuildEnvelopeEnergyTrace(samples, sampleRate, hopSamples));
        if (coarse.Length <= 2)
        {
            return [];
        }

        var candidates = new List<MimirChirpletEventObservation>();
        for (var frame = 1; frame < coarse.Length - 1; frame++)
        {
            if (coarse[frame] < 0.055 ||
                coarse[frame] < coarse[frame - 1] ||
                coarse[frame] < coarse[frame + 1])
            {
                continue;
            }

            var observation = ClassifySymbolAt(
                samples,
                sampleRate,
                frame * hopSamples,
                Math.Max(hopSamples * 3, sampleRate / 100));
            if (observation != null)
            {
                candidates.Add(observation);
            }
        }

        return SuppressNearbyDetections(candidates, sampleRate);
    }

    private MimirChirpletEventObservation? ClassifySymbolAt(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int predictedOffset,
        int searchRadiusSamples)
    {
        var bestSymbol = -1;
        var bestOffset = predictedOffset;
        var bestEnergy = 0.0;
        for (var symbol = 0; symbol < SymbolTones.Length; symbol++)
        {
            var tone = SymbolTones[symbol];
            var kernel = sampleRate == SampleRate ? symbolKernels[symbol] : RenderToneKernel(tone, sampleRate);
            if (kernel.Length == 0 || samples.Length < kernel.Length)
            {
                continue;
            }

            var start = Math.Max(0, predictedOffset - searchRadiusSamples);
            var end = Math.Min(samples.Length - kernel.Length, predictedOffset + searchRadiusSamples);
            for (var offset = start; offset <= end; offset += Math.Max(1, searchRadiusSamples / 2))
            {
                var energy = DirectionalMatchedEnergy(samples.Slice(offset, kernel.Length), kernel);
                if (energy > bestEnergy)
                {
                    bestEnergy = energy;
                    bestSymbol = symbol;
                    bestOffset = offset;
                }
            }
        }

        return bestSymbol < 0 || bestEnergy < 0.25
            ? null
            : new MimirChirpletEventObservation(0, bestSymbol, 0.0, bestOffset, bestEnergy);
    }

    private static IReadOnlyList<MimirChirpletEventObservation> SuppressNearbyDetections(
        IReadOnlyList<MimirChirpletEventObservation> observations,
        int sampleRate)
    {
        if (observations.Count == 0)
        {
            return [];
        }

        var minimumSpacingSamples = MinEventGapSeconds * sampleRate * 0.55;
        var kept = new List<MimirChirpletEventObservation>();
        foreach (var observation in observations.OrderByDescending(observation => observation.Energy))
        {
            if (kept.Any(existing => Math.Abs(existing.SampleOffset - observation.SampleOffset) < minimumSpacingSamples))
            {
                continue;
            }

            kept.Add(observation);
        }

        return kept.OrderBy(observation => observation.SampleOffset).ToArray();
    }

    private static MimirChirpletTimelinePlacement PlaceObservedSymbols(
        IReadOnlyList<MimirChirpletEventObservation> observations,
        int sampleRate)
    {
        if (observations.Count < 3)
        {
            return new MimirChirpletTimelinePlacement(0.0, 0.0, observations);
        }

        var best = Array.Empty<MimirChirpletEventObservation>();
        var bestConfidence = 0.0;
        for (var index = 0; index <= observations.Count - 3; index++)
        {
            var first = observations[index];
            var second = observations[index + 1];
            var third = observations[index + 2];
            if (!IsConsecutiveByTime(first, second, sampleRate) ||
                !IsConsecutiveByTime(second, third, sampleRate))
            {
                continue;
            }

            var code = TripleCode(first.SymbolId, second.SymbolId, third.SymbolId);
            if (!TripleToIndex.TryGetValue(code, out var eventIndex))
            {
                continue;
            }

            var placed = new[]
            {
                PlaceObservation(first, (ulong)eventIndex),
                PlaceObservation(second, (ulong)eventIndex + 1UL),
                PlaceObservation(third, (ulong)eventIndex + 2UL),
            };
            var confidence = placed.Average(observation => observation.Energy);
            if (confidence > bestConfidence)
            {
                bestConfidence = confidence;
                best = placed;
            }
        }

        return best.Length == 0
            ? new MimirChirpletTimelinePlacement(0.0, 0.0, observations)
            : FitTimelinePlacement(best, sampleRate);
    }

    private static IReadOnlyList<MimirChirpletTimelineAnchor> PlaceAllTripletAnchors(
        IReadOnlyList<MimirChirpletSymbolObservation> symbols,
        int sampleRate)
    {
        if (symbols.Count < TimelineOrder)
        {
            return [];
        }

        var anchors = new List<MimirChirpletTimelineAnchor>();
        for (var index = 0; index <= symbols.Count - TimelineOrder; index++)
        {
            var first = symbols[index];
            var second = symbols[index + 1];
            var third = symbols[index + 2];
            if (!IsConsecutiveByTime(first, second, sampleRate) ||
                !IsConsecutiveByTime(second, third, sampleRate))
            {
                continue;
            }

            var code = TripleCode(first.SymbolId, second.SymbolId, third.SymbolId);
            if (!TripleToIndex.TryGetValue(code, out var eventIndex))
            {
                continue;
            }

            var timelineEvent = Default.EventForIndex((ulong)eventIndex);
            var predictedSecond = Default.EventForIndex((ulong)eventIndex + 1UL);
            var predictedThird = Default.EventForIndex((ulong)eventIndex + 2UL);
            var measuredGapA = (second.SampleOffset - first.SampleOffset) / sampleRate;
            var measuredGapB = (third.SampleOffset - second.SampleOffset) / sampleRate;
            var expectedGapA = predictedSecond.StartSeconds - timelineEvent.StartSeconds;
            var expectedGapB = predictedThird.StartSeconds - predictedSecond.StartSeconds;
            var gapError = Math.Abs(measuredGapA - expectedGapA) + Math.Abs(measuredGapB - expectedGapB);
            var gapConfidence = 1.0 / (1.0 + gapError / 0.006);
            if (gapConfidence < 0.45)
            {
                continue;
            }

            var energyConfidence = Math.Clamp((first.Energy + second.Energy + third.Energy) / 3.0, 0.0, 1.0);
            anchors.Add(new MimirChirpletTimelineAnchor(
                (ulong)eventIndex,
                timelineEvent.StartSeconds,
                first.SampleOffset,
                gapConfidence * 0.65 + energyConfidence * 0.35,
                [first, second, third]));
        }

        return anchors
            .GroupBy(anchor => anchor.EventIndex)
            .Select(group => group.OrderByDescending(anchor => anchor.Confidence).First())
            .OrderBy(anchor => anchor.SampleOffset)
            .ToArray();
    }

    private static MimirChirpletClockFit? FitClock(
        IReadOnlyList<MimirChirpletTimelineAnchor> anchors,
        int sampleRate)
    {
        if (anchors.Count == 0)
        {
            return null;
        }

        if (anchors.Count == 1)
        {
            var anchor = anchors[0];
            return new MimirChirpletClockFit(
                anchor.SampleOffset - anchor.TimelineSeconds * sampleRate,
                sampleRate,
                anchor.Confidence * 0.35,
                1,
                0.0);
        }

        var totalWeight = anchors.Sum(anchor => Math.Max(1.0e-6, anchor.Confidence));
        var meanTimeline = anchors.Sum(anchor => anchor.TimelineSeconds * Math.Max(1.0e-6, anchor.Confidence)) / totalWeight;
        var meanSample = anchors.Sum(anchor => anchor.SampleOffset * Math.Max(1.0e-6, anchor.Confidence)) / totalWeight;
        var covariance = 0.0;
        var variance = 0.0;
        foreach (var anchor in anchors)
        {
            var weight = Math.Max(1.0e-6, anchor.Confidence);
            var dt = anchor.TimelineSeconds - meanTimeline;
            covariance += weight * dt * (anchor.SampleOffset - meanSample);
            variance += weight * dt * dt;
        }

        var effectiveSampleRate = variance > 1.0e-12 ? covariance / variance : sampleRate;
        if (!double.IsFinite(effectiveSampleRate) ||
            effectiveSampleRate < sampleRate * 0.90 ||
            effectiveSampleRate > sampleRate * 1.10)
        {
            effectiveSampleRate = sampleRate;
        }

        var sourceOffset = meanSample - effectiveSampleRate * meanTimeline;
        var meanAbsoluteError = anchors.Sum(anchor =>
        {
            var weight = Math.Max(1.0e-6, anchor.Confidence);
            var predicted = sourceOffset + anchor.TimelineSeconds * effectiveSampleRate;
            return Math.Abs(anchor.SampleOffset - predicted) * weight;
        }) / totalWeight;
        var residualConfidence = 1.0 / (1.0 + meanAbsoluteError / Math.Max(1.0, sampleRate * 0.0015));
        var countConfidence = Math.Clamp(anchors.Count / 12.0, 0.0, 1.0);
        var anchorConfidence = Math.Clamp(anchors.Average(anchor => anchor.Confidence), 0.0, 1.0);
        return new MimirChirpletClockFit(
            sourceOffset,
            effectiveSampleRate,
            residualConfidence * 0.45 + countConfidence * 0.25 + anchorConfidence * 0.30,
            anchors.Count,
            meanAbsoluteError);
    }

    private static bool IsConsecutiveByTime(
        MimirChirpletEventObservation first,
        MimirChirpletEventObservation second,
        int sampleRate)
    {
        var seconds = (second.SampleOffset - first.SampleOffset) / sampleRate;
        return seconds >= MinEventGapSeconds * 0.55 && seconds <= MaxEventGapSeconds * 1.45;
    }

    private static bool IsConsecutiveByTime(
        MimirChirpletSymbolObservation first,
        MimirChirpletSymbolObservation second,
        int sampleRate)
    {
        var seconds = (second.SampleOffset - first.SampleOffset) / sampleRate;
        return seconds >= MinEventGapSeconds * 0.55 && seconds <= MaxEventGapSeconds * 1.45;
    }

    private static MimirChirpletEventObservation PlaceObservation(
        MimirChirpletEventObservation observation,
        ulong eventIndex)
    {
        var timelineEvent = Default.EventForIndex(eventIndex);
        return observation with
        {
            EventIndex = eventIndex,
            SymbolId = timelineEvent.SymbolId,
            TimelineSeconds = timelineEvent.StartSeconds,
        };
    }

    private static MimirChirpletTimelinePlacement FitTimelinePlacement(
        IReadOnlyList<MimirChirpletEventObservation> observations,
        int sampleRate)
    {
        if (observations.Count == 0)
        {
            return new MimirChirpletTimelinePlacement(0.0, 0.0, []);
        }

        var offsets = observations
            .Select(observation => observation.TimelineSeconds - observation.SampleOffset / sampleRate)
            .ToArray();
        var timelineStart = Median(offsets);
        var residualSeconds = observations
            .Average(observation => Math.Abs(observation.TimelineSeconds - observation.SampleOffset / sampleRate - timelineStart));
        var residualConfidence = 1.0 / (1.0 + residualSeconds / 0.004);
        var countConfidence = Math.Clamp(observations.Count / 6.0, 0.0, 1.0);
        var energyConfidence = Math.Clamp(observations.Average(observation => observation.Energy), 0.0, 1.0);
        return new MimirChirpletTimelinePlacement(
            timelineStart,
            residualConfidence * 0.45 + countConfidence * 0.25 + energyConfidence * 0.30,
            observations);
    }

    private static MimirChirpletEventObservation? ObserveEvent(
        ReadOnlySpan<float> samples,
        int sampleRate,
        MimirChirpletTimelineEvent timelineEvent,
        double predictedSample,
        int searchRadiusSamples)
    {
        var kernel = RenderToneKernel(timelineEvent.Tone with { StartSeconds = 0.0 }, sampleRate);
        if (kernel.Length == 0 || samples.Length < kernel.Length)
        {
            return null;
        }

        var predictedOffset = (int)Math.Round(predictedSample);
        var start = Math.Max(0, predictedOffset - searchRadiusSamples);
        var end = Math.Min(samples.Length - kernel.Length, predictedOffset + searchRadiusSamples);
        if (end < start)
        {
            return null;
        }

        var bestOffset = start;
        var bestEnergy = 0.0;
        var step = Math.Max(1, sampleRate / 4_000);
        for (var offset = start; offset <= end; offset += step)
        {
                var energy = DirectionalMatchedEnergy(samples.Slice(offset, kernel.Length), kernel);
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestOffset = offset;
            }
        }

        if (bestEnergy < 0.045)
        {
            return null;
        }

        return new MimirChirpletEventObservation(
            timelineEvent.Index,
            timelineEvent.SymbolId,
            timelineEvent.StartSeconds,
            bestOffset,
            bestEnergy);
    }

    private static int SymbolForEvent(ulong eventIndex) =>
        TimelineSymbols[(int)(eventIndex % (ulong)TimelineSymbols.Length)];

    private static double EventStartSeconds(ulong eventIndex) =>
        FirstEventSeconds +
        (eventIndex / (ulong)TimelineSymbols.Length) * PeriodDurationSeconds +
        PeriodEventStarts[(int)(eventIndex % (ulong)TimelineSymbols.Length)];

    private static MimirChirpletTone[] BuildSymbolTones()
    {
        var tones = new MimirChirpletTone[SymbolCount];
        for (var symbol = 0; symbol < tones.Length; symbol++)
        {
            var startBand = symbol & 7;
            var glideClass = (symbol >> 3) & 3;
            var durationClass = (symbol >> 1) & 3;
            var startHz = 6_500.0 * Math.Pow(15_800.0 / 6_500.0, startBand / 7.0);
            var duration = 0.042 + durationClass * 0.009;
            var glideSemitones = glideClass switch
            {
                0 => 1.75,
                1 => -2.25,
                2 => 3.50,
                _ => -4.00,
            };
            var endHz = startHz * Math.Pow(2.0, glideSemitones / 12.0);
            startHz = Math.Clamp(startHz, 6_300.0, 16_500.0);
            endHz = Math.Clamp(endHz, 6_300.0, 16_500.0);
            tones[symbol] = new MimirChirpletTone(0.0, duration, startHz, endHz, 0.82);
        }

        return tones;
    }

    private static int[] BuildDeBruijn(int alphabetSize, int order)
    {
        var a = new int[alphabetSize * order];
        var sequence = new List<int>(TimelinePeriod);

        void Db(int t, int p)
        {
            if (t > order)
            {
                if (order % p == 0)
                {
                    for (var index = 1; index <= p; index++)
                    {
                        sequence.Add(a[index]);
                    }
                }

                return;
            }

            a[t] = a[t - p];
            Db(t + 1, p);
            for (var j = a[t - p] + 1; j < alphabetSize; j++)
            {
                a[t] = j;
                Db(t + 1, t);
            }
        }

        Db(1, 1);
        return sequence.ToArray();
    }

    private static double[] BuildPeriodEventStarts(IReadOnlyList<int> symbols)
    {
        var starts = new double[symbols.Count];
        var cursor = 0.0;
        for (var index = 0; index < symbols.Count; index++)
        {
            starts[index] = cursor;
            cursor += GapSecondsForSymbol(symbols[index]);
        }

        return starts;
    }

    private static double GapSecondsForSymbol(int symbol)
    {
        var gapClass = (symbol >> 2) & 3;
        return gapClass switch
        {
            0 => 0.094,
            1 => 0.112,
            2 => 0.137,
            _ => 0.158,
        };
    }

    private static int[] RotateToDistinctOpening(int[] sequence)
    {
        for (var index = 0; index < sequence.Length; index++)
        {
            var first = sequence[index];
            var second = sequence[(index + 1) % sequence.Length];
            var third = sequence[(index + 2) % sequence.Length];
            if (first != second && first != third && second != third)
            {
                return sequence.Skip(index).Concat(sequence.Take(index)).ToArray();
            }
        }

        return sequence;
    }

    private static Dictionary<int, int> BuildTripleIndex(IReadOnlyList<int> symbols)
    {
        var map = new Dictionary<int, int>(symbols.Count);
        for (var index = 0; index < symbols.Count; index++)
        {
            var code = TripleCode(
                symbols[index],
                symbols[(index + 1) % symbols.Count],
                symbols[(index + 2) % symbols.Count]);
            map[code] = index;
        }

        return map;
    }

    private static int TripleCode(int first, int second, int third) =>
        first * SymbolCount * SymbolCount + second * SymbolCount + third;

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0.0;
        }

        Array.Sort(values);
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) * 0.5
            : values[middle];
    }

    private static float[] RenderToneKernel(MimirChirpletTone tone, int sampleRate)
    {
        var frameCount = Math.Max(1, (int)Math.Ceiling(tone.DurationSeconds * sampleRate));
        var samples = new float[frameCount];
        AddTone(samples, sampleRate, tone with { StartSeconds = 0.0 }, segmentStartSeconds: 0.0);
        return samples;
    }

    private static void AddTone(float[] samples, int sampleRate, MimirChirpletTone tone, double segmentStartSeconds)
    {
        var relativeStartSeconds = tone.StartSeconds - segmentStartSeconds;
        var startFrame = (int)Math.Round(relativeStartSeconds * sampleRate);
        var frameCount = Math.Max(1, (int)Math.Round(tone.DurationSeconds * sampleRate));
        for (var frame = 0; frame < frameCount; frame++)
        {
            var outputFrame = startFrame + frame;
            if (outputFrame < 0 || outputFrame >= samples.Length)
            {
                continue;
            }

            var normalized = frameCount <= 1 ? 1.0 : frame / (double)(frameCount - 1);
            var t = frame / (double)sampleRate;
            var envelope = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * normalized);
            var phase = 2.0 * Math.PI * (tone.StartHz * t + 0.5 * (tone.EndHz - tone.StartHz) * t * normalized);
            samples[outputFrame] += (float)(Math.Sin(phase) * envelope * tone.Gain);
        }
    }

    private static double MatchedEnergy(ReadOnlySpan<float> samples, ReadOnlySpan<float> kernel)
    {
        var count = Math.Min(samples.Length, kernel.Length);
        if (count == 0)
        {
            return 0.0;
        }

        var dot = 0.0;
        var sampleEnergy = 0.0;
        var kernelEnergy = 0.0;
        for (var index = 0; index < count; index++)
        {
            var sample = samples[index];
            var basis = kernel[index];
            dot += sample * basis;
            sampleEnergy += sample * sample;
            kernelEnergy += basis * basis;
        }

        var denominator = Math.Sqrt(sampleEnergy * kernelEnergy);
        return denominator > 1.0e-12 ? Math.Abs(dot) / denominator : 0.0;
    }

    private static double DirectionalMatchedEnergy(ReadOnlySpan<float> samples, ReadOnlySpan<float> kernel)
    {
        var count = Math.Min(samples.Length, kernel.Length);
        if (count == 0)
        {
            return 0.0;
        }

        var dot = 0.0;
        var sampleEnergy = 0.0;
        var kernelEnergy = 0.0;
        for (var index = 0; index < count; index++)
        {
            var sample = samples[index];
            var basis = kernel[index];
            dot += sample * basis;
            sampleEnergy += sample * sample;
            kernelEnergy += basis * basis;
        }

        var denominator = Math.Sqrt(sampleEnergy * kernelEnergy);
        return denominator > 1.0e-12 ? Math.Max(0.0, dot / denominator) : 0.0;
    }

    private float[] BuildCoarseEnergyTrace(ReadOnlySpan<float> samples, int sampleRate, int hopSamples)
    {
        var traces = new List<float[]>();
        for (var index = 0; index < CoarseTones.Length; index++)
        {
            var tone = CoarseTones[index];
            var kernel = sampleRate == SampleRate ? coarseKernels[index] : RenderToneKernel(tone, sampleRate);
            var trace = BuildMatchedEnergyTrace(samples, kernel, hopSamples);
            if (trace.Length > 0)
            {
                traces.Add(trace);
            }
        }

        if (traces.Count == 0)
        {
            return [];
        }

        var length = traces.Min(trace => trace.Length);
        var output = new float[length];
        for (var frame = 0; frame < output.Length; frame++)
        {
            var best = 0.0f;
            foreach (var trace in traces)
            {
                best = Math.Max(best, trace[frame]);
            }

            output[frame] = best;
        }

        return output;
    }

    private static float[] BuildEnvelopeEnergyTrace(ReadOnlySpan<float> samples, int sampleRate, int hopSamples)
    {
        var windowSamples = Math.Max(1, (int)Math.Round(MaxEventDurationSeconds * sampleRate));
        if (samples.Length < windowSamples)
        {
            return [];
        }

        var output = new float[1 + (samples.Length - windowSamples) / hopSamples];
        var energy = 0.0;
        for (var index = 0; index < windowSamples; index++)
        {
            energy += samples[index] * samples[index];
        }

        output[0] = (float)Math.Sqrt(energy / windowSamples);
        for (var frame = 1; frame < output.Length; frame++)
        {
            var previousStart = (frame - 1) * hopSamples;
            var nextStart = frame * hopSamples;
            for (var index = previousStart; index < nextStart; index++)
            {
                energy -= samples[index] * samples[index];
            }

            for (var index = previousStart + windowSamples; index < nextStart + windowSamples; index++)
            {
                energy += samples[index] * samples[index];
            }

            output[frame] = (float)Math.Sqrt(Math.Max(0.0, energy) / windowSamples);
        }

        return output;
    }

    private static float[] BuildMatchedEnergyTrace(ReadOnlySpan<float> samples, ReadOnlySpan<float> kernel, int hopSamples)
    {
        if (samples.Length < kernel.Length || kernel.Length == 0)
        {
            return [];
        }

        var output = new float[1 + (samples.Length - kernel.Length) / hopSamples];
        for (var frame = 0; frame < output.Length; frame++)
        {
            var offset = frame * hopSamples;
            output[frame] = (float)MatchedEnergy(samples.Slice(offset, kernel.Length), kernel);
        }

        return output;
    }

    private static double MaxMatchedEnergy(ReadOnlySpan<float> samples, ReadOnlySpan<float> kernel, int hopSamples)
    {
        if (samples.Length < kernel.Length || kernel.Length == 0)
        {
            return 0.0;
        }

        var best = 0.0;
        for (var offset = 0; offset <= samples.Length - kernel.Length; offset += hopSamples)
        {
            best = Math.Max(best, MatchedEnergy(samples.Slice(offset, kernel.Length), kernel));
        }

        return best;
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
}
