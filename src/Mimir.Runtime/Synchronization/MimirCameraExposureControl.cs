namespace Mimir.Runtime.Synchronization;

public interface IMimirCameraExposureGainActuator
{
    string SourceId { get; }

    string ExposureControlKind { get; }

    bool SupportsExposureGain { get; }

    bool TrySetExposureGain(int? exposure, int? gain);
}

public sealed record MimirOnlineExposureGainSetting(string Id, int? Exposure, int? Gain);

public sealed record MimirCameraExposureControlOptions(
    bool Enabled = true,
    int ExpectedLedCount = 38,
    double MinimumLuma = 0.55,
    double SettingSeconds = 0.75,
    double ResweepSeconds = 12.0);

public sealed record MimirCameraExposureControlStatus(
    string SourceId,
    string ControlKind,
    bool SupportsExposureGain,
    string State,
    string CurrentSettingId,
    int? CurrentExposure,
    int? CurrentGain,
    string BestSettingId,
    int? BestExposure,
    int? BestGain,
    double BestScore,
    int BestDetectedLedCount,
    bool BestUsableForCalibration,
    int FramesScored,
    bool LastApplySucceeded,
    string Reason);

public sealed class MimirCameraExposureController(MimirCameraExposureControlOptions options)
{
    private readonly Dictionary<string, SourceState> states = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<MimirOnlineExposureGainSetting> settings =
    [
        new("exp-10_gain-0", -10, 0),
        new("exp-10_gain-8", -10, 8),
        new("exp-9_gain-0", -9, 0),
        new("exp-9_gain-8", -9, 8),
        new("exp-8_gain-0", -8, 0),
        new("exp-8_gain-8", -8, 8),
        new("exp-7_gain-0", -7, 0),
        new("exp-7_gain-8", -7, 8),
        new("exp-6_gain-0", -6, 0),
        new("exp-6_gain-8", -6, 8),
    ];

    public IReadOnlyList<MimirCameraExposureControlStatus> Statuses =>
        states.Values
            .OrderBy(state => state.SourceId, StringComparer.Ordinal)
            .Select(state => state.ToStatus())
            .ToArray();

    public void Update(
        DateTimeOffset now,
        IEnumerable<MimirRollingStreamBuffer> buffers,
        IEnumerable<IMimirCameraExposureGainActuator> actuators)
    {
        if (!options.Enabled)
        {
            return;
        }

        var actuatorBySource = actuators.ToDictionary(actuator => actuator.SourceId, StringComparer.Ordinal);
        foreach (var buffer in buffers.Where(static buffer => buffer.Descriptor.Kind == MimirStreamKind.Video))
        {
            if (!actuatorBySource.TryGetValue(buffer.Descriptor.SourceId, out var actuator))
            {
                continue;
            }

            var state = StateFor(actuator);
            if (!actuator.SupportsExposureGain)
            {
                state.MarkUnsupported();
                continue;
            }

            if (state.State == ExposureControlState.Idle || now >= state.NextTransitionUtc)
            {
                AdvanceState(state, actuator, now);
            }

            if (buffer.Latest is { } sample)
            {
                ScoreSample(state, sample);
            }
        }
    }

    private SourceState StateFor(IMimirCameraExposureGainActuator actuator)
    {
        if (states.TryGetValue(actuator.SourceId, out var state))
        {
            return state;
        }

        state = new SourceState(actuator.SourceId, actuator.ExposureControlKind);
        states.Add(actuator.SourceId, state);
        return state;
    }

    private void AdvanceState(SourceState state, IMimirCameraExposureGainActuator actuator, DateTimeOffset now)
    {
        if (state.State == ExposureControlState.Locked && now < state.NextTransitionUtc)
        {
            return;
        }

        if (state.State == ExposureControlState.Scanning)
        {
            state.SettingIndex++;
        }
        else
        {
            state.BeginSweep();
        }

        if (state.SettingIndex >= settings.Count)
        {
            LockBest(state, actuator, now);
            return;
        }

        var setting = settings[state.SettingIndex];
        state.CurrentSetting = setting;
        state.CurrentScore = new Accumulator();
        state.LastApplySucceeded = actuator.TrySetExposureGain(setting.Exposure, setting.Gain);
        state.State = ExposureControlState.Scanning;
        state.Reason = state.LastApplySucceeded ? "scoring-setting" : "control-apply-failed";
        state.NextTransitionUtc = now + TimeSpan.FromSeconds(Math.Max(0.10, options.SettingSeconds));
    }

    private void LockBest(SourceState state, IMimirCameraExposureGainActuator actuator, DateTimeOffset now)
    {
        var best = state.BestSetting ?? state.CurrentSetting ?? settings[0];
        state.CurrentSetting = best;
        state.LastApplySucceeded = actuator.TrySetExposureGain(best.Exposure, best.Gain);
        state.State = ExposureControlState.Locked;
        state.Reason = state.LastApplySucceeded ? "locked-best-spline-score" : "best-control-apply-failed";
        state.NextTransitionUtc = now + TimeSpan.FromSeconds(Math.Max(options.ResweepSeconds, settings.Count * options.SettingSeconds));
        state.SettingIndex = settings.Count;
    }

    private void ScoreSample(SourceState state, MimirStreamSample sample)
    {
        if (state.State != ExposureControlState.Scanning ||
            sample.Sequence == state.LastScoredSequence ||
            sample.VideoFrame is not { } frame ||
            sample.Data.IsEmpty ||
            !TryExtractLuma(frame, sample.Data.Span, out var luma, out var width, out var height))
        {
            return;
        }

        state.LastScoredSequence = sample.Sequence;
        var analyzer = new MimirLedSplineFrameAnalyzer(new MimirLedSplineFrameAnalyzerOptions(
            options.ExpectedLedCount,
            options.MinimumLuma,
            MinimumComponentPixels: 2,
            MaximumComponentPixels: 8192));
        var analysis = analyzer.AnalyzeLumaFrame(
            sample.SourceId,
            $"{sample.SourceId}:well:{sample.Sequence}",
            width,
            height,
            luma,
            frame.DeviceTimestampNs);
        state.CurrentScore.Add(analysis.Quality);
        if (state.CurrentScore.BestQuality is { } best &&
            (state.BestQuality == null ||
                best.Score > state.BestQuality.Score ||
                (Math.Abs(best.Score - state.BestQuality.Score) <= 1.0e-9 &&
                    best.DetectedLedCount > state.BestQuality.DetectedLedCount)))
        {
            state.BestQuality = best;
            state.BestSetting = state.CurrentSetting;
        }
    }

    private static bool TryExtractLuma(
        MimirVideoFrameDescriptor frame,
        ReadOnlySpan<byte> data,
        out byte[] luma,
        out int width,
        out int height)
    {
        width = frame.Width;
        height = frame.Height;
        if (width <= 0 || height <= 0)
        {
            luma = [];
            return false;
        }

        luma = new byte[checked(width * height)];
        var stride = frame.StrideBytes <= 0 ? DefaultStride(frame) : frame.StrideBytes;
        switch (frame.PixelFormat)
        {
            case MimirVideoPixelFormat.Gray8:
            case MimirVideoPixelFormat.R8:
            case MimirVideoPixelFormat.Bayer8:
                for (var y = 0; y < height; y++)
                {
                    var row = y * stride;
                    if (row + width > data.Length)
                    {
                        return false;
                    }

                    data.Slice(row, width).CopyTo(luma.AsSpan(y * width, width));
                }

                return true;
            case MimirVideoPixelFormat.Yuy2:
            case MimirVideoPixelFormat.LeapStereoIr:
            case MimirVideoPixelFormat.Rg8:
                for (var y = 0; y < height; y++)
                {
                    var row = y * stride;
                    if (row + width * 2 > data.Length)
                    {
                        return false;
                    }

                    for (var x = 0; x < width; x++)
                    {
                        luma[y * width + x] = data[row + x * 2];
                    }
                }

                return true;
            case MimirVideoPixelFormat.Bgra8:
                for (var y = 0; y < height; y++)
                {
                    var row = y * stride;
                    if (row + width * 4 > data.Length)
                    {
                        return false;
                    }

                    for (var x = 0; x < width; x++)
                    {
                        var offset = row + x * 4;
                        luma[y * width + x] = (byte)Math.Clamp(
                            (int)Math.Round(data[offset] * 0.114 + data[offset + 1] * 0.587 + data[offset + 2] * 0.299),
                            0,
                            255);
                    }
                }

                return true;
            default:
                luma = [];
                return false;
        }
    }

    private static int DefaultStride(MimirVideoFrameDescriptor frame) =>
        frame.PixelFormat switch
        {
            MimirVideoPixelFormat.Yuy2 or MimirVideoPixelFormat.LeapStereoIr or MimirVideoPixelFormat.Rg8 => frame.Width * 2,
            MimirVideoPixelFormat.Bgra8 => frame.Width * 4,
            _ => frame.Width,
        };

    private sealed class SourceState(string sourceId, string controlKind)
    {
        public string SourceId { get; } = sourceId;
        public string ControlKind { get; } = controlKind;
        public ExposureControlState State { get; set; } = ExposureControlState.Idle;
        public int SettingIndex { get; set; } = -1;
        public MimirOnlineExposureGainSetting? CurrentSetting { get; set; }
        public MimirOnlineExposureGainSetting? BestSetting { get; set; }
        public MimirLedSplineQualityReport? BestQuality { get; set; }
        public Accumulator CurrentScore { get; set; } = new();
        public ulong LastScoredSequence { get; set; } = ulong.MaxValue;
        public DateTimeOffset NextTransitionUtc { get; set; } = DateTimeOffset.MinValue;
        public bool LastApplySucceeded { get; set; }
        public string Reason { get; set; } = "not-started";

        public void BeginSweep()
        {
            State = ExposureControlState.Scanning;
            SettingIndex = 0;
            BestSetting = null;
            BestQuality = null;
            CurrentScore = new Accumulator();
            LastScoredSequence = ulong.MaxValue;
            Reason = "begin-sweep";
        }

        public void MarkUnsupported()
        {
            State = ExposureControlState.Unsupported;
            Reason = "exposure-gain-not-supported";
        }

        public MimirCameraExposureControlStatus ToStatus()
        {
            var best = BestSetting;
            var quality = BestQuality;
            return new MimirCameraExposureControlStatus(
                SourceId,
                ControlKind,
                State != ExposureControlState.Unsupported,
                State.ToString(),
                CurrentSetting?.Id ?? "",
                CurrentSetting?.Exposure,
                CurrentSetting?.Gain,
                best?.Id ?? "",
                best?.Exposure,
                best?.Gain,
                quality?.Score ?? 0.0,
                quality?.DetectedLedCount ?? 0,
                quality?.UsableForCalibration ?? false,
                CurrentScore.FramesScored,
                LastApplySucceeded,
                Reason);
        }
    }

    private sealed class Accumulator
    {
        public int FramesScored { get; private set; }
        public MimirLedSplineQualityReport? BestQuality { get; private set; }

        public void Add(MimirLedSplineQualityReport quality)
        {
            FramesScored++;
            if (BestQuality == null ||
                quality.Score > BestQuality.Score ||
                (Math.Abs(quality.Score - BestQuality.Score) <= 1.0e-9 &&
                    quality.DetectedLedCount > BestQuality.DetectedLedCount))
            {
                BestQuality = quality;
            }
        }
    }

    private enum ExposureControlState
    {
        Idle,
        Scanning,
        Locked,
        Unsupported,
    }
}
