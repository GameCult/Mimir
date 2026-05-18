from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class RemoteVideoArtifact:
    source_name: str
    url: str
    expected_latency_ns: int
    present_time_ns: int
    delta_ns: int
    synchronized: bool


def remote_video_artifact_for_present_time(
    *,
    source_name: str,
    url: str,
    present_time_ns: int,
    observed_time_ns: int,
    expected_latency_ns: int,
    tolerance_ns: int = 120_000_000,
) -> RemoteVideoArtifact:
    media_present_time = int(observed_time_ns) + int(expected_latency_ns)
    delta_ns = int(present_time_ns) - media_present_time
    return RemoteVideoArtifact(
        source_name=str(source_name),
        url=str(url),
        expected_latency_ns=int(expected_latency_ns),
        present_time_ns=media_present_time,
        delta_ns=delta_ns,
        synchronized=abs(delta_ns) <= int(tolerance_ns),
    )

