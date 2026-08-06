# iPhone TrueDepth Face Receiver

## Authority map

- Owner: `Mimir.Runtime` admits and normalizes face samples and owns the typed
  observation ledger.
- Inputs: Epic Live Link Face Apple ARKit UDP v6 datagrams from the iPhone, the
  configured stream identity, and Starfire monotonic arrival time.
- Outputs: bounded `mimir.face_tracking_observation.v1` CultCache documents.
  CultMesh publication is a separate provider-boundary cut and is not falsely
  claimed by this ingress process.
- Derived state: freshness, packet counts, Odin discovery projections, Eve
  status, and later Fensalir/avatar lowering. None of these may rewrite an
  admitted observation.
- Forbidden writers: Live Link Face, Starfire scripts, Odin, Eve, OBS, and the
  archived `Mimir.EveSensorReceiver` do not own normalized tracking truth.
- Shared paths: live UDP and `--self-test` fixtures use the same v6 decoder,
  admission ledger, and stream serialization.
- Cut line: no WebSocket/HTTP receiver, JSON ledger, renderer-owned parser, or
  direct phone-to-avatar path. The old Eve sensor socket remains archived.

## Transport

The phone emits Epic's Apple ARKit Live Link Face v6 datagrams over LAN UDP.
Mimir listens on UDP `11111`, validates the complete big-endian packet, admits
61 named channels, and assigns its own monotonic sequence. The first 52 ARKit
blendshape coefficients are bounded to `[0,1]`; the nine head/eye rotation
channels retain their signed values. Source timecode and Starfire arrival time
remain separate because an unproved cross-device clock offset is not latency.

The normalized observation is a typed MessagePack CultCache document in a
bounded `.cc` slot ledger. UDP is only the xenos ingress from the App Store
sender; it is not the internal state protocol. The old Starfire Odin authority
was retired during the Yggdrasil cut, so this receiver does not invent a local
provider or publish to an assumed endpoint. The next publication cut must have
Odin accept this Mimir-owned `.cc` schema through the live authenticated
provider boundary and expose the derived Eve surface.

Run on Starfire:

```powershell
dotnet run --project .\src\Mimir.FaceReceiver\Mimir.FaceReceiver.csproj -- --bind 0.0.0.0:11111 --stream iphone-xs-max-face --ledger state\mimir-face-observations.cc
```

Windows Firewall must allow inbound UDP 11111 for the selected private LAN
profile. Do not open this port to the public internet.

## Sender and signing decision

Use Epic's free **Live Link Face** App Store app in Apple ARKit mode. This is
the minimum Windows-practical sender: it supports network streaming of ARKit
animation data, needs iOS 16 or later, and the iPhone XS Max has the required
front TrueDepth/ARKit capability. Its newer real-time MetaHuman workflow says
iPhone 12 or later; that is a different mode and is not the receiver contract
used here.

A custom Mimir iOS sender is not a Windows-only deployment path. Building and
signing an ARKit app requires macOS with Xcode plus an Apple development team
and provisioning profile. `pymobiledevice3` can inspect the phone and install
an already signed IPA; it does not replace Xcode's iOS SDK build, entitlement,
code-signing, and provisioning authority. A custom app therefore requires a
Mac/Xcode handoff (or an external macOS CI/signing service) before Starfire can
install the resulting signed IPA.

Starfire USB inspection on 2026-08-06 verified the attached device as
`iPhone11,6` running iOS `18.7.9`, paired and reachable through
`pymobiledevice3`. The installed-app inventory contained no Live Link Face app.
Device identifiers are intentionally excluded from this repository.

## Exact phone-side handoff

1. On the iPhone, install **Live Link Face** by Epic Games from the App Store.
2. Join the same private LAN as Starfire. Obtain Starfire's active LAN IPv4
   address with `Get-NetIPAddress -AddressFamily IPv4` and exclude loopback,
   APIPA, VPN, and disconnected adapters.
3. Launch Live Link Face and grant Camera and Local Network permission. Select
   the Apple ARKit / non-MetaHuman realtime mode.
4. In Live Link Face settings, add Starfire's LAN IPv4 address as a Live Link
   target and set UDP port `11111`. Enable **Always Send Face Pose** if the app
   exposes it, so loss of face detection remains observable rather than looking
   like receiver death.
5. Return to capture, face the TrueDepth camera, and confirm Mimir reports
   advancing accepted frames. Do not infer success from app preview alone.
6. With the desired screen active, start Guided Access. In Guided Access
   Options disable Touch, keep auto-lock at Never, then start the session. This
   makes the ghost-touch panel non-authoritative while tracking continues.
   Voice Control can be used for the one-time setup before Touch is disabled.
7. Mount the phone with the front sensor array unobstructed and provide power.

The only unresolved physical handoff is installing/configuring the App Store
app and granting its first-run permissions. No receiver code can perform those
consent actions through `pymobiledevice3`.

## Verification

```powershell
dotnet run --project .\src\Mimir.FaceReceiver\Mimir.FaceReceiver.csproj -- --self-test
```

The self-test proves v6 big-endian decode, 61-channel normalization, typed
MessagePack document roundtrip, stale-frame rejection, restart epochs, and
rejection of a non-finite channel. Hardware
acceptance requires a real datagram from the configured phone and growth of the
`.cc` ledger; it cannot be claimed before the App Store sender is installed.
