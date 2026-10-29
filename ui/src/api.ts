import { host } from "./host";

const BASE = new URL("./api/v1/", location.href).pathname;

export type LinkKind = "VIDEO" | "PLAYLIST" | "MIX" | "ALBUM" | "CHANNEL" | "ARTIST" | "OTHER";
export type AudioFormat = "ORIGINAL" | "OPUS" | "MP3" | "M4A" | "FLAC";
export type AudioQuality = "BEST" | "HIGH" | "MEDIUM";
export type ToolKind = "YT_DLP" | "DENO";
export type BatchStatus = "LISTING" | "ACTIVE" | "DONE" | "FAILED" | "CANCELLED";
export type ItemStatus = "QUEUED" | "DOWNLOADING" | "CONVERTING" | "IMPORTING" | "ADDED" | "DUPLICATE" | "FAILED" | "CANCELLED";

export interface SongTags {
  title?: string | null;
  artist?: string | null;
  album?: string | null;
  albumArtist?: string | null;
  year?: number | null;
  trackNumber?: number | null;
  genre?: string | null;
}

export interface ToolStatus {
  kind: ToolKind;
  path?: string;
  version?: string;
  source?: "CONFIGURED" | "INSTALLED" | "SYSTEM";
  latest?: string;
  canInstall: boolean;
  installed: boolean;
  updateAvailable: boolean;
}

export interface Settings {
  otherSites: string[];
  concurrency: number;
  autoUpdate: boolean;
  maxItems: number;
  defaultFormat: AudioFormat;
  /** The highest picture of music videos when users leave it to the server. */
  videoHeight?: number;
}

/** The pictures a music video can be brought at. */
export const VIDEO_HEIGHTS = [2160, 1440, 1080, 720, 480, 360];

export interface Status {
  ready: boolean;
  isAdmin: boolean;
  cookies: boolean;
  tools: ToolStatus[];
  ffmpeg: { installed: boolean; version?: string };
  formats: AudioFormat[];
  libraries: { id: string; name: string }[];
  settings: Settings;
  version: string;
  /** The server keeps music videos (and this extension can bring them). */
  musicVideos?: boolean;
}

export interface LinkEntry {
  id: string;
  url: string;
  index: number;
  title?: string;
  uploader?: string;
  duration?: number;
  thumbnail?: string;
  available: boolean;
  inLibrary: boolean;
  tags: SongTags;
}

export interface LinkPreview {
  url: string;
  kind: LinkKind;
  id?: string;
  music: boolean;
  listUrl?: string;
  title?: string;
  uploader?: string;
  thumbnail?: string;
  avatar?: string;
  duration?: number;
  date?: string;
  views?: number;
  entryCount?: number;
  tags?: SongTags;
  chapters: number;
  tabs?: { id: string; title: string }[];
  tab?: string;
  entries?: LinkEntry[];
  nextCursor?: string;
  truncated: boolean;
  inLibrary: boolean;
}

/** The user's own settings. */
export interface MyYouTube {
  /** Cookies of their own, used for their links instead of the server's. */
  hasCookies: boolean;
  /** Play each song as it is added to the library. */
  playWhenDone: boolean;
}

export interface DownloadItem {
  id: string;
  videoId: string;
  url: string;
  index?: number;
  title?: string;
  uploader?: string;
  duration?: number;
  status: ItemStatus;
  progress?: number;
  speed?: number;
  eta?: number;
  errorCode?: string;
  tags?: SongTags;
  fileName?: string;
  /** The track of its only file, once catalogued. */
  trackId?: string;
  files: number;
  /** The audio of each file it put into the library. */
  contentHashes?: string[];
  /** The track each of those files became (same order; null until the library catalogues it). */
  trackIds?: (string | null)[];
  /** Only its video, as the music video of this track (a batch of music videos). */
  videoFor?: string;
  updatedAt: string;
}

export interface DownloadBatch {
  id: string;
  url: string;
  kind: LinkKind;
  title?: string;
  libraryId: string;
  libraryName?: string;
  status: BatchStatus;
  errorCode?: string;
  createdAt: string;
  updatedAt: string;
  items: DownloadItem[];
  total: number;
  finished: number;
}

export interface DownloadRequest {
  url: string;
  libraryId: string;
  format?: AudioFormat;
  quality?: AudioQuality;
  embedArtwork?: boolean;
  squareArtwork?: boolean;
  splitChapters?: boolean;
  numberTracks?: boolean;
  tab?: string;
  all?: boolean;
  entries?: { id: string; url?: string; index?: number; title?: string; uploader?: string; duration?: number }[];
  exclude?: string[];
  tags?: SongTags;
  album?: string;
  albumArtist?: string;
  wholeList?: boolean;
  title?: string;
  /** Also each song's music video, next to the song. */
  video?: boolean;
  /** Only each link's music video: next to the song when a library already has it. */
  videoOnly?: boolean;
  /** The highest picture of the music videos; left out, the administrator's. */
  videoHeight?: number;
  /** Only the video, as the music video of this track of the library. */
  videoFor?: string;
}

/** A song of the library whose music video is looked for. */
export interface VideoSearchSong {
  id: string;
  title: string;
  artist: string | null;
  durationMs: number;
  hasVideo: boolean;
}

/** A video that may be the song's music video. */
export interface VideoCandidate {
  id: string;
  url: string;
  title: string;
  channel: string | null;
  duration: number | null;
  thumbnail: string | null;
  score: number;
  official: boolean;
}

export interface VideoSearchResult {
  song: VideoSearchSong;
  query: string;
  candidates: VideoCandidate[];
}

/** A song of the libraries the user can add to. */
export interface LibrarySong {
  id: string;
  title: string;
  artist: string | null;
  album: string;
  year: number | null;
  durationMs: number;
  artworkId: string | null;
  hasVideo: boolean;
}

export interface SongPage {
  items: LibrarySong[];
  /** The offset of the next page; null on the last one. */
  next: number | null;
  totalCount: number;
}

export interface SongQuery {
  search?: string;
  libraryId?: string;
  withoutVideo?: boolean;
  offset?: number;
  first?: number;
}

/** The music videos of many songs: the video chosen for each. */
export interface VideoDownloadsRequest {
  items: { trackId: string; url: string; title?: string }[];
  title?: string;
  videoHeight?: number;
}

export class ApiError extends Error {
  constructor(
    readonly code: string,
    readonly status: number,
  ) {
    super(code);
  }
}

/** A call with the session's token; refused once, the token is renewed and the call made again. */
export async function call<T>(path: string, init: RequestInit = {}): Promise<T> {
  const send = () =>
    fetch(BASE + path, {
      ...init,
      headers: {
        ...(init.body ? { "Content-Type": "application/json" } : {}),
        ...(host.token ? { Authorization: `Bearer ${host.token}` } : {}),
        ...init.headers,
      },
    });
  let response = await send().catch(() => null);
  if (response?.status === 401 && (await host.renew())) response = await send().catch(() => null);
  if (!response) throw new ApiError("network", 0);
  if (response.status === 204) return undefined as T;
  const body = await response.json().catch(() => null);
  if (!response.ok) {
    // Problem details with the code, or the code of a field (validation).
    const fields = body?.errors ? (Object.values(body.errors)[0] as string[] | undefined) : undefined;
    throw new ApiError(fields?.[0] ?? body?.code ?? `http_${response.status}`, response.status);
  }
  return body as T;
}

const json = (value: unknown): RequestInit => ({ body: JSON.stringify(value) });

export const api = {
  status: () => call<Status>("status"),
  inspect: (url: string, tab?: string, after?: string) => {
    const query = new URLSearchParams({ url });
    if (tab) query.set("tab", tab);
    if (after) query.set("after", after);
    return call<LinkPreview>(`inspect?${query}`);
  },
  download: (request: DownloadRequest) => call<DownloadBatch>("downloads", { method: "POST", ...json(request) }),
  downloads: () => call<DownloadBatch[]>("downloads"),
  cancel: (id: string, item?: string) => call<DownloadBatch>(`downloads/${id}/cancel${item ? `?item=${item}` : ""}`, { method: "POST" }),
  retry: (id: string, item?: string) => call<DownloadBatch>(`downloads/${id}/retry${item ? `?item=${item}` : ""}`, { method: "POST" }),
  remove: (id: string) => call<void>(`downloads/${id}`, { method: "DELETE" }),
  clear: () => call<{ removed: number }>("downloads", { method: "DELETE" }),
  preview: (url: string) => call<{ url: string }>("preview", { method: "POST", ...json({ url }) }),
  videoSearch: (trackId: string) => call<VideoSearchResult>(`video-search?trackId=${encodeURIComponent(trackId)}`),
  songs: (query: SongQuery) => {
    const params = new URLSearchParams();
    if (query.search) params.set("search", query.search);
    if (query.libraryId) params.set("libraryId", query.libraryId);
    if (query.withoutVideo) params.set("withoutVideo", "true");
    if (query.offset) params.set("offset", String(query.offset));
    if (query.first) params.set("first", String(query.first));
    return call<SongPage>(`songs?${params}`);
  },
  videoDownloads: (request: VideoDownloadsRequest) => call<DownloadBatch>("video-downloads", { method: "POST", ...json(request) }),
  me: () => call<MyYouTube>("me"),
  setPreferences: (playWhenDone: boolean) => call<MyYouTube>("me/preferences", { method: "PUT", ...json({ playWhenDone }) }),
  saveMyCookies: (text: string) => call<MyYouTube>("me/cookies", { method: "PUT", body: text, headers: { "Content-Type": "text/plain" } }),
  removeMyCookies: () => call<MyYouTube>("me/cookies", { method: "DELETE" }),
};

/**
 * Follows the user's downloads as they change (server-sent events, read with fetch so the token can go in a
 * header). Reconnects by itself; returns a function that stops it.
 */
export function watchDownloads(onBatch: (batch: DownloadBatch) => void, onConnected: () => void): () => void {
  let stopped = false;
  let controller: AbortController | null = null;
  let wait = 1000;

  const run = async () => {
    while (!stopped) {
      controller = new AbortController();
      try {
        let response = await fetch(BASE + "downloads/events", {
          headers: host.token ? { Authorization: `Bearer ${host.token}` } : {},
          signal: controller.signal,
        });
        if (response.status === 401 && (await host.renew()))
          response = await fetch(BASE + "downloads/events", {
            headers: host.token ? { Authorization: `Bearer ${host.token}` } : {},
            signal: controller.signal,
          });
        if (!response.ok || !response.body) throw new Error(String(response.status));
        wait = 1000;
        onConnected();
        const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
        let buffer = "";
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += value;
          let end: number;
          while ((end = buffer.indexOf("\n\n")) >= 0) {
            const block = buffer.slice(0, end);
            buffer = buffer.slice(end + 2);
            const data = block
              .split("\n")
              .filter((line) => line.startsWith("data: "))
              .map((line) => line.slice(6))
              .join("\n");
            if (data) onBatch(JSON.parse(data) as DownloadBatch);
          }
        }
      } catch {
        // Dropped: try again a little later.
      }
      if (stopped) break;
      await new Promise((resolve) => setTimeout(resolve, wait));
      wait = Math.min(wait * 2, 15000);
    }
  };
  void run();
  return () => {
    stopped = true;
    controller?.abort();
  };
}
