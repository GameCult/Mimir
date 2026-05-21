# OBS V1 Smoke Test

The OBS bridge is allowed to remain only while it leaves evidence.

## Witness Ledger

`calibration/runs/obs-v1-smoke-ledger.json` records bridge evidence:

- sender capture timestamp;
- receiver transport timestamp;
- OBS presentation timestamp;
- endpoint drift;
- end-to-end latency;
- confidence and operator notes.

## Pass Condition

Every planned endpoint needs matching evidence at all three stages before the
receiver path grows new machinery. A visible source in OBS proves pixels arrived.
It does not prove the program is synchronized.
