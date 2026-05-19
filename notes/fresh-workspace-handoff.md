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
- The live synchronized program path is being rebuilt. The Python/OpenGL
  deadline Spout bridge has been deleted.
- `docs/native-rebuild-plan.md` is the current cut plan.
- The reservoir is now one native time-ordered rolling buffer with typed
  indexes/views. The previous typed-ring crate is gone.
- Native reservoir samples carry provenance flags; diagnostic/fallback samples
  are rejected at both the raw reservoir push and `LocalcastRuntime` typed
  producer boundary.
- `LocalcastRuntime` now exposes producer pushes and consumer reads.
  `LocalcastProducer` owns source identity and sequence assignment for native
  capture workers.
- `LocalcastAudioBlockDescriptor` is the first typed audio payload descriptor:
  caller-owned float32 interleaved audio blocks cross the ABI by descriptor
  pointer, while the reservoir still owns only timing/retention metadata.
- `LocalcastRenderPacketDescriptor` now gives render packets the same typed
  descriptor boundary for caller-owned point buffers and presentation timing.
- `LocalcastRenderPoint` is the fixed first point-buffer ABI for Aquarium
  decoding.
- Aquarium-Engine commit `4d5aec7` adds the first safe-code C# binding layer for
  this native ABI. It is not yet wired into `LocalCastRuntime` as the live
  source.
- Aquarium-Engine commit `8798908` makes `LocalCastRuntime` consume an injected
  `ILocalCastVisualFrameSource`; the remaining Aquarium cut is a native source
  implementation over the binding.
- Aquarium-Engine commit `687845c` adds that native frame source. It still needs
  the real payload decoder/runtime creation path before file polling can be
  removed from the default runtime.
- Aquarium-Engine commit `34694e6` adds the matching safe-code C# binding and
  layout tests for `LocalcastAudioBlockDescriptor` and the native audio-block
  producer helper.
- Aquarium-Engine commit `9eb40cf` adds the matching safe-code C# binding and
  layout tests for `LocalcastRenderPacketDescriptor` and the native
  render-packet producer helper.
- Aquarium-Engine commit `cfb48d7` adds `LocalCastNativeRenderDescriptorDecoder`,
  which reads render descriptor timing/target metadata from native payload
  handles while keeping point-buffer decoding injectable.
- Edge JSON is schema/diagnostic only. Runtime network/process data should remain typed CultNet documents.
- Python live producers and diagnostic CultCache/JSON file adapters are
  diagnostics, not production foundations. The OpenGL Spout sink has been
  deleted. Aquarium owns production Spout publication.
- Python audio phase/live/Faust publishers have been moved under
  `localcast.diagnostics.*`, their live PowerShell launchers have been deleted,
  and reusable mic-profile/probe-band helpers now live in `audio_field`.
  Production audio ingest must use native capture workers and
  `LocalcastProducer`.
- The AmbiX spatial-audio Python publisher is also quarantined as
  `localcast.diagnostics.spatial_audio`.
- Neighbor sender is deployed at `C:\Meta\LocalCastBridge` on `192.168.1.84`.
- Madman's desktop has `Start LocalCast Sender.cmd` and `Stop LocalCast Sender.cmd`.
- Receiver OBS scene collection has `Neighbor PC - Video`, `Neighbor PC - Focusrite`, and `Neighbor PC - System Audio` Media Sources added.
- Sender uses SoundVolumeView for default device selection. Voicemeeter is no longer the loopback authority; co-streamer playback loopback uses the direct WASAPI shim scheduled into the neighbor console session.
- Sender script was patched after failed transmission: live FFmpeg commands now include `-nostdin`, logs go under `C:\Meta\LocalCastBridge\logs`, and interactive video capture size is `1920x1080`.
- Madman's desktop launchers call PowerShell wrappers: `scripts\start-localcast-desktop.ps1` and `scripts\stop-localcast-desktop.ps1`.
- SSH-launched video capture fails with `gdigrab` error 5; test video from Madman's interactive desktop launcher.

## Current Pressure

The useful next work is foundation surgery, not hardware feature expansion:

- bind Aquarium/Faust/native workers to the rolling-buffer `LocalcastRuntime`
- build native mic/loopback/phase producers over `LocalcastProducer`; do not
  restore Python audio live launchers
- delete the remaining diagnostic `visual_producer` command once native/Aquarium
  producer paths cover it; core reservoir clipping, Leap packed transforms, RGB
  reference splats, and diagnostic clap calibration have already been split out
- bind Aquarium/Faust against the native runtime/producer ABI instead of
  reviving the deleted Python/OpenGL publisher
- route future visual/audio runtime work through Aquarium/Faust/native runtime plus typed CultNet docs
- keep FFmpeg/SRT bridge scripts only as simple LAN ingest and capture utilities

## Immediate Re-entry Instruction

Do not revive the Python/OpenGL bridge. Rehydrate, read
`docs/native-rebuild-plan.md`, then cut toward native workers,
Aquarium/Faust bindings, and typed CultNet boundaries.
