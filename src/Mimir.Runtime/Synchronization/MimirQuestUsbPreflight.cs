using GameCult.Caching;
using MessagePack;

namespace Mimir.Runtime.Synchronization;

[CultDocument("mimir.quest_usb_preflight", "mimir.quest_usb_preflight.v1")]
[MessagePackObject]
public sealed record MimirQuestUsbPreflightDocument(
    [property: Key(0)]
    [property: CultName]
    string PreflightId,
    [property: Key(1)] string CapturedAtUtc,
    [property: Key(2)] string Serial,
    [property: Key(3)] string ConnectionState,
    [property: Key(4)] string Product,
    [property: Key(5)] string Model,
    [property: Key(6)] string Device,
    [property: Key(7)] string TransportId,
    [property: Key(8)] string AndroidRelease,
    [property: Key(9)] string AndroidSdk,
    [property: Key(10)] string BuildFingerprint,
    [property: Key(11)] string BatterySummary,
    [property: Key(12)] string PoseCaptureStatus,
    [property: Key(13)] string RequiredNextStep,
    [property: Key(14)] string[] OperatorNotes);

public static class MimirQuestUsbPreflight
{
    public const string AuthorizedNoPoseBridgeStatus = "usb-authorized-no-pose-bridge-yet";

    public static MimirQuestUsbPreflightDocument Create(
        string serial,
        string connectionState,
        string product,
        string model,
        string device,
        string transportId,
        string androidRelease,
        string androidSdk,
        string buildFingerprint,
        string batterySummary,
        DateTimeOffset? capturedAt = null) =>
        new(
            PreflightId: $"quest-usb:{serial}",
            CapturedAtUtc: (capturedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            Serial: serial,
            ConnectionState: connectionState,
            Product: product,
            Model: model,
            Device: device,
            TransportId: transportId,
            AndroidRelease: androidRelease,
            AndroidSdk: androidSdk,
            BuildFingerprint: buildFingerprint,
            BatterySummary: batterySummary,
            PoseCaptureStatus: AuthorizedNoPoseBridgeStatus,
            RequiredNextStep: "Run or deploy a Quest/OpenXR witness that publishes headset and controller poses into the Mimir calibration capture.",
            OperatorNotes:
            [
                "ADB authorization proves Starfire can query the Quest over USB; it does not expose tracked headset/controller poses by itself.",
                "Do not disturb the headset runtime from this preflight. Pose capture belongs to an explicit Quest/OpenXR witness bridge.",
                "Mimir may use Quest poses as optional external validation evidence, but Mimir still owns Move calibration and fusion promotion."
            ]);
}
