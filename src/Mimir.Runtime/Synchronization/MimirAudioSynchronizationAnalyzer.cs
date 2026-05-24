using System.Buffers.Binary;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirAudioSynchronizationAnalyzer
{
    private const double MaxWindowSeconds = 2.0;
    private const double MinPassiveWindowSeconds = 2.0;
    private const double MinChirpletWindowSeconds = 2.0;
    private const double MinChirpBinWindowSeconds = 0.40;
    private const double MinBioacousticWindowSeconds = 0.50;
    private const double MaxPairClockFitDelaySeconds = 10.0;
    private const double PassiveReportConfidence = 0.08;
    private readonly List<MimirAudioSynchronizationDecodeTrace> lastDecodeTraces = [];
    private readonly Dictionary<string, MimirChirpBinCalibrationProfile> lastCalibrationProfiles = new(StringComparer.Ordinal);
    private readonly MimirPassiveAudioSynchronizationEstimator passiveEstimator = new();

    public IReadOnlyList<MimirAudioSynchronizationDecodeTrace> LastDecodeTraces => lastDecodeTraces;

    public IReadOnlyList<MimirChirpBinCalibrationProfile> LastCalibrationProfiles =>
        lastCalibrationProfiles.Values.OrderBy(profile => profile.SourceId, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<MimirAudioSynchronizationReport> Analyze(
        IEnumerable<MimirRollingStreamBuffer> buffers,
        string referenceSourceId,
        MimirAudioSyncMode mode = MimirAudioSyncMode.ChirpOnly,
        IReadOnlySet<string>? candidateSourceIds = null,
        MimirChirpBinCalibrationModel? calibrationModel = null)
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

        var referenceLatest = reference.Latest;
        if (referenceLatest?.AudioBlock == null)
        {
            return [];
        }

        var referenceSamples = ExtractMonoWindow(reference, out var referenceBlock);
        if (referenceSamples.Length == 0 ||
            referenceBlock == null)
        {
            return [];
        }

        var reports = new List<MimirAudioSynchronizationReport>();
        lastDecodeTraces.Clear();
        lastCalibrationProfiles.Clear();
        foreach (var buffer in audioBuffers)
        {
            if (ReferenceEquals(buffer, reference))
            {
                continue;
            }

            if (candidateSourceIds != null && !candidateSourceIds.Contains(buffer.Descriptor.SourceId))
            {
                continue;
            }

            var commonEndNs = Math.Min(referenceLatest.Value.TimestampNs, buffer.Latest?.TimestampNs ?? 0);
            MimirAudioBlockDescriptor? activeReferenceBlock = referenceBlock;
            var alignedReferenceSamples = referenceSamples;
            if (commonEndNs < referenceLatest.Value.TimestampNs)
            {
                alignedReferenceSamples = ExtractMonoWindow(reference, out activeReferenceBlock, commonEndNs);
            }

            if (alignedReferenceSamples.Length == 0 || activeReferenceBlock == null)
            {
                continue;
            }

            var candidateSamples = ExtractMonoWindow(buffer, out var candidateBlock, commonEndNs);
            if (candidateSamples.Length == 0 || candidateBlock == null)
            {
                continue;
            }

            candidateSamples = ResampleToRate(candidateSamples, candidateBlock.SampleRate, activeReferenceBlock.SampleRate);

            var compared = Math.Min(alignedReferenceSamples.Length, candidateSamples.Length);
            var referenceWindow = alignedReferenceSamples.AsSpan(^compared..);
            var candidateWindow = candidateSamples.AsSpan(^compared..);
            if (mode != MimirAudioSyncMode.ChirpOnly)
            {
                var minPassiveSamples = MinWindowSamples(activeReferenceBlock.SampleRate, MinPassiveWindowSeconds);
                if (compared >= minPassiveSamples)
                {
                    var passive = passiveEstimator.Estimate(referenceWindow, candidateWindow, activeReferenceBlock.SampleRate);
                    lastDecodeTraces.Add(new MimirAudioSynchronizationDecodeTrace(
                        reference.Descriptor.SourceId,
                        buffer.Descriptor.SourceId,
                        activeReferenceBlock.SampleRate,
                        passive.ComparedSamples,
                        0,
                        0,
                        0.0,
                        passive.NoiseFloor,
                        0,
                        0,
                        0.0,
                        passive.Peak,
                        0,
                        passive.Confidence,
                        passive.Status));
                    if (passive.Confidence >= PassiveReportConfidence || mode == MimirAudioSyncMode.Passive)
                    {
                        if (passive.Confidence >= PassiveReportConfidence)
                        {
                            reports.Add(new MimirAudioSynchronizationReport(
                                reference.Descriptor.SourceId,
                                buffer.Descriptor.SourceId,
                                activeReferenceBlock.SampleRate,
                                (int)Math.Round(passive.DelaySamples),
                                passive.DelaySamples,
                                passive.DelaySamples * 1000.0 / activeReferenceBlock.SampleRate,
                                passive.Confidence,
                                [],
                                commonEndNs,
                                compared,
                                reference.Latest?.Sequence ?? 0,
                                buffer.Latest?.Sequence ?? 0,
                                0,
                                passive.Confidence,
                                "passive"));
                        }

                        continue;
                    }
                }
                else
                {
                    lastDecodeTraces.Add(InsufficientWindowTrace(
                        reference.Descriptor.SourceId,
                        buffer.Descriptor.SourceId,
                        activeReferenceBlock.SampleRate,
                        compared,
                        "passive-insufficient-window"));
                    if (mode == MimirAudioSyncMode.Passive)
                    {
                        continue;
                    }
                }
            }

            var useBioacousticTimeline = mode is MimirAudioSyncMode.ChirpOnly or MimirAudioSyncMode.Hybrid;
            var activeMinSeconds = useBioacousticTimeline
                ? MinBioacousticWindowSeconds
                : MinChirpletWindowSeconds;
            var minActiveSamples = MinWindowSamples(activeReferenceBlock.SampleRate, activeMinSeconds);
            if (compared < minActiveSamples)
            {
                lastDecodeTraces.Add(InsufficientWindowTrace(
                    reference.Descriptor.SourceId,
                    buffer.Descriptor.SourceId,
                    activeReferenceBlock.SampleRate,
                    compared,
                    useBioacousticTimeline
                        ? "bioacoustic-insufficient-window"
                        : "chirplet-insufficient-window"));
                continue;
            }

            var comparedReferenceDecode = useBioacousticTimeline
                ? MimirBioacousticTimeline.Default.DecodeStreamWindow(
                    referenceWindow,
                    activeReferenceBlock.SampleRate)
                : MimirChirpletTimeline.Default.DecodeStreamWindow(referenceWindow, activeReferenceBlock.SampleRate);
            var candidateDecode = useBioacousticTimeline
                ? MimirBioacousticTimeline.Default.DecodeStreamWindow(
                    candidateWindow,
                    activeReferenceBlock.SampleRate)
                : MimirChirpletTimeline.Default.DecodeStreamWindow(candidateWindow, activeReferenceBlock.SampleRate);
            if (useBioacousticTimeline)
            {
                StoreCalibrationProfile(reference.Descriptor.SourceId, activeReferenceBlock.SampleRate, comparedReferenceDecode);
                StoreCalibrationProfile(buffer.Descriptor.SourceId, activeReferenceBlock.SampleRate, candidateDecode);
            }

            var deterministicFit = EstimateDelayFromDecodedTimeline(comparedReferenceDecode, candidateDecode);
            lastDecodeTraces.Add(new MimirAudioSynchronizationDecodeTrace(
                reference.Descriptor.SourceId,
                buffer.Descriptor.SourceId,
                activeReferenceBlock.SampleRate,
                compared,
                comparedReferenceDecode.Frames.Count,
                comparedReferenceDecode.Anchors.Count,
                comparedReferenceDecode.ClockFit?.Confidence ?? 0.0,
                BestFrameEnergy(comparedReferenceDecode),
                candidateDecode.Frames.Count,
                candidateDecode.Anchors.Count,
                candidateDecode.ClockFit?.Confidence ?? 0.0,
                BestFrameEnergy(candidateDecode),
                deterministicFit.MatchedEvents,
                deterministicFit.Confidence,
                deterministicFit.MatchedEvents >= 3 ? "report" : "insufficient-anchors"));
            if (deterministicFit.MatchedEvents < 3)
            {
                continue;
            }

            var decodedDelaySamples = useBioacousticTimeline
                ? RefineDelayFromWaveform(referenceWindow, candidateWindow, deterministicFit.DelaySamples, activeReferenceBlock.SampleRate)
                : deterministicFit.DelaySamples;
            var bandResponses = useBioacousticTimeline
                ? candidateDecode.BandResponses
                : MimirChirpletTimeline.Default.EstimateBandResponse(candidateWindow, activeReferenceBlock.SampleRate);
            reports.Add(new MimirAudioSynchronizationReport(
                reference.Descriptor.SourceId,
                buffer.Descriptor.SourceId,
                activeReferenceBlock.SampleRate,
                (int)Math.Round(decodedDelaySamples),
                decodedDelaySamples,
                decodedDelaySamples * 1000.0 / activeReferenceBlock.SampleRate,
                deterministicFit.Confidence,
                bandResponses,
                commonEndNs,
                compared,
                reference.Latest?.Sequence ?? 0,
                buffer.Latest?.Sequence ?? 0,
                deterministicFit.MatchedEvents,
                deterministicFit.Confidence,
                useBioacousticTimeline ? "bioacoustic" : "chirplet"));
        }

        return reports;
    }

    private void StoreCalibrationProfile(
        string sourceId,
        int sampleRate,
        MimirChirpletStreamDecode decode)
    {
        lastCalibrationProfiles[sourceId] = MimirChirpBinCalibrationProfile.FromDecode(sourceId, sampleRate, decode);
    }

    private static int MinWindowSamples(int sampleRate, double seconds)
    {
        return (int)Math.Ceiling(sampleRate * seconds);
    }

    private static MimirAudioSynchronizationDecodeTrace InsufficientWindowTrace(
        string referenceSourceId,
        string candidateSourceId,
        int sampleRate,
        int comparedSamples,
        string status)
    {
        return new MimirAudioSynchronizationDecodeTrace(
            referenceSourceId,
            candidateSourceId,
            sampleRate,
            comparedSamples,
            0,
            0,
            0.0,
            0.0,
            0,
            0,
            0.0,
            0.0,
            0,
            0.0,
            status);
    }

    private static double BestFrameEnergy(MimirChirpletStreamDecode decode)
    {
        return decode.Frames.Count == 0
            ? 0.0
            : decode.Frames.Max(frame => frame.BestCandidate.Energy);
    }

    private static (double DelaySamples, double Confidence, int MatchedEvents) EstimateDelayFromDecodedTimeline(
        MimirChirpletStreamDecode reference,
        MimirChirpletStreamDecode candidate)
    {
        if (reference.Anchors.Count == 0 ||
            candidate.Anchors.Count == 0 ||
            reference.ClockFit == null ||
            candidate.ClockFit == null)
        {
            return (0.0, 0.0, 0);
        }

        var candidateByEvent = candidate.Anchors.ToDictionary(anchor => anchor.EventIndex);
        var matched = new List<(double Delay, double Weight)>();
        foreach (var referenceAnchor in reference.Anchors)
        {
            if (!candidateByEvent.TryGetValue(referenceAnchor.EventIndex, out var candidateAnchor))
            {
                continue;
            }

            matched.Add((
                candidateAnchor.SampleOffset - referenceAnchor.SampleOffset,
                Math.Sqrt(referenceAnchor.Confidence * candidateAnchor.Confidence)));
        }

        if (matched.Count >= 3)
        {
            var totalWeight = matched.Sum(pair => Math.Max(1.0e-6, pair.Weight));
            var delay = matched.Sum(pair => pair.Delay * Math.Max(1.0e-6, pair.Weight)) / totalWeight;
            var error = matched.Sum(pair => Math.Abs(pair.Delay - delay) * Math.Max(1.0e-6, pair.Weight)) / totalWeight;
            var residualConfidence = 1.0 / (1.0 + error / 32.0);
            var countConfidence = Math.Clamp(matched.Count / 12.0, 0.0, 1.0);
            var energyConfidence = Math.Clamp(totalWeight / matched.Count, 0.0, 1.0);
            if (reference.ClockFit.Confidence >= 0.75 && candidate.ClockFit.Confidence >= 0.75)
            {
                var matchedClockDelay = candidate.ClockFit.SourceOffsetSamples - reference.ClockFit.SourceOffsetSamples;
                if (Math.Abs(matchedClockDelay - delay) <= reference.ClockFit.EffectiveSampleRate * 0.004)
                {
                    var clockConfidence = Math.Sqrt(reference.ClockFit.Confidence * candidate.ClockFit.Confidence);
                    return (matchedClockDelay, residualConfidence * 0.35 + countConfidence * 0.20 + energyConfidence * 0.20 + clockConfidence * 0.25, matched.Count);
                }
            }

            return (delay, residualConfidence * 0.50 + countConfidence * 0.25 + energyConfidence * 0.25, matched.Count);
        }

        if (reference.ClockFit.Confidence >= 0.50 && candidate.ClockFit.Confidence >= 0.50)
        {
            var delay = candidate.ClockFit.SourceOffsetSamples - reference.ClockFit.SourceOffsetSamples;
            if (Math.Abs(delay) <= reference.ClockFit.EffectiveSampleRate * MaxPairClockFitDelaySeconds)
            {
                var residualConfidence = 1.0 / (1.0 + (reference.ClockFit.MeanAbsoluteErrorSamples + candidate.ClockFit.MeanAbsoluteErrorSamples) / 64.0);
                var countConfidence = Math.Clamp(Math.Min(reference.ClockFit.AnchorCount, candidate.ClockFit.AnchorCount) / 12.0, 0.0, 1.0);
                var clockConfidence = Math.Sqrt(reference.ClockFit.Confidence * candidate.ClockFit.Confidence);
                return (delay, residualConfidence * 0.35 + countConfidence * 0.25 + clockConfidence * 0.40, Math.Min(reference.ClockFit.AnchorCount, candidate.ClockFit.AnchorCount));
            }
        }

        var referenceFirst = reference.Anchors.Min(anchor => anchor.TimelineSeconds);
        var referenceLast = reference.Anchors.Max(anchor => anchor.TimelineSeconds);
        var candidateFirst = candidate.Anchors.Min(anchor => anchor.TimelineSeconds);
        var candidateLast = candidate.Anchors.Max(anchor => anchor.TimelineSeconds);
        var start = Math.Max(referenceFirst, candidateFirst);
        var end = Math.Min(referenceLast, candidateLast);
        if (end <= start)
        {
            return (0.0, 0.0, matched.Count);
        }

        var timelineSeconds = (start + end) * 0.5;
        var clockDelay = candidate.ClockFit.SampleForTimelineSeconds(timelineSeconds) -
            reference.ClockFit.SampleForTimelineSeconds(timelineSeconds);
        var confidence = Math.Sqrt(reference.ClockFit.Confidence * candidate.ClockFit.Confidence) * 0.65;
        return (clockDelay, confidence, Math.Min(reference.ClockFit.AnchorCount, candidate.ClockFit.AnchorCount));
    }

    private static double RefineDelayFromWaveform(
        ReadOnlySpan<float> reference,
        ReadOnlySpan<float> candidate,
        double initialDelaySamples,
        int sampleRate)
    {
        var radius = Math.Max(16, (int)Math.Ceiling(sampleRate * 0.00035));
        var center = (int)Math.Round(initialDelaySamples);
        var firstLag = Math.Max(0, center - radius);
        var lastLag = Math.Min(candidate.Length - 2, center + radius);
        if (reference.Length == 0 || candidate.Length == 0 || firstLag > lastLag)
        {
            return initialDelaySamples;
        }

        var bestLag = center;
        var bestScore = double.NegativeInfinity;
        for (var lag = firstLag; lag <= lastLag; lag++)
        {
            var score = NormalizedLagScore(reference, candidate, lag);
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        var bestFractionalLag = (double)bestLag;
        var bestFractionalScore = bestScore;
        for (var step = -8; step <= 8; step++)
        {
            var lag = bestLag + step / 8.0;
            if (lag < firstLag || lag > lastLag)
            {
                continue;
            }

            var score = NormalizedFractionalLagScore(reference, candidate, lag);
            if (score > bestFractionalScore)
            {
                bestFractionalScore = score;
                bestFractionalLag = lag;
            }
        }

        return bestFractionalLag;
    }

    private static double NormalizedLagScore(ReadOnlySpan<float> reference, ReadOnlySpan<float> candidate, int lag)
    {
        var count = Math.Min(reference.Length, candidate.Length - lag);
        if (count <= 0)
        {
            return double.NegativeInfinity;
        }

        var dot = 0.0;
        var referenceEnergy = 0.0;
        var candidateEnergy = 0.0;
        for (var index = 0; index < count; index++)
        {
            var referenceSample = reference[index];
            var candidateSample = candidate[index + lag];
            dot += referenceSample * candidateSample;
            referenceEnergy += referenceSample * referenceSample;
            candidateEnergy += candidateSample * candidateSample;
        }

        return referenceEnergy <= 1.0e-12 || candidateEnergy <= 1.0e-12
            ? double.NegativeInfinity
            : dot / Math.Sqrt(referenceEnergy * candidateEnergy);
    }

    private static double NormalizedFractionalLagScore(ReadOnlySpan<float> reference, ReadOnlySpan<float> candidate, double lag)
    {
        var leftLag = (int)Math.Floor(lag);
        var fraction = lag - leftLag;
        var count = Math.Min(reference.Length, candidate.Length - leftLag - 1);
        if (count <= 0)
        {
            return double.NegativeInfinity;
        }

        var dot = 0.0;
        var referenceEnergy = 0.0;
        var candidateEnergy = 0.0;
        for (var index = 0; index < count; index++)
        {
            var referenceSample = reference[index];
            var candidateSample = candidate[index + leftLag] +
                (candidate[index + leftLag + 1] - candidate[index + leftLag]) * fraction;
            dot += referenceSample * candidateSample;
            referenceEnergy += referenceSample * referenceSample;
            candidateEnergy += candidateSample * candidateSample;
        }

        return referenceEnergy <= 1.0e-12 || candidateEnergy <= 1.0e-12
            ? double.NegativeInfinity
            : dot / Math.Sqrt(referenceEnergy * candidateEnergy);
    }

    private static float[] ExtractMonoWindow(
        MimirRollingStreamBuffer buffer,
        out MimirAudioBlockDescriptor? latestBlock,
        long endNs = long.MaxValue)
    {
        latestBlock = buffer.Latest?.AudioBlock;
        if (latestBlock == null || !IsSupportedPcmFormat(latestBlock.SampleFormat))
        {
            return [];
        }

        var maxWindowSamples = (int)Math.Ceiling(latestBlock.SampleRate * MaxWindowSeconds);
        var samples = new List<float>(maxWindowSamples);
        foreach (var sample in buffer.Snapshot()
                     .Where(sample => sample.AudioBlock != null && !sample.Data.IsEmpty && sample.TimestampNs <= endNs)
                     .Reverse())
        {
            var block = sample.AudioBlock!;
            if (!IsSupportedPcmFormat(block.SampleFormat) || block.Channels <= 0)
            {
                continue;
            }

            var mono = ExtractFirstChannel(sample.Data.Span, block.Channels, block.SampleFormat);
            for (var index = mono.Length - 1; index >= 0 && samples.Count < maxWindowSamples; index--)
            {
                samples.Add(mono[index]);
            }

            if (samples.Count >= maxWindowSamples)
            {
                break;
            }
        }

        samples.Reverse();
        return samples.ToArray();
    }

    private static bool IsSupportedPcmFormat(MimirAudioSampleFormat sampleFormat) =>
        sampleFormat is MimirAudioSampleFormat.Float32 or
            MimirAudioSampleFormat.Int16 or
            MimirAudioSampleFormat.Int24 or
            MimirAudioSampleFormat.Int32;

    private static float[] ExtractFirstChannel(
        ReadOnlySpan<byte> data,
        int channels,
        MimirAudioSampleFormat sampleFormat)
    {
        var bytesPerSample = sampleFormat switch
        {
            MimirAudioSampleFormat.Float32 => sizeof(float),
            MimirAudioSampleFormat.Int16 => sizeof(short),
            MimirAudioSampleFormat.Int24 => 3,
            MimirAudioSampleFormat.Int32 => sizeof(int),
            _ => 0,
        };
        if (bytesPerSample <= 0)
        {
            return [];
        }

        var frameCount = data.Length / (bytesPerSample * channels);
        var samples = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * channels * bytesPerSample;
            samples[frame] = sampleFormat switch
            {
                MimirAudioSampleFormat.Float32 => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(float)))),
                MimirAudioSampleFormat.Int16 => BinaryPrimitives.ReadInt16LittleEndian(
                    data.Slice(offset, sizeof(short))) / 32768.0f,
                MimirAudioSampleFormat.Int24 => ReadInt24LittleEndian(data.Slice(offset, 3)) / 8388608.0f,
                MimirAudioSampleFormat.Int32 => BinaryPrimitives.ReadInt32LittleEndian(
                    data.Slice(offset, sizeof(int))) / 2147483648.0f,
                _ => 0.0f,
            };
        }

        return samples;
    }

    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> data)
    {
        var value = data[0] | (data[1] << 8) | (data[2] << 16);
        return (value & 0x00800000) != 0 ? value | unchecked((int)0xff000000) : value;
    }

    private static float[] ResampleToRate(float[] samples, int sourceRate, int targetRate)
    {
        if (samples.Length == 0 || sourceRate == targetRate)
        {
            return samples;
        }

        var outputLength = Math.Max(1, (int)Math.Round(samples.Length * (double)targetRate / sourceRate));
        var output = new float[outputLength];
        var scale = sourceRate / (double)targetRate;
        for (var index = 0; index < output.Length; index++)
        {
            var sourcePosition = index * scale;
            var left = (int)Math.Floor(sourcePosition);
            if (left >= samples.Length - 1)
            {
                output[index] = samples[^1];
                continue;
            }

            var fraction = sourcePosition - left;
            output[index] = (float)(samples[left] + (samples[left + 1] - samples[left]) * fraction);
        }

        return output;
    }
}
