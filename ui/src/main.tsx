import { render } from "preact";
import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { api, ApiError, watchDownloads, type DownloadBatch, type DownloadRequest, type LibrarySong, type MyYouTube, type Status } from "./api";
import { host } from "./host";
import { errorText, t } from "./i18n";
import { Icon } from "./icons";
import { LinkCard, chosenCount, initFrom, newLink, requestFor, type LinkState } from "./link";
import { Options, loadOptions, saveOptions, type Choices } from "./options";
import { Queue, awaitsTracks, tracksOf } from "./queue";
import { MySettings } from "./settings";
import { SongPicker, VideoBatch } from "./songs";
import { VideoFinder, videoTrack } from "./video";
import "./styles.css";

/** Links in pasted text (the server checks them again). */
function findLinks(text: string): string[] {
  const found = text.match(/(?:https?:\/\/[^\s<>"']+)|(?:(?:www\.|m\.|music\.)?youtube\.com\/[^\s<>"']+)|(?:youtu\.be\/[^\s<>"']+)/gi) ?? [];
  return [...new Set(found.map((link) => link.replace(/[)\]>,.;!?"']+$/, "")))].slice(0, 20);
}

/** The extension's settings, in Arcanist's admin panel (Extensions › YouTube Audio). */
const SETTINGS_URL = "/admin/extensions/youtube";

export interface Toast {
  text: string;
  error?: boolean;
}

function App() {
  const [ready, setReady] = useState<"starting" | "signed-out" | "ready">("starting");
  const [, redraw] = useState(0);
  const [status, setStatus] = useState<Status | null>(null);
  const [text, setText] = useState("");
  const [links, setLinks] = useState<LinkState[]>([]);
  const [choices, setChoices] = useState<Choices>(loadOptions());
  const [batches, setBatches] = useState<DownloadBatch[]>([]);
  const [toast, setToast] = useState<Toast | null>(null);
  const [sending, setSending] = useState(false);
  const [me, setMe] = useState<MyYouTube | null>(null);
  const [mine, setMine] = useState(false);
  /** A library song whose music video is looked for (opened from a player's song menu). */
  const [findFor, setFindFor] = useState<string | null>(videoTrack());
  /** Links to bring, or songs of the library whose music videos are looked for. */
  const [view, setView] = useState<"links" | "videos">("links");
  /** Songs whose music videos are looked for at once. */
  const [batchSongs, setBatchSongs] = useState<LibrarySong[] | null>(null);
  /** Songs already handed to the player, or there before the app opened (never played again). */
  const handed = useRef<Set<string> | null>(null);
  const input = useRef<HTMLTextAreaElement>(null);

  const show = useCallback((message: Toast) => {
    setToast(message);
    setTimeout(() => setToast((current) => (current === message ? null : current)), 3500);
  }, []);

  const refreshStatus = useCallback(async () => {
    const current = await api.status();
    setStatus(current);
    setChoices((c) => {
      const library = current.libraries.some((l) => l.id === c.libraryId) ? c.libraryId : (current.libraries[0]?.id ?? "");
      const format = current.formats.includes(c.format) ? c.format : "ORIGINAL";
      return { ...c, libraryId: library, format };
    });
    return current;
  }, []);

  useEffect(() => {
    const off = host.onChange(() => {
      document.documentElement.lang = host.language;
      document.title = t("title");
      redraw((n) => n + 1);
    });
    document.title = t("title");
    void (async () => {
      if (!(await host.start())) {
        setReady("signed-out");
        return;
      }
      document.documentElement.lang = host.language;
      document.title = t("title");
      try {
        await refreshStatus();
        setBatches(await api.downloads());
        setMe(await api.me().catch(() => null));
        setReady("ready");
      } catch (error) {
        setReady(error instanceof ApiError && (error.status === 401 || error.status === 403) ? "signed-out" : "ready");
      }
    })();
    return () => {
      off();
    };
  }, [refreshStatus]);

  // The queue, live.
  useEffect(() => {
    if (ready !== "ready") return;
    return watchDownloads(
      (batch) =>
        setBatches((current) => {
          const index = current.findIndex((b) => b.id === batch.id);
          if (index < 0) return [batch, ...current];
          const copy = current.slice();
          copy[index] = batch;
          return copy;
        }),
      () => void api.downloads().then(setBatches).catch(() => undefined),
    );
  }, [ready]);

  useEffect(() => saveOptions(choices), [choices]);

  // Songs in the library whose tracks are not known yet (a scan takes a while): reading the list looks them up.
  const awaiting = batches.some((b) => b.items.some(awaitsTracks));
  useEffect(() => {
    if (ready !== "ready" || !awaiting) return;
    const timer = setInterval(() => void api.downloads().then(setBatches).catch(() => undefined), 8000);
    return () => clearInterval(timer);
  }, [ready, awaiting]);

  // Each song that arrives (and becomes a track of the library) goes to the player, when the user wants it.
  useEffect(() => {
    const tracks = batches.flatMap((b) => b.items).filter((i) => i.status === "ADDED").flatMap(tracksOf);
    if (handed.current === null) {
      if (ready === "ready") handed.current = new Set(tracks);
      return;
    }
    const fresh = tracks.filter((id) => !handed.current!.has(id));
    fresh.forEach((id) => handed.current!.add(id));
    if (fresh.length && me?.playWhenDone && host.play(fresh)) show({ text: t("mine.playing", { count: fresh.length }) });
  }, [batches, ready, me, show]);

  const updateLink = useCallback((key: string, change: (link: LinkState) => LinkState) => {
    setLinks((current) => current.map((link) => (link.key === key ? change(link) : link)));
  }, []);

  /** Reads each link pasted (new ones only), one after another. */
  const inspect = useCallback(
    async (value: string) => {
      const found = findLinks(value);
      if (found.length === 0) {
        if (value.trim()) setLinks([{ ...newLink(value.trim()), error: "invalid_link", loading: false }]);
        return;
      }
      const fresh = found.map(newLink);
      setLinks(fresh);
      for (const link of fresh) {
        try {
          const preview = await api.inspect(link.input);
          updateLink(link.key, (l) => ({ ...l, ...initFrom(preview), loading: false }));
        } catch (error) {
          updateLink(link.key, (l) => ({ ...l, loading: false, error: error instanceof ApiError ? error.code : "unknown" }));
        }
      }
    },
    [updateLink],
  );

  const onPaste = (event: ClipboardEvent) => {
    const pasted = event.clipboardData?.getData("text") ?? "";
    if (!findLinks(pasted).length) return;
    // Pasted into an empty box: read it at once.
    if (!text.trim()) {
      event.preventDefault();
      setText(pasted.trim());
      void inspect(pasted);
    }
  };

  const clear = () => {
    setText("");
    setLinks([]);
    input.current?.focus();
  };

  const requests = useMemo(
    () => links.filter((l) => l.preview && !l.loading).map((l) => requestFor(l, choices)).filter((r): r is DownloadRequest => r !== null),
    [links, choices],
  );
  const songs = links.reduce((sum, l) => sum + chosenCount(l), 0);

  const send = async () => {
    if (!requests.length || sending) return;
    setSending(true);
    try {
      const sent: DownloadBatch[] = [];
      for (const request of requests) sent.push(await api.download(request));
      setBatches((current) => [...sent.filter((b) => !current.some((c) => c.id === b.id)), ...current]);
      show({ text: t("added") });
      host.notify("success", t("added"));
      clear();
    } catch (error) {
      show({ text: errorOf(error), error: true });
    } finally {
      setSending(false);
    }
  };

  if (ready === "starting")
    return (
      <div class="app">
        <div class="skeleton" style={{ height: 46 }} />
      </div>
    );
  if (ready === "signed-out")
    return (
      <div class="app">
        <Header />
        <div class="notice">{t("signIn")}</div>
      </div>
    );

  const toast_ = toast && (
    <div class="toast" role="status" style={toast.error ? { background: "var(--danger)", color: "#fff" } : undefined}>
      {toast.text}
    </div>
  );

  const toolsMissing = status !== null && !status.ready;
  return (
    <div class="app">
      <Header>
        {me && (
          <button class="btn ghost icon" title={t("mine.title")} aria-label={t("mine.title")} aria-haspopup="dialog" onClick={() => setMine(true)}>
            <Icon name="settings" />
          </button>
        )}
      </Header>

      {mine && me && <MySettings me={me} onChange={setMe} onClose={() => setMine(false)} show={show} />}

      {toolsMissing && (
        <div class="card">
          <strong>{t("setupTitle")}</strong>
          <span class="muted">{status!.isAdmin ? t("setupAdminPanel") : t("setupUser")}</span>
          {status!.isAdmin && (
            <div>
              <a class="btn primary" href={SETTINGS_URL} target="_blank" rel="noopener">
                <Icon name="settings" /> {t("openSettings")}
              </a>
            </div>
          )}
        </div>
      )}

      {!toolsMissing && status && findFor && (
        <VideoFinder
          trackId={findFor}
          status={status}
          show={show}
          onAdded={(batch) => setBatches((current) => [batch, ...current.filter((b) => b.id !== batch.id)])}
          onBack={() => {
            setFindFor(null);
            history.replaceState(null, "", location.pathname);
          }}
        />
      )}

      {!toolsMissing && status && !findFor && batchSongs && (
        <VideoBatch
          songs={batchSongs}
          status={status}
          show={show}
          onAdded={(batch) => setBatches((current) => [batch, ...current.filter((b) => b.id !== batch.id)])}
          onBack={() => setBatchSongs(null)}
        />
      )}

      {!toolsMissing && status && !findFor && !batchSongs && status.musicVideos && (
        <div class="segments" role="group" style={{ alignSelf: "flex-start" }}>
          <button aria-pressed={view === "links"} onClick={() => setView("links")}>
            <Icon name="download" /> {t("mode.links")}
          </button>
          <button aria-pressed={view === "videos"} onClick={() => setView("videos")}>
            <Icon name="search" /> {t("mode.videos")}
          </button>
        </div>
      )}

      {!toolsMissing && status && !findFor && !batchSongs && view === "videos" && (
        <SongPicker status={status} show={show} onFind={(song) => setFindFor(song.id)} onBatch={setBatchSongs} />
      )}

      {!toolsMissing && status && !findFor && !batchSongs && view === "links" && (
        <>
          {status.musicVideos && <span class="muted small">{t("musicVideosNote")}</span>}
          <div class="paste">
            <textarea
              ref={input}
              rows={Math.min(4, Math.max(1, text.split("\n").length))}
              placeholder={t("placeholder")}
              value={text}
              autofocus
              onPaste={onPaste}
              onInput={(event) => setText((event.target as HTMLTextAreaElement).value)}
              onKeyDown={(event) => {
                if (event.key === "Escape") clear();
                if (event.key === "Enter" && !event.shiftKey) {
                  event.preventDefault();
                  if (links.length && links.every((l) => !l.loading) && requests.length) void send();
                  else void inspect(text);
                }
              }}
              aria-label={t("placeholder")}
            />
            <button class="btn primary big" onClick={() => void inspect(text)} disabled={!text.trim()}>
              <Icon name="search" /> <span>{t("inspect")}</span>
            </button>
            {(text || links.length > 0) && (
              <button class="btn ghost icon big" title={t("clear")} aria-label={t("clear")} onClick={clear}>
                <Icon name="x" />
              </button>
            )}
          </div>

          {links.map((link) => (
            <LinkCard
              key={link.key}
              link={link}
              status={status}
              choices={choices}
              update={(change) => updateLink(link.key, change)}
              remove={links.length > 1 ? () => setLinks((c) => c.filter((l) => l.key !== link.key)) : undefined}
              show={show}
            />
          ))}

          {links.some((l) => l.preview) && (
            <Options status={status} choices={choices} setChoices={setChoices} links={links}>
              <div class="sticky-actions row">
                <span class="muted small grow">{status.libraries.length === 0 ? t("noLibrary") : ""}</span>
                <button class="btn primary big" disabled={!requests.length || sending || !choices.libraryId} onClick={() => void send()}>
                  {sending ? <Icon name="loader" class="spin" /> : <Icon name="download" />}
                  {songs > 1 ? t("addCount", { count: songs }) : links.some((l) => l.allMode) ? t("addAll") : t("add")}
                </button>
              </div>
            </Options>
          )}
        </>
      )}

      <Queue batches={batches} setBatches={setBatches} show={show} canPlay={host.embedded} />

      <p class="footer">
        {t("legal")} · v{status?.version}
      </p>

      {toast_}
    </div>
  );
}

export function errorOf(error: unknown): string {
  return errorText(error instanceof ApiError ? error.code : "unknown");
}

function Header({ children }: { children?: preact.ComponentChildren }) {
  return (
    <div class="header">
      <img src="./icon.svg" alt="" />
      <div class="grow">
        <h1>{t("title")}</h1>
        <p>{t("subtitle")}</p>
      </div>
      {children}
    </div>
  );
}

render(<App />, document.getElementById("app")!);
