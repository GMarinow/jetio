<h1 align="center">jetio</h1>

<p align="center">
  <strong>Watch Torrentio streams in Jellyfin.</strong><br />
  Browse, add a title, and it appears in your library seconds later — nothing downloaded ahead of time.
</p>

<p align="center">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg" />
  <img alt=".NET 9" src="https://img.shields.io/badge/.NET-9.0-512BD4.svg" />
  <img alt="Jellyfin 10.11" src="https://img.shields.io/badge/Jellyfin-10.11-00A4DC.svg" />
</p>

---

jetio keeps a library of `.strm` files in Jellyfin and resolves each one to a live torrent stream
at the moment you press play. Jellyfin sees ordinary films and episodes, with its own artwork and
metadata. Nothing is stored, and the library never goes stale.

**Two parts, both optional on their own:**

| | |
|---|---|
| **The service** | Writes the library, answers playback requests, serves a web UI for browsing and adding titles. |
| **The plugin** | Adds a version picker inside Jellyfin, so you choose the release — 4K or 1080p, by seeders and size — instead of jetio choosing for you. |

## Legal notice

jetio does not host, store, index or distribute any media. It queries
[Torrentio](https://torrentio.strem.fun/), a third-party Stremio addon, and passes the results to
a media server you run.

What you stream is your responsibility and is subject to the law where you live. Sharing or
downloading copyrighted material without permission is unlawful in many countries, and BitTorrent
uploads to other peers by default. This project is offered for use with content you have the right
to access. Do not expose it to the public internet — see [SECURITY.md](SECURITY.md).

## How it works

```
  catalogs and the web UI
            │
            ▼   scheduled sync, or immediately on Add
      .strm library  ──────────────▶  Jellyfin scans it as a normal library
            │
            │  each file contains: http://jetio:9000/resolve/movie/tt0133093
            ▼   at play time
      jetio /resolve  ──▶  asks Torrentio for releases, ranks them, picks one
            │
            ▼   302 redirect
  Stremio streaming server  ──▶  joins the swarm, serves HTTP with range support
```

The redirect is the whole design. A `.strm` file holds a **stable jetio URL**, never a stream URL
— real stream URLs go stale and debrid links expire within hours. Resolution happens on every
play, so the library keeps working without maintenance.

jetio only redirects; it never proxies. Video goes straight from the streaming server to the
player, so seeking and range requests do not pass through jetio.

## Requirements

- **Jellyfin 10.11** (developed against 10.11.6).
- **Docker** on a host that can write to storage Jellyfin can read.
- **No debrid account needed.** If you have one, playback gets markedly better — see
  [Using a debrid service](#using-a-debrid-service).

## Quick start

**1. Configure**

```bash
git clone https://github.com/GMarinow/jetio.git && cd jetio
cp .env.example .env
```

Edit `.env`. Every field is explained inline; the one that catches people out is
`JETIO_HOST_IP`, which must be the docker host's real LAN address. jetio hands playback URLs to
Jellyfin *and* to every client device, so `localhost` or a docker service name breaks playback
everywhere except the host itself. jetio logs a warning at startup if it detects a loopback
address.

**2. Start**

```bash
docker compose up -d --build
```

**3. Add the libraries in Jellyfin**

jetio creates these on first start, so they exist before you have any content:

| Jellyfin library type | Path |
|---|---|
| Movies | `<JETIO_LIBRARY_PATH>/Movies` |
| Shows | `<JETIO_LIBRARY_PATH>/Shows` |
| Movies | `<JETIO_LIBRARY_PATH>/Kids/Movies` |
| Shows | `<JETIO_LIBRARY_PATH>/Kids/Shows` |

> **Turn off *Chapter image extraction* and *Trickplay image generation* on every one of them.**
> Both work by seeking repeatedly through each file. On torrent-backed streams that means
> Jellyfin starts downloading your entire library the moment it scans. Leaving them on is the
> single fastest way to make this setup unusable.

**4. Tell jetio which libraries those are**

Otherwise every rescan touches your whole server. Add to `.env`, using the exact names shown in
Jellyfin:

```bash
JELLYFIN_LIBRARY_NAMES=jetio Films,jetio Shows,jetio Kids Films,jetio Kids Shows
```

**5. Check it**

```bash
curl http://<JETIO_HOST_IP>:9000/healthz
```

Then open `http://<JETIO_HOST_IP>:9000`, add a title, and play it in Jellyfin.

## The web UI

Open jetio in a browser to browse by type, popularity, genre and year, or search by title. Press
**Add** and jetio writes the `.strm` files immediately and asks Jellyfin to rescan, so the title
shows up in seconds rather than at the next scheduled sync. Anything already in your library is
stamped, and **Remove** deletes it straight away.

Two limits come from Cinemeta rather than jetio, and the UI is explicit about both:

- **Highest rated only covers roughly 2020 onwards.** Pairing it with an older year returns
  nothing, so the empty state says so and offers to switch you to Popular.
- **Cinemeta cannot filter by year at all.** Its advertised `year` catalog returns no results, so
  jetio filters by year itself by paging through the catalog — which is why picking a year takes
  a moment longer to load.

## The Jellyfin plugin

By default jetio picks one release for you at play time. The plugin instead offers **every
eligible release as a selectable version** in Jellyfin's own player:

```
1080p · 226 seeders · 3.03 GB · The.Matrix.1999.1080p.BluRay.x265-GalaxyRG265
1080p ·  46 seeders · 7.06 GB · The Matrix 1999 1080p YT WEB-DL DDP 5 1-PiRaTeS
 720p ·  88 seeders · 1.40 GB · The.Matrix.1999.720p.BluRay.x264
```

It is deliberately thin: it implements `IMediaSourceProvider`, asks jetio for ranked candidates,
and maps them to Jellyfin media sources. All Torrentio querying, ranking and filtering stays in
the service, so there is exactly one implementation of that logic.

### Limits of the version picker

**No Jellyfin client currently shows these versions**, on any platform. This is not a client bug
and no plugin setting changes it.

Clients build their version list from the item as Jellyfin describes it, and Jellyfin fills that
in from `GetStaticMediaSources()` — the sources belonging to the file itself. Sources from an
`IMediaSourceProvider` are *dynamic*: they are added by `GetPlaybackMediaSources()`, which runs
when playback starts, after a version has already been chosen. So they exist, and they are
reachable by id, but nothing offers them to you:

```
item as described to clients : 1 source     ← the .strm, and what plays
playback info                : 12 sources   ← the .strm plus every release
```

Everything therefore plays the default `.strm` source, and the plugin's settings apply to sources
that are never selected. Subtitles are handled in the service instead, by
[muxing](#how-subtitles-reach-the-player), which is why that works regardless.

The plugin is kept because the sources are correct and the limitation is Jellyfin's to lift. If
selectable releases matter to you today, the approach that works is writing one `.strm` per
release — `Title (Year) - 1080p.strm` alongside `Title (Year) - 720p.strm` — since Jellyfin groups
those natively into real versions that every client displays.

### Installing

In Jellyfin, go to **Dashboard → Plugins → Repositories → +** and add:

```
https://raw.githubusercontent.com/GMarinow/jetio/main/manifest.json
```

Then **Catalogue → jetio → Install**, restart Jellyfin, and set the jetio base URL under
**Dashboard → Plugins → jetio**.

> This manifest lists **tagged releases only**. A fresh clone has none, so the catalogue will be
> empty until you publish one — see [Releasing](#releasing), or use the self-hosted option below.

<details>
<summary>Alternative: install from your own jetio</summary>

jetio serves the same repository itself, at:

```
http://<JETIO_HOST_IP>:9000/plugin/manifest.json
```

Use this when:

- you have not tagged a release yet,
- your Jellyfin server has no internet access, or
- you are developing. The Docker build packages the plugin from the same commit as the service,
  so the two cannot drift — worth having, because an older released plugin can call a
  `/candidates` response shape that a newer service has since changed.

It behaves identically otherwise: same plugin, same install and update flow.
</details>

<details>
<summary>Alternative: install the file by hand</summary>

Build the package (PowerShell, or `pwsh` on Linux and macOS):

```bash
pwsh ./build-plugin.ps1     # produces artifacts/jellyfin-plugin-jetio-1.0.0.0.zip
```

Then install it:

```bash
sudo mkdir -p /var/lib/jellyfin/plugins/jetio_1.0.0.0
sudo unzip jellyfin-plugin-jetio-1.0.0.0.zip -d /var/lib/jellyfin/plugins/jetio_1.0.0.0
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/jetio_1.0.0.0
sudo systemctl restart jellyfin
```

Jellyfin will not offer updates for a plugin installed this way.
</details>

> The plugin is compiled against `Jellyfin.Controller` 10.11.6 and targets .NET 9. Jellyfin
> plugins are not ABI-stable across major versions, so a 10.12 upgrade will likely need a rebuild.

### Subtitles not appearing

If subtitle tracks are **listed but never render** — typically on the Android TV app, while the
same file subtitles fine in a browser — the cause is usually that Jellyfin has never looked
inside the stream.

Without an analysis pass Jellyfin has no container, codec or bitrate for a `.strm` item. With
nothing to reason about it hands the URL straight to the player, so it is never in the data path
and cannot attach a subtitle. It also explains why client-side quality limits appear to do
nothing on these items: there is no bitrate to compare the limit against.

jetio therefore analyses each title added through the web UI (`Jellyfin.AnalyzeAddedItems`, on by
default). Titles added before that existed can be retrofitted one at a time:

```bash
curl -X POST http://<JETIO_HOST_IP>:9000/api/library/tt0133093/analyze
```

Analysis pulls the opening chunk of the torrent, so it is done one title at a time and **never**
during a bulk catalog sync — that would start every title downloading at once.

If subtitles still do not render afterwards, the fix is [muxing](#how-subtitles-reach-the-player),
which is on by default — not a Jellyfin setting.

Two things that look like they should help and do not:

- **Serve playback through Jellyfin**, in the plugin settings. It only affects media sources the
  plugin provides, and [those are unreachable](#limits-of-the-version-picker) — so it never
  engages. Left in place for the day that changes.
- **Capping the client's maximum bitrate.** Jellyfin does not apply bitrate limits to a remote
  `.strm` source: it will report direct play with a 1 Mbps cap against a 2.7 Mbps release. So it
  cannot be used to force Jellyfin into the data path, or as a diagnostic.

### How subtitles reach the player

When a title has subtitle files beside its `.strm`, jetio serves it as **HLS** — a playlist of
segments, with each subtitle offered as a rendition of that stream — rather than redirecting the
player to the streaming server.

This is the only arrangement that gives you both subtitles and seeking.

**Why not a separate subtitle file.** It has to be side-loaded onto a stream the player fetched
from a different server, and the Jellyfin Android TV app does not do it: the track is listed and
never drawn. Releases that happen to ship their own subtitles always played correctly on the same
TV, which is the difference this closes. An HLS rendition is handled inside the player's own HLS
engine, so there is nothing to side-load.

**Why not one long muxed stream.** That was 1.1.0, and subtitles worked — but scrubbing did not,
and the two turn out to be the same problem. ffmpeg writes a container's duration and seek index by
going back over its own output once it knows them, and it is writing to a pipe. The result declares
itself a live stream of unknown length with no index, so a player has nothing to seek against and
can only restart from the beginning. A playlist declares the whole film up front from the duration
alone, so a scrub is a request for a different segment.

Nothing is re-encoded — video and audio are copied — so the cost is bandwidth through jetio rather
than CPU. It engages **only** for titles that actually have subtitle files; everything else still
redirects and jetio stays out of the data path.

```bash
Jetio__Subtitles__MuxIntoStream=false   # back to redirecting, always
```

Two things worth knowing:

- **Segment boundaries are nominal.** Copying a stream means ffmpeg cuts at the nearest keyframe
  rather than exactly on the second, so real segments drift slightly from the declared durations.
  Players tolerate it, and Jellyfin's own remuxing path makes the same trade — it is what lets
  seeking work without re-encoding the video.
- **Subtitle files should be UTF-8.** jetio detects and declares a legacy encoding when it finds
  one, but converting them is better — see below.

Files are matched by name. A movie folder holds one title, so any subtitle in it is used; season
folders need the `S01E02` marker in the filename. A language suffix like `Film.bg.srt` sets the
track's language so the player can label and pre-select it — without one the track still works,
but shows as "Undefined".

### The player exits when an external subtitle is selected

Embedded tracks play fine, and picking a `.srt` file drops you straight back to the item page.
This affects jetio items and ordinary local media equally — it is not a jetio fault.

The root cause is the subtitle file's **encoding**. Cyrillic subtitles are still commonly
distributed as Windows-1251 rather than UTF-8, and burning one into the picture fails on it:

```
[srt] Invalid UTF-8 in decoded subtitles text; maybe missing -sub_charenc option
[AVFilterGraph] Error initializing filters
Error opening output files: Invalid data found when processing input
```

ffmpeg exits, so there is no output at all and playback ends rather than degrading.

Jellyfin is supposed to prevent this by detecting the character set and passing `-sub_charenc`.
Two things stop it:

- **No language on the subtitle.** Detection only runs for a stream that has one, and a file named
  `Film.srt` has none. Name it `Film.bg.srt` and Jellyfin tags it Bulgarian, which both enables
  detection and lets the track be chosen by language.
- **A remote media source.** Detection opens the subtitle using the *media source's* protocol
  rather than the subtitle's own, so for a `.strm` — an `Http` source with a local `.srt` beside
  it — Jellyfin tries to fetch a filesystem path over HTTP and throws
  `NotSupportedException: The 'file' scheme is not supported`. Same outcome, and no filename
  change avoids it.

**The permanent fix is to convert the files to UTF-8.** Everything works afterwards, on every
client and both delivery paths. List the offenders first:

```bash
find /mnt/media -name '*.srt' -exec sh -c 'iconv -f UTF-8 -t UTF-8 "$1" >/dev/null 2>&1 || echo "$1"' _ {} \;
```

Then convert one, keeping the original until it is verified:

```bash
cp "Film.bg.srt" "Film.bg.srt.bak" && iconv -f CP1251 -t UTF-8 "Film.bg.srt.bak" > "Film.bg.srt"
```

**The immediate workaround is to turn burn-in off** — in the web client, Settings → Playback →
*Burn subtitles* → **Never**. Delivered as a separate file the subtitle is converted by Jellyfin
in managed code, which detects the encoding correctly and is unaffected by either problem above.
Note that image-based subtitles (PGS, VobSub) can *only* be burned in, so those still need the
files themselves to be sound.

## Choosing what lands in the library

Five sources, all optional, merged and de-duplicated by IMDb id.

| Source | Needs | Notes |
|---|---|---|
| **Managed** | nothing | What the web UI writes, kept in `config/library.json`. On by default. |
| **Cinemeta** | nothing | Stremio's top and popular lists. Broad — `series/top` alone writes several thousand files. Off by default. |
| **Watchlist** | nothing | `config/watchlist.txt`, one IMDb id per line. For bulk entry by hand. Off by default. |
| **TMDB** | free API key | Richer lists. Each hit costs an extra `external_ids` call to map TMDB → IMDb. |
| **Trakt** | client id (+ OAuth) | Your watchlists and custom lists, managed in Trakt's own apps. |

Adding on demand is the model worth defaulting to. The bulk catalogs pre-guess what you might
watch: you will play a handful, and each unplayed entry is a promise jetio may not be able to
keep — a title with no seeders looks identical in Jellyfin to one with 400.

If you would rather curate on your phone, **Trakt** is the better fit; its apps beat anything
here, and jetio just follows the list.

### Where titles are filed

| Genres | Goes to |
|---|---|
| Animation | `Kids/Movies`, `Kids/Shows` |
| everything else | `Movies`, `Shows` |

Separate folders mean separate Jellyfin libraries, which is the point — the Kids ones can have
their own user access. Adjust with `KidsGenres`, or set `KidsFolderName` to `""` to keep
everything together.

## Picking releases

`StreamSelection` decides which of Torrentio's ~50 results actually plays:

```jsonc
"VerifyTitles": true,                                // reject releases that are not this film
"ResolutionPriority": [ "1080p", "720p", "2160p" ],  // first match wins
"MinSeeders": 5,                                     // below this, playback stalls
"MaxSizeGb": 15,                                     // caps bandwidth per title
"CacheMinutes": 30                                   // reuse a resolved stream this long
```

Three filters run by default and are worth understanding:

- **`VerifyTitles`** rejects releases that are not the film you asked for. Torrentio's index is
  crowd-sourced and occasionally wrong — a rip of *Iron Man* is indexed under *Spider-Man: Brand
  New Day*. Quality filtering makes that worse rather than better: for an unreleased title every
  genuine release is a cam and gets rejected, leaving the mislabelled clean rip as the winner.
  jetio compares the release name and year against the title's metadata, and would rather return
  nothing than the wrong film.
- **`ExcludePatterns`** drops CAM/TS/screener rips.
- **`MoviePackExcludePatterns`** drops multi-film packs — "IMDB Top 250", "The Matrix 1-4 Pack",
  "Trilogia". These routinely out-seed the correct single release, and picking one plays a
  *different film*. Films only: season packs are fine, because Torrentio pins the exact episode
  with `fileIdx`.

To see the reasoning for any title, add `?debug=1`:

```bash
curl 'http://<JETIO_HOST_IP>:9000/resolve/movie/tt0133093?debug=1'
```

That returns every candidate, ranked, with parsed resolution, seeders and size, plus a
`rejectedBecause` for anything filtered out. It is the first thing to check when a title picks a
release you did not expect.

## Configuration

Settings come from `appsettings.json`, then `/config/jetio.json`, then environment variables —
each overriding the last. Any setting can be set as an environment variable using `__` for
nesting, which is how `docker-compose.yml` passes the values from `.env`:

```bash
Jetio__StreamSelection__MinSeeders=10
Jetio__Catalogs__Cinemeta__Enabled=true
```

| Setting | Default | Purpose |
|---|---|---|
| `LibraryRoot` | `/library` | Where the `.strm` tree is written. |
| `PublicBaseUrl` | — | LAN URL of this service. Baked into every `.strm` file. |
| `MoviesFolderName` / `SeriesFolderName` | `Movies` / `Shows` | Top-level folder names. |
| `KidsFolderName` | `Kids` | Empty disables the split. |
| `KidsGenres` | `["Animation"]` | Genres routed to the Kids folders. |
| `IncludeSpecials` | `false` | Write season 0. |
| `IncludeUnairedEpisodes` | `false` | Write episodes that have not aired. |
| `PruneRemovedItems` | `true` | Delete entries no enabled source claims. |
| `Torrentio.Configuration` | — | Config segment from your Stremio install URL. |
| `StremioServer.BaseUrl` | — | LAN URL of the streaming server (port **11471**). |
| `Jellyfin.LibraryNames` | `[]` | Libraries to rescan. Empty rescans everything. |
| `Sync.IntervalHours` | `12` | How often catalogs are re-read and new episodes written. |
| `Subtitles.MuxIntoStream` | `true` | Embed subtitle files found beside a `.strm`. Off means always redirect. |
| `Subtitles.DefaultLanguage` | — | ISO code marked as the default track, e.g. `bg`. |
| `Subtitles.Extensions` | `[".srt", ".ass", ".ssa"]` | What counts as a subtitle file. |
| `Subtitles.MaxTracks` | `8` | Cap on how many are muxed into one stream. |

## HTTP API

The web UI is a thin client over these, so scripting them is fine.

| Method | Path | Purpose |
|---|---|---|
| `GET`/`HEAD` | `/resolve/movie/{imdbId}` | 302 to a playable stream |
| `GET`/`HEAD` | `/resolve/series/{imdbId}/{season}/{episode}` | 302 to a playable stream |
| `GET` | `…?debug=1` | Ranked candidates instead of a redirect |
| `GET` | `…?refresh=1` | Bypass the resolve cache |
| `GET` | `/api/browse?type=&sort=&genre=&year=&skip=` | Catalog browsing |
| `GET` | `/api/search?q=&type=` | Title search |
| `GET` | `/api/genres` | Genre and sort options for the UI |
| `GET` | `/api/library` | Everything currently managed |
| `POST` | `/api/library` | `{"imdbId":"tt0133093","kind":"Movie"}` |
| `DELETE` | `/api/library/{kind}/{imdbId}` | Remove and delete its files |
| `POST` | `/api/library/{imdbId}/analyze` | Ask Jellyfin to probe an existing title |
| `GET` | `/candidates/movie/{imdbId}` | Ranked options (used by the plugin) |
| `GET` | `/candidates/series/{imdbId}/{season}/{episode}` | Ranked options (used by the plugin) |
| `POST` | `/sync` | Trigger a catalog sync now |
| `GET` | `/status` | Last sync report and effective configuration |
| `GET` | `/healthz` | Streaming server and library root check |
| `GET` | `/plugin/manifest.json` | Jellyfin plugin repository |

> Nothing here is authenticated. `POST /sync`, `POST /api/library` and `DELETE /api/library/…`
> all change state, and the delete endpoint removes files. Keep this on your LAN — see
> [SECURITY.md](SECURITY.md).

## Where the library lives

`JETIO_LIBRARY_PATH` has two requirements that are easy to miss, and each fails differently.

**It must be writable.** Appliance and immutable distributions (TrueNAS SCALE, CoreOS/Flatcar,
Synology DSM) mount `/`, `/srv` and `/usr` read-only; writable data lives under `/mnt/<pool>`,
`/volume1` or `/var`. Pointing at a read-only path fails loudly at `docker compose up`:

```
error while creating mount source path '/srv/jetio-library': read-only file system
```

```bash
findmnt -rno TARGET,FSTYPE,OPTIONS / /var /srv /mnt /volume1 2>/dev/null
```

**Jellyfin must be able to read it.** If Jellyfin runs in a container, creating the folder on the
host is not enough — it has to be bind-mounted into the Jellyfin container too, at the path you
type into the Jellyfin UI. This one fails *silently*: jetio writes files happily and Jellyfin
shows an empty library.

```bash
docker inspect "$(docker ps -qf name=jellyfin)" \
  --format '{{range .Mounts}}{{.Source}} => {{.Destination}}{{println}}{{end}}'
```

The path of least resistance is to put the jetio library **inside a directory Jellyfin already
mounts**, in its own top-level folder. Keep it separate so a prune can never reach your real
files.

## Using a debrid service

With plain P2P, playback waits on the swarm: cold torrents start slowly, seeking is unreliable,
and your IP is visible to every peer. Point Torrentio at a debrid service and it returns direct
HTTPS URLs instead of info hashes — instant start, reliable seeking, no swarm participation.

Put the debrid part of your Stremio install URL into `Torrentio.Configuration`, e.g.
`realdebrid=<apikey>`. jetio prefers a stream's direct `url` whenever Torrentio supplies one and
falls back to the streaming server otherwise, so nothing else changes.

## Troubleshooting

**Nothing appears in Jellyfin.** jetio never creates libraries — it writes files and asks for a
rescan. A rescan of a library that does not exist does nothing. Add them by hand once, then check
the files are really there:

```bash
docker compose exec jetio sh -c 'find /library -name "*.strm" | wc -l'
```

If jetio reports files but Jellyfin shows an empty library, the path Jellyfin sees is not the
path jetio writes to.

**`Jellyfin refresh returned 401`.** The API key is wrong or revoked. jetio sends both the modern
`Authorization: MediaBrowser` header and the legacy `X-Emby-Token`, so a 401 means the key itself
is rejected. Non-fatal — only automatic rescans stop.

```bash
docker compose exec jetio printenv Jetio__Jellyfin__ApiKey
```

**`Stremio streaming server unreachable`.** Check the server's own log:

```bash
docker compose logs stremio-server | grep EngineFS
```

`EngineFS server started at http://127.0.0.1:11470` is expected and correct. Upstream binds to
loopback on purpose so browsers do not flag mixed content, which means a published `11470` is
unreachable — Docker forwards to the container's `eth0`, not its loopback. The `stremio-bridge`
sidecar exists solely to solve this: it shares the server's network namespace and re-exposes it
on **11471**. `StremioServer.BaseUrl` must point at 11471.

**Playback never starts.** Confirm the engine actually delivers bytes:

```bash
URL=$(curl -sS -o /dev/null -w '%{redirect_url}' http://localhost:9000/resolve/movie/tt0133093)
curl -sS -o /dev/null -w '%{http_code} | %{size_download} bytes | %{time_total}s\n' --max-time 90 -r 0-2000000 "$URL"
```

Bytes returned means the problem is Jellyfin-side. Nothing after 90 seconds means the swarm is
not delivering, which is the fundamental weak point of the no-debrid path.

## Limitations

- **Transcoding hurts.** A 4K remux that needs transcoding has to be pulled faster than real time
  while being re-encoded. Prefer releases your clients can direct play.
- **Availability drifts.** A title in the library is not a guarantee it is streamable today.
- **Cold-start latency.** Without debrid, first play on a poorly seeded torrent can take a while,
  and Jellyfin may time out before the first bytes arrive.
- **No tests yet.** The largest known gap; see [CONTRIBUTING.md](CONTRIBUTING.md).

## Development

```bash
dotnet build src/Jetio/Jetio.csproj
dotnet build src/Jellyfin.Plugin.Jetio/Jellyfin.Plugin.Jetio.csproj
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for running against a throwaway library, house rules, and
what to verify before opening a PR.

### Releasing

Releases are driven by [`version.json`](version.json). There is no manual tagging.

**1.** Bump the version:

```json
{ "version": "1.1.0", "targetAbi": "10.11.0.0" }
```

**2.** Add a matching section to `CHANGELOG.md`:

```markdown
## [1.1.0] - 2026-09-01

### Added
- Something worth telling people about.
```

**3.** Push to `main`.

That is the whole process. The [release workflow](.github/workflows/release.yml) then:

1. reads the version and validates it is `MAJOR.MINOR.PATCH`,
2. stops immediately if that tag already exists, so re-runs are harmless,
3. builds and packages the plugin as `1.1.0.0`,
4. extracts release notes from the matching `CHANGELOG.md` section,
5. creates the tag and publishes a GitHub Release with the zip attached,
6. computes the MD5 and commits an updated `manifest.json` back to `main`.

Jellyfin then offers it, both as a first install and as an update to anyone on an older version.

Two details worth knowing if you change this workflow:

- **It triggers only on changes to `version.json`.** That is what stops the `manifest.json`
  commit it makes from retriggering itself.
- **The checksum is why the manifest cannot be hand-written.** Jellyfin verifies the package
  against it and refuses to install on a mismatch, so it can only be generated once the artefact
  actually exists.

CI validates `version.json` on every push and warns if `CHANGELOG.md` has no matching section, so
a malformed bump is caught before it reaches `main`.

## Acknowledgements

Built on the work of others: [Jellyfin](https://jellyfin.org/),
[Stremio](https://www.stremio.com/) for the streaming server and Cinemeta,
and [Torrentio](https://torrentio.strem.fun/) for the stream index.

## License

[MIT](LICENSE).
