# Research References

## Chirplet And Chirp Analysis

- Steve Mann, *The Chirplet Transform: A new signal analysis technique based on
  affine relationships in the time-frequency plane*:
  https://www.media.mit.edu/publications/the-chirplet-transform-a-new-signal-analysis-technique-based-on-affine-relationships-in-the-time-frequency-plane/
- General linear chirplet transform overview:
  https://www.sciencedirect.com/science/article/pii/S0888327015003994
- High-resolution chirplet transform parameter analysis:
  https://arxiv.org/abs/2108.00572

## Chirp Spread Spectrum / LoRa Receiver Shape

- SDR-LoRa design notes, including synchronization via dechirp and FFT:
  https://www.sciencedirect.com/science/article/pii/S1389128624000264
- OpenLoRa demodulator evaluation:
  https://openlora.wisc.edu/demodulators-evaluated/
- OpenLoRa NSDI paper:
  https://www.usenix.org/system/files/nsdi23-mishra.pdf
- CSS receiver design:
  https://arxiv.org/abs/2105.02833
- LoRa/CSS tutorial:
  https://arxiv.org/abs/2310.10503
- I/Q CSS with coherent detector and channel estimation:
  https://arxiv.org/abs/2009.10421
- Data-over-audio reference implementation trail:
  https://github.com/cawfree/OpenChirp

## Passive Audio Timing / GCC-PHAT

- Improved GCC-PHAT weighting for time-delay estimation:
  https://pmc.ncbi.nlm.nih.gov/articles/PMC9571281/
- Acoustic DoA paper noting GCC-PHAT normalization and interpolation:
  https://pmc.ncbi.nlm.nih.gov/articles/PMC11014389/
- Parametrized GCC-PHAT features for time-delay estimation:
  https://www.isca-archive.org/interspeech_2021/salvati21_interspeech.html
- Complexity/accuracy of GCC-PHAT methods:
  https://arxiv.org/abs/1811.11787

## Low-Level Transform / Compute References

- HLSL Shader Model 6 wave intrinsics:
  https://learn.microsoft.com/windows/win32/direct3dhlsl/hlsl-shader-model-6-0-features-for-direct3d-12
- DirectX Shader Compiler wave intrinsics notes:
  https://github.com/microsoft/directxshadercompiler/wiki/wave-intrinsics
- cuFFT callback documentation:
  https://docs.nvidia.com/cuda/archive/12.5.0/cufft/index.html
- NVIDIA cuFFT callback performance article:
  https://developer.nvidia.com/blog/cuda-pro-tip-use-cufft-callbacks-custom-data-processing/
- FFTW reference:
  https://www.fftw.org/fftw2_doc/fftw_3.html
- FFTW SIMD alignment:
  https://www.fftw.org/~fftw/doc/SIMD-alignment-and-fftw_005fmalloc.html
- Chirp Z-transform theory:
  https://pyffs.readthedocs.io/en/stable/theory/CZT.html

## How These References Steer Mimir

- Dechirp-plus-bin scoring is the right hot active receiver shape for controlled
  chirps.
- Dense generic chirplet transforms are research/reference tools, not the
  runtime hot loop unless the codebook stops being controlled.
- GCC-PHAT is still valuable for passive relative delay, but it must remain an
  evidence source with confidence, not a canonical timestamp source.
- GPU or native FFT paths should be considered only where batching is real; for
  a small fixed bin bank, SIMD Goertzel/dechirp may beat FFT overhead.
- If a transform path moves to GPU, fuse dechirp/window/preprocess with transform
  load/store where possible to avoid memory bandwidth waste.

