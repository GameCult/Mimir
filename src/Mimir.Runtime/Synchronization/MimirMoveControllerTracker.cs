namespace Mimir.Runtime.Synchronization;

public sealed record MimirMoveControllerTrackerOptions(
    int MaxHistoryPointsPerController = 96,
    double MinimumLuma = 0.55,
    double MinimumBlobPixels = 6.0,
    double SearchRadiusPixels = 64.0);

public readonly record struct MimirMoveControllerColor(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

public readonly record struct MimirMoveControllerTrackPoint(
    string ControllerId,
    MimirMoveControllerColor ExpectedColor,
    double ImageX,
    double ImageY,
    double ClipX,
    double ClipY,
    double RadiusPixels,
    double VelocityXPerSecond,
    double VelocityYPerSecond,
    int AgeFrames,
    long ObservedTimeNs,
    double Confidence);

public sealed record MimirMoveControllerTrackFrame(
    string SourceId,
    int Width,
    int Height,
    long ObservedTimeNs,
    IReadOnlyList<MimirMoveControllerTrackPoint> Points,
    double Confidence);

public sealed class MimirMoveControllerTracker(MimirMoveControllerTrackerOptions? options = null)
{
    private readonly MimirMoveControllerTrackerOptions options = options ?? new();
    private readonly Dictionary<string, MoveControllerState> states = new(StringComparer.Ordinal);

    public MimirMoveControllerTrackFrame Update(
        string sourceId,
        string controllerId,
        MimirMoveControllerColor expectedColor,
        int width,
        int height,
        ReadOnlySpan<byte> luma,
        long observedTimeNs)
    {
        if (string.IsNullOrWhiteSpace(controllerId))
        {
            throw new ArgumentException("Controller id is required.", nameof(controllerId));
        }

        if (width <= 2 || height <= 2 || luma.Length < width * height)
        {
            return Frame(sourceId, width, height, observedTimeNs);
        }

        if (TryDetectSphere(width, height, luma, out var detection))
        {
            var state = states.TryGetValue(controllerId, out var existing)
                ? existing
                : states[controllerId] = new MoveControllerState(controllerId);
            state.ExpectedColor = expectedColor;
            state.Add(detection, width, height, observedTimeNs, options.MaxHistoryPointsPerController);
        }

        return Frame(sourceId, width, height, observedTimeNs);
    }

    public MimirFeatureTrackFieldCandidate ToFeatureTrackCandidate(
        MimirMoveControllerTrackFrame frame,
        string calibrationId = "move-controller-overlay",
        string producerKey = "mimir-move-controller-tracker")
    {
        var observations = frame.Points
            .GroupBy(static point => point.ControllerId, StringComparer.Ordinal)
            .Select(group =>
            {
                var tracks = group
                    .OrderBy(static point => point.AgeFrames)
                    .Select(point => new MimirFeatureTrackPoint(
                        StableTrackId(point.ControllerId),
                        point.ImageX,
                        point.ImageY,
                        point.ClipX,
                        point.ClipY,
                        point.VelocityXPerSecond,
                        point.VelocityYPerSecond,
                        point.AgeFrames,
                        point.Confidence))
                    .ToArray();
                var meanAge = tracks.Length == 0 ? 0.0 : tracks.Average(static track => track.AgeFrames);
                var meanSpeed = tracks.Length == 0
                    ? 0.0
                    : tracks.Average(static track => Math.Sqrt(
                        track.VelocityXPerSecond * track.VelocityXPerSecond +
                        track.VelocityYPerSecond * track.VelocityYPerSecond));
                var confidence = group.Average(static point => point.Confidence);
                return new MimirFeatureTrackCameraObservation(
                    $"{frame.SourceId}:move:{group.Key}:{frame.ObservedTimeNs}",
                    frame.SourceId,
                    frame.Width,
                    frame.Height,
                    frame.ObservedTimeNs,
                    tracks,
                    tracks.Length,
                    meanAge,
                    meanSpeed,
                    confidence);
            })
            .ToArray();
        var stableCount = observations.Sum(static observation => observation.Tracks.Count(track => track.AgeFrames >= 3));
        var meanFrameAge = observations.Length == 0 ? 0.0 : observations.Average(static observation => observation.MeanTrackAgeFrames);
        var meanFrameSpeed = observations.Length == 0 ? 0.0 : observations.Average(static observation => observation.MeanSpeedPixelsPerSecond);
        return new MimirFeatureTrackFieldCandidate(
            $"move-controller-history:{frame.SourceId}:{frame.ObservedTimeNs}",
            calibrationId,
            producerKey,
            observations,
            stableCount,
            meanFrameAge,
            meanFrameSpeed,
            frame.Confidence,
            frame.ObservedTimeNs);
    }

    private MimirMoveControllerTrackFrame Frame(string sourceId, int width, int height, long observedTimeNs)
    {
        var points = states.Values
            .SelectMany(static state => state.History)
            .OrderBy(static point => point.ControllerId, StringComparer.Ordinal)
            .ThenBy(static point => point.AgeFrames)
            .ToArray();
        var confidence = points.Length == 0 ? 0.0 : points.GroupBy(static point => point.ControllerId).Average(static group => group.Last().Confidence);
        return new MimirMoveControllerTrackFrame(sourceId, width, height, observedTimeNs, points, confidence);
    }

    private bool TryDetectSphere(int width, int height, ReadOnlySpan<byte> luma, out MoveSphereDetection detection)
    {
        var threshold = (byte)Math.Clamp(options.MinimumLuma * 255.0, 0.0, 255.0);
        var weightedX = 0.0;
        var weightedY = 0.0;
        var weightSum = 0.0;
        var brightPixels = 0;
        byte peak = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var value = luma[row + x];
                if (value < threshold)
                {
                    continue;
                }

                var weight = value - threshold + 1.0;
                weightedX += x * weight;
                weightedY += y * weight;
                weightSum += weight;
                brightPixels++;
                peak = Math.Max(peak, value);
            }
        }

        if (brightPixels < options.MinimumBlobPixels || weightSum <= 0.0)
        {
            detection = default;
            return false;
        }

        var xCenter = weightedX / weightSum;
        var yCenter = weightedY / weightSum;
        var radius = Math.Sqrt(brightPixels / Math.PI);
        var confidence = Math.Clamp((peak / 255.0) * Math.Min(1.0, brightPixels / Math.Max(1.0, options.MinimumBlobPixels * 4.0)), 0.0, 1.0);
        detection = new MoveSphereDetection(xCenter, yCenter, radius, confidence);
        return true;
    }

    private static double PixelToClipX(double imageX, int width) =>
        Math.Clamp(imageX / Math.Max(1.0, width) * 2.0 - 1.0, -1.0, 1.0);

    private static double PixelToClipY(double imageY, int height) =>
        Math.Clamp(1.0 - imageY / Math.Max(1.0, height) * 2.0, -1.0, 1.0);

    private static int StableTrackId(string controllerId)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in controllerId)
            {
                hash = hash * 31 + ch;
            }

            return Math.Abs(hash == int.MinValue ? 0 : hash);
        }
    }

    private readonly record struct MoveSphereDetection(double X, double Y, double RadiusPixels, double Confidence);

    private sealed class MoveControllerState(string controllerId)
    {
        private readonly List<MimirMoveControllerTrackPoint> history = [];
        private long previousTimeNs;
        private double previousX;
        private double previousY;
        private int ageFrames;

        public MimirMoveControllerColor ExpectedColor { get; set; }

        public IReadOnlyList<MimirMoveControllerTrackPoint> History => history;

        public void Add(MoveSphereDetection detection, int width, int height, long observedTimeNs, int maxHistory)
        {
            var dtSeconds = previousTimeNs <= 0 || observedTimeNs <= previousTimeNs
                ? 1.0 / 187.0
                : Math.Max(1.0e-6, (observedTimeNs - previousTimeNs) / 1_000_000_000.0);
            var velocityX = previousTimeNs <= 0 ? 0.0 : (detection.X - previousX) / dtSeconds;
            var velocityY = previousTimeNs <= 0 ? 0.0 : (detection.Y - previousY) / dtSeconds;
            previousTimeNs = observedTimeNs;
            previousX = detection.X;
            previousY = detection.Y;
            ageFrames++;
            history.Add(new MimirMoveControllerTrackPoint(
                controllerId,
                ExpectedColor,
                detection.X,
                detection.Y,
                PixelToClipX(detection.X, width),
                PixelToClipY(detection.Y, height),
                detection.RadiusPixels,
                velocityX,
                velocityY,
                ageFrames,
                observedTimeNs,
                detection.Confidence));
            if (history.Count > maxHistory)
            {
                history.RemoveRange(0, history.Count - maxHistory);
            }
        }
    }
}
