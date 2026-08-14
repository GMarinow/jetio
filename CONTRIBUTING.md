# Contributing

Thanks for taking an interest. This is a small project with a narrow purpose, so the bar for
changes is "does it make jetio work better at what it already does".

## Getting set up

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download). Docker is optional for
development but required to run the full stack.

```bash
dotnet build src/Jetio/Jetio.csproj
dotnet build src/Jellyfin.Plugin.Jetio/Jellyfin.Plugin.Jetio.csproj
```

Run the service against a throwaway library so you never touch a real one:

```bash
Urls=http://127.0.0.1:9099 \
Jetio__LibraryRoot=/tmp/jetio-library \
Jetio__Catalogs__Managed__Path=/tmp/jetio-library.json \
Jetio__Jellyfin__TriggerRefresh=false \
Jetio__Sync__RunOnStartup=false \
dotnet run --project src/Jetio/Jetio.csproj
```

Then open <http://127.0.0.1:9099>.

## House rules for code

Both projects build with `TreatWarningsAsErrors`, so a warning fails CI.

- **Match the surrounding style.** No new abstractions unless something concrete needs them.
- **Comment the *why*, never the *what*.** Most comments in this codebase exist because the
  behaviour is surprising — Cinemeta's dead `year` catalog, the streaming server binding to
  loopback inside its own container, Torrentio mislabelling releases. That is what deserves
  a comment.
- **Prefer failing visibly over guessing.** jetio returns no stream rather than the wrong film,
  and refuses to start with a bad address rather than writing thousands of broken `.strm` files.
- **Never widen what gets deleted.** Pruning removes files. Changes near `StrmLibraryWriter.Prune`
  or `RemoveTitle` get extra scrutiny.

## Testing changes

There is no automated test suite yet — that is the largest known gap, and PRs adding one are
very welcome. Start with the pure functions, which carry the fiddliest logic:

- `StreamSelector` — parsing seeders and sizes out of emoji-annotated titles, pack detection
- `TitleMatcher` — the mislabelled-release check
- `StrmLibraryWriter` — filename sanitisation, year extraction, Kids routing

Until then, verify by hand and say in the PR what you actually ran. `?debug=1` on any resolve
URL shows the full ranked candidate list with rejection reasons, which is the fastest way to
see what the selector did:

```bash
curl 'http://127.0.0.1:9099/resolve/movie/tt0133093?debug=1'
```

## Pull requests

Keep them focused — one concern per PR. Explain what you changed and why, and mention anything
you could not test. If you found a real problem in the process, say so; that is more useful
than a tidy diff.

## Releasing

Maintainers only, and there is nothing to remember: bump `version` in `version.json`, add a
matching `## [x.y.z]` section to `CHANGELOG.md`, and push to `main`. The workflow tags, builds,
publishes and updates the plugin catalogue by itself. See
[Releasing](README.md#releasing) for what it actually does.

Do not create tags by hand — the workflow owns them, and a tag it did not create will make it
skip the release as already published.

## Reporting bugs

Include the jetio log around the failure, the IMDb id if it concerns a specific title, and the
`?debug=1` output if it concerns stream selection. "It picked the wrong release" is almost
always answerable from that one URL.
