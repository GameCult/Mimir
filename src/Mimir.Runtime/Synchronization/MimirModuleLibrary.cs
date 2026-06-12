namespace Mimir.Runtime.Synchronization;

public sealed record MimirModuleCatalogEntry(
    string Id,
    string Owner,
    string Purpose,
    string[] Configurations,
    string[] VerificationCommands);

public static class MimirModuleLibrary
{
    public static IReadOnlyList<MimirModuleCatalogEntry> Entries { get; } =
    [
        new(
            "bioacoustic-decoder",
            "Mimir.Runtime",
            "Reusable indexed log-mel/MFCC receiver configurations for standalone word identity and canonical clock recovery.",
            MimirBioacousticDecoderConfiguration.BuiltInProfiles.Select(profile => profile.Id).ToArray(),
            [
                "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-cepstral-smoke",
                "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-train"
            ]),
        new(
            "bioacoustic-language",
            "Mimir.Runtime",
            "Emission duty and vocabulary profiles for passive, hybrid, continuous, calibration, and phone-witness modes.",
            MimirBioacousticLanguageConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "bioacoustic-clock",
            "Mimir.Runtime",
            "Global clock hypothesis solver over decoded word anchors; this is the timing truth shape for remote witnesses.",
            ["single-anchor-offset", "multi-anchor-rate-fit", "coverage-confidence"],
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --standalone-bioacoustic-self-test"]),
        new(
            "alignment-actuator",
            "Mimir.Runtime + Faust",
            "Control surface for fractional delay and SRO correction. Mimir estimates; Faust moves samples.",
            [MimirAlignmentActuatorProfile.SixSourceFaust.Id],
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-actuator-self-test"]),
        new(
            "audio-actuator-strategies",
            "Mimir.Runtime + Faust",
            "Actuator strategy configurations from coarse diagnostic delay through final delay-plus-ASRC drift hold.",
            MimirAudioActuatorConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-actuator-self-test"]),
        new(
            "cultmesh-contracts",
            "Mimir.Runtime",
            "Typed state documents and stream frames for codebooks, decoder state, acoustic path state, actuator state, program state, Move evidence, and Mimir-fused Move controller poses.",
            ["mimir.bioacoustic_codebook_state", "mimir.bioacoustic_decoder_state", "mimir.acoustic_path_state", "mimir.actuator_state", "mimir.move_tracking_observation", "mimir.move_controller_pose", "mimir.move_controller_pose_stream_frame"],
            [
                "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-contract-smoke",
                "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --move-fusion-smoke"
            ]),
        new(
            "move-controller-fusion",
            "Mimir.Runtime + Fensalir",
            "Mimir fuses Muninn marker candidates, controller IMU/button state, calibration, and timing into resolved wand poses, then publishes them as a realtime CultMesh pose stream for interaction consumers.",
            ["mimir.move_calibration_protocol.v1", "mimir.move_controller_pose.v1", "mimir.move_controller_pose_stream_frame.v1", "muninn.quest_access.v1", "muninn.quest_pose_frame.v1", "muninn.move_marker_candidate.v1", "muninn.move_controller_state.v1"],
            [
                "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --move-tracking-contract-smoke",
                "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --move-calibration-protocol-smoke",
                "dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --move-fusion-smoke"
            ]),
        new(
            "path-learning",
            "Mimir.Runtime",
            "Calibration session policy for usable bands, confusion matrices, delay hypotheses, group delay, and codebook adaptation.",
            MimirAcousticPathLearningConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "acoustic-localization",
            "Mimir.Runtime + Fensalir",
            "TDOA, SRP-PHAT, sparse-source, and visual-constrained localization configurations for volumetric audio constraints.",
            MimirAcousticLocalizationConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "benchmark-panels",
            "Mimir.Runtime",
            "Named degradation and acceptance panels for decoder golf and physical-path acceptance receipts.",
            MimirBenchmarkPanelConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-train"]),
        new(
            "native-capture",
            "Mimir.Runtime + native workers",
            "Device-profile library for six cameras, Scarlett ASIO, Raven witness audio, and direct-driver ownership.",
            MimirNativeCaptureConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "camera-ingest-strategies",
            "Mimir.Runtime + native/reservoir + Fensalir",
            "Ingest configurations for diagnostic metadata, managed native wrappers, native SPSC rings, and shared GPU textures.",
            MimirCameraIngestConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "reservoir-strategies",
            "Mimir.Runtime + native/reservoir + Fensalir",
            "Managed, native, and GPU-resident reservoir configurations with explicit retention owners.",
            MimirReservoirConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "distributed-witnesses",
            "Mimir.Runtime + CultMesh",
            "Remote mini-Mimir configurations for Raven, phones, microcontrollers, and networked camera rigs.",
            MimirDistributedWitnessConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "network-transports",
            "Mimir.Runtime + CultMesh + diagnostics",
            "Transport policy for typed timing state, debug windows, bridge media, and experimental browser feeds.",
            MimirNetworkTransportConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "authority-policy",
            "Mimir.Runtime",
            "Trust rules for loopback authority, Raven evidence, phone candidates, bridge diagnostics, and unknown nodes.",
            MimirAuthorityPolicyConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "field-assembly",
            "Mimir.Runtime",
            "Composable assembly plans for synthetic proof, Scarlett loopback, meatspace calibration, and distributed CultMesh witnesses.",
            MimirMachineAssemblyPlans.BuiltIn.Select(plan => plan.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "program-publication",
            "Mimir + Fensalir + Faust + Eve + Yggdrasil",
            "Mimir-owned program composition, Eve operator surfaces, Yggdrasil site publication, and temporary OBS compatibility adapters.",
            MimirProgramPublicationConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"])
    ];
}
