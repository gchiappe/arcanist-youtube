import { useState } from "preact/hooks";
import { api, ApiError, type DownloadBatch, type DownloadItem } from "./api";
import { Dialog } from "./dialog";
import { host } from "./host";
import { bytes, duration, errorText, t } from "./i18n";
import { Icon } from "./icons";
import type { Toast } from "./main";

const RUNNING = ["QUEUED", "DOWNLOADING", "CONVERTING", "IMPORTING"];

/** The player takes this many songs at once (Arcanist's host protocol). */
const PLAY_LIMIT = 50;

const inLibrary = (item: DownloadItem) => item.status === "ADDED" || item.status === "DUPLICATE";

/** The tracks of the library an item became, as far as they are known (in order). */
export function tracksOf(item: DownloadItem): string[] {
  if (!inLibrary(item)) return [];
  const ids = item.trackIds?.length ? item.trackIds.filter((id): id is string => !!id) : item.trackId ? [item.trackId] : [];
  return [...new Set(ids)];
}

/** In the library, but some of its tracks are not known yet (the library is still cataloguing it). */
export function awaitsTracks(item: DownloadItem): boolean {
  const hashes = item.contentHashes ?? [];
  return inLibrary(item) && hashes.length > 0 && (item.trackIds?.length !== hashes.length || item.trackIds.some((id) => !id));
}

/** A play button: plays the tracks known, or says the songs are still being added (nothing to play yet). */
function PlayButton({ tracks, pending, label, play, show }: { tracks: string[]; pending: boolean; label: string; play: (ids: string[]) => void; show: (toast: Toast) => void }) {
  if (tracks.length === 0 && !pending) return null;
  const waiting = tracks.length === 0;
  return (
    <button
      type="button"
      class="btn ghost icon"
      aria-label={waiting ? `${label} (${t("playPending")})` : label}
      title={waiting ? t("playPending") : label}
      aria-disabled={waiting ? "true" : undefined}
      onClick={() => (waiting ? show({ text: t("playPending") }) : play(tracks))}
    >
      <Icon name="play" />
    </button>
  );
}

function progressOf(batch: DownloadBatch): number | null {
  if (batch.status === "LISTING" || batch.items.length === 0) return null;
  const sum = batch.items.reduce(
    (total, item) => total + (RUNNING.includes(item.status) ? (item.status === "QUEUED" ? 0 : (item.progress ?? 0) * 0.9) : 1),
    0,
  );
  return sum / batch.items.length;
}

function dotOf(item: DownloadItem): string {
  if (item.status === "ADDED" || item.status === "DUPLICATE") return "dot ok";
  if (item.status === "FAILED") return "dot bad";
  if (item.status === "QUEUED" || item.status === "CANCELLED") return "dot";
  return "dot run";
}

function detailOf(item: DownloadItem): string {
  if (item.status === "FAILED") return errorText(item.errorCode ?? "download_failed");
  const parts = [t(`itemStatus.${item.status}`)];
  if (item.status === "DOWNLOADING" && item.progress != null) parts.push(`${Math.round(item.progress * 100)}%`);
  if (item.status === "DOWNLOADING" && item.speed) parts.push(t("speed", { speed: bytes(item.speed) }));
  if (item.status === "DOWNLOADING" && item.eta) parts.push(t("eta", { time: duration(item.eta) }));
  if (item.files > 1) parts.push(`${item.files}×`);
  return parts.join(" · ");
}

/** The user's downloads, live: one row per link, its songs below. */
export function Queue({
  batches,
  setBatches,
  show,
  canPlay,
}: {
  batches: DownloadBatch[];
  setBatches: (change: (batches: DownloadBatch[]) => DownloadBatch[]) => void;
  show: (toast: Toast) => void;
  /** A player opened the app: it can play songs (without one, play buttons are not shown). */
  canPlay: boolean;
}) {
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [confirming, setConfirming] = useState(false);

  /** Hands songs to the player, after what it plays now. */
  const play = (ids: string[]) => {
    const unique = [...new Set(ids)];
    const sent = unique.slice(0, PLAY_LIMIT);
    if (!sent.length) return;
    if (host.play(sent)) show({ text: unique.length > PLAY_LIMIT ? t("playLimited", { count: PLAY_LIMIT }) : t("playSent", { count: sent.length }) });
    else show({ text: t("errors.unknown"), error: true });
  };

  const replace = (batch: DownloadBatch) => setBatches((current) => current.map((b) => (b.id === batch.id ? batch : b)));
  const act = async (action: () => Promise<DownloadBatch | void>, removeId?: string) => {
    try {
      const result = await action();
      if (result) replace(result);
      if (removeId) setBatches((current) => current.filter((b) => b.id !== removeId));
    } catch (error) {
      show({ text: errorText(error instanceof ApiError ? error.code : "unknown"), error: true });
    }
  };
  const clearDone = async () => {
    setConfirming(false);
    try {
      await api.clear();
      setBatches((current) => current.filter((b) => b.status === "ACTIVE" || b.status === "LISTING"));
    } catch (error) {
      show({ text: errorText(error instanceof ApiError ? error.code : "unknown"), error: true });
    }
  };

  const finishedCount = batches.filter((b) => b.status !== "ACTIVE" && b.status !== "LISTING").length;
  const allTracks = canPlay ? batches.flatMap((b) => b.items.flatMap(tracksOf)) : [];
  return (
    <section class="card" aria-live="polite">
      <div class="row">
        <strong class="grow">{t("queue")}</strong>
        {allTracks.length > 0 && (
          <button type="button" class="btn small ghost" title={t("playAllHelp")} onClick={() => play(allTracks)}>
            <Icon name="play" /> {t("playAll")}
          </button>
        )}
        {batches.some((b) => b.items.some((i) => i.status === "ADDED")) && (
          <button
            class="btn small ghost"
            onClick={() => {
              if (!host.open("uploads")) show({ text: t("openUploads") });
            }}
          >
            <Icon name="history" /> {t("openUploads")}
          </button>
        )}
        {finishedCount > 0 && (
          <button type="button" class="btn small ghost" aria-haspopup="dialog" onClick={() => setConfirming(true)}>
            <Icon name="trash" /> {t("clearDone")}
          </button>
        )}
      </div>

      {confirming && (
        <Dialog
          role="alertdialog"
          size="small"
          title={t("confirmClear.title")}
          onClose={() => setConfirming(false)}
          actions={
            <>
              <button type="button" class="btn" onClick={() => setConfirming(false)}>
                {t("cancel")}
              </button>
              <button type="button" class="btn danger" onClick={() => void clearDone()}>
                <Icon name="trash" /> {t("confirmClear.confirm")}
              </button>
            </>
          }
        >
          <p>{t("confirmClear.text", { count: finishedCount })}</p>
        </Dialog>
      )}

      {batches.length === 0 && <span class="muted">{t("queueEmpty")}</span>}

      <div>
        {batches.map((batch) => {
          const progress = progressOf(batch);
          const done = batch.items.filter((i) => !RUNNING.includes(i.status)).length;
          const active = batch.status === "ACTIVE" || batch.status === "LISTING";
          const failed = batch.items.some((i) => i.status === "FAILED" || i.status === "CANCELLED") || (batch.status === "FAILED" && batch.items.length === 0);
          const single = batch.items.length === 1 ? batch.items[0] : null;
          const title = single ? (single.tags?.title ? `${single.tags.artist ? single.tags.artist + " – " : ""}${single.tags.title}` : (single.title ?? batch.url)) : (batch.title ?? batch.url);
          const expanded = open.has(batch.id);
          const batchTracks = canPlay ? batch.items.flatMap(tracksOf) : [];
          const batchPending = canPlay && batch.items.some(awaitsTracks);
          return (
            <div class="batch" key={batch.id}>
              <div class="row">
                <div class="grow">
                  <div class="ellipsis" style={{ fontWeight: 500 }} title={batch.url}>
                    {title}
                  </div>
                  <div class="muted small ellipsis">
                    {[
                      t(`kinds.${batch.kind}`),
                      batch.libraryName,
                      single
                        ? detailOf(single)
                        : batch.status === "ACTIVE"
                          ? t("batchStatus.ACTIVE", { done, total: batch.items.length })
                          : batch.errorCode && batch.items.length === 0
                            ? errorText(batch.errorCode)
                            : t(`batchStatus.${batch.status}`),
                    ]
                      .filter(Boolean)
                      .join(" · ")}
                  </div>
                </div>
                {canPlay && (
                  <PlayButton
                    tracks={batchTracks}
                    pending={batchPending}
                    label={single ? t("play") : t("playAll")}
                    play={play}
                    show={show}
                  />
                )}
                {active && (
                  <button class="btn small ghost" onClick={() => void act(() => api.cancel(batch.id))}>
                    {t("cancel")}
                  </button>
                )}
                {!active && failed && (
                  <button class="btn small ghost" onClick={() => void act(() => api.retry(batch.id))}>
                    <Icon name="retry" /> {t("retry")}
                  </button>
                )}
                {!active && (
                  <button class="btn ghost icon" aria-label={t("remove")} title={t("remove")} onClick={() => void act(() => api.remove(batch.id), batch.id)}>
                    <Icon name="x" />
                  </button>
                )}
                {!single && batch.items.length > 0 && (
                  <button
                    class="btn small ghost"
                    aria-expanded={expanded}
                    onClick={() =>
                      setOpen((current) => {
                        const next = new Set(current);
                        if (next.has(batch.id)) next.delete(batch.id);
                        else next.add(batch.id);
                        return next;
                      })
                    }
                  >
                    {expanded ? "▾" : "▸"} {batch.items.length}
                  </button>
                )}
              </div>
              {active && (
                <div class={`bar${progress === null ? " indeterminate" : ""}`}>
                  <div style={{ width: `${Math.round((progress ?? 0) * 100)}%` }} />
                </div>
              )}
              {expanded &&
                batch.items.map((item) => (
                  <div class="item" key={item.id}>
                    <span class={dotOf(item)} />
                    <div style={{ minWidth: 0 }}>
                      <div class="ellipsis">
                        {item.index ? <span class="muted">{item.index}. </span> : null}
                        {item.tags?.title ?? item.title ?? item.videoId}
                      </div>
                      <div class="muted small ellipsis">{detailOf(item)}</div>
                    </div>
                    {RUNNING.includes(item.status) ? (
                      <button class="btn ghost icon" aria-label={t("cancel")} title={t("cancel")} onClick={() => void act(() => api.cancel(batch.id, item.id))}>
                        <Icon name="x" />
                      </button>
                    ) : item.status === "FAILED" || item.status === "CANCELLED" ? (
                      <button class="btn ghost icon" aria-label={t("retry")} title={t("retry")} onClick={() => void act(() => api.retry(batch.id, item.id))}>
                        <Icon name="retry" />
                      </button>
                    ) : canPlay && inLibrary(item) ? (
                      <span class="item-actions">
                        <PlayButton tracks={tracksOf(item)} pending={awaitsTracks(item)} label={t("play")} play={play} show={show} />
                      </span>
                    ) : (
                      <span />
                    )}
                  </div>
                ))}
            </div>
          );
        })}
      </div>
    </section>
  );
}
