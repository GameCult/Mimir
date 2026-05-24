namespace Mimir.Runtime.Synchronization;

public enum MimirAuthorityDecision
{
    ObserveOnly,
    CandidateEvidence,
    TrustedEvidence,
    CanonicalAuthority,
    Reject
}

public sealed record MimirAuthorityRule(
    string Id,
    string AppliesTo,
    MimirAuthorityDecision Decision,
    string[] RequiredEvidence,
    string[] ForbiddenConditions,
    string Notes);

public sealed record MimirAuthorityPolicyConfiguration(
    string Id,
    string Description,
    IReadOnlyList<MimirAuthorityRule> Rules);

public static class MimirAuthorityPolicyConfigurations
{
    public static MimirAuthorityPolicyConfiguration DefaultTimingPolicy { get; } = new(
        "default-timing-authority-policy",
        "One canonical timing authority, many witnesses. Trust is earned by decoded anchors and calibration receipts.",
        [
            new(
                "starfire-loopback-authority",
                "loopback-scarlett-speakers",
                MimirAuthorityDecision.CanonicalAuthority,
                RequiredEvidence: ["same-asio-clock-domain", "fresh-loopback-buffer"],
                ForbiddenConditions: ["device-missing", "stale-buffer"],
                "Starfire loopback is the normal local source-time authority."),
            new(
                "raven-typed-evidence",
                "raven-scarlett-witness",
                MimirAuthorityDecision.TrustedEvidence,
                RequiredEvidence: ["known-node", "matching-codebook", "clock-fit-confidence", "response-profile"],
                ForbiddenConditions: ["claims-canonical-authority", "wrong-codebook", "network-only-timestamps"],
                "Raven may contribute compact decoded truth but does not own the canonical clock."),
            new(
                "phone-candidate-evidence",
                "phone-mic-witness",
                MimirAuthorityDecision.CandidateEvidence,
                RequiredEvidence: ["matching-codebook", "anchor-batch", "health-counters"],
                ForbiddenConditions: ["unknown-node", "raw-timestamp-only", "agc-unknown-and-low-confidence"],
                "Phones start as candidates until repeated calibration earns trust."),
            new(
                "diagnostic-media-observe-only",
                "srt-program-bridge",
                MimirAuthorityDecision.ObserveOnly,
                RequiredEvidence: ["explicit-diagnostic-mode"],
                ForbiddenConditions: ["used-as-clock-authority"],
                "Bridge media can be useful without being trusted as time."),
            new(
                "unknown-node-reject",
                "*",
                MimirAuthorityDecision.Reject,
                RequiredEvidence: [],
                ForbiddenConditions: ["unknown-node", "missing-identity"],
                "Codebook possession is not permission to steer the machine.")
        ]);

    public static IReadOnlyList<MimirAuthorityPolicyConfiguration> BuiltIn { get; } =
    [
        DefaultTimingPolicy
    ];
}
