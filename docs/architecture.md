# Architecture

## Objective

Make one local Windows computer available as OBS sources on another computer, with sender-side NVENC video encoding and independently mixable audio sources.

## Current Mechanism

The repo owns configuration and scripts. FFmpeg owns capture, encoding, and SRT transport. OBS owns scene composition.

Inputs become outputs like this:

1. `config/localcast.json` declares the receiver IP, SRT ports, video capture shape, and named audio devices.
2. `scripts/sender-discover.ps1` asks FFmpeg what Windows capture devices and encoders exist.
3. `scripts/sender-start.ps1` validates the config and launches one FFmpeg process per source.
4. The video process captures the desktop and encodes H.264 using `h264_nvenc`.
5. Each audio process captures one DirectShow audio device and encodes Opus.
6. Each process sends MPEG-TS over SRT to a stable receiver URL.
7. OBS adds each URL as a Media Source.

## Invariants

- Video encoding happens on the sender.
- OBS receives ordinary media-source URLs; it does not need a plugin for v1.
- Audio remains separately mixable in OBS.
- Ports are stable and derived from one base port plus explicit offsets.
- Secrets, if used, stay in local config and are not committed.

## Intended Change

The first implementation creates the repeatable operating spine. Future native code is only justified if the SRT Media Source path fails one of the invariants above.

## Cut Line

The following do not currently earn their keep:

- OBS fork
- OBS plugin
- custom receiver daemon
- synchronized multi-track container that hides audio sources from OBS mixing
- homegrown capture stack over Windows APIs

## Source Notes

- OBS SRT guide: `https://obsproject.com/kb/srt-protocol-streaming-guide`
- FFmpeg devices docs: `https://www.ffmpeg.org/ffmpeg-devices.html`
- FFmpeg protocols docs: `https://ffmpeg.org/ffmpeg-protocols.html`

