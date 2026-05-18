import argparse
import json
import math
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from scipy.io import wavfile
from scipy import signal


ROOT = Path(__file__).resolve().parents[1]
RUNS = ROOT / "calibration" / "runs"


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
        if args.play_sweep:
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
        },
    )
    print(out)


def make_sweep_at_rate(profile, sample_rate):
    clone = dict(profile)
    clone["sampleRate"] = int(sample_rate)
    return make_sweep(clone)


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
        aligned *= db_to_amp(float(mic.get("gainDb", 0.0))) * int(mic.get("polarity", 1))
        channels.append(aligned)
        max_len = max(max_len, len(aligned))
        report.append({"micId": mic["id"], "status": "ok", "file": file_name, "appliedDelaySamples": delay})

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
    write_json(run / "aligned-field-manifest.json", result)
    print(json.dumps(result, indent=2))


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
    p.set_defaults(func=cmd_record_local_calibration)

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
    p.set_defaults(func=cmd_assemble_aligned)

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

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
