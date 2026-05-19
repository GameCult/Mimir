"""Probe-band selection helpers without diagnostic runtime dependencies."""


def parse_probe_band(value: str) -> tuple[float, float]:
    parts = value.split(":")
    if len(parts) != 2:
        raise ValueError(f"probe band must be start:end Hz, got {value!r}")
    return float(parts[0]), float(parts[1])


def ultrasonic_probe_band(sample_rate: int) -> tuple[float, float]:
    nyquist = 0.5 * float(sample_rate)
    end_hz = min(22_000.0, nyquist - 1200.0)
    start_hz = min(18_500.0, end_hz - 2500.0)
    if end_hz <= 16_000.0 or start_hz <= 0.0:
        raise ValueError(f"sample rate {sample_rate} leaves no useful ultrasonic probe band")
    return start_hz, end_hz
