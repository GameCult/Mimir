// Sketch only. Purpose: native ABI shape for batched chirp-bin scoring.
// The point is to batch enough work that managed/native transition cost does
// not dominate the actual multiply-adds.

#pragma once

#include <cstdint>

extern "C"
{
    struct MimirChirpScoreCandidate
    {
        std::int64_t absoluteSampleOffset;
        std::int32_t sampleOffsetInBuffer;
        std::int32_t sampleCount;
        float proposalEnergy;
    };

    struct MimirChirpScoreKernel
    {
        const float* window;
        const float* dechirpReal;
        const float* dechirpImag;
        std::int32_t sampleCount;
    };

    struct MimirChirpScoreRequest
    {
        const float* samples;
        std::int32_t sampleCount;
        const MimirChirpScoreCandidate* candidates;
        std::int32_t candidateCount;
        const MimirChirpScoreKernel* kernels;
        std::int32_t kernelCount;
        std::int32_t topK;
    };

    struct MimirChirpScoreResult
    {
        std::int64_t absoluteSampleOffset;
        std::int32_t kernelIndex;
        float energy;
        float phaseRadians;
    };

    // Returns the number of result rows written. Production ABI would also
    // return an error/status code and expose required result capacity.
    std::int32_t mimir_score_chirp_candidates(
        const MimirChirpScoreRequest* request,
        MimirChirpScoreResult* results,
        std::int32_t resultCapacity);
}

