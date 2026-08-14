# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases are cut by bumping `version` in [version.json](version.json) and adding a matching
section below. Everything else is automated — see [Releasing](README.md#releasing).

## [Unreleased]

## [1.0.0] - 2026-08-14

First release.

### Added

- **Library service.** Writes a `.strm` tree Jellyfin reads as a normal library, and resolves
  each entry to a live stream at play time so links never go stale.
- **Web UI** for browsing and adding titles, with filters for type, order, genre and year.
- **Jellyfin plugin** exposing every eligible release as a selectable version in the player,
  with seeder counts and file sizes.
- **Plugin repository** served by jetio itself, so the plugin can be installed from Jellyfin's
  dashboard without internet access or a published release.
- **Kids routing.** Animated titles are written to `Kids/Movies` and `Kids/Shows` so they can be
  separate Jellyfin libraries with their own user access.
- **Release verification.** Torrentio occasionally indexes the wrong film against an IMDb id — a
  rip of *Iron Man* appears under *Spider-Man: Brand New Day*. Quality filtering makes this worse
  rather than better, because for an unreleased title every genuine release is a cam and gets
  rejected, leaving the mislabelled clean rip as the winner. jetio checks release names and
  years against the title's metadata and returns nothing rather than the wrong film.
- **Multi-film pack rejection.** Packs routinely out-seed the correct single release, and picking
  one plays a different film. Films only — season packs are fine, since Torrentio pins the exact
  episode.
- **Media analysis on add.** Jellyfin is asked to probe each title added through the web UI, so
  it learns the container, codecs and bitrate instead of having nothing to reason about. Without
  it Jellyfin hands the raw URL to players and client-side quality limits silently do nothing.
  `POST /api/library/{imdbId}/analyze` retrofits titles added earlier. Never runs during bulk
  syncs, which would start every title downloading at once.
- **Plugin option: serve playback through Jellyfin.** Makes Jellyfin remux rather than sending
  players straight to the streaming server, so it can deliver external subtitles. Off by default;
  costs bandwidth through the server and slower seeking.
- **Targeted Jellyfin rescans** via `Jellyfin.LibraryNames`, instead of rescanning every library
  on a server that also holds real media.
- **Catalog sources:** managed library, Cinemeta, watchlist file, TMDB and Trakt.
- **Debrid support.** jetio prefers a stream's direct URL whenever Torrentio supplies one.

### Notes

- Cinemeta's advertised `year` catalog returns no results, so jetio filters by year itself.
- Its `imdbRating` catalog only covers roughly 2020 onwards; the UI explains this rather than
  showing an empty grid.
- The Stremio streaming server binds to loopback inside its own container, so `docker-compose.yml`
  includes a `stremio-bridge` sidecar that re-exposes it on port 11471.

[Unreleased]: https://github.com/GMarinow/jetio/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/GMarinow/jetio/releases/tag/v1.0.0
