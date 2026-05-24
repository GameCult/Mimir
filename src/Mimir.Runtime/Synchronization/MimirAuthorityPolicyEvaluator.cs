namespace Mimir.Runtime.Synchronization;

public sealed record MimirAuthorityEvaluationInput(
    string AppliesTo,
    IReadOnlySet<string> Evidence,
    IReadOnlySet<string> Conditions);

public sealed record MimirAuthorityEvaluationResult(
    MimirAuthorityDecision Decision,
    string RuleId,
    string Notes);

public sealed class MimirAuthorityPolicyEvaluator(MimirAuthorityPolicyConfiguration? policy = null)
{
    private readonly MimirAuthorityPolicyConfiguration policy = policy ?? MimirAuthorityPolicyConfigurations.DefaultTimingPolicy;

    public MimirAuthorityEvaluationResult Evaluate(MimirAuthorityEvaluationInput input)
    {
        MimirAuthorityRule? wildcard = null;
        foreach (var rule in policy.Rules)
        {
            if (rule.AppliesTo == "*")
            {
                wildcard ??= rule;
                continue;
            }

            if (!string.Equals(rule.AppliesTo, input.AppliesTo, StringComparison.Ordinal))
            {
                continue;
            }

            if (RuleMatches(rule, input))
            {
                return new MimirAuthorityEvaluationResult(rule.Decision, rule.Id, rule.Notes);
            }
        }

        if (wildcard != null && RuleMatches(wildcard, input))
        {
            return new MimirAuthorityEvaluationResult(wildcard.Decision, wildcard.Id, wildcard.Notes);
        }

        return new MimirAuthorityEvaluationResult(
            MimirAuthorityDecision.ObserveOnly,
            "default-observe-only",
            "No trust rule matched; preserve observation without authority.");
    }

    private static bool RuleMatches(MimirAuthorityRule rule, MimirAuthorityEvaluationInput input)
    {
        foreach (var forbidden in rule.ForbiddenConditions)
        {
            if (input.Conditions.Contains(forbidden))
            {
                return false;
            }
        }

        foreach (var required in rule.RequiredEvidence)
        {
            if (!input.Evidence.Contains(required))
            {
                return false;
            }
        }

        return true;
    }
}
