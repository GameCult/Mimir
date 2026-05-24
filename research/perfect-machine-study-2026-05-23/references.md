# Research References

## Chirplet And Chirp Analysis

- Steve Mann, *The Chirplet Transform: A new signal analysis technique based on
  affine relationships in the time-frequency plane*:
  https://www.media.mit.edu/publications/the-chirplet-transform-a-new-signal-analysis-technique-based-on-affine-relationships-in-the-time-frequency-plane/
- General linear chirplet transform overview:
  https://www.sciencedirect.com/science/article/pii/S0888327015003994
- High-resolution chirplet transform parameter analysis:
  https://arxiv.org/abs/2108.00572
- Fast Chirplet Transform with FPGA-based implementation:
  https://www.ece.iit.edu/~eoruklu/IIT/Publications_files/04639630.pdf
- Speed-optimized fast chirplet implementation:
  https://ecasp.ece.iit.edu/publications/2012-present/2023-13.pdf

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
- Microsoft Kernel Streaming / AVStream overview:
  https://learn.microsoft.com/en-gb/windows-hardware/drivers/stream/kernel-streaming
- DXGI shared resource handle creation:
  https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiresource1-createsharedhandle
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

## Volumetric Audio / Room Acoustics / Actuation

- Pyroomacoustics beamforming module:
  https://pyroomacoustics.readthedocs.io/en/pypi-release/pyroomacoustics.beamforming.html
- Pyroomacoustics repository and room/array algorithm examples:
  https://github.com/LCAV/pyroomacoustics
- Pyroomacoustics paper:
  https://arxiv.org/abs/1710.04196
- ManyEars sound source localization, tracking, and separation:
  https://github.com/introlab/manyears
- Spatial Audio Framework, open C/C++ spatial-audio toolkit:
  https://github.com/leomccormack/Spatial_Audio_Framework
- Sound field reconstruction in reverberant environments with rigid spherical
  microphone arrays:
  https://journals.sagepub.com/doi/10.1177/14613484261447569
- Sparse Reconstruction of Sound Field Using Bayesian Compressive Sensing and
  Equivalent Source Method:
  https://pmc.ncbi.nlm.nih.gov/articles/PMC10301025/
- Near-field acoustic holography with compressive sensing:
  https://docs.lib.purdue.edu/herrick/208/
- Ambisonic encoding from equatorial microphone arrays:
  https://arxiv.org/abs/2211.00584
- Spheroidal ambisonics:
  https://arxiv.org/abs/2103.05719
- Real-time spherical array renderer:
  https://research.chalmers.se/publication/509494
- Farrow fractional delay overview:
  https://engee.com/helpcenter/stable/en/interactive-scripts/dsp/fractional_delay.html
- AMD Vitis fractional-delay Farrow filter note:
  https://docs.amd.com/r/2024.1-English/Vitis-Tutorials-AI-Engine-Development/Fractional-Delay-Farrow-Filter
- Faust delay/fractional delay reference:
  https://ringbuffer.org/faust/faust_basics/faust-delay/
- MATLAB Farrow fractional delay example:
  https://www.mathworks.com/help/dsp/ug/fractional-delay-filters-using-farrow-structures.html
- Joint SFO estimation and compensation based on Farrow structure:
  https://arxiv.org/abs/2503.07577
- Joint SFO estimation and compensation algorithms based on Farrow structure:
  https://arxiv.org/abs/2603.00627

## Realtime Gaussian Splatting / Dynamic Visual Fields

- Official 3D Gaussian Splatting implementation:
  https://github.com/graphdeco-inria/gaussian-splatting
- 3D Gaussian Splatting paper:
  https://arxiv.org/abs/2308.04079
- 4D Gaussian Splatting implementation:
  https://github.com/fudan-zvg/4d-gaussian-splatting
- 4D Gaussian Splatting paper:
  https://openaccess.thecvf.com/content/CVPR2024/papers/Wu_4D_Gaussian_Splatting_for_Real-Time_Dynamic_Scene_Rendering_CVPR_2024_paper.pdf
- Spacetime Gaussian Feature Splatting implementation:
  https://github.com/oppo-us-research/SpacetimeGaussians
- Spacetime Gaussian Feature Splatting paper:
  https://arxiv.org/abs/2312.16812
- PlayCanvas engine, an open WebGL/WebGPU engine with Gaussian splatting support:
  https://github.com/playcanvas/engine
- PlayCanvas generic Gaussian splat processing docs:
  https://developer.playcanvas.com/user-manual/gaussian-splatting/building/unified-rendering/splat-processing/
- NVIDIA vk_gaussian_splatting technical blog:
  https://developer.nvidia.com/blog/real-time-gpu-accelerated-gaussian-splatting-with-nvidia-designworks-sample-vk_gaussian_splatting/
- gsplat documentation:
  https://docs.gsplat.studio/main/
- gsplat rasterization API:
  https://docs.gsplat.studio/main/apis/rasterization.html
- gsplat paper:
  https://arxiv.org/abs/2409.06765
- StopThePop sorted Gaussian splatting implementation:
  https://github.com/r4dl/StopThePop
- FlashGS paper:
  https://arxiv.org/abs/2408.07967
- Gaussian-LIC, Gaussian splatting plus LiDAR-inertial-camera SLAM:
  https://arxiv.org/abs/2404.06926
- RMGS-SLAM, real-time multi-sensor Gaussian splatting SLAM:
  https://arxiv.org/abs/2604.12942
- Multi-Calib, online multi-sensor calibration direction:
  https://pmc.ncbi.nlm.nih.gov/articles/PMC12693731/

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
- Visual fusion should borrow Gaussian splatting data/layout ideas, but the live
  Mimir problem is online synchronized evidence update, not offline novel-view
  training from a static capture folder.
