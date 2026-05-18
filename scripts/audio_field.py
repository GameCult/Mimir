import argparse
import importlib.util
import json
import math
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from tempfile import NamedTemporaryFile

import numpy as np
from scipy.io import wavfile
from scipy import signal


ROOT = Path(__file__).resolve().parents[1]
RUNS = ROOT / "calibration" / "runs"


def load_phase_fit_module():
    spec = importlib.util.spec_from_file_location("localcast_phase_fit", ROOT / "audio_field" / "phase_fit.py")
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


_phase_fit = load_phase_fit_module()
IterativeFrequencyPhaseMapper = _phase_fit.IterativeFrequencyPhaseMapper
SmoothPhaseField = _phase_fit.SmoothPhaseField
estimate_phase_delay = _phase_fit.estimate_phase_delay


def load_room_suppression_module():
    spec = importlib.util.spec_from_file_location("localcast_room_suppression", ROOT / "audio_field" / "room_suppression.py")
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


_room_suppression = load_room_suppression_module()
RoomSuppressionConfig = _room_suppression.RoomSuppressionConfig
suppress_room_field = _room_suppression.suppress_room_field


def load_program_reference_module():
    spec = importlib.util.spec_from_file_location("localcast_program_reference", ROOT / "audio_field" / "program_reference.py")
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


_program_reference = load_program_reference_module()
suppress_program_reference = _program_reference.suppress_program_reference


def utc_stamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")


def run_dir(kind: str) -> Path:
    for suffix in ["", *[f"-{i}" for i in range(1, 100)]]:
        path = RUNS / f"{utc_stamp()}-{kind}{suffix}"
        try:
            path.mkdir(parents=True, exist_ok=False)
            return path
        except FileExistsError:
            continue
    raise RuntimeError(f"Could not create unique audio run directory for {kind}")


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, data) -> None:
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def run_checked(command, *, label: str) -> subprocess.CompletedProcess:
    completed = subprocess.run(command, text=True, capture_output=True)
    if completed.returncode != 0:
        details = "\n".join(part for part in [completed.stdout, completed.stderr] if part)
        raise SystemExit(f"{label} failed with exit {completed.returncode}\n{details}")
    return completed


def load_profile(path: Path):
    profile = read_json(path)
    validate_profile_shape(profile)
    return profile


def validate_profile_shape(profile) -> None:
    errors = []
    if int(profile.get("sampleRate", 0)) <= 0:
        errors.append("sampleRate must be a positive integer")
    capture_mode = profile.get("captureMode", "shared-input-device")
    if capture_mode not in ("shared-input-device", "distributed-clocks"):
        errors.append("captureMode must be 'shared-input-device' or 'distributed-clocks'")
    if capture_mode == "distributed-clocks":
        clock_model = profile.get("clockModel", {})
        if clock_model.get("requiresAlignmentBeforeEncoding") is not True:
            errors.append("distributed-clocks profiles must set clockModel.requiresAlignmentBeforeEncoding=true")
        capture_policy = profile.get("capturePolicy", {})
        if int(capture_policy.get("fieldSampleRate", profile.get("sampleRate", 0))) != int(profile.get("sampleRate", 0)):
            errors.append("capturePolicy.fieldSampleRate must match sampleRate for the aligned field")
    bus = profile.get("ambisonicBus", {})
    if bus.get("order") != 1 or bus.get("channelOrder") != "ACN" or bus.get("normalization") != "SN3D":
        errors.append("ambisonicBus must declare first-order ACN/SN3D for this tool")
    if bus.get("channels") != ["W", "Y", "Z", "X"]:
        errors.append("ambisonicBus.channels must be ['W', 'Y', 'Z', 'X']")

    mics = profile.get("microphones", [])
    speakers = profile.get("speakers", [])
    if len(mics) != 6:
        errors.append("profile must declare exactly 6 microphones")
    if len(speakers) != 2:
        errors.append("profile must declare exactly 2 speakers")

    machine_ids = {machine.get("id") for machine in profile.get("machines", [])}
    if capture_mode == "distributed-clocks" and not machine_ids:
        errors.append("distributed-clocks profiles must declare machines")

    seen_mic_channels = set()
    seen_clock_domains = set()
    for mic in mics:
        channel = mic_channel(mic)
        if not isinstance(channel, int) or channel < 0:
            errors.append(f"microphone {mic.get('id', '<missing>')} has invalid fieldChannel/channel")
        if channel in seen_mic_channels:
            errors.append(f"duplicate microphone field channel {channel}")
        seen_mic_channels.add(channel)
        if capture_mode == "distributed-clocks":
            if mic.get("machine") not in machine_ids:
                errors.append(f"microphone {mic.get('id', '<missing>')} references unknown machine")
            if not isinstance(mic.get("device"), dict):
                errors.append(f"microphone {mic.get('id', '<missing>')} needs device query/hostApi/channel")
            clock_domain = mic.get("clockDomain")
            if not clock_domain:
                errors.append(f"microphone {mic.get('id', '<missing>')} needs clockDomain")
            if clock_domain in seen_clock_domains:
                errors.append(f"duplicate microphone clockDomain {clock_domain}")
            seen_clock_domains.add(clock_domain)
        if len(mic.get("positionMeters", [])) != 3:
            errors.append(f"microphone {mic.get('id', '<missing>')} needs positionMeters [x,y,z]")
        orientation = mic.get("orientationDeg", {})
        if "azimuth" not in orientation or "elevation" not in orientation:
            errors.append(f"microphone {mic.get('id', '<missing>')} needs orientationDeg azimuth/elevation")
        if mic.get("polarity", 1) not in (-1, 1):
            errors.append(f"microphone {mic.get('id', '<missing>')} polarity must be 1 or -1")
        if "qualityPriority" in mic and not isinstance(mic.get("qualityPriority"), int):
            errors.append(f"microphone {mic.get('id', '<missing>')} qualityPriority must be an integer")

    seen_speaker_channels = set()
    for speaker in speakers:
        channel = speaker.get("channel")
        if not isinstance(channel, int) or channel < 0:
            errors.append(f"speaker {speaker.get('id', '<missing>')} has invalid channel")
        if channel in seen_speaker_channels:
            errors.append(f"duplicate speaker channel {channel}")
        seen_speaker_channels.add(channel)
        if len(speaker.get("positionMeters", [])) != 3:
            errors.append(f"speaker {speaker.get('id', '<missing>')} needs positionMeters [x,y,z]")
        if speaker.get("polarity", 1) not in (-1, 1):
            errors.append(f"speaker {speaker.get('id', '<missing>')} polarity must be 1 or -1")

    input_channels = int(profile.get("inputDevice", {}).get("channels", 0))
    output_channels = int(profile.get("outputDevice", {}).get("channels", 0))
    if capture_mode == "shared-input-device" and input_channels < 6:
        errors.append("inputDevice.channels must be at least 6")
    if output_channels < 2:
        errors.append("outputDevice.channels must be at least 2")

    if errors:
        raise SystemExit("Invalid audio profile:\n- " + "\n- ".join(errors))


def mic_channel(mic) -> int:
    return int(mic.get("fieldChannel", mic.get("channel", -1)))


def sounddevice_module():
    try:
        import sounddevice as sd
    except Exception as exc:
        raise SystemExit(f"sounddevice is unavailable: {exc!r}") from exc
    return sd


def match_device(sd, spec, direction: str):
    hostapis = sd.query_hostapis()
    devices = sd.query_devices()
    query = (spec.get("query") or "").lower()
    hostapi_name = (spec.get("hostApi") or "").lower()
    needed_channels = int(spec.get("channels") or 0)
    matches = []
    for device in devices:
        hostapi = hostapis[device["hostapi"]]["name"]
        channel_key = "max_input_channels" if direction == "input" else "max_output_channels"
        if device[channel_key] < needed_channels:
            continue
        if query and query not in device["name"].lower():
            continue
        if hostapi_name and hostapi_name not in hostapi.lower():
            continue
        matches.append({**device, "hostapi_name": hostapi})
    if not matches:
        raise SystemExit(f"No {direction} device matches {spec!r}")
    return matches[0]


def find_devices(sd, spec, direction: str):
    hostapis = sd.query_hostapis()
    devices = sd.query_devices()
    query = (spec.get("query") or "").lower()
    hostapi_name = (spec.get("hostApi") or "").lower()
    needed_channels = int(spec.get("channels") or 1)
    matches = []
    for device in devices:
        hostapi = hostapis[device["hostapi"]]["name"]
        channel_key = "max_input_channels" if direction == "input" else "max_output_channels"
        if device[channel_key] < needed_channels:
            continue
        if query and query not in device["name"].lower():
            continue
        if hostapi_name and hostapi_name not in hostapi.lower():
            continue
        matches.append({**device, "hostapi_name": hostapi})
    return matches


def match_indexed_device(sd, spec, direction: str):
    matches = find_devices(sd, spec, direction)
    match_index = int(spec.get("matchIndex", 0))
    if match_index < 0 or match_index >= len(matches):
        raise SystemExit(f"No {direction} device match {match_index} for {spec!r}; found {len(matches)}")
    return matches[match_index]


def cmd_devices(args):
    sd = sounddevice_module()
    hostapis = sd.query_hostapis()
    rows = []
    for dev in sd.query_devices():
        rows.append(
            {
                "index": dev["index"],
                "name": dev["name"],
                "hostapi": hostapis[dev["hostapi"]]["name"],
                "inputs": dev["max_input_channels"],
                "outputs": dev["max_output_channels"],
                "default_samplerate": dev["default_samplerate"],
            }
        )
    print(json.dumps(rows, indent=2))


def cmd_probe_rates(args):
    sd = sounddevice_module()
    spec = {
        "query": args.device_query,
        "hostApi": args.hostapi,
        "channels": args.channels,
    }
    matches = find_devices(sd, spec, args.direction)
    results = []
    for device in matches:
        rate_results = []
        for rate in sorted(set(args.rate or [48000, 96000])):
            try:
                if args.direction == "input":
                    sd.check_input_settings(
                        device=device["index"],
                        channels=args.channels,
                        samplerate=rate,
                        dtype=args.dtype,
                    )
                else:
                    sd.check_output_settings(
                        device=device["index"],
                        channels=args.channels,
                        samplerate=rate,
                        dtype=args.dtype,
                    )
                rate_results.append({"sampleRate": rate, "ok": True})
            except Exception as exc:
                rate_results.append({"sampleRate": rate, "ok": False, "error": repr(exc)})
        results.append({"device": summarize_device(device), "rates": rate_results})
    print(json.dumps({"query": spec, "direction": args.direction, "dtype": args.dtype, "results": results}, indent=2))


def cmd_validate(args):
    profile = load_profile(args.profile)
    result = {
        "profile": str(args.profile),
        "sampleRate": profile["sampleRate"],
        "captureMode": profile.get("captureMode", "shared-input-device"),
        "capturePolicy": profile.get("capturePolicy"),
        "microphones": [mic["id"] for mic in profile["microphones"]],
        "speakers": [speaker["id"] for speaker in profile["speakers"]],
        "ambisonicBus": profile["ambisonicBus"],
        "deviceCheck": None,
    }
    if args.check_devices:
        sd = sounddevice_module()
        output_device = match_device(sd, profile["outputDevice"], "output")
        if profile.get("captureMode", "shared-input-device") == "shared-input-device":
            input_device = match_device(sd, profile["inputDevice"], "input")
            result["deviceCheck"] = {
                "input": summarize_device(input_device),
                "output": summarize_device(output_device),
            }
        else:
            result["deviceCheck"] = distributed_device_check(sd, profile, output_device)
    print(json.dumps(result, indent=2))


def distributed_device_check(sd, profile, output_device):
    local_machine = profile.get("localMachine", "local")
    checks = []
    for mic in profile["microphones"]:
        if mic.get("machine") != local_machine:
            checks.append(
                {
                    "micId": mic["id"],
                    "machine": mic.get("machine"),
                    "status": "remote-unchecked",
                    "device": mic.get("device"),
                }
            )
            continue
        matches = find_devices(sd, mic.get("device", {}), "input")
        checks.append(
            {
                "micId": mic["id"],
                "machine": mic.get("machine"),
                "clockDomain": mic.get("clockDomain"),
                "status": "ok" if matches else "missing",
                "matches": [summarize_device(match) for match in matches],
            }
        )
    return {
        "distributedInputs": checks,
        "output": summarize_device(output_device),
        "alignmentRequiredBeforeFoa": bool(profile.get("clockModel", {}).get("requiresAlignmentBeforeEncoding")),
    }


def summarize_device(device):
    return {
        "index": device["index"],
        "name": device["name"],
        "hostapi": device["hostapi_name"],
        "inputs": device["max_input_channels"],
        "outputs": device["max_output_channels"],
        "default_samplerate": device["default_samplerate"],
    }


def db_to_amp(db: float) -> float:
    return 10.0 ** (db / 20.0)


def make_sweep(profile):
    calibration = profile["calibration"]
    sample_rate = int(profile["sampleRate"])
    seconds = float(calibration.get("sweepSeconds", 3.0))
    t = np.arange(int(sample_rate * seconds), dtype=np.float64) / sample_rate
    sweep = signal.chirp(
        t,
        f0=float(calibration.get("sweepStartHz", 80.0)),
        f1=float(calibration.get("sweepEndHz", 18000.0)),
        t1=seconds,
        method="logarithmic",
    )
    fade_len = max(1, int(sample_rate * 0.02))
    fade = np.sin(np.linspace(0.0, math.pi / 2.0, fade_len)) ** 2
    sweep[:fade_len] *= fade
    sweep[-fade_len:] *= fade[::-1]
    sweep *= db_to_amp(float(calibration.get("levelDbfs", -18.0)))
    return sweep.astype(np.float32)


def cmd_make_stimulus(args):
    profile = load_profile(args.profile)
    out = args.output or run_dir("audio-stimulus")
    out.mkdir(parents=True, exist_ok=True)
    sweep = make_sweep(profile)
    wavfile.write(out / "calibration-sweep.wav", int(profile["sampleRate"]), sweep)
    write_json(
        out / "manifest.json",
        {
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "profile": str(args.profile),
            "sampleRate": int(profile["sampleRate"]),
            "sweep": profile["calibration"],
            "file": "calibration-sweep.wav",
        },
    )
    print(out)


def cmd_init_run(args):
    profile = load_profile(args.profile)
    out = args.output or run_dir("audio-distributed")
    out.mkdir(parents=True, exist_ok=True)
    (out / "sources").mkdir(exist_ok=True)
    sweep = make_sweep(profile)
    wavfile.write(out / "calibration-sweep.wav", int(profile["sampleRate"]), sweep)
    sources = []
    for mic in sorted(profile["microphones"], key=mic_channel):
        sources.append(
            {
                "micId": mic["id"],
                "fieldChannel": mic_channel(mic),
                "machine": mic.get("machine"),
                "clockDomain": mic.get("clockDomain"),
                "expectedFile": f"sources/{mic['id']}.wav",
                "role": mic.get("role"),
            }
        )
    write_json(
        out / "manifest.json",
        {
            "kind": "distributed-audio-calibration",
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "profile": str(args.profile),
            "sampleRate": int(profile["sampleRate"]),
            "stimulus": "calibration-sweep.wav",
            "sources": sources,
            "notes": "Drop one mono WAV per mic at expectedFile, or use record-local-calibration for local mics.",
        },
    )
    print(out)


def cmd_record_local_calibration(args):
    profile = load_profile(args.profile)
    sd = sounddevice_module()
    out = args.output or run_dir("audio-local-calibration")
    out.mkdir(parents=True, exist_ok=True)
    (out / "sources").mkdir(exist_ok=True)

    input_rate = int(args.input_rate or profile["sampleRate"])
    seconds = float(args.seconds)
    frames = int(input_rate * seconds)
    local_mics = [mic for mic in profile["microphones"] if mic.get("machine") == args.machine]
    grouped = local_device_groups(sd, local_mics)
    captures = {
        key: np.zeros((frames, group["channels"]), dtype=np.float32)
        for key, group in grouped.items()
        if key != "_missing"
    }
    positions = {key: 0 for key in captures}

    streams = []

    def make_callback(key):
        def callback(indata, frame_count, time_info, status):
            pos = positions[key]
            end = min(pos + frame_count, frames)
            if end > pos:
                captures[key][pos:end, :] = indata[: end - pos, :]
            positions[key] = end

        return callback

    for key, group in grouped.items():
        if key == "_missing":
            continue
        streams.append(
            sd.InputStream(
                device=group["device"]["index"],
                channels=group["channels"],
                samplerate=input_rate,
                dtype="float32",
                callback=make_callback(key),
            )
        )

    stimulus_file = None
    loopback_file = None
    loopback_rate = None
    output_device = None
    output_rate = None
    if args.play_sweep:
        output_device = match_device(sd, profile["outputDevice"], "output")
        output_rate = int(args.output_rate or output_device["default_samplerate"] or profile["sampleRate"])
        sweep = make_sweep_at_rate(profile, output_rate)
        playback = np.zeros((len(sweep), int(profile["outputDevice"]["channels"])), dtype=np.float32)
        speaker_channel = int(args.speaker_channel)
        playback[:, speaker_channel] = sweep
        stimulus_file = "calibration-sweep-played.wav"
        wavfile.write(out / stimulus_file, output_rate, sweep)

    try:
        for stream in streams:
            stream.start()
        if args.record_loopback:
            loopback_rate = int(args.loopback_rate or input_rate)
            loopback = record_loopback(
                query=args.loopback_query,
                seconds=seconds,
                sample_rate=loopback_rate,
                channels=args.loopback_channels,
                play_data=playback if args.play_sweep else None,
                play_rate=output_rate,
                play_device=output_device["index"] if output_device else None,
            )
            loopback_file = "ground_truth_loopback.wav"
            wavfile.write(out / loopback_file, loopback_rate, loopback.astype(np.float32))
        elif args.play_sweep:
            sd.play(playback, samplerate=output_rate, device=output_device["index"], blocking=True)
            remaining = max(0.0, seconds - len(playback) / output_rate)
            time.sleep(remaining)
        else:
            time.sleep(seconds)
    finally:
        for stream in streams:
            try:
                stream.stop()
                stream.close()
            except Exception:
                pass

    sources = []
    for missing in grouped.get("_missing", []):
        sources.append(
            {
                "micId": missing["micId"],
                "fieldChannel": missing["fieldChannel"],
                "machine": args.machine,
                "status": "missing-device",
                "error": missing["error"],
            }
        )
    for mic in sorted(local_mics, key=mic_channel):
        if any(missing["micId"] == mic["id"] for missing in grouped.get("_missing", [])):
            continue
        group_key = device_group_key(grouped, mic)
        data = captures[group_key][:, int(mic.get("device", {}).get("channel", 0))]
        file_name = f"sources/{mic['id']}.wav"
        wavfile.write(out / file_name, input_rate, data.astype(np.float32))
        sources.append(
            {
                "micId": mic["id"],
                "fieldChannel": mic_channel(mic),
                "machine": mic.get("machine"),
                "clockDomain": mic.get("clockDomain"),
                "file": file_name,
                "sampleRate": input_rate,
            }
        )

    if stimulus_file is None:
        sweep = make_sweep(profile)
        stimulus_file = "calibration-sweep-reference.wav"
        wavfile.write(out / stimulus_file, int(profile["sampleRate"]), sweep)

    write_json(
        out / "manifest.json",
        {
            "kind": "distributed-audio-calibration",
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "profile": str(args.profile),
            "sampleRate": int(profile["sampleRate"]),
            "inputSampleRate": input_rate,
            "stimulus": stimulus_file,
            "sources": sources,
            "playback": {
                "played": bool(args.play_sweep),
                "speakerChannel": int(args.speaker_channel) if args.play_sweep else None,
                "outputDevice": summarize_device(output_device) if output_device else None,
                "outputSampleRate": output_rate,
            },
            "reference": {
                "kind": "wasapi-loopback",
                "file": loopback_file,
                "sampleRate": loopback_rate,
                "query": args.loopback_query if loopback_file else None,
            },
        },
    )
    print(out)


def cmd_record_probe_train(args):
    profile = load_profile(args.profile)
    sd = sounddevice_module()
    out = args.output or run_dir("audio-probe-train")
    out.mkdir(parents=True, exist_ok=True)
    (out / "sources").mkdir(exist_ok=True)

    input_rate = int(args.input_rate or profile["sampleRate"])
    output_device = match_device(sd, profile["outputDevice"], "output")
    output_rate = int(args.output_rate or output_device["default_samplerate"] or profile["sampleRate"])
    playback, chirp, events = make_probe_train(
        profile,
        output_rate,
        seconds=float(args.seconds),
        chirp_seconds=float(args.chirp_seconds),
        interval_seconds=float(args.interval_seconds),
        channels=int(profile["outputDevice"]["channels"]),
        start_padding_seconds=float(args.start_padding_seconds),
        chirps_per_second=int(args.chirps_per_second),
        bands=parse_probe_bands(args.probe_band),
        level_db_offset=float(args.probe_level_offset_db),
    )
    wavfile.write(out / "probe-train-played.wav", output_rate, playback.astype(np.float32))
    wavfile.write(out / "probe-chirplet.wav", output_rate, chirp.astype(np.float32))

    frames = int(input_rate * float(args.seconds))
    local_mics = [mic for mic in profile["microphones"] if mic.get("machine") == args.machine]
    grouped = local_device_groups(sd, local_mics)
    captures = {
        key: np.zeros((frames, group["channels"]), dtype=np.float32)
        for key, group in grouped.items()
        if key != "_missing"
    }
    positions = {key: 0 for key in captures}

    def make_callback(key):
        def callback(indata, frame_count, time_info, status):
            pos = positions[key]
            end = min(pos + frame_count, frames)
            if end > pos:
                captures[key][pos:end, :] = indata[: end - pos, :]
            positions[key] = end

        return callback

    streams = []
    for key, group in grouped.items():
        if key == "_missing":
            continue
        streams.append(
            sd.InputStream(
                device=group["device"]["index"],
                channels=group["channels"],
                samplerate=input_rate,
                dtype="float32",
                callback=make_callback(key),
            )
        )

    loopback_file = None
    try:
        for stream in streams:
            stream.start()
        loopback = record_loopback(
            query=args.loopback_query,
            seconds=float(args.seconds),
            sample_rate=int(args.loopback_rate or output_rate),
            channels=args.loopback_channels,
            play_data=playback,
            play_rate=output_rate,
            play_device=output_device["index"],
        )
        loopback_file = "ground_truth_loopback.wav"
        wavfile.write(out / loopback_file, int(args.loopback_rate or output_rate), loopback.astype(np.float32))
    finally:
        for stream in streams:
            try:
                stream.stop()
                stream.close()
            except Exception:
                pass

    sources = []
    for missing in grouped.get("_missing", []):
        sources.append(
            {
                "micId": missing["micId"],
                "fieldChannel": missing["fieldChannel"],
                "machine": args.machine,
                "status": "missing-device",
                "error": missing["error"],
            }
        )
    for mic in sorted(local_mics, key=mic_channel):
        if any(missing["micId"] == mic["id"] for missing in grouped.get("_missing", [])):
            continue
        group_key = device_group_key(grouped, mic)
        data = captures[group_key][:, int(mic.get("device", {}).get("channel", 0))]
        file_name = f"sources/{mic['id']}.wav"
        wavfile.write(out / file_name, input_rate, data.astype(np.float32))
        sources.append(
            {
                "micId": mic["id"],
                "fieldChannel": mic_channel(mic),
                "machine": mic.get("machine"),
                "clockDomain": mic.get("clockDomain"),
                "file": file_name,
                "sampleRate": input_rate,
            }
        )

    write_json(
        out / "manifest.json",
        {
            "kind": "distributed-audio-probe-train",
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "profile": str(args.profile),
            "sampleRate": int(profile["sampleRate"]),
            "inputSampleRate": input_rate,
            "stimulus": "probe-chirplet.wav",
            "playback": {
                "played": True,
                "file": "probe-train-played.wav",
                "outputDevice": summarize_device(output_device),
                "outputSampleRate": output_rate,
                "seconds": float(args.seconds),
            },
            "reference": {
                "kind": "wasapi-loopback",
                "file": loopback_file,
                "sampleRate": int(args.loopback_rate or output_rate),
                "query": args.loopback_query,
            },
            "probeTrain": {
                "chirpSeconds": float(args.chirp_seconds),
                "intervalSeconds": float(args.interval_seconds),
                "startPaddingSeconds": float(args.start_padding_seconds),
                "events": events,
            },
            "sources": sources,
        },
    )
    print(out)


def find_profile_mic(profile, mic_id: str):
    for mic in profile["microphones"]:
        if mic["id"] == mic_id:
            return mic
    raise SystemExit(f"No microphone {mic_id!r} exists in profile")


def upsert_manifest_source(run: Path, source: dict) -> None:
    manifest_path = run / "manifest.json"
    manifest = read_json(manifest_path)
    sources = [item for item in manifest.get("sources", []) if item.get("micId") != source["micId"]]
    sources.append(source)
    sources.sort(key=lambda item: int(item.get("fieldChannel", 999)))
    manifest["sources"] = sources
    write_json(manifest_path, manifest)


def sftp_get(ssh_target: str, remote_file: str, local_file: Path) -> None:
    remote_sftp_path = "/C:/" + remote_file.replace("\\", "/").split("C:/", 1)[-1]
    batch = f'get "{remote_sftp_path}" "{local_file}"\n'
    with NamedTemporaryFile("w", encoding="ascii", delete=False, suffix=".sftp") as fh:
        fh.write(batch)
        batch_path = fh.name
    try:
        run_checked(["sftp", "-b", batch_path, ssh_target], label="SFTP pull")
    finally:
        Path(batch_path).unlink(missing_ok=True)


def cmd_record_remote_focusrite(args):
    profile = load_profile(args.profile)
    run = args.run
    if not (run / "manifest.json").exists():
        raise SystemExit(f"Run manifest does not exist: {run / 'manifest.json'}")
    mic = find_profile_mic(profile, args.mic_id)
    device_name = args.device or args.dshow_device or "Analogue 1 + 2 (Focusrite USB Audio)"
    sample_rate = int(args.sample_rate or profile.get("capturePolicy", {}).get("focusriteCalibrationSampleRate") or profile["sampleRate"])
    seconds = float(args.seconds)
    remote_dir = args.remote_dir.rstrip("\\/")
    remote_file = f"{remote_dir}\\{args.mic_id}-{utc_stamp().replace(':', '').replace('.', '')}.wav"
    local_file = run / "sources" / f"{args.mic_id}.wav"
    local_file.parent.mkdir(parents=True, exist_ok=True)

    ffmpeg = args.ffmpeg
    remote_cmd = (
        f'cmd /c if not exist "{remote_dir}" mkdir "{remote_dir}" && '
        f'"{ffmpeg}" -y -hide_banner -nostdin -loglevel info '
        f'-f dshow -t {seconds:.3f} -i audio="{device_name}" '
        f'-vn -ac {int(args.channels)} -ar {sample_rate} -c:a pcm_f32le "{remote_file}"'
    )
    if args.dry_run:
        print(remote_cmd)
        return

    print(f"Recording {args.mic_id} on {args.ssh_target} for {seconds:.1f}s...")
    run_checked(["ssh", args.ssh_target, remote_cmd], label="Remote Focusrite capture")
    sftp_get(args.ssh_target, remote_file, local_file)
    if not local_file.exists() or local_file.stat().st_size == 0:
        raise SystemExit(f"Remote capture did not produce a usable local file: {local_file}")

    upsert_manifest_source(
        run,
        {
            "micId": mic["id"],
            "fieldChannel": mic_channel(mic),
            "machine": mic.get("machine"),
            "clockDomain": mic.get("clockDomain"),
            "file": f"sources/{mic['id']}.wav",
            "sampleRate": sample_rate,
            "capture": {
                "kind": "ssh-ffmpeg-dshow",
                "sshTarget": args.ssh_target,
                "remoteFile": remote_file,
                "device": device_name,
                "channels": int(args.channels),
            },
        },
    )
    print(local_file)


def record_loopback(query, seconds, sample_rate, channels=2, play_data=None, play_rate=None, play_device=None):
    try:
        import soundcard as sc
    except Exception as exc:
        raise SystemExit(f"soundcard loopback capture unavailable: {exc!r}") from exc
    matches = [
        mic
        for mic in sc.all_microphones(include_loopback=True)
        if "loopback" in repr(mic).lower() and query.lower() in mic.name.lower()
    ]
    if not matches:
        raise SystemExit(f"No loopback device matching {query!r}")
    loopback = matches[0]
    frame_count = int(seconds * sample_rate)
    if play_data is not None:
        sd = sounddevice_module()
        sd.play(play_data, samplerate=play_rate, device=play_device, blocking=False)
    with loopback.recorder(samplerate=sample_rate, channels=channels) as recorder:
        data = recorder.record(numframes=frame_count)
    if play_data is not None:
        try:
            sounddevice_module().stop()
        except Exception:
            pass
    return np.asarray(data, dtype=np.float32)


def make_sweep_at_rate(profile, sample_rate):
    clone = dict(profile)
    clone["sampleRate"] = int(sample_rate)
    return make_sweep(clone)


def make_probe_train(
    profile,
    sample_rate,
    *,
    seconds,
    chirp_seconds,
    interval_seconds,
    channels,
    start_padding_seconds=1.0,
    chirps_per_second=1,
    bands=None,
    level_db_offset=-18.0,
):
    chirp_profile = dict(profile)
    chirp_profile["sampleRate"] = int(sample_rate)
    chirp_profile["calibration"] = dict(profile["calibration"])
    chirp_profile["calibration"]["sweepSeconds"] = float(chirp_seconds)
    chirp = make_sweep(chirp_profile) * db_to_amp(float(level_db_offset))
    total_frames = int(float(seconds) * sample_rate)
    train = np.zeros((total_frames, int(channels)), dtype=np.float32)
    events = []
    rng = np.random.default_rng(1337)
    base_interval = 1.0 / max(1.0, float(chirps_per_second))
    effective_interval = min(float(interval_seconds), base_interval)
    time_s = float(start_padding_seconds)
    index = 0
    speaker_channels = [int(speaker["channel"]) for speaker in sorted(profile["speakers"], key=lambda item: int(item["channel"]))]
    if not speaker_channels:
        raise SystemExit("Probe train needs at least one speaker in the profile")
    while time_s + float(chirp_seconds) <= float(seconds):
        channel = speaker_channels[index % len(speaker_channels)]
        if bands:
            band = bands[index % len(bands)]
            chirp_profile["calibration"]["sweepStartHz"] = float(band[0])
            chirp_profile["calibration"]["sweepEndHz"] = float(band[1])
            chirp = make_sweep(chirp_profile) * db_to_amp(float(level_db_offset))
        jitter = 0.15 * effective_interval * float(rng.uniform(-1.0, 1.0))
        event_time = max(float(start_padding_seconds), time_s + jitter)
        start = int(round(time_s * sample_rate))
        start = int(round(event_time * sample_rate))
        end = min(total_frames, start + len(chirp))
        if 0 <= channel < int(channels) and end > start:
            train[start:end, channel] += chirp[: end - start]
        speaker = next((item for item in profile["speakers"] if int(item["channel"]) == channel), {})
        events.append(
            {
                "eventIndex": index,
                "speakerId": speaker.get("id", f"speaker_channel_{channel}"),
                "speakerChannel": channel,
                "scheduledStartSeconds": event_time,
                "scheduledStartSample": start,
                "chirpSeconds": float(chirp_seconds),
                "sweepStartHz": float(chirp_profile["calibration"].get("sweepStartHz")),
                "sweepEndHz": float(chirp_profile["calibration"].get("sweepEndHz")),
            }
        )
        index += 1
        time_s += effective_interval
    peak = float(np.max(np.abs(train))) if train.size else 0.0
    if peak > 0.95:
        train *= 0.95 / peak
    return train, chirp, events


def parse_probe_bands(values):
    if not values:
        return None
    bands = []
    for value in values:
        if ":" not in value:
            raise SystemExit(f"Probe band must be start:end Hz, got {value!r}")
        start, end = value.split(":", 1)
        bands.append((float(start), float(end)))
    return bands


def local_device_groups(sd, mics):
    groups = {"_missing": []}
    for mic in mics:
        try:
            device = match_indexed_device(sd, mic.get("device", {}), "input")
        except SystemExit as exc:
            groups["_missing"].append(
                {
                    "micId": mic["id"],
                    "fieldChannel": mic_channel(mic),
                    "error": str(exc),
                }
            )
            continue
        channel = int(mic.get("device", {}).get("channel", 0))
        key = str(device["index"])
        if key not in groups:
            groups[key] = {"device": device, "mics": [], "channels": channel + 1}
        groups[key]["mics"].append(mic)
        groups[key]["channels"] = max(groups[key]["channels"], channel + 1)
    return groups


def device_group_key(groups, mic):
    for key, group in groups.items():
        if key == "_missing":
            continue
        if any(item["id"] == mic["id"] for item in group["mics"]):
            return key
    raise KeyError(mic["id"])


def cmd_play_record(args):
    profile = load_profile(args.profile)
    if profile.get("captureMode", "shared-input-device") == "distributed-clocks":
        raise SystemExit(
            "play-record only works for shared-input-device profiles. "
            "For distributed clocks, play the shared calibration stimulus and record each mic source "
            "with its own capture process, then align the takes before FOA encoding."
        )
    sd = sounddevice_module()
    input_device = match_device(sd, profile["inputDevice"], "input")
    output_device = match_device(sd, profile["outputDevice"], "output")
    sample_rate = int(profile["sampleRate"])
    input_channels = int(profile["inputDevice"]["channels"])
    output_channels = int(profile["outputDevice"]["channels"])
    sweep = make_sweep(profile)
    tail = int(sample_rate * float(profile["calibration"].get("recordTailSeconds", 1.0)))
    silence = np.zeros(tail, dtype=np.float32)

    out = args.output or run_dir("audio-calibration")
    out.mkdir(parents=True, exist_ok=True)
    records = []
    for speaker in profile["speakers"]:
        playback = np.zeros((len(sweep) + tail, output_channels), dtype=np.float32)
        playback[: len(sweep), int(speaker["channel"])] = sweep
        recorded = sd.playrec(
            playback,
            samplerate=sample_rate,
            channels=input_channels,
            dtype="float32",
            device=(input_device["index"], output_device["index"]),
            blocking=True,
        )
        stem = f"{speaker['id']}-return.wav"
        wavfile.write(out / stem, sample_rate, recorded.astype(np.float32))
        records.append({"speakerId": speaker["id"], "file": stem})

    wavfile.write(out / "calibration-sweep.wav", sample_rate, sweep)
    write_json(
        out / "manifest.json",
        {
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "profile": str(args.profile),
            "inputDevice": summarize_device(input_device),
            "outputDevice": summarize_device(output_device),
            "records": records,
            "stimulus": "calibration-sweep.wav",
            "unusedSilenceSamples": int(len(silence)),
        },
    )
    print(out)


def cmd_analyze_calibration(args):
    profile = load_profile(args.profile)
    run = args.run
    manifest = read_json(run / "manifest.json")
    sample_rate, sweep = wavfile.read(run / manifest["stimulus"])
    sweep = as_float_matrix(sweep).reshape(-1)
    output = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "sourceRun": str(run),
        "sampleRate": int(sample_rate),
        "paths": [],
    }
    for record in manifest["records"]:
        rec_rate, recorded = wavfile.read(run / record["file"])
        if rec_rate != sample_rate:
            raise SystemExit(f"Sample-rate mismatch in {record['file']}: {rec_rate} != {sample_rate}")
        recorded = as_float_matrix(recorded)
        for mic in profile["microphones"]:
            channel = mic_channel(mic)
            if channel >= recorded.shape[1]:
                continue
            delay, peak, polarity = estimate_delay(recorded[:, channel], sweep)
            output["paths"].append(
                {
                    "speakerId": record["speakerId"],
                    "micId": mic["id"],
                    "delaySamples": int(delay),
                    "delayMs": 1000.0 * delay / sample_rate,
                    "peak": float(abs(peak)),
                    "polarity": int(polarity),
                }
            )
    write_json(run / "analysis.json", output)
    print(json.dumps(output, indent=2))


def cmd_analyze_distributed(args):
    profile = load_profile(args.profile)
    run = args.run
    manifest = read_json(run / "manifest.json")
    stim_rate, stimulus = wavfile.read(run / manifest["stimulus"])
    stimulus = as_float_matrix(stimulus).reshape(-1)
    results = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "sourceRun": str(run),
        "referenceMicId": args.reference_mic or profile.get("clockModel", {}).get("referenceMicId"),
        "sources": [],
    }
    detections = {}
    for source in manifest.get("sources", []):
        file_name = source.get("file") or source.get("expectedFile")
        if not file_name or not (run / file_name).exists():
            results["sources"].append({"micId": source.get("micId"), "status": "missing", "file": file_name})
            continue
        rec_rate, recorded = wavfile.read(run / file_name)
        recorded = as_float_matrix(recorded).reshape(-1)
        stim_for_rate = resample_if_needed(stimulus, int(stim_rate), int(rec_rate))
        delay, peak, polarity = estimate_delay(recorded, stim_for_rate)
        refined = None
        if args.chirplet_refine:
            refined = refine_chirplet_delay(
                recorded,
                stim_for_rate,
                delay,
                search_samples=args.search_samples,
                fractional_steps=args.fractional_steps,
                rate_ppm=args.rate_ppm,
            )
        rms = float(np.sqrt(np.mean(recorded * recorded))) if recorded.size else 0.0
        item = {
            "micId": source["micId"],
            "fieldChannel": source.get("fieldChannel"),
            "status": "ok",
            "file": file_name,
            "sampleRate": int(rec_rate),
            "sweepStartSample": int(delay),
            "sweepStartMs": 1000.0 * delay / rec_rate,
            "peak": float(abs(peak)),
            "polarity": int(polarity),
            "rms": rms,
        }
        if refined:
            item["chirpletDelaySamples"] = refined["delaySamples"]
            item["chirpletDelayMs"] = 1000.0 * refined["delaySamples"] / rec_rate
            item["chirpletScore"] = refined["score"]
            item["chirpletRateScale"] = refined["rateScale"]
        results["sources"].append(item)
        detections[source["micId"]] = item

    reference = detections.get(results["referenceMicId"])
    if reference:
        ref_delay = reference.get("chirpletDelaySamples", reference["sweepStartSample"])
        ref_time = ref_delay / reference["sampleRate"]
        for item in results["sources"]:
            if item.get("status") != "ok":
                continue
            item_delay = item.get("chirpletDelaySamples", item["sweepStartSample"])
            item_time = item_delay / item["sampleRate"]
            item["relativeDelaySeconds"] = item_time - ref_time
            item["relativeDelaySamplesAtFieldRate"] = int(round((item_time - ref_time) * int(profile["sampleRate"])))

    write_json(run / "distributed-analysis.json", results)
    print(json.dumps(results, indent=2))


def cmd_assemble_aligned(args):
    profile = load_profile(args.profile)
    run = args.run
    manifest = read_json(run / "manifest.json")
    analysis_path = args.analysis or run / "distributed-analysis.json"
    analysis = read_json(analysis_path) if analysis_path.exists() else {"sources": []}
    field_rate = int(profile["sampleRate"])
    seconds = args.seconds
    source_by_mic = {item.get("micId"): item for item in manifest.get("sources", [])}
    analysis_by_mic = {item.get("micId"): item for item in analysis.get("sources", []) if item.get("status") == "ok"}
    response_corrections = {}
    response_report = []
    if args.compensate_response:
        response_corrections, response_report = estimate_response_corrections(
            profile,
            run,
            manifest,
            analysis_by_mic,
            max_boost_db=float(args.max_response_boost_db),
            max_cut_db=float(args.max_response_cut_db),
            smoothing_bins=int(args.response_smoothing_bins),
        )

    channels = []
    report = []
    max_len = 0
    for mic in sorted(profile["microphones"], key=mic_channel):
        source = source_by_mic.get(mic["id"])
        if not source:
            channels.append(None)
            report.append({"micId": mic["id"], "status": "missing-source-entry"})
            continue
        file_name = source.get("file") or source.get("expectedFile")
        if not file_name or not (run / file_name).exists():
            channels.append(None)
            report.append({"micId": mic["id"], "status": "missing-file", "file": file_name})
            continue
        rec_rate, data = wavfile.read(run / file_name)
        mono = as_float_matrix(data).reshape(-1)
        mono = resample_if_needed(mono, int(rec_rate), field_rate)
        delay = int(mic.get("delaySamples", 0))
        if mic["id"] in analysis_by_mic:
            delay += int(analysis_by_mic[mic["id"]].get("relativeDelaySamplesAtFieldRate", 0))
        aligned = apply_integer_delay(mono, -delay)
        if mic["id"] in response_corrections:
            aligned = apply_frequency_response(aligned, response_corrections[mic["id"]])
        aligned *= db_to_amp(float(mic.get("gainDb", 0.0))) * int(mic.get("polarity", 1))
        channels.append(aligned)
        max_len = max(max_len, len(aligned))
        report.append(
            {
                "micId": mic["id"],
                "status": "ok",
                "file": file_name,
                "appliedDelaySamples": delay,
                "responseCompensated": mic["id"] in response_corrections,
            }
        )

    if seconds:
        frame_count = int(float(seconds) * field_rate)
    else:
        frame_count = max_len
    field = np.zeros((frame_count, len(channels)), dtype=np.float32)
    for index, channel in enumerate(channels):
        if channel is None:
            continue
        count = min(frame_count, len(channel))
        field[:count, index] = channel[:count]
    output = args.output or run / "field-aligned.wav"
    wavfile.write(output, field_rate, field)
    result = {"output": str(output), "sampleRate": field_rate, "shape": list(field.shape), "sources": report}
    if response_report:
        result["responseCompensation"] = response_report
        write_json(run / "response-compensation.json", {"sources": response_report})
    write_json(run / "aligned-field-manifest.json", result)
    print(json.dumps(result, indent=2))


def cmd_analyze_reference_sync(args):
    profile = load_profile(args.profile)
    run = args.run
    manifest = read_json(run / "manifest.json")
    reference_info = manifest.get("reference") or {}
    reference_file = args.reference or (run / reference_info.get("file", ""))
    if not reference_file.exists():
        raise SystemExit(f"Reference loopback WAV not found: {reference_file}")
    ref_rate, reference = wavfile.read(reference_file)
    reference = mono(as_float_matrix(reference))
    reference = resample_if_needed(reference, int(ref_rate), int(profile["sampleRate"]))
    field_rate = int(profile["sampleRate"])
    window = int(args.window_seconds * field_rate)
    hop = int(args.hop_seconds * field_rate)
    max_lag = int(args.max_lag_ms * field_rate / 1000.0)
    results = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "sourceRun": str(run),
        "reference": str(reference_file),
        "sampleRate": field_rate,
        "windowSeconds": args.window_seconds,
        "hopSeconds": args.hop_seconds,
        "sources": [],
    }
    for source in manifest.get("sources", []):
        file_name = source.get("file")
        if not file_name or not (run / file_name).exists():
            continue
        rec_rate, data = wavfile.read(run / file_name)
        mic = resample_if_needed(mono(as_float_matrix(data)), int(rec_rate), field_rate)
        delays = []
        scores = []
        for start in range(0, min(len(reference), len(mic)) - window + 1, hop):
            ref_win = reference[start : start + window]
            mic_win = mic[start : start + window]
            if float(np.std(ref_win)) < args.min_reference_std or float(np.std(mic_win)) < args.min_mic_std:
                continue
            if args.method == "gcc-phat":
                delay, score = gcc_phat_delay(mic_win, ref_win, max_lag=max_lag)
            else:
                delay, score = normalized_delay(mic_win, ref_win, max_lag=max_lag)
            if score < args.min_score or abs(delay) < int(args.min_abs_lag_ms * field_rate / 1000.0):
                continue
            delays.append(delay)
            scores.append(score)
        if not delays:
            continue
        delays = np.asarray(delays, dtype=np.float64)
        scores = np.asarray(scores, dtype=np.float64)
        median = float(np.median(delays))
        mad = float(np.median(np.abs(delays - median)))
        results["sources"].append(
            {
                "micId": source["micId"],
                "fieldChannel": source.get("fieldChannel"),
                "windows": int(len(delays)),
                "medianDelaySamples": median,
                "medianDelayMs": 1000.0 * median / field_rate,
                "madSamples": mad,
                "madMs": 1000.0 * mad / field_rate,
                "meanScore": float(np.mean(scores)),
                "maxScore": float(np.max(scores)),
                "method": args.method,
            }
        )
    write_json(run / "reference-sync-analysis.json", results)
    print(json.dumps(results, indent=2))


def cmd_analyze_probe_train(args):
    profile = load_profile(args.profile)
    run = args.run
    manifest = read_json(run / "manifest.json")
    train = manifest.get("probeTrain") or {}
    events = train.get("events") or []
    if not events:
        raise SystemExit("Probe-train manifest has no events")
    reference_info = manifest.get("reference") or {}
    reference_file = run / reference_info.get("file", "")
    if not reference_file.exists():
        raise SystemExit(f"Loopback reference missing: {reference_file}")

    field_rate = int(profile["sampleRate"])
    stim_rate, chirp = wavfile.read(run / manifest["stimulus"])
    chirp = resample_if_needed(mono(as_float_matrix(chirp)), int(stim_rate), field_rate)
    ref_rate, reference = wavfile.read(reference_file)
    reference = resample_if_needed(mono(as_float_matrix(reference)), int(ref_rate), field_rate)
    event_search = int(float(args.loopback_search_ms) * field_rate / 1000.0)
    mic_search = int(float(args.mic_search_ms) * field_rate / 1000.0)
    phase_frequencies = [float(value) for value in args.phase_frequency]

    loopback_events = []
    for event in events:
        event_chirp = make_event_chirp(profile, field_rate, event)
        scheduled = int(round(float(event["scheduledStartSeconds"]) * field_rate))
        detected, peak, polarity = detect_chirp_near(reference, event_chirp, scheduled, event_search)
        loopback_score = normalized_chirp_score(reference, event_chirp, detected)
        loopback_events.append(
            {
                **event,
                "loopbackStartSample": int(detected),
                "loopbackStartSeconds": detected / field_rate,
                "loopbackScore": loopback_score,
                "loopbackPeak": float(abs(peak)),
                "loopbackPolarity": int(polarity),
                "usable": bool(loopback_score >= float(args.min_loopback_score)),
            }
        )

    results = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "sourceRun": str(run),
        "sampleRate": field_rate,
        "speedOfSoundMetersPerSecond": float(args.speed_of_sound),
        "events": loopback_events,
        "sources": [],
        "notes": "Delay-to-distance values still include device latency unless solved against multiple speakers and clock offsets.",
    }
    for source in manifest.get("sources", []):
        file_name = source.get("file")
        if not file_name or not (run / file_name).exists():
            continue
        rec_rate, data = wavfile.read(run / file_name)
        mic = resample_if_needed(mono(as_float_matrix(data)), int(rec_rate), field_rate)
        observations = []
        phase_field = SmoothPhaseField(smoothing=float(args.phase_smoothing), max_step_samples=float(args.max_phase_step_samples))
        mapper = IterativeFrequencyPhaseMapper(
            phase_frequencies,
            field_rate,
            learning_rate=float(args.mapping_learning_rate),
            max_phase_step_radians=float(args.max_mapping_phase_step),
        )
        for event in loopback_events:
            if not event.get("usable"):
                continue
            event_chirp = make_event_chirp(profile, field_rate, event)
            detected, peak, polarity = detect_chirp_near(mic, event_chirp, int(event["loopbackStartSample"]), mic_search)
            delay = detected - int(event["loopbackStartSample"])
            score = normalized_chirp_score(mic, event_chirp, detected)
            if score < float(args.min_mic_score):
                continue
            loopback_segment = extract_window(reference, int(event["loopbackStartSample"]), len(event_chirp))
            mic_segment = extract_window(mic, detected, len(event_chirp))
            phase_estimate = estimate_phase_delay(
                loopback_segment,
                mic_segment,
                field_rate,
                phase_frequencies,
                max_abs_delay_ms=float(args.max_phase_delta_ms),
            )
            confidence = min(1.0, max(0.0, score))
            smooth_phase_delta = phase_field.update(source["micId"], phase_estimate.delay_samples, confidence)
            mapping_phase = mapper.update(source["micId"], phase_estimate, confidence)
            observations.append(
                {
                    "eventIndex": event["eventIndex"],
                    "speakerId": event["speakerId"],
                    "speakerChannel": event["speakerChannel"],
                    "startSample": int(detected),
                    "delaySamplesFromLoopback": int(delay),
                    "phaseDeltaSamples": phase_estimate.delay_samples,
                    "phaseDeltaMs": phase_estimate.delay_ms,
                    "smoothedPhaseDeltaSamples": smooth_phase_delta,
                    "phaseFitErrorRadians": phase_estimate.fit_error_radians,
                    "phaseRefinedDelaySamplesFromLoopback": float(delay) + phase_estimate.delay_samples,
                    "phaseBands": [
                        {
                            "frequencyHz": band.frequency_hz,
                            "phaseDeltaRadians": band.phase_delta_radians,
                            "coherence": band.coherence,
                        }
                        for band in phase_estimate.bands
                    ],
                    "delayMsFromLoopback": 1000.0 * delay / field_rate,
                    "distanceEquivalentMeters": float(args.speed_of_sound) * delay / field_rate,
                    "score": score,
                    "peak": float(abs(peak)),
                    "polarity": int(polarity),
                }
            )
        results["sources"].append(
            {
                "micId": source["micId"],
                "fieldChannel": source.get("fieldChannel"),
                "observations": observations,
                "summaryBySpeaker": summarize_probe_observations(observations, field_rate, float(args.speed_of_sound)),
                "learnedPhaseFrequencyMapping": [
                    {"frequencyHz": frequency, "phaseCorrectionRadians": float(phase)}
                    for frequency, phase in zip(phase_frequencies, mapper.correction_for(source["micId"]))
                ],
                "smoothedPhaseField": phase_field.state(),
            }
        )
    write_json(run / "probe-train-analysis.json", results)
    print(json.dumps(results, indent=2))


def detect_chirp_near(samples, chirp, center_sample, radius_samples):
    start = max(0, int(center_sample) - int(radius_samples))
    end = min(len(samples), int(center_sample) + int(radius_samples) + len(chirp))
    segment = samples[start:end]
    if len(segment) < max(8, len(chirp) // 4):
        return int(center_sample), 0.0, 1
    delay, peak, polarity = estimate_delay(segment, chirp)
    return start + delay, peak, polarity


def make_event_chirp(profile, sample_rate, event):
    clone = dict(profile)
    clone["sampleRate"] = int(sample_rate)
    clone["calibration"] = dict(profile["calibration"])
    clone["calibration"]["sweepSeconds"] = float(event.get("chirpSeconds", clone["calibration"].get("sweepSeconds", 0.03)))
    if "sweepStartHz" in event:
        clone["calibration"]["sweepStartHz"] = float(event["sweepStartHz"])
    if "sweepEndHz" in event:
        clone["calibration"]["sweepEndHz"] = float(event["sweepEndHz"])
    return make_sweep(clone)


def normalized_chirp_score(samples, chirp, start_sample):
    segment = extract_window(samples, int(start_sample), len(chirp))
    denom = float(np.linalg.norm(segment) * np.linalg.norm(chirp))
    if denom <= 1e-12:
        return 0.0
    return float(abs(np.dot(segment.astype(np.float32), chirp.astype(np.float32))) / denom)


def summarize_probe_observations(observations, sample_rate, speed_of_sound):
    by_speaker = {}
    for obs in observations:
        by_speaker.setdefault(obs["speakerId"], []).append(obs)
    summaries = []
    for speaker_id, rows in sorted(by_speaker.items()):
        delays = np.asarray([row["delaySamplesFromLoopback"] for row in rows], dtype=np.float64)
        refined = np.asarray([row.get("phaseRefinedDelaySamplesFromLoopback", row["delaySamplesFromLoopback"]) for row in rows], dtype=np.float64)
        scores = np.asarray([row["score"] for row in rows], dtype=np.float64)
        event_indexes = np.asarray([row["eventIndex"] for row in rows], dtype=np.float64)
        median = float(np.median(delays))
        refined_median = float(np.median(refined))
        mad = float(np.median(np.abs(delays - median)))
        refined_mad = float(np.median(np.abs(refined - refined_median)))
        slope = 0.0
        if len(delays) >= 2 and float(np.ptp(event_indexes)) > 0.0:
            slope = float(np.polyfit(event_indexes, refined, 1)[0])
        summaries.append(
            {
                "speakerId": speaker_id,
                "observations": int(len(rows)),
                "medianDelaySamples": median,
                "medianDelayMs": 1000.0 * median / sample_rate,
                "madSamples": mad,
                "madMs": 1000.0 * mad / sample_rate,
                "distanceEquivalentMeters": speed_of_sound * median / sample_rate,
                "phaseRefinedMedianDelaySamples": refined_median,
                "phaseRefinedMedianDelayMs": 1000.0 * refined_median / sample_rate,
                "phaseRefinedMadSamples": refined_mad,
                "phaseRefinedMadMs": 1000.0 * refined_mad / sample_rate,
                "phaseRefinedDistanceEquivalentMeters": speed_of_sound * refined_median / sample_rate,
                "meanScore": float(np.mean(scores)) if len(scores) else 0.0,
                "delaySlopeSamplesPerEvent": slope,
            }
        )
    return summaries


def mono(matrix):
    if matrix.ndim == 1 or matrix.shape[1] == 1:
        return matrix.reshape(-1).astype(np.float32)
    return np.mean(matrix, axis=1).astype(np.float32)


def gcc_phat_delay(signal_a, signal_b, max_lag):
    a = np.asarray(signal_a, dtype=np.float32)
    b = np.asarray(signal_b, dtype=np.float32)
    a = a - float(np.mean(a))
    b = b - float(np.mean(b))
    n = 1
    while n < len(a) + len(b):
        n *= 2
    A = np.fft.rfft(a, n=n)
    B = np.fft.rfft(b, n=n)
    R = A * np.conj(B)
    denom = np.abs(R)
    R = np.divide(R, denom, out=np.zeros_like(R), where=denom > 1e-12)
    corr = np.fft.irfft(R, n=n)
    corr = np.concatenate((corr[-max_lag:], corr[: max_lag + 1]))
    index = int(np.argmax(np.abs(corr)))
    lag = index - max_lag
    score = float(abs(corr[index]))
    return lag, score


def normalized_delay(signal_a, signal_b, max_lag):
    a = np.asarray(signal_a, dtype=np.float32)
    b = np.asarray(signal_b, dtype=np.float32)
    a = (a - float(np.mean(a))) / (float(np.std(a)) + 1e-9)
    b = (b - float(np.mean(b))) / (float(np.std(b)) + 1e-9)
    corr = signal.correlate(a, b, mode="full", method="fft")
    mid = len(b) - 1
    segment = corr[mid - max_lag : mid + max_lag + 1]
    index = int(np.argmax(np.abs(segment)))
    lag = index - max_lag
    score = float(abs(segment[index]) / max(1, len(a)))
    return lag, score


def estimate_response_corrections(profile, run, manifest, analysis_by_mic, *, max_boost_db, max_cut_db, smoothing_bins):
    field_rate = int(profile["sampleRate"])
    reference_id = profile.get("clockModel", {}).get("referenceMicId") or profile.get("calibration", {}).get("referenceMicId")
    source_by_mic = {item.get("micId"): item for item in manifest.get("sources", [])}
    stimulus_rate, stimulus = wavfile.read(run / manifest["stimulus"])
    stimulus = resample_if_needed(mono(as_float_matrix(stimulus)), int(stimulus_rate), field_rate)
    fft_size = int(2 ** math.ceil(math.log2(max(2048, len(stimulus)))))

    responses = {}
    for mic in sorted(profile["microphones"], key=mic_channel):
        mic_id = mic["id"]
        source = source_by_mic.get(mic_id)
        analysis = analysis_by_mic.get(mic_id)
        file_name = (source or {}).get("file") or (source or {}).get("expectedFile")
        if not source or not analysis or not file_name or not (run / file_name).exists():
            continue
        rec_rate, data = wavfile.read(run / file_name)
        data = resample_if_needed(mono(as_float_matrix(data)), int(rec_rate), field_rate)
        delay_seconds = float(analysis.get("chirpletDelaySamples", analysis["sweepStartSample"])) / float(analysis["sampleRate"])
        start = int(round(delay_seconds * field_rate))
        segment = extract_window(data, start, len(stimulus))
        if rms(segment) <= 1e-6:
            continue
        window = signal.windows.hann(len(stimulus), sym=False).astype(np.float32)
        spectrum = np.fft.rfft(segment * window, n=fft_size)
        responses[mic_id] = np.maximum(np.abs(spectrum), 1e-8).astype(np.float32)

    reference = responses.get(reference_id)
    if reference is None:
        return {}, [{"status": "disabled", "reason": f"reference response unavailable: {reference_id}"}]

    corrections = {}
    report = []
    for mic in sorted(profile["microphones"], key=mic_channel):
        mic_id = mic["id"]
        response = responses.get(mic_id)
        if response is None:
            report.append({"micId": mic_id, "status": "missing-calibration"})
            continue
        gain = reference / np.maximum(response, 1e-8)
        gain = normalize_gain_at_frequency(gain, field_rate, 1000.0)
        gain = smooth_log_gain(gain, smoothing_bins)
        gain = np.clip(gain, db_to_amp(-abs(max_cut_db)), db_to_amp(abs(max_boost_db))).astype(np.float32)
        corrections[mic_id] = gain
        report.append(
            {
                "micId": mic_id,
                "status": "ok",
                "fftBins": int(gain.size),
                "minGainDb": amp_to_db(float(np.min(gain))),
                "maxGainDb": amp_to_db(float(np.max(gain))),
            }
        )
    return corrections, report


def extract_window(data, start, length):
    out = np.zeros(length, dtype=np.float32)
    src_start = max(0, start)
    dst_start = max(0, -start)
    count = min(length - dst_start, len(data) - src_start)
    if count > 0:
        out[dst_start : dst_start + count] = data[src_start : src_start + count]
    return out


def rms(x):
    return float(np.sqrt(np.mean(np.asarray(x, dtype=np.float32) ** 2))) if len(x) else 0.0


def normalize_gain_at_frequency(gain, sample_rate, frequency):
    index = int(round(float(frequency) * (len(gain) - 1) * 2.0 / float(sample_rate)))
    index = max(1, min(len(gain) - 1, index))
    return gain / max(float(gain[index]), 1e-8)


def smooth_log_gain(gain, bins):
    if bins <= 1:
        return gain.astype(np.float32)
    bins = min(int(bins), len(gain))
    kernel = np.ones(bins, dtype=np.float32) / float(bins)
    log_gain = np.log(np.maximum(gain, 1e-8))
    return np.exp(np.convolve(log_gain, kernel, mode="same")).astype(np.float32)


def apply_frequency_response(samples, gain):
    fft_size = max(len(samples), (len(gain) - 1) * 2)
    spectrum = np.fft.rfft(samples.astype(np.float32), n=fft_size)
    if len(gain) != len(spectrum):
        x_old = np.linspace(0.0, 1.0, len(gain))
        x_new = np.linspace(0.0, 1.0, len(spectrum))
        gain = np.interp(x_new, x_old, gain).astype(np.float32)
    corrected = np.fft.irfft(spectrum * gain, n=fft_size)
    return corrected[: len(samples)].astype(np.float32)


def amp_to_db(value):
    return 20.0 * math.log10(max(float(value), 1e-12))


def as_float_matrix(data):
    arr = np.asarray(data)
    if arr.ndim == 1:
        arr = arr[:, None]
    if np.issubdtype(arr.dtype, np.integer):
        arr = arr.astype(np.float32) / np.iinfo(arr.dtype).max
    else:
        arr = arr.astype(np.float32)
    return arr


def resample_if_needed(samples, source_rate, target_rate):
    if int(source_rate) == int(target_rate):
        return samples.astype(np.float32)
    gcd = math.gcd(int(source_rate), int(target_rate))
    up = int(target_rate) // gcd
    down = int(source_rate) // gcd
    return signal.resample_poly(samples.astype(np.float32), up, down).astype(np.float32)


def estimate_delay(recorded, sweep):
    corr = signal.fftconvolve(recorded.astype(np.float32), sweep[::-1].astype(np.float32), mode="full")
    peak_index = int(np.argmax(np.abs(corr)))
    delay = peak_index - (len(sweep) - 1)
    peak = float(corr[peak_index])
    polarity = 1 if peak >= 0 else -1
    return delay, peak, polarity


def refine_chirplet_delay(recorded, sweep, coarse_delay, search_samples=3, fractional_steps=8, rate_ppm=150):
    recorded = np.asarray(recorded, dtype=np.float32).reshape(-1)
    sweep = np.asarray(sweep, dtype=np.float32).reshape(-1)
    if recorded.size == 0 or sweep.size == 0:
        return None

    rate_scales = [1.0]
    if rate_ppm:
        delta = float(rate_ppm) / 1_000_000.0
        rate_scales = [1.0 - delta, 1.0, 1.0 + delta]

    best = None
    start = int(round(coarse_delay)) - int(search_samples)
    stop = int(round(coarse_delay)) + int(search_samples)
    fractions = np.arange(max(1, int(fractional_steps)), dtype=np.float64) / max(1, int(fractional_steps))
    for rate_scale in rate_scales:
        atom = chirplet_atom(sweep, rate_scale)
        atom_norm = float(np.linalg.norm(atom))
        if atom_norm <= 0:
            continue
        for integer_delay in range(start, stop + 1):
            for frac in fractions:
                delay = float(integer_delay) + float(frac)
                segment = sample_fractional_window(recorded, delay, len(atom))
                segment_norm = float(np.linalg.norm(segment))
                if segment_norm <= 0:
                    continue
                score_signed = float(np.dot(segment, atom) / (segment_norm * atom_norm))
                score = abs(score_signed)
                if best is None or score > best["score"]:
                    best = {
                        "delaySamples": delay,
                        "score": score,
                        "signedScore": score_signed,
                        "rateScale": rate_scale,
                    }
    return best


def chirplet_atom(sweep, rate_scale):
    if rate_scale == 1.0:
        atom = sweep.astype(np.float32)
    else:
        x = np.arange(len(sweep), dtype=np.float64) * rate_scale
        atom = np.interp(x, np.arange(len(sweep), dtype=np.float64), sweep, left=0.0, right=0.0).astype(np.float32)
    atom = atom - float(np.mean(atom))
    norm = float(np.linalg.norm(atom))
    return atom / norm if norm > 0 else atom


def sample_fractional_window(samples, start, frame_count):
    x = start + np.arange(frame_count, dtype=np.float64)
    return np.interp(x, np.arange(len(samples), dtype=np.float64), samples, left=0.0, right=0.0).astype(np.float32)


def cmd_record_field(args):
    profile = load_profile(args.profile)
    if profile.get("captureMode", "shared-input-device") == "distributed-clocks":
        raise SystemExit(
            "record-field only works for shared-input-device profiles. "
            "Distributed camera/Focusrite microphones must be captured per clock domain, "
            "aligned/resampled into a six-channel WAV, then passed to encode-foa."
        )
    sd = sounddevice_module()
    input_device = match_device(sd, profile["inputDevice"], "input")
    sample_rate = int(profile["sampleRate"])
    channels = int(profile["inputDevice"]["channels"])
    frames = int(float(args.seconds) * sample_rate)
    out = args.output or run_dir("audio-field-record")
    out.mkdir(parents=True, exist_ok=True)
    rec = sd.rec(
        frames,
        samplerate=sample_rate,
        channels=channels,
        dtype="float32",
        device=input_device["index"],
        blocking=True,
    )
    wavfile.write(out / "field-raw.wav", sample_rate, rec.astype(np.float32))
    write_json(
        out / "manifest.json",
        {
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "profile": str(args.profile),
            "inputDevice": summarize_device(input_device),
            "seconds": float(args.seconds),
            "file": "field-raw.wav",
        },
    )
    print(out)


def cmd_encode_foa(args):
    profile = load_profile(args.profile)
    sample_rate, data = wavfile.read(args.input)
    if int(sample_rate) != int(profile["sampleRate"]):
        raise SystemExit(f"Input sample-rate {sample_rate} does not match profile {profile['sampleRate']}")
    data = as_float_matrix(data)
    foa = encode_foa(profile, data)
    peak = float(np.max(np.abs(foa))) if foa.size else 0.0
    if peak > 0.98:
        foa = foa / peak * 0.98
    wavfile.write(args.output, sample_rate, foa.astype(np.float32))
    print(json.dumps({"output": str(args.output), "sampleRate": int(sample_rate), "channels": ["W", "Y", "Z", "X"], "peakBeforeLimit": peak}, indent=2))


def cmd_suppress_room(args):
    profile = load_profile(args.profile)
    sample_rate, data = wavfile.read(args.input)
    field = as_float_matrix(data)
    anchors = infer_anchor_channels(profile) if not args.anchor_channel else [int(value) for value in args.anchor_channel]
    witnesses = infer_witness_channels(profile, anchors) if not args.witness_channel else [int(value) for value in args.witness_channel]
    config = RoomSuppressionConfig(
        block_size=int(args.block_size),
        hop_size=int(args.hop_size),
        transient_ratio=float(args.transient_ratio),
        max_witness_attenuation_db=float(args.max_witness_attenuation_db),
        anchor_transient_attenuation_db=float(args.anchor_transient_attenuation_db),
        room_subtraction=float(args.room_subtraction),
        envelope_floor=float(args.envelope_floor),
    )
    cleaned, report = suppress_room_field(field, anchors, witnesses, config)
    output = args.output or args.input.with_name(args.input.stem + "-room-suppressed.wav")
    peak = float(np.max(np.abs(cleaned))) if cleaned.size else 0.0
    if args.limit_peak and peak > float(args.limit_peak):
        cleaned = cleaned * (float(args.limit_peak) / peak)
    wavfile.write(output, int(sample_rate), cleaned.astype(np.float32))
    result = {
        "output": str(output),
        "sampleRate": int(sample_rate),
        "shape": list(cleaned.shape),
        "anchors": anchors,
        "witnesses": witnesses,
        "transientBlocks": report.transient_blocks,
        "meanWitnessGain": report.mean_witness_gain,
        "meanAnchorGain": report.mean_anchor_gain,
        "peakBeforeLimit": peak,
    }
    report_path = args.report or output.with_suffix(".room-suppression.json")
    write_json(report_path, result)
    print(json.dumps(result, indent=2))


def cmd_suppress_reference(args):
    profile = load_profile(args.profile)
    sample_rate, data = wavfile.read(args.input)
    field = as_float_matrix(data)
    ref_rate, reference = wavfile.read(args.reference)
    reference = mono(as_float_matrix(reference))
    reference = resample_if_needed(reference, int(ref_rate), int(sample_rate))
    channels = [int(value) for value in args.channel] if args.channel else list(range(field.shape[1]))
    cleaned, reports = suppress_program_reference(
        field,
        reference,
        int(sample_rate),
        channels,
        nperseg=int(args.window_size),
        noverlap=int(args.overlap),
        regularization=float(args.regularization),
        subtraction_strength=float(args.subtraction_strength),
    )
    output = args.output or args.input.with_name(args.input.stem + "-program-suppressed.wav")
    wavfile.write(output, int(sample_rate), cleaned.astype(np.float32))
    result = {
        "output": str(output),
        "reference": str(args.reference),
        "sampleRate": int(sample_rate),
        "shape": list(cleaned.shape),
        "channels": [
            {
                "channel": report.channel,
                "inputRms": report.input_rms,
                "predictedRms": report.predicted_rms,
                "outputRms": report.output_rms,
                "reductionDb": report.reduction_db,
                "phaseMapping": [
                    {"frequencyHz": frequency, "phaseRadians": phase, "magnitude": magnitude}
                    for frequency, phase, magnitude in report.phase_mapping
                ],
            }
            for report in reports
        ],
        "notes": "Known program/reference bleed is transformed, mapped into each mic channel, subtracted, and exported as phase/frequency evidence for runtime fitting.",
    }
    report_path = args.report or output.with_suffix(".program-reference.json")
    write_json(report_path, result)
    print(json.dumps(result, indent=2))


def infer_anchor_channels(profile):
    anchors = []
    for mic in profile["microphones"]:
        role = str(mic.get("role", ""))
        if "dialogue-anchor" in role or int(mic.get("qualityPriority", 0)) >= 100:
            anchors.append(mic_channel(mic))
    return sorted(set(anchors))


def infer_witness_channels(profile, anchors):
    anchor_set = set(int(channel) for channel in anchors)
    return [mic_channel(mic) for mic in sorted(profile["microphones"], key=mic_channel) if mic_channel(mic) not in anchor_set]


def cmd_sync_plan(args):
    profile = load_profile(args.profile)
    groups = {}
    for mic in profile["microphones"]:
        groups.setdefault(mic.get("clockDomain", "shared"), []).append(mic["id"])
    anchors = sorted(
        [
            {
                "micId": mic["id"],
                "label": mic.get("label"),
                "role": mic.get("role"),
                "qualityPriority": mic.get("qualityPriority", 0),
                "machine": mic.get("machine"),
                "clockDomain": mic.get("clockDomain"),
                "attachedMic": mic.get("attachedMic"),
                "preferredSampleRates": mic.get("device", {}).get("preferredSampleRates"),
            }
            for mic in profile["microphones"]
        ],
        key=lambda item: item["qualityPriority"],
        reverse=True,
    )
    plan = {
        "profile": str(args.profile),
        "captureMode": profile.get("captureMode", "shared-input-device"),
        "capturePolicy": profile.get("capturePolicy", {}),
        "clockModel": profile.get("clockModel", {}),
        "referenceMicId": profile.get("clockModel", {}).get("referenceMicId") or profile.get("calibration", {}).get("referenceMicId"),
        "priorityMics": anchors,
        "clockDomains": [{"id": key, "microphones": value} for key, value in sorted(groups.items())],
        "requiredBeforeFoa": [
            "drive each Focusrite through the best available native/exclusive driver path and capture lossless float/PCM for calibration",
            "capture each clock domain with source timestamps and nominal sample rate",
            "estimate initial delay from speaker calibration pulse/sweep against the reference mic",
            "estimate sampling-rate offset over time against the reference domain",
            "resample each non-reference stream into the reference timeline",
            "write one aligned six-channel WAV ordered by microphone fieldChannel",
            "run encode-foa only on that aligned WAV",
        ],
    }
    print(json.dumps(plan, indent=2))


def encode_foa(profile, data):
    frames = data.shape[0]
    corrected = []
    weights = []
    for mic in sorted(profile["microphones"], key=mic_channel):
        channel = mic_channel(mic)
        if channel >= data.shape[1]:
            raise SystemExit(f"Input has {data.shape[1]} channels, missing microphone channel {channel}")
        x = data[:, channel].copy()
        x = apply_integer_delay(x, int(mic.get("delaySamples", 0)))
        x *= db_to_amp(float(mic.get("gainDb", 0.0)))
        x *= int(mic.get("polarity", 1))
        corrected.append(x)
        weights.append(foa_weights(mic))
    mic_matrix = np.stack(corrected, axis=1)
    weight_matrix = np.stack(weights, axis=0)
    foa = mic_matrix @ weight_matrix
    return foa / max(1, len(corrected))


def apply_integer_delay(x, delay):
    if delay == 0:
        return x
    y = np.zeros_like(x)
    if delay > 0:
        y[delay:] = x[:-delay]
    else:
        y[:delay] = x[-delay:]
    return y


def foa_weights(mic):
    orientation = mic["orientationDeg"]
    az = math.radians(float(orientation["azimuth"]))
    el = math.radians(float(orientation["elevation"]))
    ce = math.cos(el)
    # ACN/SN3D first order: W, Y, Z, X.
    return np.array(
        [
            1.0 / math.sqrt(2.0),
            math.sin(az) * ce,
            math.sin(el),
            math.cos(az) * ce,
        ],
        dtype=np.float32,
    )


def main():
    parser = argparse.ArgumentParser(description="Build and calibrate the LocalCastBridge six-mic Ambisonic audio field.")
    sub = parser.add_subparsers(required=True)

    p = sub.add_parser("devices", help="List PortAudio devices as JSON.")
    p.set_defaults(func=cmd_devices)

    p = sub.add_parser("probe-rates", help="Check whether matching devices accept selected sample rates.")
    p.add_argument("--device-query", required=True)
    p.add_argument("--hostapi", default="")
    p.add_argument("--direction", choices=["input", "output"], default="input")
    p.add_argument("--channels", type=int, default=1)
    p.add_argument("--dtype", default="float32")
    p.add_argument("--rate", type=int, action="append")
    p.set_defaults(func=cmd_probe_rates)

    p = sub.add_parser("validate", help="Validate an audio field profile.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--check-devices", action="store_true")
    p.set_defaults(func=cmd_validate)

    p = sub.add_parser("make-stimulus", help="Generate the calibration sweep WAV.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--output", type=Path)
    p.set_defaults(func=cmd_make_stimulus)

    p = sub.add_parser("init-run", help="Create a distributed calibration run folder and manifest.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--output", type=Path)
    p.set_defaults(func=cmd_init_run)

    p = sub.add_parser("record-local-calibration", help="Record local distributed mic sources, optionally while playing one speaker sweep.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--output", type=Path)
    p.add_argument("--machine", default="local")
    p.add_argument("--seconds", type=float, default=8.0)
    p.add_argument("--input-rate", type=int)
    p.add_argument("--play-sweep", action="store_true")
    p.add_argument("--speaker-channel", type=int, default=0)
    p.add_argument("--output-rate", type=int)
    p.add_argument("--record-loopback", action="store_true")
    p.add_argument("--loopback-query", default="Scarlett")
    p.add_argument("--loopback-rate", type=int)
    p.add_argument("--loopback-channels", type=int, default=2)
    p.set_defaults(func=cmd_record_local_calibration)

    p = sub.add_parser("record-probe-train", help="Record local mics while emitting repeated left/right chirplets with loopback ground truth.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--output", type=Path)
    p.add_argument("--machine", default="local")
    p.add_argument("--seconds", type=float, default=12.0)
    p.add_argument("--input-rate", type=int)
    p.add_argument("--output-rate", type=int)
    p.add_argument("--chirp-seconds", type=float, default=0.35)
    p.add_argument("--interval-seconds", type=float, default=1.0)
    p.add_argument("--chirps-per-second", type=int, default=1)
    p.add_argument("--probe-band", action="append", help="Layer probe texture bands as start:end Hz, repeatable.")
    p.add_argument("--probe-level-offset-db", type=float, default=-18.0)
    p.add_argument("--start-padding-seconds", type=float, default=1.0)
    p.add_argument("--loopback-query", default="Scarlett")
    p.add_argument("--loopback-rate", type=int)
    p.add_argument("--loopback-channels", type=int, default=2)
    p.set_defaults(func=cmd_record_probe_train)

    p = sub.add_parser("record-remote-focusrite", help="Record the neighbor Focusrite to a run source WAV over SSH/SFTP.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--run", type=Path, required=True)
    p.add_argument("--mic-id", default="mic_focusrite_neighbor")
    p.add_argument("--ssh-target", default="madman's lullaby@192.168.1.84")
    p.add_argument("--ffmpeg", default=r"C:\Users\Madman's Lullaby\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe")
    p.add_argument("--remote-dir", default=r"C:\Meta\LocalCastBridge\calibration\remote-captures")
    p.add_argument("--device", default="Analogue 1 + 2 (Focusrite USB Audio)")
    p.add_argument("--dshow-device", help=argparse.SUPPRESS)
    p.add_argument("--seconds", type=float, default=8.0)
    p.add_argument("--sample-rate", type=int, default=48000)
    p.add_argument("--channels", type=int, default=1)
    p.add_argument("--dry-run", action="store_true")
    p.set_defaults(func=cmd_record_remote_focusrite)

    p = sub.add_parser("play-record", help="Play each speaker calibration sweep while recording all microphones.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--output", type=Path)
    p.set_defaults(func=cmd_play_record)

    p = sub.add_parser("sync-plan", help="Summarize clock domains and the required alignment path before FOA encoding.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.set_defaults(func=cmd_sync_plan)

    p = sub.add_parser("analyze-calibration", help="Estimate speaker-to-mic delay/gain/polarity from a calibration run.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--run", type=Path, required=True)
    p.set_defaults(func=cmd_analyze_calibration)

    p = sub.add_parser("analyze-distributed", help="Detect calibration sweep arrival in one WAV per mic and estimate relative delays.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--run", type=Path, required=True)
    p.add_argument("--reference-mic")
    p.add_argument("--chirplet-refine", action=argparse.BooleanOptionalAction, default=True)
    p.add_argument("--search-samples", type=int, default=3)
    p.add_argument("--fractional-steps", type=int, default=8)
    p.add_argument("--rate-ppm", type=int, default=150)
    p.set_defaults(func=cmd_analyze_distributed)

    p = sub.add_parser("assemble-aligned", help="Assemble a distributed run into one aligned six-channel WAV.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--run", type=Path, required=True)
    p.add_argument("--analysis", type=Path)
    p.add_argument("--output", type=Path)
    p.add_argument("--seconds", type=float)
    p.add_argument("--compensate-response", action="store_true")
    p.add_argument("--max-response-boost-db", type=float, default=9.0)
    p.add_argument("--max-response-cut-db", type=float, default=12.0)
    p.add_argument("--response-smoothing-bins", type=int, default=31)
    p.set_defaults(func=cmd_assemble_aligned)

    p = sub.add_parser("analyze-reference-sync", help="Estimate live mic delay stability against captured output loopback ground truth.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--run", type=Path, required=True)
    p.add_argument("--reference", type=Path)
    p.add_argument("--window-seconds", type=float, default=2.0)
    p.add_argument("--hop-seconds", type=float, default=1.0)
    p.add_argument("--max-lag-ms", type=float, default=500.0)
    p.add_argument("--method", choices=["normalized", "gcc-phat"], default="normalized")
    p.add_argument("--min-score", type=float, default=0.08)
    p.add_argument("--min-reference-std", type=float, default=1e-4)
    p.add_argument("--min-mic-std", type=float, default=1e-5)
    p.add_argument("--min-abs-lag-ms", type=float, default=0.5)
    p.set_defaults(func=cmd_analyze_reference_sync)

    p = sub.add_parser("analyze-probe-train", help="Analyze repeated chirplet probes against output loopback for delay/jitter/drift evidence.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--run", type=Path, required=True)
    p.add_argument("--loopback-search-ms", type=float, default=80.0)
    p.add_argument("--mic-search-ms", type=float, default=250.0)
    p.add_argument("--min-loopback-score", type=float, default=0.12)
    p.add_argument("--min-mic-score", type=float, default=0.08)
    p.add_argument("--speed-of-sound", type=float, default=343.0)
    p.add_argument("--phase-frequency", type=float, action="append", default=[250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 12000.0])
    p.add_argument("--max-phase-delta-ms", type=float, default=3.0)
    p.add_argument("--phase-smoothing", type=float, default=0.25)
    p.add_argument("--max-phase-step-samples", type=float, default=32.0)
    p.add_argument("--mapping-learning-rate", type=float, default=0.2)
    p.add_argument("--max-mapping-phase-step", type=float, default=0.2)
    p.set_defaults(func=cmd_analyze_probe_train)

    p = sub.add_parser("record-field", help="Record the raw synchronized six-channel microphone field.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--seconds", type=float, default=10.0)
    p.add_argument("--output", type=Path)
    p.set_defaults(func=cmd_record_field)

    p = sub.add_parser("encode-foa", help="Encode a calibrated six-channel WAV into first-order AmbiX B-format.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--input", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.set_defaults(func=cmd_encode_foa)

    p = sub.add_parser("suppress-room", help="Suppress room/transient witness energy before FOA encoding.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--input", type=Path, required=True)
    p.add_argument("--output", type=Path)
    p.add_argument("--report", type=Path)
    p.add_argument("--anchor-channel", type=int, action="append")
    p.add_argument("--witness-channel", type=int, action="append")
    p.add_argument("--block-size", type=int, default=1024)
    p.add_argument("--hop-size", type=int, default=512)
    p.add_argument("--transient-ratio", type=float, default=2.5)
    p.add_argument("--max-witness-attenuation-db", type=float, default=-18.0)
    p.add_argument("--anchor-transient-attenuation-db", type=float, default=-6.0)
    p.add_argument("--room-subtraction", type=float, default=0.15)
    p.add_argument("--envelope-floor", type=float, default=1e-5)
    p.add_argument("--limit-peak", type=float, default=0.98)
    p.set_defaults(func=cmd_suppress_room)

    p = sub.add_parser("suppress-reference", help="Subtract known speaker/program reference bleed from an aligned field and export phase mapping.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--input", type=Path, required=True)
    p.add_argument("--reference", type=Path, required=True)
    p.add_argument("--output", type=Path)
    p.add_argument("--report", type=Path)
    p.add_argument("--channel", type=int, action="append")
    p.add_argument("--window-size", type=int, default=2048)
    p.add_argument("--overlap", type=int, default=1536)
    p.add_argument("--regularization", type=float, default=1e-6)
    p.add_argument("--subtraction-strength", type=float, default=0.85)
    p.set_defaults(func=cmd_suppress_reference)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
