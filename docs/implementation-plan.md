# Implementation Plan

## Implemented

- Repo-local persistence machinery.
- First architecture map.
- Example config for one video source plus two audio sources.
- Sender device discovery script.
- Sender launch script with dry-run mode and per-source FFmpeg commands.
- OBS receiver setup notes.
- Neighbor sender deployment under `C:\Meta\LocalCastBridge`.
- Madman's desktop start/stop launchers for the sender.

## Temporary

- Audio and video are separate SRT endpoints. This preserves OBS mixing authority but may need latency tuning.
- Audio defaults to AAC inside MPEG-TS for compatibility; test Opus later only if there is a concrete reason.
- Desktop capture uses `gdigrab` first because it is broadly available. `ddagrab` is a candidate once the installed FFmpeg build is confirmed.
- The scripts assume Windows sender and OBS receiver on the same LAN.

## Next

1. Add OBS Media Sources on the receiver for ports `5100` and `5101`.
2. Smoke-test the video endpoint in OBS.
3. Smoke-test the Focusrite endpoint in OBS.
4. Choose and validate a real system-output loopback device if desktop/game audio is required separately.
5. Tune SRT latency and FFmpeg buffering for the local network.
6. Decide whether a small OBS scene/source generator is worth adding.
7. Reconsider plugin/fork only if standard OBS Media Source cannot preserve the required behavior.
