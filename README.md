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

Its settings (tools, the server's cookies, other sites, downloads at a time, default format, quality of music videos) are in Arcanist's admin
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
library and the cover. Downloads go on in the server; their progress shows live. Opened from a player, the list of
downloads plays what is already in the library (one song, a list, or everything finished); clearing the finished ones
asks first.

### Responsible use

The first time each user opens the app, it shows a short notice: download only what belongs to you or what you have
the right to keep; YouTube's Terms of Service do not allow downloading its content except where YouTube offers it, and
complying with them (and with copyright) is up to whoever downloads; the extension is not YouTube's or Google's. It can
be accepted only after it has been on screen for a few seconds (the app counts them down, the server checks them), once
per user. Administrators see it at the top of **Extensions › YouTube Audio** until they accept it; it stays at the end
of that page, and in the app's footer.

### Songs you already have

Before anything is downloaded again, the app says when you already have a song: a song is the same when its name,
without what says which pressing or kind of video it is ("(Official Video)", "(Remastered 2011)", "feat. …"), and one of
its artists are the same, **on any album** (YouTube often names the single or another album than your library).
"Live", "Acoustic", "Remix" and the like stay in the name: those are other songs. A single video shows the copies you
have (album, year, format, library) and is only brought again if you tick **Add it anyway**; in a list those songs start
unchosen, also with **All**.

### Explore

**Explore** searches YouTube Music (songs, albums, artists, videos and playlists, or all at once) and opens artists'
pages: their songs, albums, singles, videos and the artists their fans also like. Before searching it shows **New from
your artists** (this year's and last year's albums and singles by the 20 artists with the most songs in your libraries,
without those you have), **New releases** and the **Charts** of the country chosen. Everything you have is marked;
choosing a song, album or list opens it as a pasted link, where you choose what to bring and where, and **Back to
explore** returns to where you were.

It reads YouTube Music through the API of YouTube Music's own web app (no account, nothing downloaded); pictures go
through the server as everywhere else in the app. Answers are kept for a while (15 minutes for searches, 6 hours for
pages and news).

### Music videos

With Arcanist 0.1.0-alpha.11 or later (music videos), the app asks what to bring from each link:

- **Audio**, as always.
- **Audio and music video**: the song, and its video next to it in the library.
- **Only the music video**, for songs your libraries already have: the extension finds the song by its title and artist
  (as Arcanist's scans link music videos; live performances only go with live recordings) and puts the video next to
  it. When no library has the song yet, the video goes into the library chosen and is linked when the song arrives.

Arcanist then plays each video with the song's own audio where they match. The **video quality** is the server's
(the administrator chooses it in **Extensions › YouTube Audio**, 1080p by default) or one the user picks, from 360p up
to 2160p (4K UHD); YouTube gives the best it has up to that.

A player's song menu also offers **Find the music video on YouTube**: the app searches YouTube for that song, ranks
what it finds (title and artist, official channels, length) and adds the one chosen next to the song.

The app's **Music videos for my songs** view does the same from the app: it lists the songs of the libraries you can
add to (searched by title, artist or album, by default only those without a music video yet), and finds one song's
video, or **many at once**: choose songs (or all those of a search, up to 200) and the app looks for each one's video,
two at a time. The likeliest video of each is chosen when it looks right (60% or more); for the others, choose among
the candidates or leave the song out. Then everything chosen downloads as one batch of the queue, each video next to
its song, at the quality chosen.

## API

The app is a client of the extension's REST API, under `/ext/youtube/api/v1` with Arcanist's Bearer token: see
[`openapi.json`](ui/public/openapi.json) (served at `/ext/youtube/api/v1/openapi.json`). In short:

- `GET status`, `GET inspect?url=&tab=&after=`
- `POST downloads` (one video with its `tags`, chosen `entries` of a list, or `all` but `exclude`; `video`,
  `videoOnly`, `videoHeight` and `videoFor` for music videos), `GET video-search?trackId=`,
  `GET songs?search=&libraryId=&withoutVideo=&offset=&first=` (the songs whose videos can be looked for),
  `POST video-downloads` (many songs' music videos as one batch: `items` of `trackId` and `url`), `GET downloads`
  (each song in the library says the tracks it became, `trackIds`, once the library catalogues them),
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
