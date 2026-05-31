namespace Mimir.Runtime.Synchronization;

public sealed record MimirSparseFeatureTrackerOptions(
    int MaxFeatures = 96,
    int CellSizePixels = 12,
    int SearchRadiusPixels = 18,
    int MinimumTrackAgeFrames = 3,
    double MinimumCornerScore = 0.10,
    double MinimumLuma = 0.08);

public sealed record MimirSparseFeatureTrackerFrame(
    string SourceId,
    int Width,
    int Height,
    long ObservedTimeNs,
    int FrameCount,
    IReadOnlyList<MimirFeatureTrackPoint> Tracks,
    int StableTrackCount,
    double MeanTrackAgeFrames,
    double MeanSpeedPixelsPerSecond,
    double Confidence);

public sealed class MimirSparseFeatureTracker(MimirSparseFeatureTrackerOptions? options = null)
{
    private readonly MimirSparseFeatureTrackerOptions options = options ?? new();
    private readonly List<TrackedFeature> tracks = [];
    private int nextTrackId = 1;
    private int frameCount;
    private long previousTimeNs;

    public MimirSparseFeatureTrackerFrame Update(
        string sourceId,
        int width,
        int height,
        ReadOnlySpan<byte> luma,
        long observedTimeNs)
    {
        if (width <= 2 || height <= 2 || luma.Length < width * height)
        {
            return Empty(sourceId, width, height, observedTimeNs);
        }

        frameCount++;
        var dtSeconds = previousTimeNs <= 0 || observedTimeNs <= previousTimeNs
            ? 1.0 / 187.0
            : Math.Max(1.0e-6, (observedTimeNs - previousTimeNs) / 1_000_000_000.0);
        previousTimeNs = observedTimeNs;

        var detections = DetectFeatures(width, height, luma);
        MatchDetections(detections, dtSeconds);
        PruneTracks();

        var output = tracks
            .Where(track => track.MissedFrames == 0)
            .OrderByDescending(track => track.AgeFrames)
            .ThenByDescending(track => track.Confidence)
            .Take(options.MaxFeatures)
            .Select(track => new MimirFeatureTrackPoint(
                track.TrackId,
                track.X,
                track.Y,
                PixelToClipX(track.X, width),
                PixelToClipY(track.Y, height),
                track.VelocityXPerSecond,
                track.VelocityYPerSecond,
                track.AgeFrames,
                track.Confidence))
            .ToArray();
        var stable = output.Count(point => point.AgeFrames >= options.MinimumTrackAgeFrames);
        var meanAge = output.Length == 0 ? 0.0 : output.Average(static point => point.AgeFrames);
        var meanSpeed = output.Length == 0
            ? 0.0
            : output.Average(static point => Math.Sqrt(
                point.VelocityXPerSecond * point.VelocityXPerSecond +
                point.VelocityYPerSecond * point.VelocityYPerSecond));
        var stability = output.Length == 0 ? 0.0 : stable / (double)Math.Min(options.MaxFeatures, output.Length);
        var density = Math.Clamp(output.Length / (double)Math.Max(1, options.MaxFeatures), 0.0, 1.0);
        var confidence = Math.Clamp(0.55 * stability + 0.45 * density, 0.0, 1.0);
        return new MimirSparseFeatureTrackerFrame(
            sourceId,
            width,
            height,
            observedTimeNs,
            frameCount,
            output,
            stable,
            meanAge,
            meanSpeed,
            confidence);
    }

    public MimirFeatureTrackCameraObservation ToCameraObservation(MimirSparseFeatureTrackerFrame frame) =>
        new(
            $"{frame.SourceId}:feature-tracks:{frame.FrameCount}",
            frame.SourceId,
            frame.Width,
            frame.Height,
            frame.ObservedTimeNs,
            frame.Tracks,
            frame.FrameCount,
            frame.MeanTrackAgeFrames,
            frame.MeanSpeedPixelsPerSecond,
            frame.Confidence);

    private MimirSparseFeatureTrackerFrame Empty(string sourceId, int width, int height, long observedTimeNs) =>
        new(sourceId, width, height, observedTimeNs, frameCount, [], 0, 0.0, 0.0, 0.0);

    private IReadOnlyList<FeatureDetection> DetectFeatures(int width, int height, ReadOnlySpan<byte> luma)
    {
        var cell = Math.Max(4, options.CellSizePixels);
        var gridWidth = Math.Max(1, (width + cell - 1) / cell);
        var gridHeight = Math.Max(1, (height + cell - 1) / cell);
        var bestByCell = new FeatureDetection?[gridWidth * gridHeight];
        var minimumLuma = Math.Clamp(options.MinimumLuma, 0.0, 1.0) * 255.0;
        var minimumScore = Math.Clamp(options.MinimumCornerScore, 0.0, 1.0) * 255.0;

        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var center = luma[row + x];
                if (center < minimumLuma)
                {
                    continue;
                }

                var gx = Math.Abs(luma[row + x + 1] - luma[row + x - 1]);
                var gy = Math.Abs(luma[row + width + x] - luma[row - width + x]);
                var diagonalA = Math.Abs(luma[row + width + x + 1] - luma[row - width + x - 1]);
                var diagonalB = Math.Abs(luma[row + width + x - 1] - luma[row - width + x + 1]);
                var score = Math.Min(Math.Max(gx, diagonalA), Math.Max(gy, diagonalB));
                if (score < minimumScore)
                {
                    continue;
                }

                var cellX = Math.Min(gridWidth - 1, x / cell);
                var cellY = Math.Min(gridHeight - 1, y / cell);
                var cellIndex = cellY * gridWidth + cellX;
                var candidate = new FeatureDetection(x, y, score / 255.0);
                if (bestByCell[cellIndex] is not { } existing || candidate.Score > existing.Score)
                {
                    bestByCell[cellIndex] = candidate;
                }
            }
        }

        return bestByCell
            .Where(static detection => detection.HasValue)
            .Select(static detection => detection!.Value)
            .OrderByDescending(static detection => detection.Score)
            .Take(options.MaxFeatures)
            .ToArray();
    }

    private void MatchDetections(IReadOnlyList<FeatureDetection> detections, double dtSeconds)
    {
        var matchedTracks = new HashSet<int>();
        var matchedDetections = new bool[detections.Count];
        var radiusSquared = options.SearchRadiusPixels * options.SearchRadiusPixels;
        for (var detectionIndex = 0; detectionIndex < detections.Count; detectionIndex++)
        {
            var detection = detections[detectionIndex];
            TrackedFeature? bestTrack = null;
            var bestDistance = double.MaxValue;
            foreach (var track in tracks)
            {
                if (matchedTracks.Contains(track.TrackId))
                {
                    continue;
                }

                var dx = detection.X - track.X;
                var dy = detection.Y - track.Y;
                var distance = dx * dx + dy * dy;
                if (distance <= radiusSquared && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTrack = track;
                }
            }

            if (bestTrack == null)
            {
                continue;
            }

            var velocityX = (detection.X - bestTrack.X) / dtSeconds;
            var velocityY = (detection.Y - bestTrack.Y) / dtSeconds;
            bestTrack.X = detection.X;
            bestTrack.Y = detection.Y;
            bestTrack.VelocityXPerSecond = velocityX;
            bestTrack.VelocityYPerSecond = velocityY;
            bestTrack.Confidence = Math.Clamp(0.7 * bestTrack.Confidence + 0.3 * detection.Score, 0.0, 1.0);
            bestTrack.AgeFrames++;
            bestTrack.MissedFrames = 0;
            matchedTracks.Add(bestTrack.TrackId);
            matchedDetections[detectionIndex] = true;
        }

        foreach (var track in tracks)
        {
            if (!matchedTracks.Contains(track.TrackId))
            {
                track.MissedFrames++;
            }
        }

        for (var index = 0; index < detections.Count; index++)
        {
            if (matchedDetections[index])
            {
                continue;
            }

            var detection = detections[index];
            tracks.Add(new TrackedFeature(
                nextTrackId++,
                detection.X,
                detection.Y,
                0.0,
                0.0,
                1,
                0,
                detection.Score));
        }
    }

    private void PruneTracks()
    {
        tracks.RemoveAll(static track => track.MissedFrames > 5);
        if (tracks.Count <= options.MaxFeatures * 2)
        {
            return;
        }

        var survivors = tracks
            .OrderBy(static track => track.MissedFrames)
            .ThenByDescending(static track => track.AgeFrames)
            .ThenByDescending(static track => track.Confidence)
            .Take(options.MaxFeatures * 2)
            .ToHashSet();
        tracks.RemoveAll(track => !survivors.Contains(track));
    }

    private static double PixelToClipX(double imageX, int width) =>
        Math.Clamp(imageX / Math.Max(1.0, width) * 2.0 - 1.0, -1.0, 1.0);

    private static double PixelToClipY(double imageY, int height) =>
        Math.Clamp(1.0 - imageY / Math.Max(1.0, height) * 2.0, -1.0, 1.0);

    private readonly record struct FeatureDetection(double X, double Y, double Score);

    private sealed class TrackedFeature(
        int trackId,
        double x,
        double y,
        double velocityXPerSecond,
        double velocityYPerSecond,
        int ageFrames,
        int missedFrames,
        double confidence)
    {
        public int TrackId { get; } = trackId;
        public double X { get; set; } = x;
        public double Y { get; set; } = y;
        public double VelocityXPerSecond { get; set; } = velocityXPerSecond;
        public double VelocityYPerSecond { get; set; } = velocityYPerSecond;
        public int AgeFrames { get; set; } = ageFrames;
        public int MissedFrames { get; set; } = missedFrames;
        public double Confidence { get; set; } = confidence;
    }
}
