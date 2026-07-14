using System.Numerics;

namespace Mimir.Runtime.Synchronization;

public static class MimirMoveSphereCalibrationFitter
{
    private const int ParameterCount = 8;

    public static MimirSensorCalibrationReceiptDocument Fit(
        MimirSensorCalibrationSessionDocument session,
        MoveVisibilityWindowReceipt evidence,
        int imageWidth,
        int imageHeight,
        double orbRadiusMeters,
        DateTimeOffset? completedAt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(evidence);
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));

        var rejectionReasons = new List<string>();
        if (session.Phase != MimirSensorCalibrationSessionPhase.Fitting)
            rejectionReasons.Add("session-is-not-ready-for-fitting");
        if (!(double.IsFinite(orbRadiusMeters) && orbRadiusMeters > 0.0))
            rejectionReasons.Add("measured-orb-radius-meters");
        if (session.SensorIds.Length != 2)
            rejectionReasons.Add("sphere-stereo-fit-requires-exactly-two-sensors");

        var pairs = BuildPairs(session, evidence);
        if (pairs.Length < session.Acceptance.MinimumSameFrameCorrespondences)
            rejectionReasons.Add($"same-frame-correspondences-{pairs.Length}-of-{session.Acceptance.MinimumSameFrameCorrespondences}");

        if (rejectionReasons.Count > 0)
            return Rejected(session, pairs.Length, rejectionReasons, completedAt);

        var heldOut = pairs.Where(IsHeldOut).ToArray();
        var training = pairs.Where(pair => !IsHeldOut(pair)).ToArray();
        if (heldOut.Length < 50 || training.Length < 200)
            return Rejected(session, training.Length, ["insufficient-deterministic-training-or-held-out-partition"], completedAt, heldOut.Length);

        var parameters = InitialParameters(training, imageWidth, imageHeight, orbRadiusMeters);
        var iterations = Optimize(parameters, training, imageWidth, imageHeight, orbRadiusMeters);
        var trainingConsensus = training;
        for (var pass = 0; pass < 3; pass++)
        {
            var pairErrors = PairErrors(parameters, trainingConsensus, imageWidth, imageHeight, orbRadiusMeters);
            var cutoff = Math.Clamp(Percentile(pairErrors.Select(value => value.Error).Order().ToArray(), 0.70) * 1.5, 4.0, 25.0);
            var next = pairErrors.Where(value => value.Error <= cutoff).Select(value => value.Pair).ToArray();
            if (next.Length < 200 || next.Length == trainingConsensus.Length) break;
            trainingConsensus = next;
            iterations += Optimize(parameters, trainingConsensus, imageWidth, imageHeight, orbRadiusMeters);
        }
        var validationCutoff = Math.Max(4.0, session.Acceptance.MaximumP95ReprojectionErrorPx * 2.0);
        var heldOutConsensus = PairErrors(parameters, heldOut, imageWidth, imageHeight, orbRadiusMeters)
            .Where(value => value.Error <= validationCutoff).Select(value => value.Pair).ToArray();
        var trainingErrors = ReprojectionErrors(parameters, trainingConsensus, imageWidth, imageHeight, orbRadiusMeters);
        var heldOutErrors = ReprojectionErrors(parameters, heldOutConsensus, imageWidth, imageHeight, orbRadiusMeters);
        var skewMs = pairs.Select(pair => pair.AbsoluteSkewNs / 1_000_000.0).Order().ToArray();
        var residuals = new MimirSensorCalibrationFitResiduals(
            Median(trainingErrors), Percentile(trainingErrors, 0.95),
            Median(heldOutErrors), Percentile(heldOutErrors, 0.95),
            Median(skewMs), skewMs[^1],
            ConditionEstimate(parameters, training, imageWidth, imageHeight, orbRadiusMeters),
            iterations,
            (double)trainingConsensus.Length / training.Length,
            (double)heldOutConsensus.Length / heldOut.Length);

        if (!parameters.All(double.IsFinite)) rejectionReasons.Add("non-finite-fit");
        if (Math.Exp(parameters[0]) is < 100.0 or > 3000.0) rejectionReasons.Add($"camera-0-focal-length-{Math.Exp(parameters[0]):F1}-px-out-of-range");
        if (Math.Exp(parameters[1]) is < 100.0 or > 3000.0) rejectionReasons.Add($"camera-1-focal-length-{Math.Exp(parameters[1]):F1}-px-out-of-range");
        if (residuals.HeldOutMedianReprojectionErrorPx > session.Acceptance.MaximumMedianReprojectionErrorPx)
            rejectionReasons.Add($"held-out-median-reprojection-error-{residuals.HeldOutMedianReprojectionErrorPx:F3}-px");
        if (residuals.HeldOutP95ReprojectionErrorPx > session.Acceptance.MaximumP95ReprojectionErrorPx)
            rejectionReasons.Add($"held-out-p95-reprojection-error-{residuals.HeldOutP95ReprojectionErrorPx:F3}-px");
        if (residuals.MaximumAssociationSkewMilliseconds > session.Acceptance.MaximumAssociationSkewMilliseconds)
            rejectionReasons.Add($"association-skew-{residuals.MaximumAssociationSkewMilliseconds:F3}-ms");
        if (heldOutConsensus.Length < 50 || residuals.HeldOutInlierFraction < 0.65)
            rejectionReasons.Add($"held-out-consensus-{heldOutConsensus.Length}-pairs-{residuals.HeldOutInlierFraction:P1}");

        var calibration = rejectionReasons.Count == 0
            ? BuildCalibration(session, parameters, imageWidth, imageHeight)
            : null;
        return new MimirSensorCalibrationReceiptDocument(
            $"{session.SessionId}:receipt",
            session.SessionId,
            session.TrackingSpaceId,
            (completedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            calibration is null ? MimirSensorCalibrationSessionPhase.Rejected : MimirSensorCalibrationSessionPhase.Promoted,
            training.Length,
            heldOut.Length,
            residuals,
            calibration,
            rejectionReasons.ToArray(),
            "mimir.sphere-stereo-bundle.v1");
    }

    private static Pair[] BuildPairs(MimirSensorCalibrationSessionDocument session, MoveVisibilityWindowReceipt evidence)
    {
        var camera0 = session.SensorIds[0];
        var camera1 = session.SensorIds[1];
        var wands = session.RequiredWandIds.ToHashSet(StringComparer.Ordinal);
        var candidates = evidence.Correspondences
            .Where(pair => wands.Contains(pair.MoveId) &&
                pair.AbsoluteSkewNs <= session.Acceptance.MaximumAssociationSkewMilliseconds * 1_000_000.0 &&
                string.Equals(pair.First.FrameId, pair.Second.FrameId, StringComparison.Ordinal))
            .Select(pair => Order(pair, camera0, camera1))
            .Where(pair => pair.HasValue)
            .Select(pair => pair!.Value)
            .Where(pair => Valid(pair.First) && Valid(pair.Second))
            .GroupBy(pair => (pair.First.FrameId, pair.MoveId))
            .Select(group => group.First())
            .OrderBy(pair => pair.First.PublishedAtNs)
            .ThenBy(pair => pair.MoveId, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) return candidates;
        var firstMedian = Median(candidates.Select(pair => (double)pair.First.RadiusPx).Order().ToArray());
        var secondMedian = Median(candidates.Select(pair => (double)pair.Second.RadiusPx).Order().ToArray());
        return candidates.Where(pair =>
        {
            var firstRatio = pair.First.RadiusPx / firstMedian;
            var secondRatio = pair.Second.RadiusPx / secondMedian;
            var crossRatio = pair.First.RadiusPx / pair.Second.RadiusPx;
            return firstRatio is >= 0.35 and <= 4.0 &&
                secondRatio is >= 0.35 and <= 4.0 &&
                crossRatio is >= 0.25f and <= 4.0f;
        }).ToArray();
    }

    private static Pair? Order(MoveCrossCameraCorrespondence pair, string camera0, string camera1)
    {
        if (pair.First.CameraId == camera0 && pair.Second.CameraId == camera1)
            return new Pair(pair.MoveId, pair.First, pair.Second, pair.AbsoluteSkewNs);
        if (pair.First.CameraId == camera1 && pair.Second.CameraId == camera0)
            return new Pair(pair.MoveId, pair.Second, pair.First, pair.AbsoluteSkewNs);
        return null;
    }

    private static bool Valid(MoveVisibilityObservation value) =>
        float.IsFinite(value.CenterXPx) && float.IsFinite(value.CenterYPx) &&
        float.IsFinite(value.RadiusPx) && value.RadiusPx > 0.0f;

    private static bool IsHeldOut(Pair pair) => (Fnva64($"{pair.First.FrameId}|{pair.MoveId}") % 10) < 2;

    private static double[] InitialParameters(Pair[] pairs, int width, int height, double radius)
    {
        var focal = Math.Max(width, height) * 0.82;
        var source = pairs.Select(pair => PointFromSphere(pair.First, focal, width, height, radius)).ToArray();
        var target = pairs.Select(pair => PointFromSphere(pair.Second, focal, width, height, radius)).ToArray();
        var (rotation, translation) = FitRigid(source, target);
        var rotationVector = RotationVector(rotation);
        return [Math.Log(focal), Math.Log(focal), rotationVector.X, rotationVector.Y, rotationVector.Z,
            translation.X, translation.Y, translation.Z];
    }

    private static int Optimize(double[] p, Pair[] pairs, int width, int height, double radius)
    {
        var lambda = 1e-3;
        var cost = Cost(p, pairs, width, height, radius);
        var iterations = 0;
        for (; iterations < 80; iterations++)
        {
            BuildNormalEquations(p, pairs, width, height, radius, out var normal, out var gradient);
            for (var i = 0; i < ParameterCount; i++) normal[i, i] += lambda * Math.Max(1.0, normal[i, i]);
            if (!Solve(normal, gradient.Select(value => -value).ToArray(), out var delta)) break;
            var candidate = p.Zip(delta, (value, step) => value + step).ToArray();
            var candidateCost = Cost(candidate, pairs, width, height, radius);
            if (candidateCost < cost)
            {
                Array.Copy(candidate, p, ParameterCount);
                if (Math.Abs(cost - candidateCost) <= Math.Max(1e-9, cost * 1e-8)) { iterations++; break; }
                cost = candidateCost;
                lambda = Math.Max(1e-8, lambda * 0.35);
            }
            else
            {
                lambda = Math.Min(1e8, lambda * 8.0);
            }
        }
        return iterations;
    }

    private static void BuildNormalEquations(double[] p, Pair[] pairs, int width, int height, double radius, out double[,] normal, out double[] gradient)
    {
        normal = new double[ParameterCount, ParameterCount];
        gradient = new double[ParameterCount];
        foreach (var pair in Sample(pairs, 2500))
        {
            var residual = Residual(p, pair, width, height, radius);
            var jacobian = new double[residual.Length, ParameterCount];
            for (var column = 0; column < ParameterCount; column++)
            {
                var step = column < 2 ? 1e-5 : 1e-6;
                var perturbed = (double[])p.Clone();
                perturbed[column] += step;
                var shifted = Residual(perturbed, pair, width, height, radius);
                for (var row = 0; row < residual.Length; row++) jacobian[row, column] = (shifted[row] - residual[row]) / step;
            }
            for (var row = 0; row < residual.Length; row++)
            {
                var weight = HuberWeight(residual[row], 4.0) * Math.Max(0.05, Math.Min(pair.First.Confidence, pair.Second.Confidence));
                for (var i = 0; i < ParameterCount; i++)
                {
                    gradient[i] += weight * jacobian[row, i] * residual[row];
                    for (var j = 0; j <= i; j++) normal[i, j] += weight * jacobian[row, i] * jacobian[row, j];
                }
            }
        }
        for (var i = 0; i < ParameterCount; i++)
            for (var j = 0; j < i; j++) normal[j, i] = normal[i, j];
    }

    private static double[] Residual(double[] p, Pair pair, int width, int height, double radius)
    {
        var f0 = Math.Exp(p[0]);
        var f1 = Math.Exp(p[1]);
        var rotation = QuaternionFromRotationVector(new Vector3((float)p[2], (float)p[3], (float)p[4]));
        var translation = new Vector3((float)p[5], (float)p[6], (float)p[7]);
        var point0 = PointFromSphere(pair.First, f0, width, height, radius);
        var point1 = Vector3.Transform(point0, rotation) + translation;
        var forward = Project(point1, f1, width, height, radius);
        var pointFrom1 = PointFromSphere(pair.Second, f1, width, height, radius);
        var reversePoint = Vector3.Transform(pointFrom1 - translation, Quaternion.Conjugate(rotation));
        var reverse = Project(reversePoint, f0, width, height, radius);
        return [
            forward.X - pair.Second.CenterXPx, forward.Y - pair.Second.CenterYPx, (forward.Z - pair.Second.RadiusPx) * 0.5,
            reverse.X - pair.First.CenterXPx, reverse.Y - pair.First.CenterYPx, (reverse.Z - pair.First.RadiusPx) * 0.5];
    }

    private static double Cost(double[] p, Pair[] pairs, int width, int height, double radius) =>
        Sample(pairs, 2500).Sum(pair => Residual(p, pair, width, height, radius).Sum(value => HuberLoss(value, 4.0)));

    private static double[] ReprojectionErrors(double[] p, Pair[] pairs, int width, int height, double radius) =>
        pairs.SelectMany(pair =>
        {
            var residual = Residual(p, pair, width, height, radius);
            return new[] { Math.Sqrt(residual[0] * residual[0] + residual[1] * residual[1]), Math.Sqrt(residual[3] * residual[3] + residual[4] * residual[4]) };
        }).Order().ToArray();

    private static (Pair Pair, double Error)[] PairErrors(double[] p, Pair[] pairs, int width, int height, double radius) =>
        pairs.Select(pair =>
        {
            var residual = Residual(p, pair, width, height, radius);
            var forward = Math.Sqrt(residual[0] * residual[0] + residual[1] * residual[1]);
            var reverse = Math.Sqrt(residual[3] * residual[3] + residual[4] * residual[4]);
            return (pair, Math.Max(forward, reverse));
        }).OrderBy(value => value.Item2).Select(value => (value.pair, value.Item2)).ToArray();

    private static MimirMoveFusionRigCalibration BuildCalibration(MimirSensorCalibrationSessionDocument session, double[] p, int width, int height)
    {
        var worldToCamera = QuaternionFromRotationVector(new Vector3((float)p[2], (float)p[3], (float)p[4]));
        var cameraToWorld = Quaternion.Normalize(Quaternion.Conjugate(worldToCamera));
        var translation = new Vector3((float)p[5], (float)p[6], (float)p[7]);
        var position = Vector3.Transform(-translation, cameraToWorld);
        return new MimirMoveFusionRigCalibration(
            $"{session.SessionId}:sphere-stereo",
            session.TrackingSpaceId,
            [
                new MimirMoveFusionCameraCalibration(session.SensorIds[0], Fnva64(session.SensorIds[0]),
                    new MimirVector3Snapshot(0, 0, 0), new MimirQuaternionSnapshot(0, 0, 0, 1),
                    Math.Exp(p[0]), Math.Exp(p[0]), width * 0.5, height * 0.5),
                new MimirMoveFusionCameraCalibration(session.SensorIds[1], Fnva64(session.SensorIds[1]),
                    new MimirVector3Snapshot(position.X, position.Y, position.Z),
                    new MimirQuaternionSnapshot(cameraToWorld.X, cameraToWorld.Y, cameraToWorld.Z, cameraToWorld.W),
                    Math.Exp(p[1]), Math.Exp(p[1]), width * 0.5, height * 0.5)
            ],
            MaximumAssociationSkewMilliseconds: session.Acceptance.MaximumAssociationSkewMilliseconds);
    }

    private static MimirSensorCalibrationReceiptDocument Rejected(MimirSensorCalibrationSessionDocument session, int trainingCount, IEnumerable<string> reasons, DateTimeOffset? completedAt, int heldOutCount = 0) =>
        new($"{session.SessionId}:receipt", session.SessionId, session.TrackingSpaceId,
            (completedAt ?? DateTimeOffset.UtcNow).ToString("O"), MimirSensorCalibrationSessionPhase.Rejected,
            trainingCount, heldOutCount,
            new MimirSensorCalibrationFitResiduals(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.PositiveInfinity, 0),
            null, reasons.ToArray(), "mimir.sphere-stereo-bundle.v1");

    private static Vector3 PointFromSphere(MoveVisibilityObservation observation, double focal, int width, int height, double radius)
    {
        var z = radius * Math.Sqrt(1.0 + focal * focal / (observation.RadiusPx * observation.RadiusPx));
        return new Vector3((float)((observation.CenterXPx - width * 0.5) * z / focal),
            (float)((height * 0.5 - observation.CenterYPx) * z / focal), (float)z);
    }

    private static Vector3 Project(Vector3 point, double focal, int width, int height, double radius)
    {
        if (point.Z <= radius * 1.001) return new Vector3(1e6f, 1e6f, 1e6f);
        var imageRadius = focal * radius / Math.Sqrt(point.Z * point.Z - radius * radius);
        return new Vector3((float)(width * 0.5 + focal * point.X / point.Z),
            (float)(height * 0.5 - focal * point.Y / point.Z), (float)imageRadius);
    }

    private static (Quaternion Rotation, Vector3 Translation) FitRigid(Vector3[] source, Vector3[] target)
    {
        var sourceCenter = source.Aggregate(Vector3.Zero, (sum, value) => sum + value) / source.Length;
        var targetCenter = target.Aggregate(Vector3.Zero, (sum, value) => sum + value) / target.Length;
        double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var a = source[i] - sourceCenter;
            var b = target[i] - targetCenter;
            sxx += a.X * b.X; sxy += a.X * b.Y; sxz += a.X * b.Z;
            syx += a.Y * b.X; syy += a.Y * b.Y; syz += a.Y * b.Z;
            szx += a.Z * b.X; szy += a.Z * b.Y; szz += a.Z * b.Z;
        }
        var n = new double[,] {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz }
        };
        var q = new[] { 1.0, 0.0, 0.0, 0.0 };
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var next = new double[4];
            for (var row = 0; row < 4; row++) for (var column = 0; column < 4; column++) next[row] += n[row, column] * q[column];
            var norm = Math.Sqrt(next.Sum(value => value * value));
            if (norm < 1e-12) break;
            for (var j = 0; j < 4; j++) q[j] = next[j] / norm;
        }
        var rotation = Quaternion.Normalize(new Quaternion((float)q[1], (float)q[2], (float)q[3], (float)q[0]));
        return (rotation, targetCenter - Vector3.Transform(sourceCenter, rotation));
    }

    private static Vector3 RotationVector(Quaternion quaternion)
    {
        quaternion = Quaternion.Normalize(quaternion);
        if (quaternion.W < 0) quaternion = new Quaternion(-quaternion.X, -quaternion.Y, -quaternion.Z, -quaternion.W);
        var angle = 2.0 * Math.Acos(Math.Clamp(quaternion.W, -1.0f, 1.0f));
        var sinHalf = Math.Sqrt(Math.Max(0.0, 1.0 - quaternion.W * quaternion.W));
        return sinHalf < 1e-8 ? Vector3.Zero : new Vector3(quaternion.X, quaternion.Y, quaternion.Z) * (float)(angle / sinHalf);
    }

    private static Quaternion QuaternionFromRotationVector(Vector3 value)
    {
        var angle = value.Length();
        return angle < 1e-9f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(value / angle, angle);
    }

    private static Pair[] Sample(Pair[] pairs, int maximum)
    {
        if (pairs.Length <= maximum) return pairs;
        var stride = (double)pairs.Length / maximum;
        return Enumerable.Range(0, maximum).Select(index => pairs[(int)(index * stride)]).ToArray();
    }

    private static double ConditionEstimate(double[] p, Pair[] pairs, int width, int height, double radius)
    {
        BuildNormalEquations(p, pairs, width, height, radius, out var normal, out _);
        var diagonal = Enumerable.Range(0, ParameterCount).Select(index => Math.Abs(normal[index, index])).Where(value => value > 1e-12).ToArray();
        return diagonal.Length == 0 ? double.PositiveInfinity : diagonal.Max() / diagonal.Min();
    }

    private static bool Solve(double[,] matrix, double[] rhs, out double[] solution)
    {
        var n = rhs.Length;
        var augmented = new double[n, n + 1];
        for (var row = 0; row < n; row++) { for (var column = 0; column < n; column++) augmented[row, column] = matrix[row, column]; augmented[row, n] = rhs[row]; }
        for (var pivot = 0; pivot < n; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < n; row++) if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot])) best = row;
            if (Math.Abs(augmented[best, pivot]) < 1e-12) { solution = []; return false; }
            if (best != pivot) for (var column = pivot; column <= n; column++) (augmented[pivot, column], augmented[best, column]) = (augmented[best, column], augmented[pivot, column]);
            var scale = augmented[pivot, pivot];
            for (var column = pivot; column <= n; column++) augmented[pivot, column] /= scale;
            for (var row = 0; row < n; row++) if (row != pivot)
            {
                var factor = augmented[row, pivot];
                for (var column = pivot; column <= n; column++) augmented[row, column] -= factor * augmented[pivot, column];
            }
        }
        solution = Enumerable.Range(0, n).Select(row => augmented[row, n]).ToArray();
        return solution.All(double.IsFinite);
    }

    private static double HuberLoss(double value, double threshold) => Math.Abs(value) <= threshold ? 0.5 * value * value : threshold * (Math.Abs(value) - 0.5 * threshold);
    private static double HuberWeight(double value, double threshold) => Math.Abs(value) <= threshold ? 1.0 : threshold / Math.Abs(value);
    private static double Median(double[] sorted) => Percentile(sorted, 0.5);
    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return double.NaN;
        var position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static ulong Fnva64(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var item in System.Text.Encoding.UTF8.GetBytes(value)) { hash ^= item; hash *= 1099511628211UL; }
        return hash;
    }

    private readonly record struct Pair(string MoveId, MoveVisibilityObservation First, MoveVisibilityObservation Second, long AbsoluteSkewNs);
}
