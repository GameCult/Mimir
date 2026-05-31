using GameCult.Caching;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

[CultDocument("mimir.bioacoustic_codebook_state", "mimir.bioacoustic_codebook_state.v1")]
[MessagePackObject]
public sealed record MimirBioacousticCodebookState(
    [property: Key(0)]
    [property: CultName]
    string CodebookId,
    [property: Key(1)] string CreatedAtUtc,
    [property: Key(2)] int WordCount,
    [property: Key(3)] int SpeakerCount,
    [property: Key(4)] double SegmentSeconds,
    [property: Key(5)] string ScheduleId,
    [property: Key(6)] MimirBioacousticMotifSnapshot[] Motifs);

[MessagePackObject]
public sealed record MimirBioacousticMotifSnapshot(
    [property: Key(0)] int SymbolId,
    [property: Key(1)] string Speaker,
    [property: Key(2)] MimirBioacousticSyllableSnapshot[] Syllables);

[MessagePackObject]
public sealed record MimirBioacousticSyllableSnapshot(
    [property: Key(0)] double StartSeconds,
    [property: Key(1)] double DurationSeconds,
    [property: Key(2)] double StartHz,
    [property: Key(3)] double EndHz,
    [property: Key(4)] double Weight);

[CultDocument("mimir.bioacoustic_decoder_state", "mimir.bioacoustic_decoder_state.v1")]
[MessagePackObject]
public sealed record MimirBioacousticDecoderState(
    [property: Key(0)]
    [property: CultName]
    string DecoderId,
    [property: Key(1)] string CreatedAtUtc,
    [property: Key(2)] string CodebookId,
    [property: Key(3)] MimirBioacousticDecoderConfigurationSnapshot Configuration);

[MessagePackObject]
public sealed record MimirBioacousticDecoderConfigurationSnapshot(
    [property: Key(0)] string Id,
    [property: Key(1)] string Description,
    [property: Key(2)] int FftSize,
    [property: Key(3)] int HopSize,
    [property: Key(4)] int MelBins,
    [property: Key(5)] int CepstralCoefficients,
    [property: Key(6)] double MinFrequencyHz,
    [property: Key(7)] double MaxFrequencyHz,
    [property: Key(8)] int ProjectionTableCount,
    [property: Key(9)] int ProjectionHashBits,
    [property: Key(10)] int NearHashRadius,
    [property: Key(11)] double DenseStepSeconds,
    [property: Key(12)] double ProposalBudgetMultiplier,
    [property: Key(13)] MimirCepstralDegradationSnapshot[] TemplateAugmentations);

[MessagePackObject]
public sealed record MimirCepstralDegradationSnapshot(
    [property: Key(0)] string Id,
    [property: Key(1)] double WarpFrames,
    [property: Key(2)] double WarpCoefficients,
    [property: Key(3)] int BlurPasses);

[CultDocument("mimir.acoustic_path_state", "mimir.acoustic_path_state.v1")]
[MessagePackObject]
public sealed record MimirAcousticPathState(
    [property: Key(0)]
    [property: CultName]
    string PathId,
    [property: Key(1)] string UpdatedAtUtc,
    [property: Key(2)] string OutputSourceId,
    [property: Key(3)] string MicSourceId,
    [property: Key(4)] int SampleRate,
    [property: Key(5)] double DelaySamples,
    [property: Key(6)] double SamplingRateOffsetPpm,
    [property: Key(7)] double Confidence,
    [property: Key(8)] MimirBandResponseSnapshot[] BandResponses,
    [property: Key(9)] string EvidenceKind);

[MessagePackObject]
public sealed record MimirBandResponseSnapshot(
    [property: Key(0)] double CenterHz,
    [property: Key(1)] double Energy);

[CultDocument("mimir.actuator_state", "mimir.actuator_state.v1")]
[MessagePackObject]
public sealed record MimirActuatorStateDocument(
    [property: Key(0)]
    [property: CultName]
    string ActuatorId,
    [property: Key(1)] string UpdatedAtUtc,
    [property: Key(2)] string SourceId,
    [property: Key(3)] string ProfileId,
    [property: Key(4)] double TargetDelaySamples,
    [property: Key(5)] double ResampleRatio,
    [property: Key(6)] double Confidence,
    [property: Key(7)] MimirFaustControlSnapshot[] FaustControls);

[MessagePackObject]
public sealed record MimirFaustControlSnapshot(
    [property: Key(0)] string Path,
    [property: Key(1)] float Value);

[CultDocument("mimir.eve_dashboard_manifest", "mimir.eve_dashboard_manifest.v1")]
[MessagePackObject]
public sealed record MimirEveDashboardManifestDocument(
    [property: Key(0)]
    [property: CultName]
    string ProviderId,
    [property: Key(1)] string Title,
    [property: Key(2)] string Description,
    [property: Key(3)] string Version,
    [property: Key(4)] string Endpoint,
    [property: Key(5)] string[] Capabilities,
    [property: Key(6)] bool UsesCultMesh,
    [property: Key(7)] string Transport);

[CultDocument("mimir.eve_dashboard_state", "mimir.eve_dashboard_state.v1")]
[MessagePackObject]
public sealed record MimirEveDashboardStateDocument(
    [property: Key(0)]
    [property: CultName]
    string ProviderId,
    [property: Key(1)] string Title,
    [property: Key(2)] long Version,
    [property: Key(3)] string UpdatedAtUtc,
    [property: Key(4)] string SelectedNodeId,
    [property: Key(5)] string LutPreset,
    [property: Key(6)] MimirEveDashboardNodeSnapshot[] Nodes,
    [property: Key(7)] MimirEveDashboardSurfaceSnapshot? Surface);

[MessagePackObject]
public sealed record MimirEveDashboardNodeSnapshot(
    [property: Key(0)] string Id,
    [property: Key(1)] string Label,
    [property: Key(2)] string Kind,
    [property: Key(3)] bool Visible,
    [property: Key(4)] double X,
    [property: Key(5)] double Y,
    [property: Key(6)] double Z,
    [property: Key(7)] double Rotation,
    [property: Key(8)] double Scale,
    [property: Key(9)] double Width,
    [property: Key(10)] double Height,
    [property: Key(11)] string Health,
    [property: Key(12)] string? ProviderId,
    [property: Key(13)] string? Command,
    [property: Key(14)] string? Endpoint);

[MessagePackObject]
public sealed record MimirEveDashboardSurfaceSnapshot(
    [property: Key(0)] string Schema,
    [property: Key(1)] string Id,
    [property: Key(2)] string Title,
    [property: Key(3)] MimirEveDashboardUiElementSnapshot Root,
    [property: Key(4)] MimirEveDashboardSurfaceAssetSnapshot[] Assets);

[MessagePackObject]
public sealed record MimirEveDashboardSurfaceAssetSnapshot(
    [property: Key(0)] string Id,
    [property: Key(1)] string Kind,
    [property: Key(2)] string Uri);

[MessagePackObject]
public sealed record MimirEveDashboardUiElementSnapshot(
    [property: Key(0)] string Id,
    [property: Key(1)] string Kind,
    [property: Key(2)] string? Role,
    [property: Key(3)] string? Text,
    [property: Key(4)] string? AssetRef,
    [property: Key(5)] string? AssetUri,
    [property: Key(6)] string? BindNodeId,
    [property: Key(7)] string? CommandId,
    [property: Key(8)] MimirEveDashboardUiLayoutSnapshot? Layout,
    [property: Key(9)] MimirEveDashboardUiStyleSnapshot? Style,
    [property: Key(10)] MimirEveDashboardUiMetricSnapshot? Metric,
    [property: Key(11)] MimirEveDashboardUiElementSnapshot[] Children);

[MessagePackObject]
public sealed record MimirEveDashboardUiLayoutSnapshot(
    [property: Key(0)] string Direction,
    [property: Key(1)] double? Width,
    [property: Key(2)] double? Height,
    [property: Key(3)] double? Grow,
    [property: Key(4)] double? Gap,
    [property: Key(5)] double? Padding,
    [property: Key(6)] string? Overflow);

[MessagePackObject]
public sealed record MimirEveDashboardUiStyleSnapshot(
    [property: Key(0)] string Variant,
    [property: Key(1)] string? Tone);

[MessagePackObject]
public sealed record MimirEveDashboardUiMetricSnapshot(
    [property: Key(0)] string Label,
    [property: Key(1)] double Value,
    [property: Key(2)] string Tone);

[CultDocument("mimir.eve_dashboard_command", "mimir.eve_dashboard_command.v1")]
[MessagePackObject]
public sealed record MimirEveDashboardCommandDocument(
    [property: Key(0)]
    [property: CultName]
    string CommandId,
    [property: Key(1)] string DeviceId,
    [property: Key(2)] string ClientId,
    [property: Key(3)] string ProviderId,
    [property: Key(4)] string Type,
    [property: Key(5)] string NodeId,
    [property: Key(6)] double? X,
    [property: Key(7)] double? Y,
    [property: Key(8)] double? Rotation,
    [property: Key(9)] double? Scale,
    [property: Key(10)] bool? Visible,
    [property: Key(11)] long Sequence,
    [property: Key(12)] long DeviceTimestampNs);

[CultDocument("mimir.eve_sensor_observation", "mimir.eve_sensor_observation.v1")]
[MessagePackObject]
public sealed record MimirEveSensorObservationDocument(
    [property: Key(0)]
    [property: CultName]
    string ObservationId,
    [property: Key(1)] string DeviceId,
    [property: Key(2)] string StreamId,
    [property: Key(3)] string Kind,
    [property: Key(4)] long Sequence,
    [property: Key(5)] long SensorTimestampNs,
    [property: Key(6)] long ElapsedRealtimeNs,
    [property: Key(7)] string WallClockUtc,
    [property: Key(8)] string ClockDomainId,
    [property: Key(9)] double[] Values,
    [property: Key(10)] string? Action,
    [property: Key(11)] int? PointerCount,
    [property: Key(12)] double? X,
    [property: Key(13)] double? Y,
    [property: Key(14)] int? Accuracy);

[CultDocument("mimir.eve_media_observation", "mimir.eve_media_observation.v1")]
[MessagePackObject]
public sealed record MimirEveMediaObservationDocument(
    [property: Key(0)]
    [property: CultName]
    string ObservationId,
    [property: Key(1)] string DeviceId,
    [property: Key(2)] string StreamId,
    [property: Key(3)] string Kind,
    [property: Key(4)] long Sequence,
    [property: Key(5)] long SensorTimestampNs,
    [property: Key(6)] long ElapsedRealtimeNs,
    [property: Key(7)] string WallClockUtc,
    [property: Key(8)] string ClockDomainId,
    [property: Key(9)] string Format,
    [property: Key(10)] int? Width,
    [property: Key(11)] int? Height,
    [property: Key(12)] int? SampleRate,
    [property: Key(13)] int? Channels,
    [property: Key(14)] int? FrameCount,
    [property: Key(15)] string PayloadEncoding,
    [property: Key(16)] byte[] Payload);

[CultDocument("mimir.local_frustum_hint_state", "mimir.local_frustum_hint_state.v1")]
[MessagePackObject]
public sealed record MimirLocalFrustumHintStateDocument(
    [property: Key(0)]
    [property: CultName]
    string HintId,
    [property: Key(1)] string UpdatedAtUtc,
    [property: Key(2)] string ProducerNodeId,
    [property: Key(3)] string CalibrationId,
    [property: Key(4)] string CandidateKey,
    [property: Key(5)] string MarkerSetId,
    [property: Key(6)] double MeanReprojectionErrorClip,
    [property: Key(7)] double Confidence,
    [property: Key(8)] MimirLocalFrustumSnapshot[] Frustums);

[MessagePackObject]
public sealed record MimirLocalFrustumSnapshot(
    [property: Key(0)] string SourceId,
    [property: Key(1)] float PositionX,
    [property: Key(2)] float PositionY,
    [property: Key(3)] float PositionZ,
    [property: Key(4)] float RotationX,
    [property: Key(5)] float RotationY,
    [property: Key(6)] float RotationZ,
    [property: Key(7)] float RotationW,
    [property: Key(8)] double HorizontalTanHalfFov,
    [property: Key(9)] double VerticalTanHalfFov,
    [property: Key(10)] int UsedPointCount,
    [property: Key(11)] double MeanReprojectionErrorClip,
    [property: Key(12)] double Confidence);

public static class MimirCultMeshContractFactory
{
    public static MimirBioacousticCodebookState CreateCodebookState(
        string codebookId,
        string scheduleId,
        MimirBioacousticTimeline timeline,
        DateTimeOffset? createdAt = null) =>
        new(
            codebookId,
            (createdAt ?? DateTimeOffset.UtcNow).ToString("O"),
            MimirBioacousticTimeline.WordCount,
            MimirBioacousticTimeline.SpeakerCount,
            MimirBioacousticTimeline.SegmentSeconds,
            scheduleId,
            timeline.Codebook.Select(motif => new MimirBioacousticMotifSnapshot(
                motif.SymbolId,
                (motif.SymbolId & 1) == 0 ? "left" : "right",
                motif.Syllables
                    .Select(syllable => new MimirBioacousticSyllableSnapshot(
                        syllable.StartSeconds,
                        syllable.DurationSeconds,
                        syllable.StartHz,
                        syllable.EndHz,
                        syllable.Weight))
                    .ToArray()))
                .ToArray());

    public static MimirBioacousticDecoderState CreateDecoderState(
        string decoderId,
        string codebookId,
        MimirBioacousticDecoderConfiguration configuration,
        DateTimeOffset? createdAt = null) =>
        new(
            decoderId,
            (createdAt ?? DateTimeOffset.UtcNow).ToString("O"),
            codebookId,
            Snapshot(configuration));

    public static MimirAcousticPathState CreatePathState(MimirAudioSynchronizationState state, string outputSourceId, string evidenceKind) =>
        new(
            $"{outputSourceId}->{state.SourceId}",
            DateTimeOffset.UtcNow.ToString("O"),
            outputSourceId,
            state.SourceId,
            state.SampleRate,
            state.SmoothedDelaySamples,
            state.SamplingRateOffsetPpm,
            state.Confidence,
            state.BandResponses
                .Select(response => new MimirBandResponseSnapshot(response.CenterHz, response.Energy))
                .ToArray(),
            evidenceKind);

    public static MimirActuatorStateDocument CreateActuatorState(
        string actuatorId,
        string profileId,
        MimirActuatorCommand command,
        DateTimeOffset? updatedAt = null) =>
        new(
            actuatorId,
            (updatedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            command.SourceId,
            profileId,
            command.TargetDelaySamples,
            command.ResampleRatio,
            command.Confidence,
            command.FaustControls
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new MimirFaustControlSnapshot(pair.Key, pair.Value))
                .ToArray());

    public static MimirLocalFrustumHintStateDocument CreateLocalFrustumHintState(
        string hintId,
        string producerNodeId,
        MimirCameraFrustumSolveFrame frame,
        DateTimeOffset? updatedAt = null) =>
        new(
            hintId,
            (updatedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            producerNodeId,
            frame.CalibrationId,
            frame.CandidateKey,
            frame.MarkerSetId,
            frame.MeanReprojectionErrorClip,
            frame.Confidence,
            frame.FrustumUpdates
                .OrderBy(update => update.SourceId, StringComparer.Ordinal)
                .Select(update => new MimirLocalFrustumSnapshot(
                    update.SourceId,
                    update.EstimatedPositionMeters.X,
                    update.EstimatedPositionMeters.Y,
                    update.EstimatedPositionMeters.Z,
                    update.EstimatedCameraToWorldRotation.X,
                    update.EstimatedCameraToWorldRotation.Y,
                    update.EstimatedCameraToWorldRotation.Z,
                    update.EstimatedCameraToWorldRotation.W,
                    update.HorizontalTanHalfFov,
                    update.VerticalTanHalfFov,
                    update.UsedPointCount,
                    update.MeanReprojectionErrorClip,
                    update.Confidence))
                .ToArray());

    private static MimirBioacousticDecoderConfigurationSnapshot Snapshot(MimirBioacousticDecoderConfiguration configuration) =>
        new(
            configuration.Id,
            configuration.Description,
            configuration.FftSize,
            configuration.HopSize,
            configuration.MelBins,
            configuration.CepstralCoefficients,
            configuration.MinFrequencyHz,
            configuration.MaxFrequencyHz,
            configuration.ProjectionTableCount,
            configuration.ProjectionHashBits,
            configuration.NearHashRadius,
            configuration.DenseStepSeconds,
            configuration.ProposalBudgetMultiplier,
            configuration.TemplateAugmentations
                .Select(profile => new MimirCepstralDegradationSnapshot(
                    profile.Id,
                    profile.WarpFrames,
                    profile.WarpCoefficients,
                    profile.BlurPasses))
                .ToArray());
}
