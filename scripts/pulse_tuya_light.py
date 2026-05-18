import argparse
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from localcast.sensor_fusion.active_illumination import (
    ActiveIlluminationController,
    IlluminationPulsePlan,
    TinytuyaLightPort,
)


def main() -> None:
    parser = argparse.ArgumentParser(description="Pulse a local Tuya light as active vision calibration telemetry.")
    parser.add_argument("--device-id", required=True)
    parser.add_argument("--address", required=True)
    parser.add_argument("--local-key", required=True)
    parser.add_argument("--version", default="3.3")
    parser.add_argument("--count", type=int, default=6)
    parser.add_argument("--bright-ms", type=int, default=90)
    parser.add_argument("--settle-ms", type=int, default=210)
    parser.add_argument("--brightness", type=int, default=100)
    parser.add_argument("--idle-brightness", type=int, default=18)
    parser.add_argument("--color-temp", type=int, default=50)
    parser.add_argument("--dps-power", default="20")
    parser.add_argument("--dps-brightness", default="22")
    parser.add_argument("--dps-color-temp", default="23")
    args = parser.parse_args()

    light = TinytuyaLightPort(
        device_id=args.device_id,
        address=args.address,
        local_key=args.local_key,
        version=args.version,
        dps_power=args.dps_power,
        dps_brightness=args.dps_brightness,
        dps_color_temp=args.dps_color_temp,
    )
    plan = IlluminationPulsePlan(
        pulse_count=args.count,
        bright_ms=args.bright_ms,
        settle_ms=args.settle_ms,
        brightness_percent=args.brightness,
        idle_brightness_percent=args.idle_brightness,
        color_temp_percent=args.color_temp,
    )
    pulses = ActiveIlluminationController(light, plan).run()
    for pulse in pulses:
        print(
            f"pulse={pulse.pulse_index} "
            f"start_ns={pulse.start_monotonic_ns} "
            f"bright_until_ns={pulse.bright_until_monotonic_ns} "
            f"settle_until_ns={pulse.settle_until_monotonic_ns}"
        )


if __name__ == "__main__":
    main()
