from __future__ import annotations

from dataclasses import dataclass
import time
from typing import Protocol


class LightPort(Protocol):
    def set_white(self, *, brightness_percent: int, color_temp_percent: int) -> None:
        ...


@dataclass(frozen=True)
class IlluminationPulse:
    pulse_index: int
    start_monotonic_ns: int
    bright_until_monotonic_ns: int
    settle_until_monotonic_ns: int
    brightness_percent: int
    color_temp_percent: int


@dataclass(frozen=True)
class IlluminationPulsePlan:
    pulse_count: int = 6
    bright_ms: int = 90
    settle_ms: int = 210
    brightness_percent: int = 100
    idle_brightness_percent: int = 18
    color_temp_percent: int = 50

    @property
    def period_ms(self) -> int:
        return self.bright_ms + self.settle_ms


class ActiveIlluminationController:
    def __init__(self, light: LightPort, plan: IlluminationPulsePlan):
        self.light = light
        self.plan = plan

    def run(self, *, sleep=time.sleep, clock_ns=time.monotonic_ns) -> tuple[IlluminationPulse, ...]:
        pulses: list[IlluminationPulse] = []
        try:
            for index in range(max(0, self.plan.pulse_count)):
                start = clock_ns()
                self.light.set_white(
                    brightness_percent=self.plan.brightness_percent,
                    color_temp_percent=self.plan.color_temp_percent,
                )
                bright_until = start + self.plan.bright_ms * 1_000_000
                sleep(max(0.0, self.plan.bright_ms / 1000.0))
                self.light.set_white(
                    brightness_percent=self.plan.idle_brightness_percent,
                    color_temp_percent=self.plan.color_temp_percent,
                )
                settle_until = bright_until + self.plan.settle_ms * 1_000_000
                pulses.append(
                    IlluminationPulse(
                        pulse_index=index,
                        start_monotonic_ns=start,
                        bright_until_monotonic_ns=bright_until,
                        settle_until_monotonic_ns=settle_until,
                        brightness_percent=self.plan.brightness_percent,
                        color_temp_percent=self.plan.color_temp_percent,
                    )
                )
                sleep(max(0.0, self.plan.settle_ms / 1000.0))
        finally:
            self.light.set_white(
                brightness_percent=self.plan.idle_brightness_percent,
                color_temp_percent=self.plan.color_temp_percent,
            )
        return tuple(pulses)


class TinytuyaLightPort:
    def __init__(
        self,
        *,
        device_id: str,
        address: str,
        local_key: str,
        version: str = "3.3",
        dps_power: str = "20",
        dps_brightness: str = "22",
        dps_color_temp: str = "23",
    ) -> None:
        import tinytuya

        self.device = tinytuya.BulbDevice(device_id, address, local_key)
        self.device.set_version(float(version))
        self.dps_power = dps_power
        self.dps_brightness = dps_brightness
        self.dps_color_temp = dps_color_temp

    def set_white(self, *, brightness_percent: int, color_temp_percent: int) -> None:
        brightness = _clamp_percent(brightness_percent)
        color_temp = _clamp_percent(color_temp_percent)
        if hasattr(self.device, "turn_on"):
            self.device.turn_on()
        self._set_value(self.dps_power, True)
        self._set_value(self.dps_brightness, int(round(brightness * 10)))
        self._set_value(self.dps_color_temp, int(round(color_temp * 10)))

    def _set_value(self, dps: str, value: object) -> None:
        if hasattr(self.device, "set_value"):
            self.device.set_value(dps, value)
            return
        raise RuntimeError("tinytuya device does not expose set_value")


def _clamp_percent(value: int) -> int:
    return max(0, min(100, int(value)))
