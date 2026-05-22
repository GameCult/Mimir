using System.Numerics;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirPassiveAudioSynchronizationEstimate(
    double DelaySamples,
    double Confidence,
    double Peak,
    double NoiseFloor,
    double SecondPeak,
    int ComparedSamples,
    string Status);

public sealed class MimirPassiveAudioSynchronizationEstimator
{
    private const int TargetWindowSamples = 32_768;
    private const double MaxSingleWindowConfidence = 0.85;
    private const double Preemphasis = 0.97;

    public MimirPassiveAudioSynchronizationEstimate Estimate(
        ReadOnlySpan<float> reference,
        ReadOnlySpan<float> candidate,
        int sampleRate)
    {
        var compared = Math.Min(reference.Length, candidate.Length);
        if (compared < 4096 || sampleRate <= 0)
        {
            return new MimirPassiveAudioSynchronizationEstimate(0.0, 0.0, 0.0, 0.0, 0.0, compared, "passive-insufficient-window");
        }

        var windowSamples = Math.Min(TargetWindowSamples, compared);
        var fftSize = NextPowerOfTwo(windowSamples * 2);
        var referenceSpectrum = new Complex[fftSize];
        var candidateSpectrum = new Complex[fftSize];
        FillWindow(reference[^windowSamples..], referenceSpectrum, windowSamples);
        FillWindow(candidate[^windowSamples..], candidateSpectrum, windowSamples);

        FastFourierTransform(referenceSpectrum, inverse: false);
        FastFourierTransform(candidateSpectrum, inverse: false);
        for (var index = 0; index < fftSize; index++)
        {
            var cross = Complex.Conjugate(referenceSpectrum[index]) * candidateSpectrum[index];
            var magnitude = cross.Magnitude;
            referenceSpectrum[index] = magnitude > 1.0e-12 ? cross / magnitude : Complex.Zero;
        }

        FastFourierTransform(referenceSpectrum, inverse: true);
        var maxLag = Math.Min(sampleRate, windowSamples / 2);
        var bestIndex = 0;
        var best = double.NegativeInfinity;
        var sum = 0.0;
        var sumSquares = 0.0;
        var count = 0;
        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            var value = CorrelationAt(referenceSpectrum, lag);
            var abs = Math.Abs(value);
            sum += abs;
            sumSquares += abs * abs;
            count++;
            if (abs > best)
            {
                best = abs;
                bestIndex = lag;
            }
        }

        var secondBest = FindSecondPeak(referenceSpectrum, -maxLag, maxLag, bestIndex);
        var mean = sum / Math.Max(1, count);
        var variance = Math.Max(0.0, sumSquares / Math.Max(1, count) - mean * mean);
        var sigma = Math.Sqrt(variance);
        var refinedLag = bestIndex + RefinePeak(referenceSpectrum, bestIndex);
        var peakRatio = mean > 1.0e-12 ? best / mean : 0.0;
        var zScore = sigma > 1.0e-12 ? (best - mean) / sigma : 0.0;
        var peakDominance = secondBest > 1.0e-12 ? (best - secondBest) / best : 1.0;
        var positiveLagConfidence = refinedLag >= 0.0 ? 1.0 : 0.0;
        var confidence = Math.Clamp((peakRatio - 1.5) / 8.0, 0.0, 1.0) *
            Math.Clamp(zScore / 16.0, 0.0, 1.0) *
            Math.Clamp(peakDominance / 0.35, 0.0, 1.0) *
            positiveLagConfidence;
        confidence = Math.Min(confidence, MaxSingleWindowConfidence);
        var status = refinedLag < 0.0
            ? "passive-negative-lag"
            : confidence > 0.08
                ? "passive-report"
                : "passive-low-confidence";
        return new MimirPassiveAudioSynchronizationEstimate(refinedLag, confidence, best, mean, secondBest, compared, status);
    }

    private static void FillWindow(ReadOnlySpan<float> source, Complex[] destination, int count)
    {
        var mean = 0.0;
        for (var index = 0; index < count; index++)
        {
            mean += source[index];
        }

        mean /= count;
        var previous = 0.0;
        for (var index = 0; index < count; index++)
        {
            var centered = source[index] - mean;
            var emphasized = centered - previous * Preemphasis;
            previous = centered;
            var window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / Math.Max(1, count - 1));
            destination[index] = new Complex(emphasized * window, 0.0);
        }
    }

    private static double CorrelationAt(Complex[] correlation, int lag)
    {
        var index = lag >= 0 ? lag : correlation.Length + lag;
        return correlation[index].Real;
    }

    private static double RefinePeak(Complex[] correlation, int lag)
    {
        var left = Math.Abs(CorrelationAt(correlation, lag - 1));
        var center = Math.Abs(CorrelationAt(correlation, lag));
        var right = Math.Abs(CorrelationAt(correlation, lag + 1));
        var denominator = left - 2.0 * center + right;
        return Math.Abs(denominator) < 1.0e-12
            ? 0.0
            : Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
    }

    private static double FindSecondPeak(Complex[] correlation, int firstLag, int lastLag, int bestLag)
    {
        var second = 0.0;
        var exclusionSamples = 256;
        for (var lag = firstLag; lag <= lastLag; lag++)
        {
            if (Math.Abs(lag - bestLag) <= exclusionSamples)
            {
                continue;
            }

            second = Math.Max(second, Math.Abs(CorrelationAt(correlation, lag)));
        }

        return second;
    }

    private static int NextPowerOfTwo(int value)
    {
        var power = 1;
        while (power < value)
        {
            power <<= 1;
        }

        return power;
    }

    private static void FastFourierTransform(Complex[] values, bool inverse)
    {
        var n = values.Length;
        for (int index = 1, swap = 0; index < n; index++)
        {
            var bit = n >> 1;
            for (; (swap & bit) != 0; bit >>= 1)
            {
                swap ^= bit;
            }

            swap ^= bit;
            if (index < swap)
            {
                (values[index], values[swap]) = (values[swap], values[index]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2.0 * Math.PI / length * (inverse ? 1.0 : -1.0);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += length)
            {
                var rotation = Complex.One;
                var half = length >> 1;
                for (var offset = 0; offset < half; offset++)
                {
                    var even = values[start + offset];
                    var odd = values[start + offset + half] * rotation;
                    values[start + offset] = even + odd;
                    values[start + offset + half] = even - odd;
                    rotation *= step;
                }
            }
        }

        if (!inverse)
        {
            return;
        }

        for (var index = 0; index < n; index++)
        {
            values[index] /= n;
        }
    }
}
