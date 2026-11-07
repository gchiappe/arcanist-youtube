import { useEffect, useMemo, useRef, useState } from "preact/hooks";
import {
  api,
  ApiError,
  type MusicArtistPage,
  type MusicCharts,
  type MusicItem,
  type MusicSection,
  type MyArtistsNews,
  type SearchKind,
} from "./api";
import { host } from "./host";
import { duration, errorText, locale, t } from "./i18n";
import { Icon } from "./icons";
import { Listen } from "./link";
import type { Toast } from "./main";

const COUNTRY_KEY = "arcanist-youtube.country";
const KINDS: SearchKind[] = ["all", "songs", "albums", "artists", "videos", "playlists"];
const SECTION_KEYS = ["songs", "albums", "artists", "videos", "playlists"];

/** The country of charts and releases: the one chosen before, else the browser's region, else the United States. */
function initialCountry(): string {
  try {
    const saved = localStorage.getItem(COUNTRY_KEY);
    if (saved && /^[A-Z]{2}$/.test(saved)) return saved;
  } catch {
    // No storage (a private window): the browser's region.
  }
  for (const tag of [host.language, ...navigator.languages]) {
    try {
      const region = new Intl.Locale(tag).maximize().region;
      if (region && /^[A-Z]{2}$/.test(region)) return region;
    } catch {
      // Not a language tag.
    }
  }
  return "US";
}

function countryName(code: string): string {
  if (code === "ZZ") return t("explore.world");
  try {
    return new Intl.DisplayNames([locale()], { type: "region" }).of(code) ?? code;
  } catch {
    return code;
  }
}

const errorOf = (error: unknown) => errorText(error instanceof ApiError ? error.code : "explore_failed");

interface Props {
  show: (toast: Toast) => void;
  /** Opens a link in the links view, where the user chooses what to bring and where. */
  onOpen: (url: string) => void;
}

/**
 * Exploring YouTube Music: search, artists' pages (their albums, singles, videos and the artists their fans also
 * like), new releases, a country's charts and the new releases of the user's own artists. Each item says when the user
 * already has it; choosing one opens it like a pasted link.
 */
export function Explore({ show, onOpen }: Props) {
  const [country, setCountry] = useState(initialCountry);
  const [query, setQuery] = useState("");
  const [kind, setKind] = useState<SearchKind>("all");
  const [results, setResults] = useState<MusicSection[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [searched, setSearched] = useState("");
  /** The artists' pages opened, the last one shown (back goes to the one before). */
  const [artists, setArtists] = useState<string[]>([]);
  const top = useRef<HTMLDivElement>(null);

  useEffect(() => {
    try {
      localStorage.setItem(COUNTRY_KEY, country);
    } catch {
      // Not remembered: no storage.
    }
  }, [country]);

  const search = async (text = query, wanted = kind) => {
    const q = text.trim();
    if (!q) {
      setResults(null);
      setSearched("");
      return;
    }
    setSearching(true);
    setArtists([]);
    try {
      setResults(await api.explore.search(q, wanted, country));
      setSearched(q);
    } catch (error) {
      show({ text: errorOf(error), error: true });
    } finally {
      setSearching(false);
    }
  };

  const openArtist = (id: string) => {
    setArtists((current) => [...current, id]);
    top.current?.scrollIntoView({ block: "start" });
  };

  const open = async (item: MusicItem) => {
    if (item.kind === "ARTIST") return openArtist(item.id);
    if (item.url) return onOpen(item.url);
    try {
      onOpen((await api.explore.album(item.id, country)).url);
    } catch (error) {
      show({ text: errorOf(error), error: true });
    }
  };

  const shown = artists[artists.length - 1];
  return (
    <div class="explore" ref={top}>
      <form
        class="paste"
        role="search"
        onSubmit={(event) => {
          event.preventDefault();
          void search();
        }}
      >
        <input
          class="input grow"
          type="text"
          enterKeyHint="search"
          value={query}
          placeholder={t("explore.searchHint")}
          aria-label={t("explore.search")}
          onInput={(event) => setQuery((event.target as HTMLInputElement).value)}
        />
        <button class="btn primary big" type="submit" disabled={!query.trim() || searching}>
          {searching ? <Icon name="loader" class="spin" /> : <Icon name="search" />} <span>{t("explore.search")}</span>
        </button>
        {(query || results) && (
          <button
            type="button"
            class="btn ghost icon big"
            title={t("clear")}
            aria-label={t("clear")}
            onClick={() => {
              setQuery("");
              setResults(null);
              setSearched("");
            }}
          >
            <Icon name="x" />
          </button>
        )}
      </form>

      {(results || searching) && !shown && (
        <div class="segments" role="tablist" style={{ alignSelf: "flex-start", flexWrap: "wrap" }}>
          {KINDS.map((k) => (
            <button
              key={k}
              role="tab"
              aria-pressed={k === kind}
              onClick={() => {
                setKind(k);
                void search(searched || query, k);
              }}
            >
              {t(`explore.kinds.${k}`)}
            </button>
          ))}
        </div>
      )}

      {shown ? (
        <ArtistView
          key={shown}
          id={shown}
          country={country}
          show={show}
          onBack={() => setArtists((current) => current.slice(0, -1))}
          onArtist={openArtist}
          onOpen={open}
        />
      ) : searching ? (
        <Loading text={t("explore.searching")} />
      ) : results ? (
        results.every((s) => s.items.length === 0) ? (
          <div class="notice">{t("explore.nothing")}</div>
        ) : (
          results.map((section) => <Section key={section.title} section={section} show={show} onArtist={openArtist} onOpen={open} />)
        )
      ) : (
        <Home country={country} setCountry={setCountry} show={show} onArtist={openArtist} onOpen={open} />
      )}
    </div>
  );
}

function Loading({ text }: { text: string }) {
  return (
    <div class="row muted small" aria-busy="true">
      <Icon name="loader" class="spin" /> {text}
    </div>
  );
}

interface ItemActions {
  show: (toast: Toast) => void;
  onArtist: (id: string) => void;
  onOpen: (item: MusicItem) => Promise<void>;
}

/** What to see before searching: news from the user's artists, new releases and the charts. */
function Home(props: ItemActions & { country: string; setCountry: (country: string) => void }) {
  const { country, setCountry } = props;
  const [mine, setMine] = useState<MyArtistsNews | null>(null);
  const [mineState, setMineState] = useState<"loading" | "ready" | "failed">("loading");
  const [releases, setReleases] = useState<MusicItem[] | null>(null);
  const [charts, setCharts] = useState<MusicCharts | null>(null);
  const [failed, setFailed] = useState(false);

  const loadMine = (refresh = false) => {
    setMineState("loading");
    api.explore
      .myArtists(country, refresh)
      .then((news) => {
        setMine(news);
        setMineState("ready");
      })
      .catch(() => setMineState("failed"));
  };

  useEffect(() => {
    setReleases(null);
    setCharts(null);
    setFailed(false);
    loadMine();
    api.explore.newReleases(country).then(setReleases).catch(() => setFailed(true));
    api.explore.charts(country).then(setCharts).catch(() => setFailed(true));
  }, [country]);

  const countries = useMemo(
    () =>
      [...new Set([...(charts?.countries ?? []), country])].sort((a, b) =>
        a === "ZZ" ? -1 : b === "ZZ" ? 1 : countryName(a).localeCompare(countryName(b), locale()),
      ),
    [charts, country],
  );

  return (
    <>
      <div class="row">
        <span class="grow" />
        <label class="small muted" for="explore-country">
          {t("explore.country")}
        </label>
        <select id="explore-country" class="select" style={{ width: "auto" }} value={country} onChange={(e) => setCountry((e.target as HTMLSelectElement).value)}>
          {countries.map((code) => (
            <option key={code} value={code}>
              {countryName(code)}
            </option>
          ))}
        </select>
      </div>

      <div class="card">
        <div class="row">
          <h2 class="grow">{t("explore.myArtists")}</h2>
          {mineState !== "loading" && (
            <button class="btn small ghost" onClick={() => loadMine(true)}>
              <Icon name="retry" /> {t("explore.refresh")}
            </button>
          )}
        </div>
        <span class="muted small">{t("explore.myArtistsHelp")}</span>
        {mineState === "loading" ? (
          <Loading text={t("explore.myArtistsLoading")} />
        ) : mineState === "failed" ? (
          <div class="notice error">{t("explore.loadFailed")}</div>
        ) : mine && mine.artists.length === 0 ? (
          <div class="notice">{t("explore.myArtistsEmpty")}</div>
        ) : mine && mine.items.length === 0 ? (
          <div class="notice">{t("explore.myArtistsNone")}</div>
        ) : (
          mine && (
            <>
              <Tiles items={mine.items} {...props} />
              <span class="muted small" title={mine.artists.join(", ")}>
                {t("explore.myArtistsChecked", { count: mine.artists.length })}
              </span>
            </>
          )
        )}
      </div>

      {failed && <div class="notice error">{t("explore.loadFailed")}</div>}

      <div class="card">
        <h2>{t("explore.newReleases")}</h2>
        {releases ? <Tiles items={releases} {...props} /> : !failed && <Loading text={t("loading")} />}
      </div>

      <div class="card">
        <h2>
          {t("explore.charts")} · {countryName(country)}
        </h2>
        {charts ? (
          charts.sections.map((section) => <Section key={section.title} section={section} plain {...props} />)
        ) : (
          !failed && <Loading text={t("loading")} />
        )}
      </div>
    </>
  );
}

/** An artist's page on YouTube Music. */
function ArtistView(props: ItemActions & { id: string; country: string; onBack: () => void }) {
  const { id, country, onBack, show } = props;
  const [page, setPage] = useState<MusicArtistPage | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    api.explore
      .artist(id, country)
      .then(setPage)
      .catch((error) => {
        setFailed(true);
        show({ text: errorOf(error), error: true });
      });
  }, [id, country]);

  return (
    <>
      <div>
        <button class="btn small ghost" onClick={onBack}>
          <span aria-hidden="true">←</span> {t("explore.backTo")}
        </button>
      </div>
      {failed ? (
        <div class="notice error">{t("explore.loadFailed")}</div>
      ) : !page ? (
        <Loading text={t("loading")} />
      ) : (
        <>
          <div class="card artist-head">
            {page.thumbnail ? <img class="round" src={page.thumbnail} alt="" /> : <span class="round" />}
            <div style={{ display: "grid", gap: 6, minWidth: 0 }}>
              <span class="pill accent" style={{ justifySelf: "start" }}>
                YouTube Music · {t("kinds.ARTIST")}
              </span>
              <h2>{page.name}</h2>
              {page.description && <p class="muted small clamp">{page.description}</p>}
            </div>
          </div>
          {page.sections.map((section) => (
            <Section key={section.title} section={section} {...props} />
          ))}
        </>
      )}
    </>
  );
}

/** A titled group: songs and videos as rows, albums, artists and lists as tiles. */
function Section(props: ItemActions & { section: MusicSection; plain?: boolean }) {
  const { section, plain } = props;
  const rows = section.items.filter((i) => i.kind === "SONG" || i.kind === "VIDEO");
  const tiles = section.items.filter((i) => i.kind !== "SONG" && i.kind !== "VIDEO");
  const title = SECTION_KEYS.includes(section.title) ? t(`explore.sections.${section.title}`) : section.title;
  const body = (
    <>
      {title && (plain ? <h3>{title}</h3> : <h2>{title}</h2>)}
      {rows.length > 0 && <Rows items={rows} {...props} />}
      {tiles.length > 0 && <Tiles items={tiles} {...props} />}
    </>
  );
  return plain ? <div style={{ display: "grid", gap: 10 }}>{body}</div> : <div class="card">{body}</div>;
}

function Artists({ item, onArtist }: { item: MusicItem; onArtist: (id: string) => void }) {
  return (
    <>
      {item.artists.map((artist, index) => (
        <span key={artist.name + index}>
          {index > 0 && ", "}
          {artist.id ? (
            <button
              class="link"
              onClick={(event) => {
                event.stopPropagation();
                onArtist(artist.id!);
              }}
            >
              {artist.name}
            </button>
          ) : (
            artist.name
          )}
        </span>
      ))}
    </>
  );
}

function Rows(props: ItemActions & { items: MusicItem[] }) {
  const { items, show, onArtist, onOpen } = props;
  const [all, setAll] = useState(false);
  const visible = all ? items : items.slice(0, 8);
  return (
    <div class="results">
      {visible.map((item) => (
        <div key={item.kind + item.id} class="entry">
          {item.thumbnail ? <img class="mini" src={item.thumbnail} alt="" loading="lazy" /> : <span class="mini" />}
          <div class="t">
            <div>
              {item.title}
              {item.inLibrary && (
                <span class="pill ok" style={{ marginInlineStart: 6 }} title={ownedText(item)}>
                  <Icon name="check" /> {t("explore.owned")}
                </span>
              )}
            </div>
            <div class="muted small">
              <Artists item={item} onArtist={onArtist} />
              {[item.album, duration(item.duration), item.detail].filter(Boolean).map((part) => ` · ${part}`)}
            </div>
          </div>
          <div class="row" style={{ gap: 4, flexWrap: "nowrap" }}>
            {item.url && <Listen url={item.url} show={show} small />}
            <button class={`btn small${item.inLibrary ? "" : " primary"}`} title={t("explore.openHelp")} onClick={() => void onOpen(item)}>
              <Icon name="download" /> {t("explore.open")}
            </button>
          </div>
        </div>
      ))}
      {items.length > 8 && (
        <button class="btn small ghost" onClick={() => setAll((a) => !a)}>
          {all ? t("explore.showLess") : t("explore.showMore")}
        </button>
      )}
    </div>
  );
}

function Tiles(props: ItemActions & { items: MusicItem[] }) {
  const { items, onOpen } = props;
  const [all, setAll] = useState(false);
  const [opening, setOpening] = useState<string | null>(null);
  const visible = all ? items : items.slice(0, 12);
  return (
    <>
      <div class="tiles">
        {visible.map((item) => {
          const key = item.kind + item.id;
          const subtitle = [item.label, item.year, item.detail].filter(Boolean).join(" · ");
          return (
            <button
              key={key}
              class={`tile${item.kind === "ARTIST" ? " artist" : ""}`}
              disabled={opening === key}
              title={item.kind === "ARTIST" ? item.title : t("explore.openHelp")}
              onClick={async () => {
                setOpening(key);
                try {
                  await onOpen(item);
                } finally {
                  setOpening(null);
                }
              }}
            >
              <span class="art">
                {item.thumbnail && <img src={item.thumbnail} alt="" loading="lazy" />}
                {opening === key && <Icon name="loader" class="spin" />}
                {item.inLibrary && (
                  <span class="pill ok owned-badge">
                    <Icon name="check" /> {item.kind === "ALBUM" ? t("explore.ownedAlbum") : t("explore.owned")}
                  </span>
                )}
              </span>
              <span class="name">{item.title}</span>
              {/* Plain names: a tile is a button already. */}
              {item.artists.length > 0 && <span class="muted small ellipsis">{item.artists.map((a) => a.name).join(", ")}</span>}
              {subtitle && <span class="muted small ellipsis">{subtitle}</span>}
            </button>
          );
        })}
      </div>
      {items.length > 12 && (
        <button class="btn small ghost" style={{ justifySelf: "start" }} onClick={() => setAll((a) => !a)}>
          {all ? t("explore.showLess") : t("explore.showMore")}
        </button>
      )}
    </>
  );
}

/** "You have it: Album · FLAC · Library" for a song's badge. */
function ownedText(item: MusicItem): string {
  return (item.owned ?? []).map((c) => [c.album, c.year, c.lossless ? "LOSSLESS" : c.format, c.libraries.join(", ")].filter(Boolean).join(" · ")).join("\n");
}
