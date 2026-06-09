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

[CultDocument("mimir.program_scene", "mimir.program_scene.v1")]
[MessagePackObject]
public sealed record MimirProgramSceneDocument(
    [property: Key(0)]
    [property: CultName]
    string SceneId,
    [property: Key(1)] string UpdatedAtUtc,
    [property: Key(2)] int CanvasWidth,
    [property: Key(3)] int CanvasHeight,
    [property: Key(4)] string Owner,
    [property: Key(5)] MimirProgramSceneLayer[] Layers);

[MessagePackObject]
public sealed record MimirProgramSceneLayer(
    [property: Key(0)] string LayerId,
    [property: Key(1)] string SourceRef,
    [property: Key(2)] string SourceKind,
    [property: Key(3)] bool Visible,
    [property: Key(4)] double X,
    [property: Key(5)] double Y,
    [property: Key(6)] double Width,
    [property: Key(7)] double Height,
    [property: Key(8)] int ZIndex,
    [property: Key(9)] MimirProgramCrop Crop,
    [property: Key(10)] MimirProgramChromaKey? ChromaKey);

[MessagePackObject]
public sealed record MimirProgramCrop(
    [property: Key(0)] double Left,
    [property: Key(1)] double Top,
    [property: Key(2)] double Right,
    [property: Key(3)] double Bottom);

[MessagePackObject]
public sealed record MimirProgramChromaKey(
    [property: Key(0)] uint KeyColorRgba,
    [property: Key(1)] double Similarity,
    [property: Key(2)] double Smoothness,
    [property: Key(3)] double Spill);

[CultDocument("mimir.program_output", "mimir.program_output.v1")]
[MessagePackObject]
public sealed record MimirProgramOutputDocument(
    [property: Key(0)]
    [property: CultName]
    string OutputId,
    [property: Key(1)] string UpdatedAtUtc,
    [property: Key(2)] string SceneId,
    [property: Key(3)] string VideoSurface,
    [property: Key(4)] string AudioBus,
    [property: Key(5)] string PublisherRoute,
    [property: Key(6)] bool DiagnosticOnly);

[CultDocument("mimir.eve_operator_surface", "mimir.eve_operator_surface.v1")]
[MessagePackObject]
public sealed record MimirEveOperatorSurfaceDocument(
    [property: Key(0)]
    [property: CultName]
    string SurfaceId,
    [property: Key(1)] string UpdatedAtUtc,
    [property: Key(2)] string SceneId,
    [property: Key(3)] bool CanPreviewProgram,
    [property: Key(4)] bool CanEditComposition,
    [property: Key(5)] bool CanSelectSources,
    [property: Key(6)] bool CanShowStats,
    [property: Key(7)] string[] CommandTopics);

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

    public static MimirProgramSceneDocument CreateObsSceneMirror(
        string sceneId,
        DateTimeOffset? updatedAt = null) =>
        new(
            sceneId,
            (updatedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            CanvasWidth: 1920,
            CanvasHeight: 1080,
            Owner: "Mimir",
            Layers:
            [
                new(
                    "starfire-monitor",
                    "muninn:starfire:monitor:primary",
                    "monitor",
                    Visible: true,
                    X: 0,
                    Y: 0,
                    Width: 1920,
                    Height: 1080,
                    ZIndex: 0,
                    new MimirProgramCrop(Left: 0, Top: 0, Right: 0, Bottom: 48),
                    ChromaKey: null),
                new(
                    "discord-chat",
                    "muninn:starfire:window:discord",
                    "window",
                    Visible: true,
                    X: 1225.5,
                    Y: 0.5,
                    Width: 1012.2,
                    Height: 569.4,
                    ZIndex: 10,
                    new MimirProgramCrop(Left: 350, Top: 38, Right: 271, Bottom: 87),
                    new MimirProgramChromaKey(0xff2b7c5a, Similarity: 1, Smoothness: 2, Spill: 1)),
                new(
                    "raven-monitor",
                    "muninn:raven:monitor:primary",
                    "network-monitor",
                    Visible: true,
                    X: 0,
                    Y: 0,
                    Width: 2560,
                    Height: 1440,
                    ZIndex: 20,
                    new MimirProgramCrop(Left: 0, Top: 0, Right: 0, Bottom: 0),
                    ChromaKey: null)
            ]);

    public static MimirProgramOutputDocument CreateProgramOutput(
        string outputId,
        string sceneId,
        MimirProgramPublicationConfiguration publication,
        DateTimeOffset? updatedAt = null) =>
        new(
            outputId,
            (updatedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            sceneId,
            publication.VideoSurfaceName,
            publication.AudioKind.ToString(),
            publication.SitePublisherRoute,
            publication.DiagnosticOnly);

    public static MimirEveOperatorSurfaceDocument CreateOperatorSurface(
        string surfaceId,
        string sceneId,
        MimirProgramControlSurface surface,
        DateTimeOffset? updatedAt = null) =>
        new(
            surfaceId,
            (updatedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            sceneId,
            surface.CanPreviewProgram,
            surface.CanEditComposition,
            surface.CanSelectSources,
            surface.CanShowStats,
            CommandTopics:
            [
                "mimir.program.scene.select",
                "mimir.program.layer.transform",
                "mimir.program.layer.crop",
                "mimir.program.layer.chroma_key",
                "mimir.program.source.subscribe",
                "mimir.program.output.publish"
            ]);

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
