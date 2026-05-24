namespace Mimir.Runtime.Synchronization;

public sealed record MimirNetworkTransportRequest(
    MimirNetworkPayloadKind PayloadKind,
    bool RequiresClockInfluence,
    bool AllowsRawMedia,
    double MaximumLatencyMilliseconds,
    IReadOnlySet<string> AvailableDocuments,
    IReadOnlySet<string> Conditions);

public sealed record MimirNetworkTransportSelection(
    MimirNetworkTransportConfiguration? Transport,
    string Status,
    string Notes);

public sealed class MimirNetworkTransportSelector(
    IReadOnlyList<MimirNetworkTransportConfiguration>? transports = null)
{
    private readonly IReadOnlyList<MimirNetworkTransportConfiguration> transports =
        transports ?? MimirNetworkTransportConfigurations.BuiltIn;

    public MimirNetworkTransportSelection Select(MimirNetworkTransportRequest request)
    {
        foreach (var transport in transports.OrderBy(transport => transport.TargetLatencyMilliseconds))
        {
            if (transport.PayloadKind != request.PayloadKind)
            {
                continue;
            }

            if (request.RequiresClockInfluence && !transport.MayAffectClock)
            {
                continue;
            }

            if (!request.AllowsRawMedia && transport.CarriesRawMedia)
            {
                continue;
            }

            if (transport.TargetLatencyMilliseconds > request.MaximumLatencyMilliseconds)
            {
                continue;
            }

            if (transport.RequiredDocuments.Any(required => !request.AvailableDocuments.Contains(required)))
            {
                continue;
            }

            if (transport.RejectionConditions.Any(condition => request.Conditions.Contains(condition)))
            {
                continue;
            }

            return new MimirNetworkTransportSelection(transport, "selected", transport.Notes);
        }

        return new MimirNetworkTransportSelection(null, "no-match", "No configured transport satisfies payload, authority, latency, document, and condition constraints.");
    }
}
