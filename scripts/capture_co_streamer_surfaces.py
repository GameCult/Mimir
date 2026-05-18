from __future__ import annotations

import argparse
import json
import math
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path
from tempfile import NamedTemporaryFile

import numpy as np
from scipy import signal
from scipy.io import wavfile


DEFAULT_RUN = Path("calibration/runs/audio-program-live-20260518-180226")


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Capture the late-arriving co-streamer audio surfaces, align them to "
            "local loopback ground truth, and write synced OBS stem inputs."
        )
    )
    parser.add_argument("--run", type=Path, default=DEFAULT_RUN)
    parser.add_argument("--seconds", type=float, default=24.0)
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--ssh-target", default="madman's lullaby@192.168.1.84")
    parser.add_argument("--ffmpeg", default=r"C:\Users\Madman's Lullaby\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe")
    parser.add_argument("--remote-dir", default=r"C:\Meta\LocalCastBridge\calibration\remote-captures")
    parser.add_argument("--focusrite-device", default="Analogue 1 + 2 (Focusrite USB Audio)")
    parser.add_argument("--loopback-device", default="")
    parser.add_argument("--loopback-capture", choices=("wasapi", "dshow"), default="wasapi")
    parser.add_argument("--remote-wasapi-script", default=r"C:\Meta\LocalCastBridge\scripts\wasapi-loopback-capture.ps1")
    parser.add_argument("--remote-wasapi-role", choices=("Console", "Multimedia", "Communications"), default="Console")
    parser.add_argument("--local-loopback-query", default="Scarlett")
    parser.add_argument("--max-lag-ms", type=float, default=3000.0)
    parser.add_argument("--min-loopback-rms", type=float, default=1e-4)
    parser.add_argument("--min-loopback-score", type=float, default=0.01)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    run = args.run.resolve()
    surface_dir = run / "co_streamer_surfaces"
    surface_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S")

    remote_focusrite = f"{args.remote_dir.rstrip('\\\\/')}\\co_streamer_focusrite-{stamp}.wav"
    remote_loopback_ext = "f32" if args.loopback_capture == "wasapi" else "wav"
    remote_loopback = f"{args.remote_dir.rstrip('\\\\/')}\\co_streamer_loopback-{stamp}.{remote_loopback_ext}"
    local_focusrite = surface_dir / "raw_focusrite.wav"
    local_remote_loopback = surface_dir / f"raw_loopback.{remote_loopback_ext}"
    local_reference = surface_dir / "local_loopback_reference.wav"

    commands = [
        remote_ffmpeg_command(args, args.focusrite_device, remote_focusrite, channels=1),
        remote_loopback_command(args, remote_loopback),
    ]
    if args.dry_run:
        for command in commands:
            print(command)
        return 0

    print("starting remote co-streamer captures")
    procs = [subprocess.Popen(["ssh", args.ssh_target, command], text=True) for command in commands]
    time.sleep(0.2)
    local_loopback = record_local_loopback(
        query=args.local_loopback_query,
        seconds=float(args.seconds),
        sample_rate=int(args.sample_rate),
        channels=2,
    )
    wavfile.write(local_reference, int(args.sample_rate), local_loopback.astype(np.float32))

    failures = []
    for proc in procs:
        rc = proc.wait()
        if rc:
            failures.append(rc)
    if failures:
        raise SystemExit(f"remote capture failed with exit codes {failures}")

    sftp_get(args.ssh_target, remote_focusrite, local_focusrite)
    sftp_get(args.ssh_target, remote_loopback, local_remote_loopback)

    sample_rate = int(args.sample_rate)
    _, reference = read_wav_float(local_reference, sample_rate)
    _, focusrite = read_wav_float(local_focusrite, sample_rate)
    remote_loop = read_loopback_capture(local_remote_loopback, sample_rate, args.loopback_capture)
    reference_mono = mono(reference)
    remote_loop_mono = mono(remote_loop)
    max_lag = int(float(args.max_lag_ms) * sample_rate / 1000.0)
    remote_loop_rms = rms(remote_loop_mono)
    focusrite_mono = mono(focusrite)
    focusrite_rms = rms(focusrite_mono)
    loopback_delay, loopback_score = normalized_delay(remote_loop_mono, reference_mono, max_lag=max_lag)
    focusrite_delay, focusrite_score = normalized_delay(focusrite_mono, reference_mono, max_lag=max_lag)
    if remote_loop_rms >= float(args.min_loopback_rms) and loopback_score >= float(args.min_loopback_score):
        delay_samples = loopback_delay
        score = loopback_score
        alignment_witness = "remote-loopback"
    else:
        delay_samples = focusrite_delay
        score = focusrite_score
        alignment_witness = "neighbor-focusrite-program-bleed"

    aligned_focusrite = shift_by_delay(focusrite, delay_samples, len(reference))
    aligned_loopback = shift_by_delay(remote_loop, delay_samples, len(reference))
    wavfile.write(surface_dir / "aligned_focusrite.wav", sample_rate, aligned_focusrite.astype(np.float32))
    wavfile.write(surface_dir / "aligned_loopback.wav", sample_rate, aligned_loopback.astype(np.float32))

    report = {
        "schema_version": "gamecult.localcast.co_streamer_surfaces.v1",
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "sample_rate": sample_rate,
        "seconds": float(args.seconds),
        "alignment_witness": alignment_witness,
        "delay_samples": int(delay_samples),
        "delay_ms": 1000.0 * float(delay_samples) / sample_rate,
        "correlation_score": float(score),
        "remote_loopback": {
            "rms": remote_loop_rms,
            "delay_samples": int(loopback_delay),
            "delay_ms": 1000.0 * float(loopback_delay) / sample_rate,
            "correlation_score": float(loopback_score),
        },
        "neighbor_focusrite": {
            "rms": focusrite_rms,
            "delay_samples": int(focusrite_delay),
            "delay_ms": 1000.0 * float(focusrite_delay) / sample_rate,
            "correlation_score": float(focusrite_score),
        },
        "interpretation": (
            "positive delay means the remote loopback arrived later than local loopback "
            "and was advanced into the local presentation timeline"
        ),
        "files": {
            "local_loopback_reference": str(local_reference),
            "raw_focusrite": str(local_focusrite),
            "raw_loopback": str(local_remote_loopback),
            "aligned_focusrite": str(surface_dir / "aligned_focusrite.wav"),
            "aligned_loopback": str(surface_dir / "aligned_loopback.wav"),
        },
    }
    (surface_dir / "alignment-report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0


def remote_ffmpeg_command(args: argparse.Namespace, device: str, output: str, *, channels: int) -> str:
    remote_dir = args.remote_dir.rstrip("\\/")
    return (
        f'cmd /c if not exist "{remote_dir}" mkdir "{remote_dir}" && '
        f'"{args.ffmpeg}" -y -hide_banner -nostdin -loglevel warning '
        f'-f dshow -t {float(args.seconds):.3f} -i audio="{device}" '
        f'-vn -ac {channels} -ar {int(args.sample_rate)} -c:a pcm_f32le "{output}"'
    )


def remote_loopback_command(args: argparse.Namespace, output: str) -> str:
    if args.loopback_capture == "dshow":
        if not args.loopback_device:
            raise SystemExit("--loopback-device is required when --loopback-capture dshow")
        return remote_ffmpeg_command(args, args.loopback_device, output, channels=2)
    log = output.rsplit(".", 1)[0] + ".log"
    task = "LocalCastWasapiLoopbackCapture"
    return (
        "powershell -NoProfile -ExecutionPolicy Bypass -Command "
        + quote_for_cmd(
            "$ErrorActionPreference='Stop'; "
            f"$out='{escape_ps(output)}'; $log='{escape_ps(log)}'; "
            "Remove-Item -LiteralPath $out,$log -ErrorAction SilentlyContinue; "
            f"$cmd='powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{escape_ps(args.remote_wasapi_script)}\" "
            f"-Output \"{escape_ps(output)}\" -Seconds {float(args.seconds):.3f} "
            f"-SampleRate {int(args.sample_rate)} -Channels 2 -Role {args.remote_wasapi_role} *> \"{escape_ps(log)}\"'; "
            "$bat=[IO.Path]::ChangeExtension($out,'.cmd'); "
            "Set-Content -LiteralPath $bat -Encoding ASCII -Value ('@echo off' + [Environment]::NewLine + $cmd + [Environment]::NewLine); "
            "$action=New-ScheduledTaskAction -Execute $bat; "
            "$trigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(10); "
            "$principal=New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME + '\\' + $env:USERNAME) -LogonType Interactive -RunLevel Limited; "
            f"Register-ScheduledTask -TaskName '{task}' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null; "
            f"Start-ScheduledTask -TaskName '{task}'; "
            f"$deadline=(Get-Date).AddSeconds({float(args.seconds) + 12.0:.3f}); "
            "while((Get-Date) -lt $deadline){ "
            "  $info=Get-ScheduledTaskInfo -TaskName '" + task + "'; "
            "  if((Test-Path -LiteralPath $out) -and ((Get-Item -LiteralPath $out).Length -gt 0) -and $info.LastTaskResult -eq 0){ break } "
            "  Start-Sleep -Milliseconds 250 "
            "}; "
            "if(-not (Test-Path -LiteralPath $out)){ if(Test-Path -LiteralPath $log){Get-Content $log -Tail 40}; throw 'WASAPI loopback capture did not create output' }; "
            "Write-Host ('wasapiLoopbackBytes=' + (Get-Item -LiteralPath $out).Length)"
        )
    )


def quote_for_cmd(command: str) -> str:
    return '"' + command.replace('"', '\\"') + '"'


def escape_ps(value: str) -> str:
    return value.replace("'", "''")


def sftp_get(ssh_target: str, remote_file: str, local_file: Path) -> None:
    remote_sftp_path = "/C:/" + remote_file.replace("\\", "/").split("C:/", 1)[-1]
    batch = f'get "{remote_sftp_path}" "{local_file}"\n'
    with NamedTemporaryFile("w", encoding="ascii", delete=False, suffix=".sftp") as fh:
        fh.write(batch)
        batch_path = fh.name
    try:
        completed = subprocess.run(["sftp", "-b", batch_path, ssh_target], text=True, capture_output=True)
        if completed.returncode:
            raise SystemExit(completed.stderr or completed.stdout)
    finally:
        Path(batch_path).unlink(missing_ok=True)


def record_local_loopback(query: str, seconds: float, sample_rate: int, channels: int) -> np.ndarray:
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
    with matches[0].recorder(samplerate=sample_rate, channels=channels) as recorder:
        return recorder.record(numframes=int(seconds * sample_rate)).astype(np.float32)


def read_wav_float(path: Path, target_rate: int) -> tuple[int, np.ndarray]:
    rate, samples = wavfile.read(path)
    samples = pcm_to_float(samples)
    if int(rate) != int(target_rate):
        gcd = math.gcd(int(rate), int(target_rate))
        samples = signal.resample_poly(samples, int(target_rate) // gcd, int(rate) // gcd, axis=0).astype(np.float32)
        rate = target_rate
    return int(rate), ensure_2d(samples)


def read_loopback_capture(path: Path, target_rate: int, capture_kind: str) -> np.ndarray:
    if capture_kind == "wasapi":
        samples = np.fromfile(path, dtype=np.float32)
        if samples.size % 2:
            samples = samples[:-1]
        return samples.reshape(-1, 2).astype(np.float32)
    _, samples = read_wav_float(path, target_rate)
    return samples


def pcm_to_float(samples: np.ndarray) -> np.ndarray:
    array = np.asarray(samples)
    if array.ndim == 1:
        array = array[:, None]
    if np.issubdtype(array.dtype, np.floating):
        return array.astype(np.float32, copy=False)
    if np.issubdtype(array.dtype, np.signedinteger):
        scale = float(max(abs(np.iinfo(array.dtype).min), np.iinfo(array.dtype).max))
        return (array.astype(np.float32) / scale).clip(-1.0, 1.0)
    raise TypeError(f"unsupported WAV dtype: {array.dtype}")


def ensure_2d(samples: np.ndarray) -> np.ndarray:
    return samples[:, None] if samples.ndim == 1 else samples


def mono(samples: np.ndarray) -> np.ndarray:
    samples = ensure_2d(samples)
    return np.mean(samples, axis=1, dtype=np.float32)


def normalized_delay(signal_a: np.ndarray, signal_b: np.ndarray, max_lag: int) -> tuple[int, float]:
    count = min(len(signal_a), len(signal_b))
    if count <= 0:
        return 0, 0.0
    a = signal_a[:count].astype(np.float32)
    b = signal_b[:count].astype(np.float32)
    if float(np.std(a)) <= 1e-12 or float(np.std(b)) <= 1e-12:
        return 0, 0.0
    a = (a - float(np.mean(a))) / (float(np.std(a)) + 1e-9)
    b = (b - float(np.mean(b))) / (float(np.std(b)) + 1e-9)
    corr = signal.correlate(a, b, mode="full", method="fft")
    mid = len(b) - 1
    segment = corr[mid - max_lag : mid + max_lag + 1]
    index = int(np.argmax(np.abs(segment)))
    lag = index - max_lag
    score = float(abs(segment[index]) / max(1, len(a)))
    return lag, score


def rms(samples: np.ndarray) -> float:
    samples = np.asarray(samples, dtype=np.float32)
    return float(np.sqrt(np.mean(samples * samples))) if samples.size else 0.0


def shift_by_delay(samples: np.ndarray, delay_samples: int, frame_count: int) -> np.ndarray:
    samples = ensure_2d(samples)
    out = np.zeros((frame_count, samples.shape[1]), dtype=np.float32)
    if delay_samples >= 0:
        src_start = delay_samples
        dst_start = 0
    else:
        src_start = 0
        dst_start = -delay_samples
    count = min(frame_count - dst_start, samples.shape[0] - src_start)
    if count > 0:
        out[dst_start : dst_start + count] = samples[src_start : src_start + count]
    return out


if __name__ == "__main__":
    raise SystemExit(main())
