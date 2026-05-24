namespace Mimir.Runtime.Synchronization;

public enum MimirAudioActuatorKind
{
    IntegerDelay,
    FarrowFractionalDelay,
    VariableAsrc,
    FrequencyDomainPhaseCorrection,
    HybridDelayAsrc
}

public sealed record MimirAudioActuatorConfiguration(
    string Id,
    string Description,
    MimirAudioActuatorKind Kind,
    string Owner,
    double MaxDelaySamples,
    double MaxSroPpm,
    double TargetResidualMicroseconds,
    bool SuitableForHotPath,
    string[] RequiredSignals,
    string[] ProofCommands);

public static class MimirAudioActuatorConfigurations
{
    public static MimirAudioActuatorConfiguration IntegerDelayBaseline { get; } = new(
        "integer-delay-baseline",
        "Coarse diagnostic baseline only.",
        MimirAudioActuatorKind.IntegerDelay,
        "Mimir.Runtime diagnostic",
        MaxDelaySamples: 4096.0,
        MaxSroPpm: 0.0,
        TargetResidualMicroseconds: 1000.0 / 192.0,
        SuitableForHotPath: false,
        RequiredSignals: ["smoothed-delay"],
        ProofCommands: ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-actuator-self-test"]);

    public static MimirAudioActuatorConfiguration FaustFarrowDelay { get; } = new(
        "faust-farrow-fractional-delay",
        "Production-shaped fractional delay surface; Mimir estimates, Faust moves samples.",
        MimirAudioActuatorKind.FarrowFractionalDelay,
        "Faust/native DSP",
        MaxDelaySamples: 4096.0,
        MaxSroPpm: 0.0,
        TargetResidualMicroseconds: 1.0,
        SuitableForHotPath: true,
        RequiredSignals: ["smoothed-delay", "confidence", "band-response"],
        ProofCommands: ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --bioacoustic-actuator-self-test"]);

    public static MimirAudioActuatorConfiguration VariableAsrc { get; } = new(
        "variable-asrc-drift-hold",
        "Long-horizon sample-rate-offset correction after delay is already bounded.",
        MimirAudioActuatorKind.VariableAsrc,
        "Faust/native DSP",
        MaxDelaySamples: 512.0,
        MaxSroPpm: 300.0,
        TargetResidualMicroseconds: 1.0,
        SuitableForHotPath: true,
        RequiredSignals: ["delay-slope", "sro-ppm", "confidence"],
        ProofCommands: ["dotnet run --project .\\src\\Mimir.BufferSmoke\\Mimir.BufferSmoke.csproj -- --perfect-machine-profile-smoke"]);

    public static MimirAudioActuatorConfiguration HybridDelayAsrc { get; } = FaustFarrowDelay with
    {
        Id = "hybrid-delay-asrc",
        Description = "Final target: fractional delay for phase, ASRC for drift, both driven by one sync state.",
        Kind = MimirAudioActuatorKind.HybridDelayAsrc,
        MaxSroPpm = 300.0,
        RequiredSignals = ["smoothed-delay", "sro-ppm", "confidence", "band-response", "group-delay-model"]
    };

    public static IReadOnlyList<MimirAudioActuatorConfiguration> BuiltIn { get; } =
    [
        IntegerDelayBaseline,
        FaustFarrowDelay,
        VariableAsrc,
        HybridDelayAsrc
    ];
}
