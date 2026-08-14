# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases are cut by bumping `version` in [version.json](version.json) and adding a matching
section below. Everything else is automated — see [Releasing](README.md#releasing).

## [Unreleased]

## [1.1.0] - 2026-08-14

### Added

- **Subtitle files are muxed into the stream.** When a title has subtitles beside its `.strm`,
  jetio now serves the release through ffmpeg with those tracks embedded, instead of redirecting
  the player to the streaming server.

  This is the only arrangement several clients render reliably. A separate subtitle file has to be
  side-loaded onto a stream the player fetched from somewhere else, and the Jellyfin Android TV
  app does not do that — the track appears in the menu and never draws. An embedded track is read
  straight out of the container the player is already decoding, so there is nothing to side-load.
  Releases that happen to ship their own subtitles always worked on that TV for exactly this
  reason; this gives downloaded ones the same standing.

  Nothing is re-encoded — video and audio are copied and only the container is rebuilt — so the
  cost is bandwidth through jetio, not CPU. It engages **only** for titles that actually have
  subtitle files, so everything else still redirects and stays out of jetio's data path.

  Turn it off with `Jetio__Subtitles__MuxIntoStream=false`.

- **Legacy subtitle encodings are handled.** Cyrillic subtitles are still commonly distributed as
  Windows-1251, which ffmpeg rejects outright with `Invalid UTF-8 in decoded subtitles text`,
  failing the whole stream rather than one track. Files that are not UTF-8 now have their encoding
  declared, chosen from the byte layout so a Western European file is not mangled into Cyrillic.

### Notes

- Seeking is approximate. Players seek a progressive stream by byte offset and ffmpeg seeks by
  time, and for a variable-bitrate release there is no exact conversion — the offset is placed
  proportionally along the timeline, which lands within a few seconds rather than exactly.
  jetio learns the duration and size with one `ffprobe` per release, cached; if that fails, it
  declines range requests rather than seeking to the wrong place.
- The runtime image now carries ffmpeg, so it is larger.

## [1.0.1] - 2026-08-14

### Fixed

- **Subtitles on releases picked from the version picker.** The plugin described every release it
  offered as having no streams at all, so choosing one left the player with nothing to switch on —
  in every client, the web one included. Jellyfin indexes the `.srt` files next to a `.strm`
  against the item and attaches them only to the source it builds from the `.strm` itself; the
  plugin's sources are separate and were never given them. They are now copied onto each release
  the picker offers.

  Embedded tracks are deliberately not copied: those belong to whichever release the `.strm`
  currently resolves to, and their indexes mean nothing inside a different release's container.

### Notes

- **Subtitle burn-in ends playback on non-UTF-8 subtitle files, and no jetio change can fix it.**
  Windows-1251 Cyrillic `.srt` files make ffmpeg exit with `Invalid UTF-8 in decoded subtitles
  text`, so nothing is produced and the player returns to the item page. It affects local media
  and jetio items alike. Convert the files to UTF-8, or set *Burn subtitles* to **Never** in the
  client — separate-file delivery converts them correctly.
  See [The player exits when an external subtitle is selected](README.md#the-player-exits-when-an-external-subtitle-is-selected).

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

[Unreleased]: https://github.com/GMarinow/jetio/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/GMarinow/jetio/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/GMarinow/jetio/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/GMarinow/jetio/releases/tag/v1.0.0
