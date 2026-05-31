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
            "Typed state documents for codebooks, decoder state, acoustic path state, and actuator state.",
            ["mimir.bioacoustic_codebook_state", "mimir.bioacoustic_decoder_state", "mimir.acoustic_path_state", "mimir.actuator_state"],
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-contract-smoke"]),
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
            "stereo-depth",
            "Fensalir D3D12 compute",
            "SGM-shaped stereo disparity/depth profiles. libSGM is provenance for the first D3D12 lane, not a CUDA dependency.",
            MimirStereoDepthConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --stereo-depth-contract-smoke"]),
        new(
            "reservoir-strategies",
            "Mimir.Runtime + native/reservoir + Fensalir",
            "Managed, native, and GPU-resident reservoir configurations with explicit retention owners.",
            MimirReservoirConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]),
        new(
            "distributed-witnesses",
            "Mimir.Runtime + CultMesh",
            "Remote mini-Mimir configurations for Raven, phones, microcontrollers, Nightwing Eyes/Moves, and networked camera rigs.",
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
            "obs-publication",
            "Fensalir + Faust + OBS",
            "OBS-facing program surface and separately mixable audio stem configurations.",
            MimirObsPublicationConfigurations.BuiltIn.Select(profile => profile.Id).ToArray(),
            ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"])
    ];
}
