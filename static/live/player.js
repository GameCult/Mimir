(function () {
  const playlistUrl = "https://live.mimir.gamecult.org/hls/mimir.m3u8"
  const video = document.getElementById("mimir-live-video")
  const status = document.getElementById("mimir-live-status")
  const link = document.getElementById("mimir-live-playlist-link")

  if (!video || !status || !link) {
    return
  }

  link.href = playlistUrl
  link.textContent = playlistUrl.replace(/^https?:\/\//, "")

  function setStatus(message) {
    status.textContent = message
  }

  if (video.canPlayType("application/vnd.apple.mpegurl")) {
    video.src = playlistUrl
    setStatus("Using native HLS playback.")
    return
  }

  if (window.Hls && window.Hls.isSupported()) {
    const hls = new window.Hls({
      lowLatencyMode: false,
      backBufferLength: 30,
      maxBufferLength: 18,
    })
    hls.loadSource(playlistUrl)
    hls.attachMedia(video)
    hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
      setStatus("Live edge found. Playback is ready.")
    })
    hls.on(window.Hls.Events.ERROR, (_event, data) => {
      const detail = data && data.details ? ` ${data.details}` : ""
      setStatus(`Waiting for stream.${detail}`)
    })
    return
  }

  setStatus("This browser cannot play HLS here. Try Safari, Edge, or Chrome with hls.js available.")
})()
