# Bibliography: Chirplet Sync Decoder

Mirrored copies live under `research/chirplet-sync-decoder/mirrors/`.

## Controlled Chirp / Symbol Decoding

- H. Hannuna et al., "ChirpMu: Chirp Signal-Based Low-Power Wide-Area Networks," IWQoS 2021.  
  Source: https://hhannuaa.gitlab.io/papers/iwqos_chirpmu_2021.pdf  
  Mirror: `mirrors/chirpmu-iwqos-2021.pdf`  
  Relevance: Uses FFT-band energy over chirp-like symbols and explicitly treats STFT/Hough-style chirp decoding as too complex for constrained devices.

- J. Zhang et al., "ChirpTransformer: Versatile LoRa Encoding for Low-Power Wide-Area IoT," MobiSys 2024.  
  Source: https://cse.msu.edu/~caozc/papers/mobisys24-ren.pdf  
  Mirror: `mirrors/chirptransformer-mobisys-2024.pdf`  
  Relevance: LoRa-style chirp communication reference for dechirp/FFT symbol detection and chirp-symbol manipulation.

- D. Vasisht et al., "Choir: Decoding Multiple Transmitters Simultaneously," SIGCOMM 2017.  
  Source: https://users.ece.cmu.edu/~oyagan/Conferences/SIGCOMM2017.pdf  
  Mirror: `mirrors/choir-sigcomm-2017.pdf`  
  Relevance: Demonstrates chirp collision/separation ideas in the LoRa family; useful for future multi-emitter or interference-aware timing beacons.

## Chirplet Transform / Matching Pursuit

- J. Cui and W. Wong, "The Adaptive Chirplet Transform and Visual Evoked Potentials," 2017.  
  Source: https://arxiv.org/pdf/1709.08328  
  Mirror: `mirrors/mpact-biosignal-adaptive-chirplet-transform-1709.08328.pdf`  
  Relevance: Shows the dictionary/matching-pursuit family of chirplet analysis. Useful as a warning: it is expressive, but analysis-oriented and heavier than a controlled sync beacon should be.

- J. Cui, "MPACT: Matching Pursuit Adaptive Chirplet Transform."  
  Source: https://github.com/jiecui/mpact  
  Mirrors: `mirrors/mpact-github.html`, `mirrors/mpact-readme.md`  
  Relevance: Code reference for adaptive chirplet matching-pursuit structure; not a realtime sync decoder architecture.

- L. Mann and S. Haykin, "The Chirplet Transform: Physical Considerations," overviewed by later sources.  
  Source: https://en.wikipedia.org/wiki/Chirplet_transform  
  Mirror: `mirrors/chirplet-transform-wikipedia.html`  
  Relevance: Defines the canonical chirplet transform as projection onto chirplet atoms. This legitimizes matched filtering, but does not make dense sliding dictionary search the right machine for Mimir.

## Fast Chirplet / Detection Notes

- J. Hu et al., "Fast Chirplet Transform," arXiv:1611.08749.  
  Source: https://arxiv.org/pdf/1611.08749  
  Mirror: `mirrors/fast-chirplet-transform-1611.08749.pdf`  
  Relevance: Fast chirplet-transform framing; useful for avoiding naive dense scans when a more general chirplet transform is needed.

- "Edinburgh Maximum Chirplet Transform Code Notes."  
  Source: https://udrc.eng.ed.ac.uk/sites/udrc.eng.ed.ac.uk/files/attachments/Edinburgh%20Maximum%20Chirplet%20Transform%20Code%20Notes.pdf  
  Mirror: `mirrors/edinburgh-maximum-chirplet-transform-code-notes.pdf`  
  Relevance: Practical detection notes around precomputed chirplet windows and CFAR-style detection. Useful for detector architecture, but still broader than Mimir's controlled-code sync problem.
