from __future__ import annotations

import json
from pathlib import Path

from localcast.sensor_fusion.stochastic_mapping import MultiLODSceneCache


def write_lod_cache_json(path: Path, cache: MultiLODSceneCache) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(cache.to_dict(), indent=2), encoding="utf-8")
