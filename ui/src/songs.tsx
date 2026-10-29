import { useEffect, useRef, useState } from "preact/hooks";
import { api, type DownloadBatch, type LibrarySong, type Status, type VideoCandidate } from "./api";
import { t } from "./i18n";
import { Icon } from "./icons";
import type { Toast } from "./main";
import { errorOf } from "./main";
import { loadOptions, saveOptions, VideoHeightSelect } from "./options";

/** The most songs looked for in one batch (each search takes some seconds). */
export const BATCH_LIMIT = 200;

/** From this likelihood on, a batch takes the best video without asking. */
const SURE = 60;

const PAGE = 50;

function clock(ms: number): string {
  const s = Math.round(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

export function songName(song: { title: string; artist: string | null }): string {
  return song.artist ? `${song.artist} – ${song.title}` : song.title;
}

function Cover({ song }: { song: LibrarySong }) {
  return song.artworkId ? (
    <img class="cover" src={`/rest/v1/artwork/${song.artworkId}?size=64`} alt="" loading="lazy" />
  ) : (
    <div class="cover" />
  );
}

/**
 * The songs of the user's libraries, to look for their music videos: one at a time (the finder), or many at once
 * (a batch). Searched by title, artist or album; by default only those without a music video yet.
 */
export function SongPicker(props: {
  status: Status;
  onFind: (song: LibrarySong) => void;
  onBatch: (songs: LibrarySong[]) => void;
  show: (toast: Toast) => void;
}) {
  const [search, setSearch] = useState("");
  const [libraryId, setLibraryId] = useState("");
  const [withoutVideo, setWithoutVideo] = useState(true);
  const [songs, setSongs] = useState<LibrarySong[] | null>(null);
  const [next, setNext] = useState<number | null>(null);
  const [total, setTotal] = useState(0);
  const [chosen, setChosen] = useState<Map<string, LibrarySong>>(new Map());
  const [gathering, setGathering] = useState(false);
  const generation = useRef(0);

  const query = { search: search.trim() || undefined, libraryId: libraryId || undefined, withoutVideo };

  // A new search (a little after typing stops).
  useEffect(() => {
    const mine = ++generation.current;
    const timer = setTimeout(() => {
      setSongs(null);
      api
        .songs({ ...query, first: PAGE })
        .then((page) => {
          if (mine !== generation.current) return;
          setSongs(page.items);
          setNext(page.next);
          setTotal(page.totalCount);
        })
        .catch((e) => {
          if (mine !== generation.current) return;
          setSongs([]);
          props.show({ text: errorOf(e), error: true });
        });
    }, 300);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, libraryId, withoutVideo]);

  const more = async () => {
    if (next === null) return;
    const page = await api.songs({ ...query, offset: next, first: PAGE }).catch((e) => {
      props.show({ text: errorOf(e), error: true });
      return null;
    });
    if (!page) return;
    setSongs((current) => [...(current ?? []), ...page.items]);
    setNext(page.next);
  };

  const toggle = (song: LibrarySong) =>
    setChosen((current) => {
      const copy = new Map(current);
      if (copy.has(song.id)) copy.delete(song.id);
      else if (copy.size < BATCH_LIMIT) copy.set(song.id, song);
      return copy;
    });

  /** Every song of this search (up to the batch's limit), not only those shown. */
  const chooseAll = async () => {
    setGathering(true);
    try {
      const all = new Map(chosen);
      let offset: number | null = 0;
      while (offset !== null && all.size < BATCH_LIMIT) {
        const page = await api.songs({ ...query, offset, first: 200 });
        for (const song of page.items) if (all.size < BATCH_LIMIT) all.set(song.id, song);
        offset = page.next;
      }
      setChosen(all);
      if (all.size >= BATCH_LIMIT && total > BATCH_LIMIT) props.show({ text: t("pick.limited", { count: BATCH_LIMIT }) });
    } catch (e) {
      props.show({ text: errorOf(e), error: true });
    } finally {
      setGathering(false);
    }
  };

  return (
    <div class="card">
      <div class="grid2">
        <div class="field">
          <label for="songs-search">{t("pick.search")}</label>
          <input
            id="songs-search"
            class="input"
            type="search"
            value={search}
            placeholder={t("pick.searchHint")}
            onInput={(e) => setSearch((e.target as HTMLInputElement).value)}
          />
        </div>
        <div class="field">
          <label for="songs-library">{t("pick.library")}</label>
          <select id="songs-library" class="select" value={libraryId} onChange={(e) => setLibraryId((e.target as HTMLSelectElement).value)}>
            <option value="">{t("pick.allLibraries")}</option>
            {props.status.libraries.map((l) => (
              <option key={l.id} value={l.id}>
                {l.name}
              </option>
            ))}
          </select>
        </div>
      </div>
      <div class="row">
        <label class="check grow">
          <input type="checkbox" checked={withoutVideo} onChange={(e) => setWithoutVideo((e.target as HTMLInputElement).checked)} />
          {t("pick.withoutVideo")}
        </label>
        {songs !== null && <span class="muted small">{t("pick.count", { count: total })}</span>}
      </div>

      {songs === null ? (
        <div class="skeleton" style={{ height: 120 }} />
      ) : songs.length === 0 ? (
        <div class="notice">{withoutVideo && !search ? t("pick.allHaveVideo") : t("pick.none")}</div>
      ) : (
        <div class="entries">
          {songs.map((song) => (
            <div key={song.id} class="song">
              <input type="checkbox" checked={chosen.has(song.id)} onChange={() => toggle(song)} aria-label={songName(song)} />
              <Cover song={song} />
              <div class="t">
                <div style={{ fontWeight: 600 }}>{song.title}</div>
                <div class="muted small">
                  {song.artist ?? ""} · {song.album}
                  {song.year ? ` (${song.year})` : ""} · {clock(song.durationMs)}
                  {song.hasVideo && <span class="pill ok"> {t("pick.hasVideo")}</span>}
                </div>
              </div>
              <button class="btn small" onClick={() => props.onFind(song)}>
                <Icon name="search" /> {t("pick.find")}
              </button>
            </div>
          ))}
          {next !== null && (
            <div style={{ padding: 8, textAlign: "center" }}>
              <button class="btn ghost small" onClick={() => void more()}>
                {t("pick.more")}
              </button>
            </div>
          )}
        </div>
      )}

      <div class="sticky-actions row">
        <span class="muted small grow">{chosen.size > 0 ? t("pick.chosen", { count: chosen.size }) : t("pick.chooseHint")}</span>
        {chosen.size > 0 && (
          <button class="btn ghost" onClick={() => setChosen(new Map())}>
            {t("pick.clear")}
          </button>
        )}
        <button class="btn" disabled={gathering || !songs?.length} onClick={() => void chooseAll()}>
          {gathering ? <Icon name="loader" class="spin" /> : <Icon name="check" />} {t("pick.chooseAll")}
        </button>
        <button class="btn primary" disabled={chosen.size === 0} onClick={() => props.onBatch([...chosen.values()])}>
          <Icon name="search" /> {t("pick.findChosen", { count: chosen.size })}
        </button>
      </div>
    </div>
  );
}

interface BatchRow {
  song: LibrarySong;
  state: "waiting" | "searching" | "done" | "failed";
  candidates: VideoCandidate[];
  /** The video chosen for it; null leaves the song out. */
  pick: string | null;
  error?: string;
  open: boolean;
}

/**
 * Looks for the music videos of many songs, two at a time, and proposes the likeliest of each: those that look right
 * are chosen already, the others wait for the user (another candidate, or leaving the song out). What is chosen goes
 * to the queue as one download, each video next to its song.
 */
export function VideoBatch(props: {
  songs: LibrarySong[];
  status: Status;
  onAdded: (batch: DownloadBatch) => void;
  onBack: () => void;
  show: (toast: Toast) => void;
}) {
  const [rows, setRows] = useState<BatchRow[]>(() =>
    props.songs.map((song) => ({ song, state: "waiting", candidates: [], pick: null, open: false })),
  );
  const [height, setHeight] = useState(() => loadOptions().videoHeight);
  const [sending, setSending] = useState(false);
  const chooseHeight = (value: number) => {
    setHeight(value);
    saveOptions({ ...loadOptions(), videoHeight: value });
  };

  const update = (id: string, change: (row: BatchRow) => BatchRow) =>
    setRows((current) => current.map((row) => (row.song.id === id ? change(row) : row)));

  useEffect(() => {
    let stopped = false;
    const queue = [...props.songs];
    const work = async () => {
      for (let song = queue.shift(); song && !stopped; song = queue.shift()) {
        const id = song.id;
        update(id, (r) => ({ ...r, state: "searching" }));
        try {
          const result = await api.videoSearch(id);
          if (stopped) return;
          const best = result.candidates[0];
          update(id, (r) => ({
            ...r,
            state: "done",
            candidates: result.candidates,
            pick: best && best.score >= SURE ? best.url : null,
          }));
        } catch (e) {
          if (stopped) return;
          update(id, (r) => ({ ...r, state: "failed", error: errorOf(e) }));
        }
      }
    };
    void Promise.all([work(), work()]);
    return () => {
      stopped = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const searched = rows.filter((r) => r.state === "done" || r.state === "failed").length;
  const picked = rows.filter((r) => r.pick !== null);
  const doubtful = rows.filter((r) => r.state === "done" && r.pick === null && r.candidates.length > 0).length;

  const send = async () => {
    setSending(true);
    try {
      const batch = await api.videoDownloads({
        items: picked.map((r) => ({ trackId: r.song.id, url: r.pick!, title: songName(r.song) })),
        title: t("batch.queueTitle", { count: picked.length }),
        videoHeight: height > 0 ? height : undefined,
      });
      props.onAdded(batch);
      props.show({ text: t("batch.added", { count: picked.length }) });
      props.onBack();
    } catch (e) {
      props.show({ text: errorOf(e), error: true });
    } finally {
      setSending(false);
    }
  };

  return (
    <div class="card">
      <div class="row">
        <strong class="grow">{t("batch.title", { count: rows.length })}</strong>
        <button class="btn ghost" onClick={props.onBack}>
          {t("findVideo.back")}
        </button>
      </div>
      <span class="muted small">{t("batch.help", { score: SURE })}</span>
      {searched < rows.length && (
        <div class="row muted small">
          <Icon name="loader" class="spin" /> {t("batch.progress", { done: searched, total: rows.length })}
        </div>
      )}
      <div style={{ maxWidth: 260 }}>
        <VideoHeightSelect id="batch-height" status={props.status} value={height} onChange={chooseHeight} />
      </div>

      <div class="entries" style={{ maxHeight: 560 }}>
        {rows.map((row) => {
          const pick = row.candidates.find((c) => c.url === row.pick);
          return (
            <div key={row.song.id} class="batch-row">
              <div class="song">
                <input
                  type="checkbox"
                  checked={row.pick !== null}
                  disabled={row.candidates.length === 0}
                  onChange={(e) =>
                    update(row.song.id, (r) => ({ ...r, pick: (e.target as HTMLInputElement).checked ? (r.candidates[0]?.url ?? null) : null }))
                  }
                  aria-label={songName(row.song)}
                />
                <Cover song={row.song} />
                <div class="t">
                  <div style={{ fontWeight: 600 }}>{songName(row.song)}</div>
                  <div class="muted small">
                    {row.state === "waiting" && t("batch.waiting")}
                    {row.state === "searching" && t("findVideo.searching")}
                    {row.state === "failed" && row.error}
                    {row.state === "done" && row.candidates.length === 0 && t("batch.nothing")}
                    {row.state === "done" && pick && (
                      <>
                        {pick.title} · {pick.channel ?? ""} · {t("findVideo.score", { score: pick.score })}
                        {pick.official && <span class="pill ok"> {t("findVideo.official")}</span>}
                      </>
                    )}
                    {row.state === "done" && !pick && row.candidates.length > 0 && t("batch.unsure")}
                  </div>
                </div>
                {row.candidates.length > 0 && (
                  <button class="btn small ghost" onClick={() => update(row.song.id, (r) => ({ ...r, open: !r.open }))} aria-expanded={row.open}>
                    {row.open ? t("batch.hide") : t("batch.others", { count: row.candidates.length })}
                  </button>
                )}
              </div>
              {row.open && (
                <div class="candidates">
                  {row.candidates.map((c) => (
                    <label key={c.id} class="candidate">
                      <input
                        type="radio"
                        name={`pick-${row.song.id}`}
                        checked={row.pick === c.url}
                        onChange={() => update(row.song.id, (r) => ({ ...r, pick: c.url }))}
                      />
                      {c.thumbnail ? <img class="mini" src={c.thumbnail} alt="" loading="lazy" /> : <div class="mini" />}
                      <span class="t">
                        <div>{c.title}</div>
                        <div class="muted small">
                          {c.channel ?? ""} · {t("findVideo.score", { score: c.score })}
                          {c.official && <span class="pill ok"> {t("findVideo.official")}</span>}
                        </div>
                      </span>
                      <a class="btn small ghost icon" href={c.url} target="_blank" rel="noopener" title={t("batch.watch")} aria-label={t("batch.watch")}>
                        <Icon name="external" />
                      </a>
                    </label>
                  ))}
                  <label class="candidate">
                    <input type="radio" name={`pick-${row.song.id}`} checked={row.pick === null} onChange={() => update(row.song.id, (r) => ({ ...r, pick: null }))} />
                    <span class="t muted">{t("batch.skip")}</span>
                  </label>
                </div>
              )}
            </div>
          );
        })}
      </div>

      <div class="sticky-actions row">
        <span class="muted small grow">{doubtful > 0 ? t("batch.doubtful", { count: doubtful }) : ""}</span>
        <button class="btn primary big" disabled={picked.length === 0 || sending} onClick={() => void send()}>
          {sending ? <Icon name="loader" class="spin" /> : <Icon name="download" />} {t("batch.download", { count: picked.length })}
        </button>
      </div>
    </div>
  );
}
