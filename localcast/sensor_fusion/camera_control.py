from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol

import numpy as np


@dataclass(frozen=True)
class FrameQuality:
    mean_luma: float
    dark_clip: float
    bright_clip: float
    contrast: float
    sharpness: float


@dataclass(frozen=True)
class CameraControlState:
    exposure: float = 0.5
    gain: float = 0.25
    focus: float = 0.5


@dataclass(frozen=True)
class CameraControlCommand:
    exposure: float | None = None
    gain: float | None = None
    focus: float | None = None

    @property
    def changed(self) -> bool:
        return self.exposure is not None or self.gain is not None or self.focus is not None


@dataclass(frozen=True)
class CameraQualityTarget:
    mean_luma: float = 0.52
    luma_deadband: float = 0.045
    max_bright_clip: float = 0.015
    max_dark_clip: float = 0.035
    min_sharpness: float = 0.035
    exposure_step: float = 0.035
    gain_step: float = 0.025
    focus_step: float = 0.020


class CameraSettingPort(Protocol):
    def apply(self, command: CameraControlCommand) -> None:
        ...


def measure_frame_quality(frame_bgr: np.ndarray) -> FrameQuality:
    if frame_bgr.ndim != 3 or frame_bgr.shape[2] != 3:
        raise ValueError("frame_bgr must have shape HxWx3")
    frame = frame_bgr.astype(np.float32) / 255.0
    luma = 0.0722 * frame[:, :, 0] + 0.7152 * frame[:, :, 1] + 0.2126 * frame[:, :, 2]
    gx = np.diff(luma, axis=1)
    gy = np.diff(luma, axis=0)
    sharpness = float(np.mean(np.abs(gx)) + np.mean(np.abs(gy)))
    return FrameQuality(
        mean_luma=float(np.mean(luma)),
        dark_clip=float(np.mean(luma < 0.025)),
        bright_clip=float(np.mean(luma > 0.975)),
        contrast=float(np.percentile(luma, 95) - np.percentile(luma, 5)),
        sharpness=sharpness,
    )


class AdaptiveCameraController:
    """Small bounded controller for manual capture settings.

    The controller owns measurement quality policy. It deliberately emits
    normalized commands; the driver adapter owns mapping those commands to
    OpenCV, DirectShow, vendor tools, or a no-op mock in tests.
    """

    def __init__(
        self,
        state: CameraControlState | None = None,
        target: CameraQualityTarget | None = None,
    ) -> None:
        self.state = state or CameraControlState()
        self.target = target or CameraQualityTarget()
        self._focus_direction = 1.0

    def update(self, quality: FrameQuality) -> CameraControlCommand:
        exposure = self.state.exposure
        gain = self.state.gain
        focus = self.state.focus

        too_bright = quality.mean_luma > self.target.mean_luma + self.target.luma_deadband
        too_dark = quality.mean_luma < self.target.mean_luma - self.target.luma_deadband

        if too_bright or quality.bright_clip > self.target.max_bright_clip:
            exposure -= self.target.exposure_step
            if quality.bright_clip > self.target.max_bright_clip * 2.0:
                gain -= self.target.gain_step
        elif too_dark or quality.dark_clip > self.target.max_dark_clip:
            exposure += self.target.exposure_step
            if exposure >= 0.92:
                gain += self.target.gain_step

        if quality.sharpness < self.target.min_sharpness:
            focus += self._focus_direction * self.target.focus_step
            if focus <= 0.05 or focus >= 0.95:
                self._focus_direction *= -1.0

        exposure = _clamp01(exposure)
        gain = _clamp01(gain)
        focus = _clamp01(focus)

        command = CameraControlCommand(
            exposure=None if abs(exposure - self.state.exposure) < 1e-9 else exposure,
            gain=None if abs(gain - self.state.gain) < 1e-9 else gain,
            focus=None if abs(focus - self.state.focus) < 1e-9 else focus,
        )
        self.state = CameraControlState(exposure=exposure, gain=gain, focus=focus)
        return command


class OpenCvCameraSettingPort:
    def __init__(self, capture: object) -> None:
        self.capture = capture

    def apply(self, command: CameraControlCommand) -> None:
        if not command.changed:
            return
        import cv2

        # Disable auto controls where OpenCV exposes the knobs, then apply a
        # normalized manual target. Driver backends differ; failed sets are
        # non-fatal because image quality feedback will continue next frame.
        try:
            self.capture.set(cv2.CAP_PROP_AUTO_EXPOSURE, 0.25)
        except Exception:
            pass
        if command.exposure is not None:
            _try_set(self.capture, cv2.CAP_PROP_EXPOSURE, -13.0 + command.exposure * 12.0)
        if command.gain is not None:
            _try_set(self.capture, cv2.CAP_PROP_GAIN, command.gain * 255.0)
        if command.focus is not None and hasattr(cv2, "CAP_PROP_FOCUS"):
            _try_set(self.capture, cv2.CAP_PROP_FOCUS, command.focus * 255.0)


def _try_set(capture: object, prop: int, value: float) -> None:
    try:
        capture.set(prop, float(value))
    except Exception:
        pass


def _clamp01(value: float) -> float:
    return max(0.0, min(1.0, float(value)))
