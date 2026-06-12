using GameCult.Caching;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

[CultDocument("mimir.move_calibration_protocol", "mimir.move_calibration_protocol.v1")]
[MessagePackObject]
public sealed record MimirMoveCalibrationProtocolDocument(
    [property: Key(0)]
    [property: CultName]
    string ProtocolId,
    [property: Key(1)] string CreatedAtUtc,
    [property: Key(2)] string TrackingSpaceId,
    [property: Key(3)] string FusionAuthorityId,
    [property: Key(4)] MimirMoveCalibrationStreamRequirement[] StreamRequirements,
    [property: Key(5)] MimirMoveCalibrationPhase[] Phases,
    [property: Key(6)] MimirMoveCalibrationOutput[] Outputs,
    [property: Key(7)] MimirMoveCalibrationAcceptance Acceptance,
    [property: Key(8)] string[] OperatorNotes);

[MessagePackObject]
public sealed record MimirMoveCalibrationStreamRequirement(
    [property: Key(0)] string StreamId,
    [property: Key(1)] string HostId,
    [property: Key(2)] string Owner,
    [property: Key(3)] string Kind,
    [property: Key(4)] string Transport,
    [property: Key(5)] bool Required,
    [property: Key(6)] string Evidence);

[MessagePackObject]
public sealed record MimirMoveCalibrationPhase(
    [property: Key(0)] string PhaseId,
    [property: Key(1)] string Title,
    [property: Key(2)] double DurationSeconds,
    [property: Key(3)] string OperatorCue,
    [property: Key(4)] string[] RequiredStreamIds,
    [property: Key(5)] string[] Produces);

[MessagePackObject]
public sealed record MimirMoveCalibrationOutput(
    [property: Key(0)] string OutputId,
    [property: Key(1)] string SchemaId,
    [property: Key(2)] string Owner,
    [property: Key(3)] string Purpose);

[MessagePackObject]
public sealed record MimirMoveCalibrationAcceptance(
    [property: Key(0)] int MinimumControllerStateSamplesPerMove,
    [property: Key(1)] int MinimumOpticalSamplesPerCamera,
    [property: Key(2)] int MinimumPeriwinkleMotionSamples,
    [property: Key(3)] double MaximumClockSkewMilliseconds,
    [property: Key(4)] double MaximumStillGyroRadiansPerSecond,
    [property: Key(5)] double MinimumTriangulatedPoseConfidence,
    [property: Key(6)] string[] RequiredDerivedOutputs);

public static class MimirMoveCalibrationProtocol
{
    public static MimirMoveCalibrationProtocolDocument CreateStarfireNightwingProtocol(
        DateTimeOffset? createdAt = null) =>
        new(
            ProtocolId: "mimir-move-calibration-starfire-nightwing-v1",
            CreatedAtUtc: (createdAt ?? DateTimeOffset.UtcNow).ToString("O"),
            TrackingSpaceId: "mimir-stage-space",
            FusionAuthorityId: "mimir.runtime.move-fusion",
            StreamRequirements:
            [
                new(
                    "muninn:starfire:move-evidence",
                    "starfire",
                    "Muninn on Starfire",
                    "Move optical marker candidates plus USB controller IMU/buttons",
                    "CultMesh shared-memory bytes frame, same-host when Mimir runs on Starfire",
                    Required: true,
                    "Starfire owns the USB-attached Move and local camera witnesses."),
                new(
                    "muninn:nightwing:move-evidence",
                    "nightwing",
                    "Muninn on Nightwing",
                    "Move optical marker candidates plus USB controller IMU/buttons",
                    "CultMesh bytes stream over the Verse; shared-memory on Nightwing, network bridge to Starfire",
                    Required: true,
                    "Nightwing owns its USB-attached Move and local camera witnesses."),
                new(
                    "mimir:starfire:move-controller-poses",
                    "starfire",
                    "Mimir.Runtime",
                    "Mimir-fused controller pose output",
                    "CultMesh shared-memory bytes frame",
                    Required: true,
                    "The calibration run must prove Mimir can republish resolved poses."),
                new(
                    "periwinkle:eve:motion",
                    "periwinkle",
                    "Periwinkle Eve client",
                    "phone IMU/motion witness",
                    "CultMesh observation ledger through Mimir Eve sensor intake",
                    Required: false,
                    "Independent body/clock witness for staged motions and operator cues."),
                new(
                    "periwinkle:eve:camera",
                    "periwinkle",
                    "Periwinkle Eve client",
                    "phone camera/media witness",
                    "CultMesh observation ledger through Mimir Eve sensor intake",
                    Required: false,
                    "Optional visual record of calibration poses and sweep compliance."),
                new(
                    "starfire:scarlett:loopback",
                    "starfire",
                    "Mimir.Runtime ASIO source",
                    "audio loopback timing witness",
                    "in-process ASIO into Mimir rolling buffers",
                    Required: false,
                    "Optional calibration metronome/cue timing witness; not a Move pose owner.")
            ],
            Phases:
            [
                new(
                    "preflight-streams",
                    "Stream liveness and clock edge check",
                    10.0,
                    "Hold both Moves still and visible. Confirm Starfire, Nightwing, and optional Periwinkle streams are fresh.",
                    ["muninn:starfire:move-evidence", "muninn:nightwing:move-evidence"],
                    ["stream-freshness", "clock-skew-estimate", "controller-id-map"]),
                new(
                    "dark-stillness",
                    "IMU bias with orbs off",
                    8.0,
                    "Set Move lights off. Place both Moves still on the calibration surface.",
                    ["muninn:starfire:move-evidence", "muninn:nightwing:move-evidence"],
                    ["gyro-bias", "accelerometer-gravity-vector", "magnetometer-baseline"]),
                new(
                    "lit-stillness",
                    "Optical centroid and static gravity alignment",
                    8.0,
                    "Light one Move at a time, then both. Keep them still and visible to both camera sets.",
                    ["muninn:starfire:move-evidence", "muninn:nightwing:move-evidence"],
                    ["camera-marker-centroid-stability", "light-command-to-controller-association"]),
                new(
                    "axis-sweeps",
                    "Slow single-axis rotations",
                    24.0,
                    "Sweep each Move slowly through pitch, yaw, and roll while keeping the orb visible.",
                    ["muninn:starfire:move-evidence", "muninn:nightwing:move-evidence"],
                    ["gyro-scale-fit", "accelerometer-frame-fit", "optical-angular-consistency"]),
                new(
                    "figure-eight",
                    "Magnetometer and cross-axis motion",
                    20.0,
                    "Move each controller through a broad figure-eight. Keep Periwinkle nearby if it is being used as the phone witness.",
                    ["muninn:starfire:move-evidence", "muninn:nightwing:move-evidence"],
                    ["magnetometer-hard-soft-iron-fit", "cross-axis-coupling-check"]),
                new(
                    "periwinkle-witness",
                    "Optional Periwinkle body witness",
                    12.0,
                    "Hold Periwinkle rigidly near the active Move for a short synchronized sweep if the phone is available.",
                    ["periwinkle:eve:motion"],
                    ["independent-motion-witness", "phone-clock-offset-estimate"]),
                new(
                    "validation-pass",
                    "Held-out tracking validation",
                    15.0,
                    "Run free hand motion with both orbs visible. Mimir may publish validation poses, but not promote calibration if confidence fails.",
                    ["muninn:starfire:move-evidence", "muninn:nightwing:move-evidence", "mimir:starfire:move-controller-poses"],
                    ["triangulated-position-residuals", "imu-prediction-residuals", "promotion-decision"])
            ],
            Outputs:
            [
                new(
                    "move-rig-calibration",
                    "mimir.move_fusion_rig_calibration.v1",
                    "Mimir.Runtime",
                    "Camera intrinsics/extrinsics, clock association policy, and gyro unit scale for Move fusion."),
                new(
                    "move-imu-calibration",
                    "mimir.move_imu_calibration.v1",
                    "Mimir.Runtime",
                    "Per-controller gyro bias/scale, accelerometer gravity frame, magnetometer correction, and noise estimates."),
                new(
                    "move-controller-map",
                    "mimir.move_controller_identity_map.v1",
                    "Mimir.Runtime",
                    "Stable association between Muninn USB controller ids, light-command receipts, and optical orb identities."),
                new(
                    "move-calibration-receipt",
                    "mimir.move_calibration_receipt.v1",
                    "Mimir.Runtime",
                    "Immutable receipt of captured sample counts, residuals, rejected phases, and promotion decision.")
            ],
            Acceptance: new(
                MinimumControllerStateSamplesPerMove: 600,
                MinimumOpticalSamplesPerCamera: 300,
                MinimumPeriwinkleMotionSamples: 120,
                MaximumClockSkewMilliseconds: 20.0,
                MaximumStillGyroRadiansPerSecond: 0.035,
                MinimumTriangulatedPoseConfidence: 0.65,
                RequiredDerivedOutputs:
                [
                    "move-rig-calibration",
                    "move-imu-calibration",
                    "move-controller-map",
                    "move-calibration-receipt"
                ]),
            OperatorNotes:
            [
                "Mimir owns fusion and calibration promotion. Muninn only publishes source-local evidence and executes local Move light commands.",
                "CultCache receipts are durable evidence; CultMesh stream frames are the hot path.",
                "Do not promote orientation until gyro bias/scale, gravity alignment, optical residuals, and held-out validation all pass."
            ]);

    public static string[] Validate(MimirMoveCalibrationProtocolDocument protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var errors = new List<string>();
        if (protocol.StreamRequirements.Count(stream => stream.Required) < 3)
        {
            errors.Add("required-streams-too-small");
        }

        if (!protocol.StreamRequirements.Any(stream => stream.StreamId == "muninn:starfire:move-evidence" && stream.Required))
        {
            errors.Add("missing-starfire-muninn-move-evidence");
        }

        if (!protocol.StreamRequirements.Any(stream => stream.StreamId == "muninn:nightwing:move-evidence" && stream.Required))
        {
            errors.Add("missing-nightwing-muninn-move-evidence");
        }

        if (!protocol.Phases.Any(phase => phase.PhaseId == "dark-stillness"))
        {
            errors.Add("missing-imu-bias-phase");
        }

        if (!protocol.Phases.Any(phase => phase.Produces.Contains("gyro-scale-fit", StringComparer.Ordinal)))
        {
            errors.Add("missing-gyro-scale-fit");
        }

        foreach (var requiredOutput in protocol.Acceptance.RequiredDerivedOutputs)
        {
            if (!protocol.Outputs.Any(output => output.OutputId == requiredOutput))
            {
                errors.Add($"missing-output:{requiredOutput}");
            }
        }

        return errors.ToArray();
    }
}
