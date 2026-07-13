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
    [property: Key(2)] int MinimumExternalPoseSamples,
    [property: Key(3)] double MaximumClockSkewMilliseconds,
    [property: Key(4)] double MaximumStillGyroRadiansPerSecond,
    [property: Key(5)] double MinimumTriangulatedPoseConfidence,
    [property: Key(6)] string[] RequiredDerivedOutputs);

public static class MimirMoveCalibrationProtocol
{
    public static MimirMoveCalibrationProtocolDocument CreateNightwingFourWandProtocol(
        DateTimeOffset? createdAt = null) =>
        new(
            ProtocolId: "mimir-move-calibration-nightwing-four-wand-v1",
            CreatedAtUtc: (createdAt ?? DateTimeOffset.UtcNow).ToString("O"),
            TrackingSpaceId: "mimir-stage-space",
            FusionAuthorityId: "mimir.runtime.move-fusion",
            StreamRequirements:
            [
                new(
                    "muninn:nightwing:move-evidence",
                    "nightwing",
                    "Muninn on Nightwing",
                    "Move optical marker candidates plus USB controller IMU/buttons",
                    "CultMesh bytes stream over the Verse; shared-memory on Nightwing, network bridge to Starfire",
                    Required: true,
                    "Nightwing owns its USB-attached Move and local camera witnesses."),
                new(
                    "mimir:starfire:sensor-calibration-session",
                    "starfire",
                    "Mimir.Runtime",
                    "Bounded calibration lifecycle, coverage, fit, validation, and promotion state",
                    "Typed CultMesh state with CultCache persistence",
                    Required: true,
                    "Mimir owns the calibration task and its completion verdict."),
                new(
                    "mimir:starfire:move-controller-poses",
                    "starfire",
                    "Mimir.Runtime",
                    "Mimir-fused controller pose output",
                    "CultMesh shared-memory bytes frame",
                    Required: true,
                    "The calibration run must prove Mimir can republish resolved poses."),
                new(
                    "quest:usb:headset-pose",
                    "starfire",
                    "Quest USB/OpenXR witness",
                    "Quest headset pose near the Move/controllers calibration cluster",
                    "USB ADB/OpenXR witness bridged into Mimir calibration capture",
                    Required: false,
                    "External tracked VR frame for validating Mimir's optical/IMU pose frame."),
                new(
                    "quest:usb:left-controller-pose",
                    "starfire",
                    "Quest USB/OpenXR witness",
                    "Quest left controller pose",
                    "USB ADB/OpenXR witness bridged into Mimir calibration capture",
                    Required: false,
                    "External tracked controller reference sitting beside the Move."),
                new(
                    "quest:usb:right-controller-pose",
                    "starfire",
                    "Quest USB/OpenXR witness",
                    "Quest right controller pose",
                    "USB ADB/OpenXR witness bridged into Mimir calibration capture",
                    Required: false,
                    "External tracked controller reference sitting beside the Move."),
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
                    "Hold the four Moves visible. Confirm Nightwing evidence and stable identities are fresh.",
                    ["muninn:nightwing:move-evidence"],
                    ["stream-freshness", "clock-skew-estimate", "controller-id-map"]),
                new(
                    "dark-stillness",
                    "IMU bias with orbs off",
                    8.0,
                    "Set Move lights off. Place both Moves still on the calibration surface.",
                    ["muninn:nightwing:move-evidence"],
                    ["gyro-bias", "accelerometer-gravity-vector", "magnetometer-baseline"]),
                new(
                    "lit-stillness",
                    "Optical centroid and static gravity alignment",
                    8.0,
                    "Light one Move at a time, then both. Keep them still and visible to both camera sets.",
                    ["muninn:nightwing:move-evidence"],
                    ["camera-marker-centroid-stability", "light-command-to-controller-association"]),
                new(
                    "axis-sweeps",
                    "Slow single-axis rotations",
                    24.0,
                    "Sweep each Move slowly through pitch, yaw, and roll while keeping the orb visible.",
                    ["muninn:nightwing:move-evidence"],
                    ["gyro-scale-fit", "accelerometer-frame-fit", "optical-angular-consistency"]),
                new(
                    "figure-eight",
                    "Magnetometer and cross-axis motion",
                    20.0,
                    "Move each controller through a broad figure-eight. Leave the Quest headset/controllers stationary as the external VR reference cluster.",
                    ["muninn:nightwing:move-evidence"],
                    ["magnetometer-hard-soft-iron-fit", "cross-axis-coupling-check"]),
                new(
                    "quest-reference",
                    "Optional Quest headset/controller reference",
                    12.0,
                    "Keep the Move between the two Quest controllers in front of the headset. Capture the static Quest controller/headset frame while Mimir observes the Move.",
                    ["quest:usb:headset-pose", "quest:usb:left-controller-pose", "quest:usb:right-controller-pose"],
                    ["external-vr-pose-frame", "quest-to-mimir-frame-fit", "quest-clock-offset-estimate"]),
                new(
                    "wand-volume-sweep",
                    "Four-wand optical volume coverage",
                    120.0,
                    "Wave all four uniquely identified wands throughout the shared camera volume until Mimir reports sufficient per-sensor coverage.",
                    ["muninn:nightwing:move-evidence", "mimir:starfire:sensor-calibration-session"],
                    ["sensor-grid-coverage", "same-frame-correspondences", "sphere-radius-range", "fit-and-held-out-partitions"]),
                new(
                    "validation-pass",
                    "Held-out tracking validation",
                    15.0,
                    "Run free hand motion with both orbs visible. Mimir may publish validation poses, but not promote calibration if confidence fails.",
                    ["muninn:nightwing:move-evidence", "mimir:starfire:move-controller-poses"],
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
                MinimumExternalPoseSamples: 120,
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
                "Do not promote orientation until gyro bias/scale, gravity alignment, optical residuals, external Quest reference residuals when available, and held-out validation all pass."
            ]);

    public static string[] Validate(MimirMoveCalibrationProtocolDocument protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var errors = new List<string>();
        if (protocol.StreamRequirements.Count(stream => stream.Required) < 3)
        {
            errors.Add("required-streams-too-small");
        }

        if (!protocol.StreamRequirements.Any(stream => stream.StreamId == "muninn:nightwing:move-evidence" && stream.Required))
        {
            errors.Add("missing-nightwing-muninn-move-evidence");
        }

        if (!protocol.StreamRequirements.Any(stream => stream.StreamId == "mimir:starfire:sensor-calibration-session" && stream.Required))
        {
            errors.Add("missing-mimir-calibration-session");
        }

        if (!protocol.Phases.Any(phase => phase.PhaseId == "wand-volume-sweep"))
        {
            errors.Add("missing-wand-volume-sweep");
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
