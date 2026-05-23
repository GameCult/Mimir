// Sketch only. Compile-time target would be /arch:AVX2 plus FMA.
// This illustrates a CPU hot loop for dechirp + small-bin scoring.

#include <immintrin.h>
#include <cstddef>
#include <cstdint>

struct KernelSoA
{
    const float* cosBase;
    const float* sinBase;
    const float* binCos;
    const float* binSin;
    int sampleCount;
    int binCount;
};

struct BinScore
{
    float real;
    float imag;
    float energy;
};

static inline float hsum256(__m256 value)
{
    __m128 low = _mm256_castps256_ps128(value);
    __m128 high = _mm256_extractf128_ps(value, 1);
    __m128 sum = _mm_add_ps(low, high);
    sum = _mm_hadd_ps(sum, sum);
    sum = _mm_hadd_ps(sum, sum);
    return _mm_cvtss_f32(sum);
}

void score_dechirped_bins_avx2(
    const float* samples,
    const KernelSoA& kernel,
    BinScore* outScores)
{
    for (int bin = 0; bin < kernel.binCount; ++bin)
    {
        __m256 accRe = _mm256_setzero_ps();
        __m256 accIm = _mm256_setzero_ps();

        const float* binCos = kernel.binCos + bin * kernel.sampleCount;
        const float* binSin = kernel.binSin + bin * kernel.sampleCount;

        int i = 0;
        for (; i + 7 < kernel.sampleCount; i += 8)
        {
            __m256 x = _mm256_loadu_ps(samples + i);
            __m256 chirpC = _mm256_loadu_ps(kernel.cosBase + i);
            __m256 chirpS = _mm256_loadu_ps(kernel.sinBase + i);
            __m256 toneC = _mm256_loadu_ps(binCos + i);
            __m256 toneS = _mm256_loadu_ps(binSin + i);

            // Real input multiplied by conjugate chirp, then projected onto
            // the candidate bin tone. Algebra can be fused further once the
            // final kernel convention is frozen.
            __m256 deRe = _mm256_mul_ps(x, chirpC);
            __m256 deIm = _mm256_mul_ps(x, _mm256_sub_ps(_mm256_setzero_ps(), chirpS));

            accRe = _mm256_fmadd_ps(deRe, toneC, accRe);
            accRe = _mm256_fnmadd_ps(deIm, toneS, accRe);
            accIm = _mm256_fmadd_ps(deRe, toneS, accIm);
            accIm = _mm256_fmadd_ps(deIm, toneC, accIm);
        }

        float re = hsum256(accRe);
        float im = hsum256(accIm);
        for (; i < kernel.sampleCount; ++i)
        {
            float deRe = samples[i] * kernel.cosBase[i];
            float deIm = -samples[i] * kernel.sinBase[i];
            re += deRe * binCos[i] - deIm * binSin[i];
            im += deRe * binSin[i] + deIm * binCos[i];
        }

        outScores[bin] = { re, im, re * re + im * im };
    }
}

