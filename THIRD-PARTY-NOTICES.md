# Third-party notices

## Shipped in the package

| Component | License | Use |
|---|---|---|
| [Preact](https://preactjs.com) | MIT | The app's interface (bundled into `wwwroot`). |

The app's icons are drawn like [Lucide](https://lucide.dev)'s (ISC).

## Run, not shipped

The extension downloads these programs from their official releases when an administrator asks, checks them against
the SHA-256 sums those releases publish, and runs them as separate processes:

| Program | License |
|---|---|
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | The Unlicense (public domain) |
| [Deno](https://github.com/denoland/deno) | MIT |

Arcanist's ffmpeg (found or installed by Arcanist) converts and makes the previews.

## Build tools

Vite, @preact/preset-vite and TypeScript (MIT or Apache-2.0) build the app; they are not shipped.
