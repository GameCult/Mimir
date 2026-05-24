namespace Mimir.Runtime.Synchronization;

public sealed record MimirPerfectMachineManifest(
    string Schema,
    string GeneratedAtUtc,
    IReadOnlyList<MimirModuleCatalogEntry> Modules,
    IReadOnlyList<MimirPerfectMachineProfile> NodeProfiles,
    IReadOnlyList<MimirBioacousticDecoderConfiguration> DecoderProfiles,
    IReadOnlyList<MimirBioacousticLanguageConfiguration> LanguageProfiles,
    IReadOnlyList<MimirAcousticPathLearningConfiguration> PathLearningProfiles,
    IReadOnlyList<MimirAcousticLocalizationConfiguration> AcousticLocalizationProfiles,
    IReadOnlyList<MimirBenchmarkPanelConfiguration> BenchmarkPanels,
    IReadOnlyList<MimirAudioActuatorConfiguration> AudioActuatorStrategies,
    IReadOnlyList<MimirNativeCaptureDeviceProfile> CaptureProfiles,
    IReadOnlyList<MimirCameraIngestConfiguration> CameraIngestStrategies,
    IReadOnlyList<MimirReservoirConfiguration> ReservoirStrategies,
    IReadOnlyList<MimirDistributedWitnessConfiguration> DistributedWitnesses,
    IReadOnlyList<MimirNetworkTransportConfiguration> NetworkTransports,
    IReadOnlyList<MimirAuthorityPolicyConfiguration> AuthorityPolicies,
    IReadOnlyList<MimirAudioFieldConfiguration> AudioFields,
    IReadOnlyList<MimirVisualFusionConfiguration> VisualFields,
    IReadOnlyList<MimirComputeOffloadConfiguration> ComputePlans,
    IReadOnlyList<MimirObsPublicationConfiguration> Publications,
    IReadOnlyList<MimirMachineAssemblyPlan> AssemblyPlans);

public static class MimirPerfectMachineManifestFactory
{
    public const string Schema = "gamecult.mimir.perfect_machine_manifest.v1";

    public static MimirPerfectMachineManifest Create(DateTimeOffset? generatedAt = null) =>
        new(
            Schema,
            (generatedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            MimirModuleLibrary.Entries,
            MimirPerfectMachineProfiles.All,
            MimirBioacousticDecoderConfiguration.BuiltInProfiles,
            MimirBioacousticLanguageConfigurations.BuiltIn,
            MimirAcousticPathLearningConfigurations.BuiltIn,
            MimirAcousticLocalizationConfigurations.BuiltIn,
            MimirBenchmarkPanelConfigurations.BuiltIn,
            MimirAudioActuatorConfigurations.BuiltIn,
            MimirNativeCaptureConfigurations.BuiltIn,
            MimirCameraIngestConfigurations.BuiltIn,
            MimirReservoirConfigurations.BuiltIn,
            MimirDistributedWitnessConfigurations.BuiltIn,
            MimirNetworkTransportConfigurations.BuiltIn,
            MimirAuthorityPolicyConfigurations.BuiltIn,
            MimirAudioFieldConfigurations.BuiltIn,
            MimirVisualFusionConfigurations.BuiltIn,
            MimirComputeOffloadConfigurations.BuiltIn,
            MimirObsPublicationConfigurations.BuiltIn,
            MimirMachineAssemblyPlans.BuiltIn);
}
