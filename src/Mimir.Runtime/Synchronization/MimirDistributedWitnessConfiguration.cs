namespace Mimir.Runtime.Synchronization;

public enum MimirDistributedWitnessKind
{
    RavenScarlett,
    PhoneMicrophone,
    MicrocontrollerListener,
    RemoteCameraRig,
    BrowserDiagnostics
}

public sealed record MimirDistributedWitnessConfiguration(
    string Id,
    string Description,
    MimirDistributedWitnessKind Kind,
    string NodeRole,
    string[] Inputs,
    string[] RequiredStateDocuments,
    string[] EmittedStateDocuments,
    bool MayStreamRawMedia,
    bool MayOwnCanonicalClock,
    double ExpectedNetworkLatencyMilliseconds,
    string Notes);

public static class MimirDistributedWitnessConfigurations
{
    public static MimirDistributedWitnessConfiguration Raven { get; } = new(
        "raven-scarlett-witness",
        "Co-streamer/game machine with Scarlett loopback/mic evidence and network latency.",
        MimirDistributedWitnessKind.RavenScarlett,
        "remote-witness",
        Inputs: ["raven-scarlett-input-1", "raven-scarlett-input-2", "raven-loopback", "raven-camera"],
        RequiredStateDocuments: ["mimir.bioacoustic_codebook_state", "mimir.bioacoustic_decoder_state"],
        EmittedStateDocuments: ["mimir.acoustic_path_state", "mimir.bioacoustic_decoder_state"],
        MayStreamRawMedia: true,
        MayOwnCanonicalClock: false,
        ExpectedNetworkLatencyMilliseconds: 2.0,
        "Raven decodes locally and ships compact timing/path truth. Starfire still owns heavy field work.");

    public static MimirDistributedWitnessConfiguration Phone { get; } = new(
        "phone-mic-witness",
        "Harsh receiver target: OS resampling, AGC, lossy mic path, and mobile CPU.",
        MimirDistributedWitnessKind.PhoneMicrophone,
        "remote-witness",
        Inputs: ["phone-mic"],
        RequiredStateDocuments: ["mimir.bioacoustic_codebook_state", "mimir.bioacoustic_decoder_state"],
        EmittedStateDocuments: ["mimir.acoustic_path_state"],
        MayStreamRawMedia: false,
        MayOwnCanonicalClock: false,
        ExpectedNetworkLatencyMilliseconds: 20.0,
        "If the phone can self-locate from codebook and schedule, the language is earning its keep.");

    public static MimirDistributedWitnessConfiguration Microcontroller { get; } = Phone with
    {
        Id = "microcontroller-listener",
        Description = "Tiny low-power listener for room timing beacons and coarse occupancy evidence.",
        Kind = MimirDistributedWitnessKind.MicrocontrollerListener,
        Inputs = ["single-channel-adc"],
        RequiredStateDocuments = ["mimir.bioacoustic_codebook_state"],
        EmittedStateDocuments = ["mimir.acoustic_path_state"],
        ExpectedNetworkLatencyMilliseconds = 50.0,
        Notes = "Use compact profiles and narrow output: anchors, confidence, health counters."
    };

    public static MimirDistributedWitnessConfiguration RemoteCameraRig { get; } = Raven with
    {
        Id = "remote-camera-rig",
        Description = "Networked camera/audio producer with local timing decode.",
        Kind = MimirDistributedWitnessKind.RemoteCameraRig,
        Inputs = ["remote-video", "remote-audio", "remote-loopback"],
        MayStreamRawMedia = true,
        ExpectedNetworkLatencyMilliseconds = 8.0,
        Notes = "Raw media may arrive late; decoded anchors tell Mimir where it belongs in the window."
    };

    public static IReadOnlyList<MimirDistributedWitnessConfiguration> BuiltIn { get; } =
    [
        Raven,
        Phone,
        Microcontroller,
        RemoteCameraRig
    ];
}
