# Spout OBS Bridge Summary

Spout is the Windows-local GPU texture sharing bridge for this rig. The OBS path is the Off World Live OBS Spout2 plugin, which creates an OBS source from a Spout shared texture and can also send the OBS canvas back to Spout. The Spout2 SDK is the application-side sender/receiver surface; its modern path centers on DirectX shared textures and has native DirectX/OpenGL integration options.

Decision: Mimir should not render final pixels itself. It should publish typed render-frame packets from the fusion cache. Fensalir should consume those packets, render them into a GPU render target, and publish that texture as a named Spout sender. OBS then receives that sender through the OBS Spout2 plugin.

Latency decision: the render packet carries both source timestamps and intended presentation time. The stream compositor can buffer visual frames until they align with the ambisonic audio field's presentation timeline. Latency is allowed; unbounded drift is not.
