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
- The live synchronized program path is being rebuilt. The Python/OpenGL deadline Spout bridge is not a foundation.
- `docs/native-rebuild-plan.md` is the current cut plan.
- The reservoir target is one native time-ordered rolling buffer with typed indexes/views. The current typed-ring crate proved the expiry invariant but should be rewritten before new live features.
- Edge JSON is schema/diagnostic only. Runtime network/process data should remain typed CultNet documents.
- Python live producers and the OpenGL Spout sink should be deleted or quarantined as diagnostics before the next feature pass. Aquarium owns production Spout publication.
- Neighbor sender is deployed at `C:\Meta\LocalCastBridge` on `192.168.1.84`.
- Madman's desktop has `Start LocalCast Sender.cmd` and `Stop LocalCast Sender.cmd`.
- Receiver OBS scene collection has `Neighbor PC - Video`, `Neighbor PC - Focusrite`, and `Neighbor PC - System Audio` Media Sources added.
- Sender uses SoundVolumeView for default device selection. Voicemeeter is no longer the loopback authority; co-streamer playback loopback uses the direct WASAPI shim scheduled into the neighbor console session.
- Sender script was patched after failed transmission: live FFmpeg commands now include `-nostdin`, logs go under `C:\Meta\LocalCastBridge\logs`, and interactive video capture size is `1920x1080`.
- Madman's desktop launchers call PowerShell wrappers: `scripts\start-localcast-desktop.ps1` and `scripts\stop-localcast-desktop.ps1`.
- SSH-launched video capture fails with `gdigrab` error 5; test video from Madman's interactive desktop launcher.

## Current Pressure

The useful next work is foundation surgery, not hardware feature expansion:

- rewrite `native/reservoir` as one rolling buffer with typed indexes/views
- update ABI and tests around single-edge retention
- quarantine or delete production use of `scripts/diagnostic_live_sensor_fusion.py` and the OpenGL `spout_output` sink
- route future visual/audio runtime work through Aquarium/Faust/native runtime plus typed CultNet docs
- keep FFmpeg/SRT bridge scripts only as simple LAN ingest and capture utilities

## Immediate Re-entry Instruction

Do not continue the Python/OpenGL bridge. Rehydrate, read `docs/native-rebuild-plan.md`, then cut toward the native rolling reservoir.
