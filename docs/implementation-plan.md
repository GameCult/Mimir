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
- Receiver OBS scene has `Neighbor PC - Video` and `Neighbor PC - Focusrite` Media Sources.

## Temporary

- Audio and video are separate SRT endpoints. This preserves OBS mixing authority but may need latency tuning.
- Audio defaults to AAC inside MPEG-TS for compatibility; test Opus later only if there is a concrete reason.
- Desktop capture uses `gdigrab` first because it is broadly available. `ddagrab` is a candidate once the installed FFmpeg build is confirmed.
- The scripts assume Windows sender and OBS receiver on the same LAN.

## Next

1. Open OBS and confirm the `Neighbor PC - Video` and `Neighbor PC - Focusrite` sources load.
2. Start the sender from Madman's desktop.
3. Smoke-test the video endpoint in OBS.
4. Smoke-test the Focusrite endpoint in OBS.
5. Choose and validate a real system-output loopback device if desktop/game audio is required separately.
6. Tune SRT latency and FFmpeg buffering for the local network.
7. Decide whether a small OBS scene/source generator is worth adding.
8. Reconsider plugin/fork only if standard OBS Media Source cannot preserve the required behavior.
