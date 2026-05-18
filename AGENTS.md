# AGENTS.md

## Purpose

LocalCastBridge is the local LAN streaming rig for making another Windows PC available inside OBS as video plus separately mixable audio sources, with video encoded by NVENC on the sender.

Keep this repo blunt. The goal is not to fork OBS for sport. Use existing OBS and FFmpeg surfaces until a real invariant is impossible without native code.

## Persistence Scaffolding

- Treat `state/map.yaml` as the canonical current map for this repo.
- Treat `state/scratch.md` as disposable scratch for one bounded subgoal.
- Treat `state/evidence.jsonl` as the distilled durable ledger of decisions, verifications, rejected paths, and scars that change future belief.
- Treat `state/branches.json` as the lightweight branch/hypothesis ledger.
- Treat `notes/fresh-workspace-handoff.md` as the short re-entry packet.
- Treat `notes/current-system-map.md` as the current source-grounded architecture and data-flow map.
- Treat `docs/implementation-plan.md` as the forward plan for what is implemented, temporary, and next.
- Update `state/map.yaml` when project understanding changes. Add evidence only when the lesson changes what the next agent should believe.
- Keep exact branch and HEAD snapshots out of handoff prose. Git commands own volatile truth.

## Persistence Commands

Use a real Python if the Windows Store alias gets cute. In this Codex workstation, the bundled runtime is:
`C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe`.

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\localcast_state.py status
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\localcast_state.py add-evidence --type decision --status ok --note "..."
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\localcast_prepare_compaction.py
```

## Session Bootstrap And Re-entry Protocol

On a fresh session, do this before editing:

1. read:
   - `state/map.yaml`
   - `notes/fresh-workspace-handoff.md`
   - `notes/current-system-map.md`
   - `docs/implementation-plan.md`
2. run:
   - `& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\localcast_state.py status`
3. restate the current mechanism, intended change, and next bounded move before broad edits

After compaction, resume, or suspicious continuity loss:

1. rerun `& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\localcast_state.py status`
2. reread `state/map.yaml` and `notes/fresh-workspace-handoff.md`
3. treat the persisted next action as orientation, not permission to wander

When the user says to prepare for imminent compaction:

1. run `tools/localcast_prepare_compaction.py` before editing persistence surfaces
2. use its warnings as the checklist for map, handoff, scratch, evidence, and git hygiene
3. update only the state that actually changed
4. run `tools/localcast_prepare_compaction.py` again after edits
5. commit the completed persistence pass unless the work is deliberately mid-surgery

## Operating Discipline

- Before substantial edits, restate the current mechanism and intended change.
- Prefer one clear hypothesis per iteration.
- Verify against OBS ingest behavior and actual sender-side FFmpeg capability, not vibes wearing a lab coat.
- Do not add an OBS plugin, fork, registry, custom transport daemon, or compatibility layer until the script-based SRT design fails a concrete invariant.
- Keep video, audio-source isolation, transport, and OBS composition as separate responsibilities.
- If the diff grows while understanding shrinks, stop implementation and update the map.

## Product Truths

- V1 is a LAN bridge, not an internet relay.
- Sender-side video encoding must use NVENC when available.
- Receiver-side OBS should see stable source URLs that can be added as Media Sources.
- Audio must remain separately mixable in OBS. That means separate audio endpoints in v1, even if a later synchronized bundle becomes useful.
- The repo owns repeatable setup, scripts, config, and operating docs. FFmpeg and OBS own media work.
