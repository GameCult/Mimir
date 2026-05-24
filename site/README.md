# Mimir Quartz Site

Quartz overlay for the future `https://mimir.gamecult.org` knowledge base.
The content source is the repo root Obsidian vault; this `site/` directory owns
presentation, navigation, and static-site wiring.

The site follows the shared GameCult-Quartz deployment pattern:

- content directory: repo root (`.`)
- overlay directory: `site`
- output directory: `quartz-site/public`
- GitHub Pages host: `gamecult.github.io`
- custom domain: `mimir.gamecult.org`

The overlay intentionally excludes source/build/runtime folders from publishing
while keeping the docs, research notes, handoff notes, and vault index visible.

