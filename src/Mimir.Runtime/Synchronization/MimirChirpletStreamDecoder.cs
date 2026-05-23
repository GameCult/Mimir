using System.Runtime.InteropServices;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirChirpletStreamDecoder
{
    private readonly MimirChirpletTimeline timeline;
    private readonly int sampleRate;
    private readonly int maxSamples;
    private readonly List<float> samples;
    private long originSample;

    public MimirChirpletStreamDecoder(
        MimirChirpletTimeline? timeline = null,
        int sampleRate = MimirChirpletTimeline.SampleRate,
        TimeSpan? windowDuration = null)
    {
        this.timeline = timeline ?? MimirChirpletTimeline.Default;
        this.sampleRate = sampleRate;
        maxSamples = Math.Max(
            sampleRate / 2,
            (int)Math.Round((windowDuration ?? TimeSpan.FromSeconds(5)).TotalSeconds * sampleRate));
        samples = new List<float>(maxSamples);
    }

    public long OriginSample => originSample;

    public long NextSample => originSample + samples.Count;

    public MimirChirpletStreamDecode Decode() =>
        WithAbsoluteOffsets(timeline.DecodeStreamWindow(CollectionsMarshal.AsSpan(samples), sampleRate));

    public MimirChirpletStreamDecode Append(ReadOnlySpan<float> block)
    {
        if (block.Length == 0)
        {
            return Decode();
        }

        foreach (var sample in block)
        {
            samples.Add(sample);
        }

        var overflow = samples.Count - maxSamples;
        if (overflow > 0)
        {
            samples.RemoveRange(0, overflow);
            originSample += overflow;
        }

        return Decode();
    }

    private MimirChirpletStreamDecode WithAbsoluteOffsets(MimirChirpletStreamDecode decode)
    {
        if (originSample == 0)
        {
            return decode;
        }

        var symbols = decode.Symbols
            .Select(symbol => symbol with { SampleOffset = symbol.SampleOffset + originSample })
            .ToArray();
        var anchors = decode.Anchors
            .Select(anchor => anchor with
            {
                SampleOffset = anchor.SampleOffset + originSample,
                Symbols = anchor.Symbols
                    .Select(symbol => symbol with { SampleOffset = symbol.SampleOffset + originSample })
                    .ToArray(),
            })
            .ToArray();
        var clock = decode.ClockFit == null
            ? null
            : decode.ClockFit with { SourceOffsetSamples = decode.ClockFit.SourceOffsetSamples + originSample };
        var frames = decode.Frames
            .Select(frame => frame with
            {
                SampleOffset = frame.SampleOffset + originSample,
                Candidates = frame.Candidates
                    .Select(candidate => candidate with { SampleOffset = candidate.SampleOffset + originSample })
                    .ToArray(),
            })
            .ToArray();
        return new MimirChirpletStreamDecode(frames, symbols, anchors, clock, decode.BandResponses);
    }
}
