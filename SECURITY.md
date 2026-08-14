# Security

## Reporting a vulnerability

Please report security issues privately through
[GitHub's private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
rather than opening a public issue.

Include what an attacker could do, and the steps to reproduce it. You should get a first
response within a week.

## Threat model

jetio is built for a trusted LAN and nothing more. Be aware of the following before deploying
it anywhere else.

**There is no authentication.** Every endpoint is open to anyone who can reach the port.
`POST /sync`, `POST /api/library` and `DELETE /api/library/...` all change state, and the
delete endpoint removes files from your library directory. Do not expose port 9000 beyond
your LAN, and do not put it behind a reverse proxy without adding authentication in front.

**The Stremio streaming server joins public torrent swarms.** Your IP address is visible to
every peer in any swarm you stream from. jetio does not proxy or anonymise this traffic. If
that matters where you live, use a debrid service — Torrentio then returns direct HTTPS URLs
and no swarm participation happens at all.

**jetio deletes files it does not recognise.** Everything under `LibraryRoot` that is not
claimed by an enabled catalog source is pruned. Point it at a dedicated directory. Never at
one containing media you care about.

**The Jellyfin API key is stored in plaintext** in `.env` or `config/jetio.json`. It grants
full administrative access to your Jellyfin server. Keep those files out of version control
(`.gitignore` already covers them) and readable only by the user running jetio.

**Outbound requests go to third parties.** jetio queries Cinemeta, Torrentio, and optionally
TMDB and Trakt. Those services see the IMDb ids you look up.

## What is not a vulnerability

- The absence of authentication on a LAN-only service — that is documented above, not a bug.
- Torrentio returning wrong or malicious metadata. jetio validates release titles against
  expected metadata precisely because that upstream data is untrusted; if you find a way past
  that validation, though, please do report it.
