# Mimir Live

<link rel="stylesheet" href="/static/live/player.css">

<main class="mimir-live-shell">
  <section class="mimir-live-stage" aria-label="Mimir live stream">
    <video id="mimir-live-video" controls playsinline preload="metadata" poster="/Mimir.png"></video>
    <div class="mimir-live-status" id="mimir-live-status">Waiting for the live edge.</div>
  </section>
  <section class="mimir-live-panel">
    <p class="mimir-live-kicker">Self-hosted broadcast</p>
    <h2>Mimir Live</h2>
    <p>
      Starfire encodes the final Mimir program once. Yggdrasil receives that
      stream, rotates HLS segments, and serves this player over public HTTPS.
    </p>
    <dl>
      <div>
        <dt>Playlist</dt>
        <dd><a id="mimir-live-playlist-link" href="https://live.mimir.gamecult.org/hls/mimir.m3u8">live.mimir.gamecult.org/hls/mimir.m3u8</a></dd>
      </div>
      <div>
        <dt>Authority</dt>
        <dd>Mimir encodes. Yggdrasil distributes. The static site only watches.</dd>
      </div>
    </dl>
  </section>
</main>

<script src="https://cdn.jsdelivr.net/npm/hls.js@1.5.17/dist/hls.min.js" crossorigin="anonymous"></script>
<script src="/static/live/player.js"></script>
