namespace Mimir.Runtime.Synchronization;

public sealed class MimirCanonicalClockMapper
{
    private const long MaxArrivalDriftBeforeRebaseNs = 10_000_000_000L;
    private const long LocalEpochTimestampNs = 60_000_000_000L;
    private readonly Dictionary<string, ClockMap> maps = new(StringComparer.Ordinal);

    public IReadOnlyList<MimirCanonicalClockMapSnapshot> Snapshots =>
        maps
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new MimirCanonicalClockMapSnapshot(
                pair.Key,
                pair.Value.OffsetNs,
                pair.Value.FirstSourceTimestampNs,
                pair.Value.FirstArrivalNs,
                pair.Value.LatestSourceTimestampNs,
                pair.Value.LatestCanonicalTimestampNs,
                pair.Value.SampleCount))
            .ToArray();

    public MimirStreamSample ToCanonical(MimirStreamSample sample)
    {
        var sourceTimestampNs = sample.TimestampNs;
        if (sourceTimestampNs <= 0)
        {
            sourceTimestampNs = sample.ArrivalNs > 0 ? sample.ArrivalNs : NowNs();
        }

        var arrivalNs = sample.ArrivalNs > 0 ? sample.ArrivalNs : NowNs();
        var key = $"{sample.Kind}:{sample.Origin}:{sample.SourceId}";
        if (!maps.TryGetValue(key, out var map))
        {
            var initialOffsetNs = LooksLikeLocalEpoch(sourceTimestampNs, arrivalNs)
                ? 0L
                : checked(arrivalNs - sourceTimestampNs);
            map = new ClockMap(initialOffsetNs, sourceTimestampNs, arrivalNs);
            maps.Add(key, map);
        }
        else
        {
            var projectedArrivalNs = checked(sourceTimestampNs + map.OffsetNs);
            if (!LooksLikeLocalEpoch(sourceTimestampNs, arrivalNs) &&
                Math.Abs(projectedArrivalNs - arrivalNs) > MaxArrivalDriftBeforeRebaseNs)
            {
                map.OffsetNs = checked(arrivalNs - sourceTimestampNs);
            }
        }

        var canonicalTimestampNs = checked(sourceTimestampNs + map.OffsetNs);
        map.LatestSourceTimestampNs = sourceTimestampNs;
        map.LatestCanonicalTimestampNs = canonicalTimestampNs;
        map.SampleCount++;
        return sample with
        {
            TimestampNs = canonicalTimestampNs,
            ArrivalNs = arrivalNs,
        };
    }

    private static long NowNs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

    private static bool LooksLikeLocalEpoch(long sourceTimestampNs, long arrivalNs) =>
        sourceTimestampNs > 0 &&
        arrivalNs > 0 &&
        Math.Abs(sourceTimestampNs - arrivalNs) <= LocalEpochTimestampNs;

    private sealed class ClockMap(long offsetNs, long firstSourceTimestampNs, long firstArrivalNs)
    {
        public long OffsetNs { get; set; } = offsetNs;

        public long FirstSourceTimestampNs { get; } = firstSourceTimestampNs;

        public long FirstArrivalNs { get; } = firstArrivalNs;

        public long LatestSourceTimestampNs { get; set; } = firstSourceTimestampNs;

        public long LatestCanonicalTimestampNs { get; set; } = firstArrivalNs;

        public long SampleCount { get; set; }
    }
}

public sealed record MimirCanonicalClockMapSnapshot(
    string StreamKey,
    long OffsetNs,
    long FirstSourceTimestampNs,
    long FirstArrivalNs,
    long LatestSourceTimestampNs,
    long LatestCanonicalTimestampNs,
    long SampleCount);
