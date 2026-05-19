"""Audio-field profile helpers with no runtime device dependencies."""

from localcast.sensor_fusion import phase_source_id_for_microphone


HOST_SOURCE_BY_MIC_ID = {
    "mic_focusrite_local": "host-focusrite",
    "mic_focusrite_neighbor": "co-streamer-focusrite",
}


def source_id_for_microphone(mic: dict) -> str | None:
    return str(
        mic.get("phaseSourceId")
        or mic.get("sourceId")
        or HOST_SOURCE_BY_MIC_ID.get(str(mic.get("id", "")))
        or phase_source_id_for_microphone(mic)
        or ""
    )


def local_microphones(profile: dict, machine: str) -> list[dict]:
    return [mic for mic in profile.get("microphones", []) if mic.get("machine") == machine and source_id_for_microphone(mic)]
