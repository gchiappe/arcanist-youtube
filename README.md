# YouTube Audio for Arcanist

An extension for [Arcanist](https://github.com/linkfast/arcanist) that brings the audio of YouTube videos, playlists,
channels and YouTube Music albums into your libraries, with [yt-dlp](https://github.com/yt-dlp/yt-dlp). Each song
comes in as **the user's upload**: it goes where the library's template says and shows in their history of uploads.
The administrator can allow other sites yt-dlp reads (SoundCloud, Bandcamp…).

Download only what you have the right to keep.

## Installing

Version 0.2 needs Arcanist 0.1.0-alpha.2 or later (its settings page is drawn by the panel); 0.1 works with 0.1.0-alpha.1.

1. In Arcanist's panel, **Extensions → Install from URL**, with the `.zip` of a
   [release](https://github.com/gchiappe/arcanist-youtube/releases) (or upload it).
2. Switch it on and choose **Restart now**.
3. In the admin panel, **Extensions › YouTube Audio**, install yt-dlp and Deno: the extension downloads them from their official releases, checks them against their published
   SHA-256 and keeps them up to date. yt-dlp needs [Deno](https://deno.com) to get every format from YouTube.
   Converting (Opus, MP3, M4A, FLAC) and previews use Arcanist's ffmpeg.

Users need the **UPLOAD** permission and a library they can upload to.

Its settings (tools, the server's cookies, other sites, downloads at a time, default format) are in Arcanist's admin
panel, under **Extensions › YouTube Audio**, which the panel draws from the page the extension describes.

If YouTube asks the server to "confirm you're not a bot", add the cookies of a YouTube session there (a `cookies.txt`
exported from a browser; use an account of its own). Each user can also add their own in the app's **My settings**:
theirs are used for their links instead of the server's. All are kept encrypted and never shown back.

In **My settings** users also choose to play each song as soon as it is added to their library: the app asks the
player that opened it to play it after what is playing (Arcanist's `arcanist:play`).

## Using it

Paste one or more links: videos (`watch`, `youtu.be`, shorts, live), playlists, mixes, channels (and their tabs:
releases, videos…), YouTube Music songs, albums and artists, or text with links in it. The app shows what each one
has, lets you listen to 30 seconds, edit the tags, pick songs of a list (or all of them), and choose the format, the
library and the cover. Downloads go on in the server; their progress shows live.

## API

The app is a client of the extension's REST API, under `/ext/youtube/api/v1` with Arcanist's Bearer token: see
[`openapi.json`](ui/public/openapi.json) (served at `/ext/youtube/api/v1/openapi.json`). In short:

- `GET status`, `GET inspect?url=&tab=&after=`
- `POST downloads` (one video with its `tags`, chosen `entries` of a list, or `all` but `exclude`), `GET downloads`,
  `GET downloads/{id}`, `POST downloads/{id}/cancel|retry`, `DELETE downloads/{id}`, `GET downloads/events` (SSE)
- `POST preview` → a link to 30 seconds of audio
- each user's: `GET me`, `PUT me/preferences`, `PUT|DELETE me/cookies`
- administrators: `GET|PUT settings`, `POST tools/install|check`, `PUT|DELETE cookies`

The app talks to the player that opened it with Arcanist's host protocol (session, language and theme by
`postMessage`), so it looks like whatever player opens it.

## Building

Needs .NET 10, Node 24 and a checkout of Arcanist beside this one (`../../Arcanist`, or `ARCANIST_ROOT`):

```sh
./pack.sh                        # artifacts/arcanist-youtube-<version>.zip and its .sha256
dotnet test --project tests/Arcanist.YouTube.Tests --artifacts-path artifacts/build
cd ui && npm ci && npm run dev   # the app against a running Arcanist (dotnet run, port 5263)
```

The tests run a real Arcanist in memory with the extension installed and a fake yt-dlp: they never reach the internet.

## License

AGPL-3.0-or-later, like Arcanist. yt-dlp (Unlicense) and Deno (MIT) are separate programs the extension downloads and
runs; they are not part of it. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
