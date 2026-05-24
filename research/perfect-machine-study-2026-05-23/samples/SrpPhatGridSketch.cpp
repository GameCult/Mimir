// Sketch only. Purpose: first acoustic source-position proof after mic timing is
// calibrated. This is intentionally small and direct, not a full acoustic camera.

#include <cmath>
#include <cstdint>
#include <span>

struct MicPoseSketch
{
    float x;
    float y;
    float z;
};

struct CandidatePointSketch
{
    float x;
    float y;
    float z;
};

struct PairDelaySketch
{
    int micA;
    int micB;
    double delaySeconds;
    float confidence;
};

struct SrpResultSketch
{
    int bestIndex;
    float score;
};

static inline double distanceMeters(
    const MicPoseSketch& mic,
    const CandidatePointSketch& point)
{
    const double dx = static_cast<double>(point.x) - mic.x;
    const double dy = static_cast<double>(point.y) - mic.y;
    const double dz = static_cast<double>(point.z) - mic.z;
    return std::sqrt(dx * dx + dy * dy + dz * dz);
}

SrpResultSketch scoreGrid(
    std::span<const MicPoseSketch> mics,
    std::span<const CandidatePointSketch> grid,
    std::span<const PairDelaySketch> observedPairs,
    double speedOfSoundMetersPerSecond)
{
    auto best = SrpResultSketch{-1, -1.0f};

    for (int pointIndex = 0; pointIndex < static_cast<int>(grid.size()); pointIndex++)
    {
        const auto& point = grid[static_cast<size_t>(pointIndex)];
        double score = 0.0;
        double weight = 0.0;

        for (const auto& pair : observedPairs)
        {
            if (pair.micA < 0 || pair.micB < 0 ||
                pair.micA >= static_cast<int>(mics.size()) ||
                pair.micB >= static_cast<int>(mics.size()) ||
                pair.confidence <= 0.0f)
            {
                continue;
            }

            const double da = distanceMeters(mics[static_cast<size_t>(pair.micA)], point);
            const double db = distanceMeters(mics[static_cast<size_t>(pair.micB)], point);
            const double predicted = (db - da) / speedOfSoundMetersPerSecond;
            const double errorUs = std::abs(predicted - pair.delaySeconds) * 1'000'000.0;

            // Smooth robust score: 1 at zero error, half around 100 us.
            const double local = 1.0 / (1.0 + errorUs / 100.0);
            score += local * pair.confidence;
            weight += pair.confidence;
        }

        if (weight <= 0.0)
        {
            continue;
        }

        const float normalized = static_cast<float>(score / weight);
        if (normalized > best.score)
        {
            best = SrpResultSketch{pointIndex, normalized};
        }
    }

    return best;
}

