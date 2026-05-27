namespace Mimir.Runtime.Synchronization;

public sealed record MimirAlignmentActuatorProfile(
    string Id,
    string FaustDspPath,
    int SourceCount,
    double MaxDelaySamples,
    string DelayControlFormat,
    string GainControlFormat)
{
    public static MimirAlignmentActuatorProfile SixSourceFaust { get; } = new(
        "six-source-faust-fractional-delay",
        "faust/mimir_alignment_actuator.dsp",
        SourceCount: 6,
        MaxDelaySamples: 4096.0,
        DelayControlFormat: "source{0}/delay_samples",
        GainControlFormat: "source{0}/gain");
}

public sealed record MimirActuatorCommand(
    string SourceId,
    double TargetDelaySamples,
    double ResampleRatio,
    double Confidence,
    IReadOnlyDictionary<string, float> FaustControls);

public sealed record MimirActuatorFrame(
    string ReferenceSourceId,
    double ReferenceHoldbackSamples,
    IReadOnlyList<MimirActuatorCommand> Commands,
    int TruncatedSourceCount)
{
    public static MimirActuatorFrame Empty { get; } = new("", 0.0, [], 0);
}

public sealed record MimirActuatorControllerOptions(
    double MinimumConfidence = 0.05,
    double MinimumDtSeconds = 0.001,
    double DelayAlphaMin = 0.005,
    double DelayAlphaMax = 0.05,
    double SroBetaMin = 0.0005,
    double SroBetaMax = 0.006,
    double MaxSroPpm = 300.0);

public sealed class MimirSroPllActuatorController(
    MimirAlignmentActuatorProfile? profile = null,
    MimirActuatorControllerOptions? options = null)
{
    private readonly MimirAlignmentActuatorProfile profile = profile ?? MimirAlignmentActuatorProfile.SixSourceFaust;
    private readonly MimirActuatorControllerOptions options = options ?? new();
    private readonly Dictionary<string, ActuatorState> states = new(StringComparer.Ordinal);

    public MimirActuatorCommand Update(string sourceId, int sourceIndex, double delaySamples, double confidence, double dtSeconds)
    {
        if (!states.TryGetValue(sourceId, out var state))
        {
            state = new ActuatorState();
            states[sourceId] = state;
        }

        if (confidence >= options.MinimumConfidence && dtSeconds >= options.MinimumDtSeconds)
        {
            var error = delaySamples - state.DelaySamples;
            var alpha = Math.Clamp(confidence * 0.08, options.DelayAlphaMin, options.DelayAlphaMax);
            var beta = Math.Clamp(confidence * 0.01, options.SroBetaMin, options.SroBetaMax);
            state.DelaySamples += alpha * error;
            state.SroPpm = Math.Clamp(
                state.SroPpm + beta * error / dtSeconds,
                -options.MaxSroPpm,
                options.MaxSroPpm);
            state.Confidence += (confidence - state.Confidence) * 0.05;
        }
        else
        {
            state.Confidence *= 0.995;
        }

        var targetDelay = Math.Clamp(state.DelaySamples, 0.0, profile.MaxDelaySamples);
        var resampleRatio = 1.0 - state.SroPpm * 1.0e-6;
        var controls = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            [string.Format(profile.DelayControlFormat, Math.Clamp(sourceIndex, 0, profile.SourceCount - 1))] = (float)targetDelay,
            [string.Format(profile.GainControlFormat, Math.Clamp(sourceIndex, 0, profile.SourceCount - 1))] = 1.0f
        };
        return new MimirActuatorCommand(sourceId, targetDelay, resampleRatio, state.Confidence, controls);
    }

    public MimirActuatorCommand Update(string sourceId, int sourceIndex, MimirAudioSynchronizationState state, double dtSeconds) =>
        Update(sourceId, sourceIndex, state.SmoothedDelaySamples, state.Confidence, dtSeconds);

    private sealed class ActuatorState
    {
        public double DelaySamples { get; set; }
        public double SroPpm { get; set; }
        public double Confidence { get; set; }
    }
}

public sealed class MimirAlignmentActuatorBank(
    MimirAlignmentActuatorProfile? profile = null,
    MimirSroPllActuatorController? controller = null)
{
    private readonly MimirAlignmentActuatorProfile profile = profile ?? MimirAlignmentActuatorProfile.SixSourceFaust;
    private readonly MimirSroPllActuatorController controller = controller ?? new(profile);
    private readonly Dictionary<string, int> sourceSlots = new(StringComparer.Ordinal);

    public MimirActuatorFrame Update(IReadOnlyList<MimirAudioSynchronizationState> states, double dtSeconds)
    {
        if (states.Count == 0)
        {
            return MimirActuatorFrame.Empty;
        }

        var ordered = states
            .Where(state => !string.IsNullOrWhiteSpace(state.SourceId))
            .OrderBy(state => state.SourceId, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            return MimirActuatorFrame.Empty;
        }

        var referenceSourceId = ordered[0].ReferenceSourceId;
        var referenceHoldbackSamples = Math.Clamp(
            ordered.Max(state => Math.Max(0.0, state.SmoothedDelaySamples)),
            0.0,
            profile.MaxDelaySamples);
        var commands = new List<MimirActuatorCommand>(Math.Min(ordered.Length, profile.SourceCount));
        var truncatedSourceCount = 0;
        foreach (var state in ordered)
        {
            if (!sourceSlots.TryGetValue(state.SourceId, out var sourceIndex))
            {
                if (sourceSlots.Count >= profile.SourceCount)
                {
                    truncatedSourceCount++;
                    continue;
                }

                sourceIndex = sourceSlots.Count;
                sourceSlots[state.SourceId] = sourceIndex;
            }

            var holdbackSamples = Math.Clamp(
                referenceHoldbackSamples - state.SmoothedDelaySamples,
                0.0,
                profile.MaxDelaySamples);
            commands.Add(controller.Update(
                state.SourceId,
                sourceIndex,
                holdbackSamples,
                state.Confidence,
                dtSeconds));
        }

        return new MimirActuatorFrame(
            referenceSourceId,
            referenceHoldbackSamples,
            commands,
            truncatedSourceCount);
    }
}
