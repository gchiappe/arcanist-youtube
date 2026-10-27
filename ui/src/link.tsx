import { useEffect, useRef, useState } from "preact/hooks";
import { api, ApiError, type DownloadRequest, type LinkEntry, type LinkPreview, type SongTags, type Status } from "./api";
import { date, duration, errorText, number, t } from "./i18n";
import { Icon } from "./icons";
import type { Choices } from "./options";
import type { Toast } from "./main";

/** A link pasted, and what the user chose of it. */
export interface LinkState {
  key: string;
  input: string;
  loading: boolean;
  error?: string;
  preview?: LinkPreview;
  /** The single video, while its list is shown instead ("the whole list"). */
  video?: LinkPreview;
  entries: LinkEntry[];
  chosen: Set<string>;
  /** Every song of the list (also those not loaded), but those excluded. */
  allMode: boolean;
  excluded: Set<string>;
  tags: SongTags;
  album: string;
  numberTracks: boolean;
  tab?: string;
  split: boolean;
  filter: string;
  loadingMore: boolean;
}

let counter = 0;

export function newLink(input: string): LinkState {
  return {
    key: `link-${++counter}`,
    input,
    loading: true,
    entries: [],
    chosen: new Set(),
    allMode: false,
    excluded: new Set(),
    tags: {},
    album: "",
    numberTracks: false,
    split: false,
    filter: "",
    loadingMore: false,
  };
}

const isList = (preview?: LinkPreview) => !!preview && preview.kind !== "VIDEO" && preview.kind !== "OTHER";

/** What a new preview starts with: the songs not in the library yet are chosen. */
export function initFrom(preview: LinkPreview): Partial<LinkState> {
  return {
    preview,
    error: undefined,
    entries: preview.entries ?? [],
    chosen: new Set((preview.entries ?? []).filter((e) => e.available && !e.inLibrary).map((e) => e.id)),
    allMode: false,
    excluded: new Set(),
    tags: preview.tags ? { ...preview.tags } : {},
    album: preview.kind === "ALBUM" ? (preview.title ?? "") : "",
    numberTracks: preview.kind === "ALBUM",
    tab: preview.tab,
    split: false,
    filter: "",
  };
}

export function chosenCount(link: LinkState): number {
  if (!link.preview || link.loading) return 0;
  if (!isList(link.preview)) return 1;
  if (link.allMode) return Math.max(0, (link.preview.entryCount ?? link.entries.length) - link.excluded.size);
  return link.chosen.size;
}

/** The download a link asks for, with the options chosen for all. */
export function requestFor(link: LinkState, choices: Choices): DownloadRequest | null {
  const preview = link.preview;
  if (!preview || chosenCount(link) === 0) return null;
  const common: DownloadRequest = {
    url: preview.url,
    libraryId: choices.libraryId,
    format: choices.format,
    quality: choices.quality,
    embedArtwork: choices.artwork,
    squareArtwork: choices.square,
    title: preview.title,
  };
  if (!isList(preview)) {
    const tags = link.tags;
    return {
      ...common,
      splitChapters: link.split,
      video: choices.video || undefined,
      tags: tags.title?.trim() ? { ...tags, year: tags.year ? Number(tags.year) : null } : undefined,
    };
  }
  const list: DownloadRequest = {
    ...common,
    tab: link.tab,
    album: link.album.trim() || undefined,
    numberTracks: link.numberTracks,
  };
  if (link.allMode) return { ...list, all: true, exclude: [...link.excluded] };
  return {
    ...list,
    entries: link.entries
      .filter((e) => link.chosen.has(e.id))
      .map((e) => ({ id: e.id, url: e.url, index: e.index, title: e.title, uploader: e.uploader, duration: e.duration })),
  };
}

// ── The one preview playing ─────────────────────────────────────────────────

let audio: HTMLAudioElement | null = null;
let playing: string | null = null;
const listeners = new Set<() => void>();

function setPlaying(url: string | null) {
  playing = url;
  listeners.forEach((l) => l());
}

function usePlaying(): string | null {
  const [, redraw] = useState(0);
  useEffect(() => {
    const listener = () => redraw((n) => n + 1);
    listeners.add(listener);
    return () => void listeners.delete(listener);
  }, []);
  return playing;
}

function Listen({ url, show, small }: { url: string; show: (toast: Toast) => void; small?: boolean }) {
  const current = usePlaying();
  const [loading, setLoading] = useState(false);
  const on = current === url;
  const toggle = async (event: Event) => {
    event.stopPropagation();
    audio ??= new Audio();
    if (on) {
      audio.pause();
      setPlaying(null);
      return;
    }
    setLoading(true);
    try {
      const link = await api.preview(url);
      audio.pause();
      audio.src = link.url;
      audio.onended = () => setPlaying(null);
      audio.onerror = () => {
        setPlaying(null);
        show({ text: errorText("download_failed"), error: true });
      };
      setPlaying(url);
      await audio.play();
    } catch (error) {
      setPlaying(null);
      show({ text: errorText(error instanceof ApiError ? error.code : "unknown"), error: true });
    } finally {
      setLoading(false);
    }
  };
  return (
    <button
      class={`btn ${small ? "ghost icon" : "small"}`}
      onClick={toggle}
      title={on ? t("stop") : t("listen")}
      aria-label={on ? t("stop") : t("listen")}
      aria-pressed={on}
    >
      <Icon name={loading ? "loader" : on ? "stop" : "play"} class={loading ? "spin" : undefined} />
      {!small && <span>{on ? t("stop") : t("listen")}</span>}
    </button>
  );
}

// ── The card ────────────────────────────────────────────────────────────────

interface CardProps {
  link: LinkState;
  status: Status;
  choices: Choices;
  update: (change: (link: LinkState) => LinkState) => void;
  remove?: () => void;
  show: (toast: Toast) => void;
}

export function LinkCard(props: CardProps) {
  const { link, remove } = props;
  if (link.loading)
    return (
      <div class="card" aria-busy="true">
        <div class="preview">
          <div class="thumb skeleton" />
          <div style={{ display: "grid", gap: 8 }}>
            <div class="skeleton" style={{ height: 18, width: "80%" }} />
            <div class="skeleton" style={{ height: 12, width: "50%" }} />
            <span class="muted small">{t("loading")}</span>
          </div>
        </div>
      </div>
    );
  if (link.error || !link.preview)
    return (
      <div class="card">
        <div class="row">
          <div class="grow">
            <div class="ellipsis small muted">{link.input}</div>
            <div class="notice error">{errorText(link.error ?? "unknown")}</div>
          </div>
          {remove && (
            <button class="btn ghost icon" onClick={remove} aria-label={t("remove")}>
              <Icon name="x" />
            </button>
          )}
        </div>
      </div>
    );
  return isList(link.preview) ? <ListCard {...props} /> : <VideoCard {...props} />;
}

function Kind({ preview }: { preview: LinkPreview }) {
  return (
    <span class="pill accent">
      {preview.music ? "YouTube Music · " : ""}
      {t(`kinds.${preview.kind}`)}
    </span>
  );
}

function VideoCard({ link, status, update, remove, show }: CardProps) {
  const preview = link.preview!;
  const tags = link.tags;
  const setTag = (key: keyof SongTags, value: string) => update((l) => ({ ...l, tags: { ...l.tags, [key]: value } }));

  const wholeList = async () => {
    if (!preview.listUrl) return;
    update((l) => ({ ...l, loading: true, video: preview }));
    try {
      const list = await api.inspect(preview.listUrl);
      update((l) => ({ ...l, ...initFrom(list), loading: false }));
    } catch (error) {
      update((l) => ({ ...l, loading: false, preview: l.video, error: undefined }));
      show({ text: errorText(error instanceof ApiError ? error.code : "unknown"), error: true });
    }
  };

  return (
    <div class="card">
      <div class="preview">
        <div class="thumb">
          {preview.thumbnail && <img src={preview.thumbnail} alt="" loading="lazy" />}
          {preview.duration != null && <span class="badge-time">{duration(preview.duration)}</span>}
        </div>
        <div style={{ display: "grid", gap: 8, minWidth: 0 }}>
          <div class="row">
            <Kind preview={preview} />
            {preview.inLibrary && <span class="pill ok">{t("inLibrary")}</span>}
            {preview.chapters > 1 && <span class="pill">{t("chapters", { count: preview.chapters })}</span>}
            <span class="grow" />
            {remove && (
              <button class="btn ghost icon" onClick={remove} aria-label={t("remove")}>
                <Icon name="x" />
              </button>
            )}
          </div>
          <h2>{preview.title}</h2>
          <div class="meta">
            {preview.uploader && <span>{preview.uploader}</span>}
            {preview.date && <span>{date(preview.date)}</span>}
            {preview.views != null && <span>{t("views", { count: number(preview.views) })}</span>}
          </div>
          <div class="row">
            <Listen url={preview.url} show={show} />
          </div>
        </div>
      </div>

      {preview.listUrl && (
        <div class="row">
          <span class="muted small">{t("whichOne")}</span>
          <div class="segments" role="group">
            <button aria-pressed="true">{t("onlyThis")}</button>
            <button aria-pressed="false" onClick={() => void wholeList()}>
              {t("wholeList")}
            </button>
          </div>
        </div>
      )}

      <details open={!tags.artist}>
        <summary>
          {t("tags")}
          <span class="muted"> · {[tags.artist, tags.title].filter(Boolean).join(" – ")}</span>
        </summary>
        <div class="grid2" style={{ marginTop: 10 }}>
          <TagField label={t("tagTitle")} value={tags.title} onInput={(v) => setTag("title", v)} />
          <TagField label={t("tagArtist")} value={tags.artist} onInput={(v) => setTag("artist", v)} />
          <TagField label={t("tagAlbum")} value={tags.album} onInput={(v) => setTag("album", v)} placeholder={tags.title ?? ""} />
          <TagField label={t("tagYear")} value={tags.year?.toString()} onInput={(v) => setTag("year", v.replace(/\D/g, "").slice(0, 4))} inputMode="numeric" />
        </div>
      </details>

      {preview.chapters > 1 && (
        <label class="check" title={status.ffmpeg.installed ? undefined : t("noFfmpeg")}>
          <input
            type="checkbox"
            checked={link.split}
            disabled={!status.ffmpeg.installed}
            onChange={(e) => update((l) => ({ ...l, split: (e.target as HTMLInputElement).checked }))}
          />
          {t("split")} <span class="muted small">({t("chapters", { count: preview.chapters })})</span>
        </label>
      )}
    </div>
  );
}

function TagField(props: { label: string; value?: string | null; onInput: (value: string) => void; placeholder?: string; inputMode?: string }) {
  return (
    <div class="field">
      <label>{props.label}</label>
      <input
        class="input"
        value={props.value ?? ""}
        placeholder={props.placeholder}
        inputMode={props.inputMode as never}
        onInput={(e) => props.onInput((e.target as HTMLInputElement).value)}
      />
    </div>
  );
}

function ListCard({ link, update, remove, show }: CardProps) {
  const preview = link.preview!;
  const list = useRef<HTMLDivElement>(null);
  const sentinel = useRef<HTMLDivElement>(null);
  const square = preview.kind === "ALBUM" || preview.kind === "ARTIST";

  const loadMore = async () => {
    if (!link.preview?.nextCursor || link.loadingMore) return;
    update((l) => ({ ...l, loadingMore: true }));
    try {
      const page = await api.inspect(link.preview.url, link.tab, link.preview.nextCursor);
      update((l) => {
        // Everything chosen so far: the new songs too.
        const allChosen = l.entries.filter((e) => e.available && !e.inLibrary).every((e) => l.chosen.has(e.id));
        const chosen = new Set(l.chosen);
        if (allChosen) for (const e of page.entries ?? []) if (e.available && !e.inLibrary) chosen.add(e.id);
        return {
          ...l,
          loadingMore: false,
          entries: [...l.entries, ...(page.entries ?? [])],
          chosen,
          preview: { ...l.preview!, nextCursor: page.nextCursor },
        };
      });
    } catch (error) {
      update((l) => ({ ...l, loadingMore: false }));
      show({ text: errorText(error instanceof ApiError ? error.code : "unknown"), error: true });
    }
  };

  // More as the list scrolls to its end.
  useEffect(() => {
    if (!sentinel.current || !list.current || !preview.nextCursor) return;
    const observer = new IntersectionObserver((seen) => seen.some((s) => s.isIntersecting) && void loadMore(), { root: list.current });
    observer.observe(sentinel.current);
    return () => observer.disconnect();
  });

  const switchTab = async (tab: string) => {
    if (tab === link.tab) return;
    update((l) => ({ ...l, tab, loadingMore: true }));
    try {
      const page = await api.inspect(link.input, tab);
      update((l) => ({ ...l, ...initFrom(page), loadingMore: false }));
    } catch (error) {
      update((l) => ({ ...l, loadingMore: false }));
      show({ text: errorText(error instanceof ApiError ? error.code : "unknown"), error: true });
    }
  };

  const filter = link.filter.trim().toLowerCase();
  const shown = filter
    ? link.entries.filter((e) => `${e.title ?? ""} ${e.uploader ?? ""}`.toLowerCase().includes(filter))
    : link.entries;
  const selected = (e: LinkEntry) => (link.allMode ? e.available && !link.excluded.has(e.id) : link.chosen.has(e.id));
  const toggle = (e: LinkEntry) => {
    if (!e.available) return;
    update((l) => {
      if (l.allMode) {
        const excluded = new Set(l.excluded);
        if (excluded.has(e.id)) excluded.delete(e.id);
        else excluded.add(e.id);
        return { ...l, excluded };
      }
      const chosen = new Set(l.chosen);
      if (chosen.has(e.id)) chosen.delete(e.id);
      else chosen.add(e.id);
      return { ...l, chosen };
    });
  };
  const total = preview.entryCount ?? link.entries.length;
  const hasMore = !!preview.nextCursor;
  const time = link.entries.filter(selected).reduce((sum, e) => sum + (e.duration ?? 0), 0);

  const back = () => {
    if (!link.video) return;
    update((l) => ({ ...l, ...initFrom(l.video!), video: undefined }));
  };

  return (
    <div class="card">
      <div class="preview" style={{ gridTemplateColumns: "minmax(0, 120px) minmax(0, 1fr)" }}>
        <div class={`thumb${square ? " square" : ""}`}>{preview.thumbnail && <img src={preview.thumbnail} alt="" loading="lazy" />}</div>
        <div style={{ display: "grid", gap: 6, minWidth: 0 }}>
          <div class="row">
            <Kind preview={preview} />
            {total > 0 && <span class="pill">{t("songs", { count: number(total) })}</span>}
            <span class="grow" />
            {remove && (
              <button class="btn ghost icon" onClick={remove} aria-label={t("remove")}>
                <Icon name="x" />
              </button>
            )}
          </div>
          <h2>{preview.title}</h2>
          <div class="meta">
            {preview.avatar && <img class="avatar" src={preview.avatar} alt="" />}
            {preview.uploader && <span>{preview.uploader}</span>}
            {preview.date && <span>{date(preview.date)}</span>}
          </div>
        </div>
      </div>

      {link.video && (
        <div class="row">
          <span class="muted small">{t("whichOne")}</span>
          <div class="segments" role="group">
            <button aria-pressed="false" onClick={back}>
              {t("onlyThis")}
            </button>
            <button aria-pressed="true">{t("wholeList")}</button>
          </div>
        </div>
      )}

      {preview.tabs && preview.tabs.length > 1 && (
        <div class="segments" role="tablist">
          {preview.tabs.map((tab) => (
            <button key={tab.id} role="tab" aria-pressed={tab.id === link.tab} onClick={() => void switchTab(tab.id)}>
              {t(`tabs.${tab.id}`)}
            </button>
          ))}
        </div>
      )}

      {preview.truncated && <div class="notice">{t("truncated")}</div>}

      {(preview.kind === "ALBUM" || preview.kind === "PLAYLIST") && (
        <div class="grid2">
          <div class="field">
            <label>{t("albumFor")}</label>
            <input class="input" value={link.album} onInput={(e) => update((l) => ({ ...l, album: (e.target as HTMLInputElement).value }))} />
          </div>
          <label class="check" style={{ alignSelf: "end", paddingBottom: 8 }}>
            <input
              type="checkbox"
              checked={link.numberTracks}
              onChange={(e) => update((l) => ({ ...l, numberTracks: (e.target as HTMLInputElement).checked }))}
            />
            {t("numberTracks")}
          </label>
        </div>
      )}

      <div class="row">
        <input
          class="input grow"
          style={{ width: "auto" }}
          placeholder={t("filter")}
          value={link.filter}
          onInput={(e) => update((l) => ({ ...l, filter: (e.target as HTMLInputElement).value }))}
        />
        <button class="btn small" aria-pressed={link.allMode} onClick={() => update((l) => ({ ...l, allMode: true, excluded: new Set() }))}>
          <Icon name="check" /> {t("selectAll", { count: number(total) })}
        </button>
        <button class="btn small" onClick={() => update((l) => ({ ...l, allMode: false, chosen: new Set(), excluded: new Set() }))}>
          {t("selectNone")}
        </button>
      </div>

      <div class="entries" ref={list} role="listbox" aria-multiselectable="true">
        {shown.map((entry) => (
          <div
            key={entry.id + entry.index}
            class={`entry${entry.available ? "" : " off"}`}
            role="option"
            aria-selected={selected(entry)}
            onClick={() => toggle(entry)}
          >
            <input type="checkbox" checked={selected(entry)} disabled={!entry.available} tabIndex={-1} aria-hidden="true" />
            <span class="n">{entry.index}</span>
            {entry.thumbnail ? <img class="mini" src={entry.thumbnail} alt="" loading="lazy" /> : <span class="mini" />}
            <div class="t">
              <div style={entry.available ? undefined : { textDecoration: "line-through" }}>{entry.title ?? entry.id}</div>
              <div class="muted small">
                {[entry.uploader, duration(entry.duration)].filter(Boolean).join(" · ")}
                {entry.inLibrary && (
                  <>
                    {" "}
                    · <span style={{ color: "var(--ok)" }}>{t("inLibrary")}</span>
                  </>
                )}
                {!entry.available && <> · {t("unavailable")}</>}
              </div>
            </div>
            {entry.available ? <Listen url={entry.url} show={show} small /> : <span />}
          </div>
        ))}
        {hasMore && (
          <div ref={sentinel} class="row" style={{ justifyContent: "center", padding: 8 }}>
            <button class="btn small" disabled={link.loadingMore} onClick={() => void loadMore()}>
              {link.loadingMore ? <Icon name="loader" class="spin" /> : null} {t("loadMore")}
            </button>
          </div>
        )}
      </div>

      <div class="muted small">
        {link.allMode
          ? `${t("allChosen")}${link.excluded.size ? ` (−${link.excluded.size})` : ""}`
          : t("selected", { count: link.chosen.size, time: duration(time) })}
      </div>
    </div>
  );
}
