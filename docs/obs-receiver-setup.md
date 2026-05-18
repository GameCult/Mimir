# OBS Receiver Setup

Add each stream as a separate OBS Media Source.

For every source:

1. Add `Sources > + > Media Source`.
2. Uncheck `Local File`.
3. Set `Input` to the relevant SRT URL.
4. Set `Input Format` to `mpegts`.
5. Enable `Restart playback when source becomes active`.

## Default URLs

These match `config/localcast.example.json` when the receiver is listening locally:

```text
video:   srt://0.0.0.0:5100?mode=listener&latency=120000&timeout=5000000
desktop: srt://0.0.0.0:5101?mode=listener&latency=120000&timeout=5000000
voice:   srt://0.0.0.0:5102?mode=listener&latency=120000&timeout=5000000
```

If using a passphrase, append:

```text
&passphrase=YOUR_SECRET&pbkeylen=16
```

OBS should be open before the sender starts if OBS is the SRT listener. If a source gets wedged, deactivate and reactivate the Media Source before changing ports; port churn is a symptom, not a configuration strategy.

## Source Layout

Recommended OBS scene:

```text
Husband PC - Video       Media Source, monitor off
Husband PC - Desktop     Media Source, audio only, monitor as needed
Husband PC - Voice       Media Source, audio only, monitor as needed
```

Mute the video source if it carries no audio. Keep audio filters on the separate audio sources.

