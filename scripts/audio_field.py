import argparse
import json
import math
import sys
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

    seen_mic_channels = set()
    for mic in mics:
        channel = mic.get("channel")
        if not isinstance(channel, int) or channel < 0:
            errors.append(f"microphone {mic.get('id', '<missing>')} has invalid channel")
        if channel in seen_mic_channels:
            errors.append(f"duplicate microphone channel {channel}")
        seen_mic_channels.add(channel)
        if len(mic.get("positionMeters", [])) != 3:
            errors.append(f"microphone {mic.get('id', '<missing>')} needs positionMeters [x,y,z]")
        orientation = mic.get("orientationDeg", {})
        if "azimuth" not in orientation or "elevation" not in orientation:
            errors.append(f"microphone {mic.get('id', '<missing>')} needs orientationDeg azimuth/elevation")
        if mic.get("polarity", 1) not in (-1, 1):
            errors.append(f"microphone {mic.get('id', '<missing>')} polarity must be 1 or -1")

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
    if input_channels < 6:
        errors.append("inputDevice.channels must be at least 6")
    if output_channels < 2:
        errors.append("outputDevice.channels must be at least 2")

    if errors:
        raise SystemExit("Invalid audio profile:\n- " + "\n- ".join(errors))


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


def cmd_validate(args):
    profile = load_profile(args.profile)
    result = {
        "profile": str(args.profile),
        "sampleRate": profile["sampleRate"],
        "microphones": [mic["id"] for mic in profile["microphones"]],
        "speakers": [speaker["id"] for speaker in profile["speakers"]],
        "ambisonicBus": profile["ambisonicBus"],
        "deviceCheck": None,
    }
    if args.check_devices:
        sd = sounddevice_module()
        input_device = match_device(sd, profile["inputDevice"], "input")
        output_device = match_device(sd, profile["outputDevice"], "output")
        result["deviceCheck"] = {
            "input": summarize_device(input_device),
            "output": summarize_device(output_device),
        }
    print(json.dumps(result, indent=2))


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


def cmd_play_record(args):
    profile = load_profile(args.profile)
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
            channel = int(mic["channel"])
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


def as_float_matrix(data):
    arr = np.asarray(data)
    if arr.ndim == 1:
        arr = arr[:, None]
    if np.issubdtype(arr.dtype, np.integer):
        arr = arr.astype(np.float32) / np.iinfo(arr.dtype).max
    else:
        arr = arr.astype(np.float32)
    return arr


def estimate_delay(recorded, sweep):
    corr = signal.fftconvolve(recorded.astype(np.float32), sweep[::-1].astype(np.float32), mode="full")
    peak_index = int(np.argmax(np.abs(corr)))
    delay = peak_index - (len(sweep) - 1)
    peak = float(corr[peak_index])
    polarity = 1 if peak >= 0 else -1
    return delay, peak, polarity


def cmd_record_field(args):
    profile = load_profile(args.profile)
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


def encode_foa(profile, data):
    frames = data.shape[0]
    corrected = []
    weights = []
    for mic in sorted(profile["microphones"], key=lambda item: item["channel"]):
        channel = int(mic["channel"])
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

    p = sub.add_parser("validate", help="Validate an audio field profile.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--check-devices", action="store_true")
    p.set_defaults(func=cmd_validate)

    p = sub.add_parser("make-stimulus", help="Generate the calibration sweep WAV.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--output", type=Path)
    p.set_defaults(func=cmd_make_stimulus)

    p = sub.add_parser("play-record", help="Play each speaker calibration sweep while recording all microphones.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--output", type=Path)
    p.set_defaults(func=cmd_play_record)

    p = sub.add_parser("analyze-calibration", help="Estimate speaker-to-mic delay/gain/polarity from a calibration run.")
    p.add_argument("--profile", type=Path, default=ROOT / "config" / "audio-field.example.json")
    p.add_argument("--run", type=Path, required=True)
    p.set_defaults(func=cmd_analyze_calibration)

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
