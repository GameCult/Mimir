# AGENTS.md

## Purpose

Mimir is the public face of this repo: the local realtime field machine for
turning cameras, microphones, speakers, and timing signals into one coherent
OBS-facing volumetric program surface.

Some native ABI names still use `localcast` until a deliberate rename cut exists.

Keep this repo blunt. The goal is not to fork OBS for sport. Use existing OBS and FFmpeg surfaces until a real invariant is impossible without native code.

For the persistent Mimir Face agent identity, voice, jurisdiction, avatar, and
VoidBot heartbeat contract, read `docs/mimir-face.md`.

## Persistence Scaffolding

- Treat `state/map.yaml` as the canonical current map for this repo.
- Treat `state/scratch.md` as disposable scratch for one bounded subgoal.
- Treat `state/evidence.jsonl` as the distilled durable ledger of decisions, verifications, rejected paths, and scars that change future belief.
- Treat `state/branches.json` as the lightweight branch/hypothesis ledger.
- Treat `notes/fresh-workspace-handoff.md` as the short re-entry packet.
- Treat `notes/current-system-map.md` as the current source-grounded architecture and data-flow map.
- Treat `docs/implementation-plan.md` as the forward plan for what is implemented, temporary, and next.
- Treat `docs/mimir-face.md` as the persistent Face memory for the Mimir agent.
- Update `state/map.yaml` when project understanding changes. Add evidence only when the lesson changes what the next agent should believe.
- Keep exact branch and HEAD snapshots out of handoff prose. Git commands own volatile truth.
- Commit `.voidbot` state whenever it is dirty, even if you did not create the
  changes. Put it in a separate state commit so Face memory and status do not
  stay stranded as local-only residue.

## Session Bootstrap And Re-entry Protocol

On a fresh session, do this before editing:

1. read:
   - `state/map.yaml`
   - `notes/fresh-workspace-handoff.md`
   - `notes/current-system-map.md`
   - `docs/implementation-plan.md`
2. run:
   - `git status --short --branch`
   - `git log --oneline -5`
   - `Get-Content .\state\evidence.jsonl -Tail 8`
3. restate the current mechanism, intended change, and next bounded move before broad edits

After compaction, resume, or suspicious continuity loss:

1. rerun `git status --short --branch`
2. reread `state/map.yaml` and `notes/fresh-workspace-handoff.md`
3. treat the persisted next action as orientation, not permission to wander

When the user says to prepare for imminent compaction:

1. immediately write the hot context into a collision-proof scratch file under `state/`
2. use `state/map.yaml`, `notes/fresh-workspace-handoff.md`, `notes/current-system-map.md`, `state/scratch.md`, `state/evidence.jsonl`, and git status as the checklist for map, handoff, scratch, evidence, and git hygiene
3. update only the state that actually changed
4. commit the completed persistence pass unless the work is deliberately mid-surgery

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
