namespace Mimir.Runtime.Synchronization;

public enum MimirWorkloadKind
{
    BioacousticDecode,
    PassiveGccPhat,
    ClockHypothesis,
    PathCalibration,
    ActuatorControl,
    FaustDsp,
    CameraCapture,
    VisualFeatureExtraction,
    VisualFusion,
    ObsPublication
}

public sealed record MimirWorkloadPlacement(
    MimirWorkloadKind Workload,
    MimirComputeBackend PreferredBackend,
    string Owner,
    bool CanRunRemote,
    bool RequiresRealtime,
    string Invariant);

public sealed record MimirComputeOffloadConfiguration(
    string Id,
    string Description,
    IReadOnlyList<MimirWorkloadPlacement> Placements);

public static class MimirComputeOffloadConfigurations
{
    public static MimirComputeOffloadConfiguration StarfireLocalHeavy { get; } = new(
        "starfire-local-heavy",
        "Keep canonical timing, DSP, and fusion local to Starfire; remote nodes send typed observations.",
        [
            new(MimirWorkloadKind.BioacousticDecode, MimirComputeBackend.ManagedScalar, "local receiver or remote witness", CanRunRemote: true, RequiresRealtime: true, "Decode emits observations, not global truth."),
            new(MimirWorkloadKind.PassiveGccPhat, MimirComputeBackend.ManagedScalar, "Starfire", CanRunRemote: false, RequiresRealtime: true, "Loopback remains local timing authority."),
            new(MimirWorkloadKind.ClockHypothesis, MimirComputeBackend.ManagedScalar, "Starfire", CanRunRemote: false, RequiresRealtime: true, "Canonical clock fit is centralized."),
            new(MimirWorkloadKind.PathCalibration, MimirComputeBackend.ManagedScalar, "Starfire", CanRunRemote: true, RequiresRealtime: false, "Calibration receipts are replayable typed evidence."),
            new(MimirWorkloadKind.ActuatorControl, MimirComputeBackend.FaustNativeDsp, "Faust/native DSP", CanRunRemote: false, RequiresRealtime: true, "DSP moves samples; runtime only estimates state."),
            new(MimirWorkloadKind.FaustDsp, MimirComputeBackend.FaustNativeDsp, "Faust/native DSP", CanRunRemote: false, RequiresRealtime: true, "Alignment, separation, and spatial bed are audio-rate DSP."),
            new(MimirWorkloadKind.CameraCapture, MimirComputeBackend.NativeSimd, "native capture workers", CanRunRemote: true, RequiresRealtime: true, "Drivers own device reads and timestamps."),
            new(MimirWorkloadKind.VisualFeatureExtraction, MimirComputeBackend.D3D12Compute, "Fensalir", CanRunRemote: false, RequiresRealtime: true, "GPU owns dense feature work."),
            new(MimirWorkloadKind.VisualFusion, MimirComputeBackend.D3D12Compute, "Fensalir", CanRunRemote: false, RequiresRealtime: true, "Fensalir owns temporal evidence field."),
            new(MimirWorkloadKind.ObsPublication, MimirComputeBackend.D3D12Compute, "OBS/Fensalir output", CanRunRemote: false, RequiresRealtime: true, "OBS receives program surfaces, not sync authority.")
        ]);

    public static MimirComputeOffloadConfiguration DistributedWitnesses { get; } = new(
        "distributed-witnesses",
        "Phones, Raven, and microcontrollers decode locally and return typed observations over CultMesh.",
        [
            new(MimirWorkloadKind.BioacousticDecode, MimirComputeBackend.RemoteCultMeshWorker, "remote witness", CanRunRemote: true, RequiresRealtime: true, "Remote nodes self-locate but do not own canonical time."),
            new(MimirWorkloadKind.ClockHypothesis, MimirComputeBackend.ManagedScalar, "Starfire", CanRunRemote: false, RequiresRealtime: true, "Starfire arbitrates clock/path hypotheses."),
            new(MimirWorkloadKind.PathCalibration, MimirComputeBackend.RemoteCultMeshWorker, "remote witness", CanRunRemote: true, RequiresRealtime: false, "Remote path evidence comes home as receipts."),
            new(MimirWorkloadKind.ActuatorControl, MimirComputeBackend.FaustNativeDsp, "Starfire Faust/native DSP", CanRunRemote: false, RequiresRealtime: true, "Only the program graph moves program samples."),
            new(MimirWorkloadKind.CameraCapture, MimirComputeBackend.NativeSimd, "local or remote capture worker", CanRunRemote: true, RequiresRealtime: true, "Each machine reads its own devices directly.")
        ]);

    public static MimirComputeOffloadConfiguration CalibrationSweep { get; } = new(
        "calibration-sweep",
        "Offline-heavy calibration sweep using native/GPU batches where useful, preserving all scores.",
        [
            new(MimirWorkloadKind.BioacousticDecode, MimirComputeBackend.NativeSimd, "calibration worker", CanRunRemote: true, RequiresRealtime: false, "Large candidate batches can use native SIMD."),
            new(MimirWorkloadKind.PathCalibration, MimirComputeBackend.D3D12Compute, "calibration worker", CanRunRemote: true, RequiresRealtime: false, "GPU is useful when batches are large enough."),
            new(MimirWorkloadKind.ClockHypothesis, MimirComputeBackend.ManagedScalar, "calibration worker", CanRunRemote: true, RequiresRealtime: false, "Clock solve stays branchy and inspectable."),
            new(MimirWorkloadKind.VisualFeatureExtraction, MimirComputeBackend.D3D12Compute, "Fensalir/calibration worker", CanRunRemote: true, RequiresRealtime: false, "Offline feature batches may run away from the render tick.")
        ]);

    public static IReadOnlyList<MimirComputeOffloadConfiguration> BuiltIn { get; } =
    [
        StarfireLocalHeavy,
        DistributedWitnesses,
        CalibrationSweep
    ];
}
