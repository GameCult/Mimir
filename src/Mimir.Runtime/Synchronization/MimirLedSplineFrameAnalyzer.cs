namespace Mimir.Runtime.Synchronization;

public sealed record MimirLedSplineFrameAnalyzerOptions(
    int ExpectedLedCount,
    double MinimumNormalizedLuma = 0.70,
    int MinimumComponentPixels = 2,
    int MaximumComponentPixels = 4096,
    int MaximumDetections = 96);

public sealed record MimirLedSplineFrameAnalysis(
    MimirLedSplineCurveFit Curve,
    MimirLedSplineQualityReport Quality,
    int CandidateComponentCount,
    double SaturatedFraction);

public sealed class MimirLedSplineFrameAnalyzer(MimirLedSplineFrameAnalyzerOptions options)
{
    public MimirLedSplineFrameAnalysis AnalyzeLumaFrame(
        string sourceId,
        string observationKey,
        int width,
        int height,
        ReadOnlySpan<byte> luma,
        long observedTimeNs)
    {
        if (width <= 0 || height <= 0 || luma.Length < width * height)
        {
            var empty = new MimirLedSplineCurveFit(sourceId, observationKey, [], 0.0, 0.0);
            return new MimirLedSplineFrameAnalysis(
                empty,
                new MimirLedSplineQualityScorer().Score(empty, options.ExpectedLedCount),
                0,
                0.0);
        }

        var threshold = (byte)Math.Clamp((int)Math.Round(options.MinimumNormalizedLuma * 255.0), 0, 255);
        var visited = new bool[width * height];
        var detections = new List<MimirLedPixelDetection>();
        var saturatedComponents = 0;
        var queue = new int[Math.Min(width * height, Math.Max(options.MaximumComponentPixels + 1, 64))];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * width + x;
                if (visited[offset] || luma[offset] < threshold)
                {
                    continue;
                }

                if (TryReadComponent(width, height, luma, threshold, visited, x, y, queue, out var detection, out var saturated))
                {
                    detections.Add(detection);
                    saturatedComponents += saturated ? 1 : 0;
                    if (detections.Count >= options.MaximumDetections)
                    {
                        break;
                    }
                }
            }

            if (detections.Count >= options.MaximumDetections)
            {
                break;
            }
        }

        var strongest = detections
            .OrderByDescending(static detection => detection.Confidence)
            .Take(options.MaximumDetections)
            .ToArray();
        var curve = new MimirLedSplineCurveSolver().SolveCameraCurve(
            sourceId,
            observationKey,
            width,
            height,
            observedTimeNs,
            strongest);
        var saturatedFraction = detections.Count == 0 ? 0.0 : saturatedComponents / (double)detections.Count;
        var exposureFitness = strongest.Length == 0
            ? 0.0
            : strongest.Average(static detection =>
            {
                var peak = Math.Clamp(detection.PeakLuma, 0.0, 1.0);
                return peak < 0.18 ? peak / 0.18 : peak > 0.92 ? Math.Max(0.0, 1.0 - (peak - 0.92) / 0.08) : 1.0;
            });
        return new MimirLedSplineFrameAnalysis(
            curve,
            new MimirLedSplineQualityScorer().Score(curve, options.ExpectedLedCount, saturatedFraction, exposureFitness),
            detections.Count,
            saturatedFraction);
    }

    private bool TryReadComponent(
        int width,
        int height,
        ReadOnlySpan<byte> luma,
        byte threshold,
        bool[] visited,
        int startX,
        int startY,
        int[] queue,
        out MimirLedPixelDetection detection,
        out bool saturated)
    {
        var head = 0;
        var tail = 0;
        var overflow = false;
        var startOffset = startY * width + startX;
        visited[startOffset] = true;
        queue[tail++] = startOffset;
        var count = 0;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumWeight = 0.0;
        var maxLuma = 0;
        var saturatedPixels = 0;

        while (head < tail)
        {
            var offset = queue[head++];
            var x = offset % width;
            var y = offset / width;
            var value = luma[offset];
            var weight = Math.Max(1, value - threshold + 1);
            count++;
            sumX += x * weight;
            sumY += y * weight;
            sumWeight += weight;
            maxLuma = Math.Max(maxLuma, value);
            saturatedPixels += value >= 252 ? 1 : 0;

            for (var neighbor = 0; neighbor < 4; neighbor++)
            {
                var nextX = neighbor switch
                {
                    0 => x - 1,
                    1 => x + 1,
                    _ => x,
                };
                var nextY = neighbor switch
                {
                    2 => y - 1,
                    3 => y + 1,
                    _ => y,
                };
                if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height || overflow)
                {
                    continue;
                }

                var next = nextY * width + nextX;
                if (visited[next] || luma[next] < threshold)
                {
                    continue;
                }

                visited[next] = true;
                if (tail >= queue.Length)
                {
                    overflow = true;
                    continue;
                }

                queue[tail++] = next;
            }
        }

        if (overflow ||
            count < options.MinimumComponentPixels ||
            count > options.MaximumComponentPixels ||
            sumWeight <= 0.0)
        {
            detection = default;
            saturated = saturatedPixels > 0;
            return false;
        }

        var centroidX = sumX / sumWeight;
        var centroidY = sumY / sumWeight;
        var radius = Math.Sqrt(count / Math.PI);
        var peak = maxLuma / 255.0;
        saturated = saturatedPixels > Math.Max(1, count / 4);
        var confidence = Math.Clamp(peak, 0.0, 1.0) * (saturated ? 0.55 : 1.0);
        detection = new MimirLedPixelDetection(
            LedIndex: -1,
            ImageX: centroidX,
            ImageY: centroidY,
            RadiusPixels: radius,
            Confidence: confidence,
            PeakLuma: peak,
            LocalContrast: Math.Clamp((maxLuma - threshold) / Math.Max(1.0, 255.0 - threshold), 0.0, 1.0),
            IsSaturated: saturated);
        return true;
    }
}
