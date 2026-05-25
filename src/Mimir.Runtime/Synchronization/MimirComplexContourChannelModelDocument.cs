using System.Text.Json;

namespace Mimir.Runtime.Synchronization;

public sealed record MimirComplexContourChannelModelDocument(
    string Schema,
    DateTimeOffset CreatedUtc,
    string SourceReceiptPath,
    IReadOnlyList<MimirComplexContourPathChannelModel> Paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static MimirComplexContourChannelModelDocument Load(string path) =>
        JsonSerializer.Deserialize<MimirComplexContourChannelModelDocument>(
            File.ReadAllText(path),
            JsonOptions) ?? new MimirComplexContourChannelModelDocument(
            "mimir.bioacoustic.complex-contour-channel-model.v1",
            DateTimeOffset.MinValue,
            "",
            []);

    public MimirComplexContourPathChannelModel? PathFor(string referenceSourceId, string sourceId, int sampleRate)
    {
        var referenceChannel = ParseAsioChannel(referenceSourceId);
        var candidateChannel = ParseAsioChannel(sourceId);
        if (referenceChannel == null || candidateChannel == null)
        {
            return null;
        }

        return Paths.FirstOrDefault(path =>
            path.SampleRate == sampleRate &&
            path.ReferenceChannel == referenceChannel.Value &&
            path.CandidateChannel == candidateChannel.Value);
    }

    private static int? ParseAsioChannel(string sourceId)
    {
        const string prefix = "asio-ch";
        return sourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(sourceId[prefix.Length..], out var channel)
            ? channel
            : null;
    }
}

public sealed record MimirComplexContourPathChannelModel(
    string PathId,
    int SampleRate,
    int ReferenceChannel,
    int CandidateChannel,
    IReadOnlyList<string> CaseIds,
    IReadOnlyList<MimirComplexContourBandCorrection> Corrections,
    IReadOnlyList<MimirComplexContourReflectionCorrection> ReflectionTaps,
    int UsableBandCount,
    double Reliability,
    double DelaySpreadSamples)
{
    public MimirDirectPathChannelModel ToRuntimeModel() =>
        new(Corrections
            .Where(correction => correction.Usable)
            .Select(correction => new MimirDirectPathBandCorrection(
                correction.CenterHz,
                correction.DelayCorrectionSamples,
                correction.PhaseCorrectionRadians,
                correction.Weight * correction.Reliability))
            .ToArray());
}

public sealed record MimirComplexContourBandCorrection(
    double CenterHz,
    double DelayCorrectionSamples,
    double PhaseCorrectionRadians,
    double Weight,
    int ObservationCount,
    double DelayStdDevSamples,
    double Reliability,
    bool Usable);

public sealed record MimirComplexContourReflectionCorrection(
    double RelativeDelaySamples,
    int ObservationCount,
    double MeanRelativeDelaySamples);
