# Fresh Workspace Handoff

This is the short re-entry packet for `E:\Projects\LocalCastBridge`.

Do not trust this file for the exact live HEAD. Exact branch, HEAD, and dirty state are volatile. Always ask git.

## Rehydrate

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\localcast_state.py status
Get-Content .\state\map.yaml
Get-Content .\notes\fresh-workspace-handoff.md
Get-Content .\notes\current-system-map.md
Get-Content .\docs\implementation-plan.md
git status --short --branch
git log --oneline -5
Get-Content .\state\evidence.jsonl -Tail 8
```

## Current Shape

- V1 uses FFmpeg plus OBS Media Source over SRT.
- Sender-side video encoding is `h264_nvenc`.
- Audio sources are separate SRT endpoints so OBS can mix them separately.
- The repo contains scripts and docs, not a native OBS plugin.
- Visual fusion now has a deadline Spout sender: `scripts/stream_spout.py` consumes `RenderFramePacket` JSON, renders points into an OpenGL FBO, and publishes the Spout sender `LocalCastBridge Point Cloud` for OBS.
- The detached sender status file is `calibration/runs/stream-spout-status.json`; logs are `calibration/runs/stream-spout.log` and `calibration/runs/stream-spout.err.log`.
- Aquarium remains the intended dense splat/brush renderer behind the same `RenderFramePacket` boundary. Do not let the OpenGL sink become scene authority.
- Neighbor sender is deployed at `C:\Meta\LocalCastBridge` on `192.168.1.84`.
- Madman's desktop has `Start LocalCast Sender.cmd` and `Stop LocalCast Sender.cmd`.
- Receiver OBS scene collection has `Neighbor PC - Video`, `Neighbor PC - Focusrite`, and `Neighbor PC - System Audio` Media Sources added.
- Sender uses SoundVolumeView for default device selection. Voicemeeter is no longer the loopback authority; co-streamer playback loopback uses the direct WASAPI shim scheduled into the neighbor console session.
- Sender script was patched after failed transmission: live FFmpeg commands now include `-nostdin`, logs go under `C:\Meta\LocalCastBridge\logs`, and interactive video capture size is `1920x1080`.
- Madman's desktop launchers call PowerShell wrappers: `scripts\start-localcast-desktop.ps1` and `scripts\stop-localcast-desktop.ps1`.
- SSH-launched video capture fails with `gdigrab` error 5; test video from Madman's interactive desktop launcher.

## Current Pressure

The useful next work is real hardware validation:

- in OBS, add/select a Spout2 Capture source named `LocalCastBridge Point Cloud`
- replace demo packets by writing live fusion packets to `calibration/runs/live-render-frame.json`
- move the packet consumer into Aquarium Engine once the stream survives deadline pressure
- open OBS and smoke-test the three configured receiver sources from Madman's desktop launcher
- tune latency once streams are live in OBS
- add a real loopback/virtual audio capture source if system-output audio is required

## Immediate Re-entry Instruction

Do not continue implementation automatically from a rehydrate-only request. Rehydrate, then follow the user's next instruction.
