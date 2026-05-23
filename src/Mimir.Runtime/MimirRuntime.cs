using Aquarium.Engine;
using Aquarium.Engine.Audio;
using Aquarium.Engine.Input;
using Aquarium.Engine.Render;
using Aquarium.Engine.Ui;
using Aquarium.Fensalir;
using Mimir.Runtime.Synchronization;
using System.Diagnostics;

namespace Mimir.Runtime;

public sealed class MimirRuntime : IAquariumRuntime
{
    private const float DefaultAudioSyncUpdateIntervalSeconds = 0.1f;
    private const double HybridPassiveConfidenceThreshold = 0.12;
    private const double CalibrationStartSeconds = 0.5;
    private const int CalibrationBatchSegments = 120;
    private const int HybridWatermarkIntervalSegments = 4;
    private readonly IAquariumRuntime visualRuntime;
    private readonly MimirSynchronizationHub synchronization;
    private readonly AquariumUiDocument ui;
    private readonly MimirAudioSynchronizationSettings audioSyncSettings;
    private readonly float telemetryIntervalSeconds;
    private readonly float audioSyncUpdateIntervalSeconds;
    private readonly float calibrationGain;
    private readonly float watermarkGain;
    private int lastPollCount;
    private float runtimeSeconds;
    private float nextAudioSyncSeconds;
    private float nextTelemetrySeconds;
    private ulong calibrationSegmentIndex;
    private long lastHybridWatermarkSegment = -1;
    private double lastAudioSyncAnalysisMilliseconds;
    private double lastPassiveSynchronizationConfidence;
    private IReadOnlyList<MimirAudioSynchronizationReport> lastAudioSynchronizationReports = [];

    public MimirRuntime(AquariumRuntimeOptions options)
        : this(options, MimirRuntimeConfiguration.Load())
    {
    }

    public MimirRuntime(AquariumRuntimeOptions options, MimirRuntimeConfiguration configuration)
        : this(options, configuration.Settings, configuration.Sources)
    {
    }

    public MimirRuntime(AquariumRuntimeOptions options, MimirSynchronizationSettings settings)
        : this(options, settings, [])
    {
    }

    public MimirRuntime(
        AquariumRuntimeOptions options,
        MimirSynchronizationSettings settings,
        IEnumerable<IMimirStreamSource> streamSources)
    {
        Options = options;
        visualRuntime = new FensalirRuntimeFactory().Create(options);
        synchronization = new MimirSynchronizationHub(settings);
        audioSyncSettings = settings.Audio;
        telemetryIntervalSeconds = ParseTelemetryIntervalSeconds();
        audioSyncUpdateIntervalSeconds = ParseAudioSyncIntervalSeconds();
        calibrationGain = settings.Audio.CalibrationGain;
        watermarkGain = settings.Audio.WatermarkGain;
        nextAudioSyncSeconds = audioSyncUpdateIntervalSeconds;
        nextTelemetrySeconds = telemetryIntervalSeconds;
        foreach (var source in streamSources)
        {
            synchronization.AddSource(source);
        }

        ui = CreateUi();
    }

    public AquariumRuntimeOptions Options { get; }

    public AquariumFrame Frame => visualRuntime.Frame;

    public GraphicsSettings GraphicsSettings
    {
        get => visualRuntime.GraphicsSettings;
        set => visualRuntime.GraphicsSettings = value;
    }

    public AquariumRenderPlan RenderPlan => visualRuntime.RenderPlan;

    public AquariumUiDocument Ui => ui;

    public AquariumAudioDocument Audio => visualRuntime.Audio;

    public AquariumSynthDocument Synth => visualRuntime.Synth;

    public void RegisterStreamSource(IMimirStreamSource source)
    {
        synchronization.AddSource(source);
    }

    public void Start()
    {
        Console.WriteLine($"Mimir runtime sync buffers: {synchronization.Summary()} @ {synchronization.Settings.BufferDuration.TotalSeconds:0.###}s audioSync={audioSyncSettings.Mode} reference={audioSyncSettings.ReferenceSourceId}");
        visualRuntime.Start();
    }

    public void Update(float deltaSeconds, InputState input)
    {
        runtimeSeconds += Math.Max(deltaSeconds, 0.0f);
        QueueCalibrationTimeline();
        lastPollCount = synchronization.PollSources();
        UpdateAudioSynchronization();
        EmitTelemetry();
        visualRuntime.Update(deltaSeconds, input);
    }

    public AquariumFrame ComposeFrame(AquariumFrame frame, AquariumFrameInput input)
    {
        return visualRuntime.ComposeFrame(frame, input);
    }

    public void FlushState()
    {
        visualRuntime.FlushState();
    }

    public void Dispose()
    {
        synchronization.Dispose();
        visualRuntime.Dispose();
    }

    private AquariumUiDocument CreateUi()
    {
        return new AquariumUiDocument()
            .Panel("Mimir Sync", 18.0f, 82.0f, 390.0f, panel =>
            {
                panel.Section("Rolling Buffers");
                panel.Readout("Window", () => $"{synchronization.Settings.BufferDuration.TotalSeconds:0.###}s");
                panel.Readout("Streams", synchronization.Summary);
                panel.Readout("Sources", () => $"{synchronization.SourceCount}");
                panel.Readout("Last poll", () => $"{lastPollCount} samples");
                panel.Readout("Ingested", () => $"{synchronization.IngestedSamples}");
                panel.Readout("Buffer details", DescribeBuffers);
                panel.Readout("Audio sync", DescribeAudioSync);
                panel.Readout("Audio sync state", DescribeAudioSyncState);
                panel.Readout("Aligned audio", DescribeAlignedAudio);
                panel.Readout("Chirplet reference", DescribeChirpletReference);
            });
    }

    private void QueueCalibrationTimeline()
    {
        if (!ShouldEmitCalibrationTimeline())
        {
            return;
        }

        var currentSegmentIndex = CurrentCalibrationSegmentIndex();
        if (audioSyncSettings.Mode == MimirAudioSyncMode.Hybrid)
        {
            calibrationSegmentIndex = currentSegmentIndex;
            if (calibrationSegmentIndex % HybridWatermarkIntervalSegments != 0UL ||
                (long)calibrationSegmentIndex == lastHybridWatermarkSegment)
            {
                return;
            }
        }
        else if (calibrationSegmentIndex + 1UL < currentSegmentIndex)
        {
            calibrationSegmentIndex = currentSegmentIndex;
        }

        var nextSegmentSeconds = CalibrationStartSeconds + calibrationSegmentIndex * MimirChirpletTimeline.SegmentSeconds;
        if (runtimeSeconds < nextSegmentSeconds)
        {
            return;
        }

        var segmentCount = audioSyncSettings.Mode == MimirAudioSyncMode.Hybrid ? 1 : CalibrationBatchSegments;
        var outputGain = audioSyncSettings.Mode == MimirAudioSyncMode.Hybrid ? watermarkGain : calibrationGain;
        var batch = RenderCalibrationBatchPcm16Base64(
            calibrationSegmentIndex,
            segmentCount,
            UseChirpBinTimeline(audioSyncSettings.Mode),
            out var peak);
        visualRuntime.Audio.EnqueuePcm16Base64(
            batch,
            MimirChirpletTimeline.SampleRate,
            channels: 1,
            gain: outputGain);
        Console.WriteLine(
            $"mimir-chirplet-batch mode={DescribeAudioSyncMode()} firstSegment={calibrationSegmentIndex} segments={segmentCount} seconds={segmentCount * MimirChirpletTimeline.SegmentSeconds:0.00} peak={peak:0.000000} gain={outputGain:0.###} base64Bytes={batch.Length}");
        if (audioSyncSettings.Mode == MimirAudioSyncMode.Hybrid)
        {
            lastHybridWatermarkSegment = (long)calibrationSegmentIndex;
        }
        else
        {
            calibrationSegmentIndex += (ulong)segmentCount;
        }
    }

    private ulong CurrentCalibrationSegmentIndex()
    {
        if (runtimeSeconds <= CalibrationStartSeconds)
        {
            return 0UL;
        }

        return (ulong)Math.Floor((runtimeSeconds - CalibrationStartSeconds) / MimirChirpletTimeline.SegmentSeconds);
    }

    private string DescribeChirpletReference()
    {
        if (!ShouldEmitCalibrationTimeline())
        {
            return $"{audioSyncSettings.ReferenceSourceId} passive mode; calibration emission disabled";
        }

        var emittedUntilSeconds = calibrationSegmentIndex * MimirChirpletTimeline.SegmentSeconds;
        return $"{audioSyncSettings.ReferenceSourceId} {DescribeAudioSyncMode()} timeline {MimirChirpletTimeline.SegmentSeconds:0.00}s segments emitted to {emittedUntilSeconds:0.00}s passiveConfidence={lastPassiveSynchronizationConfidence:0.000}";
    }

    private static string RenderCalibrationBatchPcm16Base64(
        ulong firstSegment,
        int segmentCount,
        bool useChirpBinTimeline,
        out float peak)
    {
        var samplesPerSegment = (int)Math.Round(MimirChirpletTimeline.SegmentSeconds * MimirChirpletTimeline.SampleRate);
        var bytes = new byte[samplesPerSegment * Math.Max(1, segmentCount) * sizeof(short)];
        var byteIndex = 0;
        peak = 0.0f;
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var samples = useChirpBinTimeline
                ? MimirChirpBinTimeline.Default.RenderSegmentMonoFloat(firstSegment + (ulong)segment)
                : MimirChirpletTimeline.Default.RenderSegmentMonoFloat(firstSegment + (ulong)segment);
            for (var index = 0; index < samples.Length; index++)
            {
                peak = Math.Max(peak, Math.Abs(samples[index]));
                var sample = (short)Math.Round(Math.Clamp(samples[index], -1.0f, 1.0f) * short.MaxValue);
                bytes[byteIndex++] = (byte)(sample & 0xff);
                bytes[byteIndex++] = (byte)((sample >> 8) & 0xff);
            }
        }

        return Convert.ToBase64String(bytes);
    }

    private static bool UseChirpBinTimeline(MimirAudioSyncMode mode)
    {
        return mode is MimirAudioSyncMode.ChirpOnly or MimirAudioSyncMode.Hybrid;
    }

    private string DescribeBuffers()
    {
        return string.Join(" | ", synchronization.Buffers.Buffers.Select(DescribeBuffer));
    }

    private void UpdateAudioSynchronization()
    {
        if (runtimeSeconds < nextAudioSyncSeconds)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        synchronization.AnalyzeAudioSynchronizationStep(audioSyncSettings.ReferenceSourceId, audioSyncSettings.Mode);
        stopwatch.Stop();
        lastAudioSyncAnalysisMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        lastAudioSynchronizationReports = synchronization.AudioSynchronizationReports;
        UpdatePassiveSynchronizationConfidence(lastAudioSynchronizationReports);
        nextAudioSyncSeconds = runtimeSeconds + audioSyncUpdateIntervalSeconds;
    }

    private void EmitTelemetry()
    {
        if (telemetryIntervalSeconds <= 0.0f || runtimeSeconds < nextTelemetrySeconds)
        {
            return;
        }

        var loopback = synchronization.Buffers.Buffers.FirstOrDefault(buffer =>
            string.Equals(buffer.Descriptor.SourceId, audioSyncSettings.ReferenceSourceId, StringComparison.Ordinal));
        var states = synchronization.AudioSynchronizationStates;
        Console.WriteLine(
            $"mimir-sync-telemetry t={runtimeSeconds:0.00}s audioSync={audioSyncSettings.Mode} loopbackCount={loopback?.Count ?? 0} loopbackEdgeNs={loopback?.EdgeNs ?? 0} reports={lastAudioSynchronizationReports.Count} states={states.Count} analyzeMs={lastAudioSyncAnalysisMilliseconds:0.0} aligned={DescribeAlignedAudio()}");
        Console.WriteLine($"mimir-sync-buffers {DescribeAudioBuffers()}");
        foreach (var report in lastAudioSynchronizationReports.OrderBy(report => report.SourceId, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"mimir-sync-report {report.ReferenceSourceId}->{report.SourceId} evidence={report.EvidenceKind} delaySamples={report.FractionalDelaySamples:0.000000} delayUs={report.DelayMicroseconds:0.000} delayMs={report.DelayMilliseconds:0.000} confidence={report.Confidence:0.000} timelineEvents={report.TimelineMatchedEvents} timelineConfidence={report.TimelineConfidence:0.000}");
        }

        foreach (var state in states)
        {
            Console.WriteLine(
                $"mimir-sync-state {state.ReferenceSourceId}->{state.SourceId} delaySamples={state.SmoothedDelaySamples:0.000000} delayUs={state.DelayMicroseconds:0.000} delayMs={state.DelayMilliseconds:0.000} sroPpm={state.SamplingRateOffsetPpm:0.000} confidence={state.Confidence:0.000}");
        }

        foreach (var trace in synchronization.AudioSynchronizationDecodeTraces.OrderBy(trace => trace.SourceId, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"mimir-sync-decode {trace.ReferenceSourceId}->{trace.SourceId} status={trace.Status} compared={trace.ComparedSamples} rate={trace.SampleRate} refFrames={trace.ReferenceFrames} refAnchors={trace.ReferenceAnchors} refClock={trace.ReferenceClockConfidence:0.000} refEnergy={trace.ReferenceBestEnergy:0.000} candFrames={trace.CandidateFrames} candAnchors={trace.CandidateAnchors} candClock={trace.CandidateClockConfidence:0.000} candEnergy={trace.CandidateBestEnergy:0.000} matched={trace.MatchedEvents} confidence={trace.Confidence:0.000}");
        }

        nextTelemetrySeconds += telemetryIntervalSeconds;
    }

    private static float ParseTelemetryIntervalSeconds()
    {
        return float.TryParse(Environment.GetEnvironmentVariable("MIMIR_SYNC_TELEMETRY_SECONDS"), out var seconds)
            ? Math.Clamp(seconds, 0.0f, 60.0f)
            : 0.0f;
    }

    private static float ParseAudioSyncIntervalSeconds()
    {
        return float.TryParse(Environment.GetEnvironmentVariable("MIMIR_AUDIO_SYNC_INTERVAL_SECONDS"), out var seconds)
            ? Math.Clamp(seconds, 0.1f, 10.0f)
            : DefaultAudioSyncUpdateIntervalSeconds;
    }

    private bool ShouldEmitCalibrationTimeline()
    {
        return audioSyncSettings.Mode switch
        {
            MimirAudioSyncMode.ChirpOnly => true,
            MimirAudioSyncMode.Passive => false,
            MimirAudioSyncMode.Hybrid => lastPassiveSynchronizationConfidence < HybridPassiveConfidenceThreshold,
            _ => true,
        };
    }

    private string DescribeAudioSyncMode()
    {
        return audioSyncSettings.Mode switch
        {
            MimirAudioSyncMode.ChirpOnly => "chirp-only",
            MimirAudioSyncMode.Passive => "passive",
            MimirAudioSyncMode.Hybrid => "hybrid",
            _ => audioSyncSettings.Mode.ToString(),
        };
    }

    private string DescribeAudioBuffers()
    {
        return string.Join(" | ", synchronization.Buffers.Buffers
            .Where(buffer => buffer.Descriptor.Kind == MimirStreamKind.Audio)
            .OrderBy(buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal)
            .Select(buffer => $"{buffer.Descriptor.SourceId}:{buffer.Count}@{buffer.EdgeNs}"));
    }

    private void UpdatePassiveSynchronizationConfidence(IReadOnlyList<MimirAudioSynchronizationReport> reports)
    {
        var passiveConfidence = reports
            .Where(report => string.Equals(report.EvidenceKind, "passive", StringComparison.Ordinal))
            .Select(report => report.Confidence)
            .DefaultIfEmpty(0.0)
            .Max();
        lastPassiveSynchronizationConfidence = passiveConfidence > 0.0
            ? passiveConfidence
            : lastPassiveSynchronizationConfidence * 0.95;
    }

    private static string DescribeBuffer(MimirRollingStreamBuffer buffer)
    {
        var latest = buffer.Latest;
        if (latest?.VideoFrame is { } frame)
        {
            return $"{buffer.Descriptor.SourceId}: {buffer.Count} {frame.Width}x{frame.Height} {frame.PixelFormat} bytes {latest.Value.ByteLength} edge {buffer.EdgeNs}";
        }

        if (latest?.AudioBlock is { } block)
        {
            return $"{buffer.Descriptor.SourceId}: {buffer.Count} {block.Channels}ch {block.SampleRate}Hz {block.SampleFormat} frames {block.FrameCount} bytes {latest.Value.ByteLength} edge {buffer.EdgeNs}";
        }

        return $"{buffer.Descriptor.SourceId}: {buffer.Count} edge {buffer.EdgeNs}";
    }

    private string DescribeAudioSync()
    {
        if (audioSyncSettings.Mode == MimirAudioSyncMode.Passive)
        {
            return "passive mode; waiting for program-audio coherence";
        }

        var reports = synchronization.AudioSynchronizationReports;
        return reports.Count == 0
            ? "no payload windows"
            : string.Join(" | ", reports.Select(report => $"{report.SourceId}: {report.FractionalDelaySamples:0.000} samples {report.DelayMicroseconds:0.0}us c={report.Confidence:0.00} {report.EvidenceKind} events={report.TimelineMatchedEvents}"));
    }

    private string DescribeAlignedAudio()
    {
        var states = synchronization.AudioSynchronizationStates;
        return states.Count == 0
            ? "no aligned state"
            : $"{states.Count + 1}ch state-ready";
    }

    private string DescribeAudioSyncState()
    {
        var states = synchronization.AudioSynchronizationStates;
        return states.Count == 0
            ? "no sync state"
            : string.Join(" | ", states.Select(state => $"{state.SourceId}: {state.SmoothedDelaySamples:0.000} samples {state.DelayMicroseconds:0.0}us sro={state.SamplingRateOffsetPpm:0.0}ppm c={state.Confidence:0.00}"));
    }
}
