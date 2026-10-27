import { useEffect, useState } from "preact/hooks";
import { api, type DownloadBatch, type Status, type VideoSearchResult } from "./api";
import { t } from "./i18n";
import { Icon } from "./icons";
import type { Toast } from "./main";
import { errorOf } from "./main";

/** The track whose music video was asked for (a player's "find the music video" opens the app with ?video=<id>). */
export function videoTrack(): string | null {
  return new URLSearchParams(location.search).get("video");
}

function duration(seconds: number | null): string {
  if (!seconds) return "";
  const s = Math.round(seconds);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

/**
 * Looks for a library song's music video on YouTube: the likeliest videos first (by their title, channel and
 * length), each to listen to before adding it. The one added goes next to the song.
 */
export function VideoFinder(props: {
  trackId: string;
  status: Status;
  onAdded: (batch: DownloadBatch) => void;
  onBack: () => void;
  show: (toast: Toast) => void;
}) {
  const [result, setResult] = useState<VideoSearchResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => {
    setResult(null);
    setError(null);
    api
      .videoSearch(props.trackId)
      .then(setResult)
      .catch((e) => setError(errorOf(e)));
  }, [props.trackId]);

  const add = async (url: string) => {
    setBusy(url);
    try {
      const batch = await api.download({ url, libraryId: props.status.libraries[0]?.id ?? "", videoFor: props.trackId, title: result?.song.title });
      props.onAdded(batch);
      props.show({ text: t("findVideo.added") });
    } catch (e) {
      props.show({ text: errorOf(e), error: true });
    } finally {
      setBusy(null);
    }
  };

  return (
    <div class="card">
      <div class="row">
        <strong class="grow">{t("findVideo.title", { title: result ? `${result.song.artist ? `${result.song.artist} – ` : ""}${result.song.title}` : "…" })}</strong>
        <button class="btn ghost" onClick={props.onBack}>
          {t("findVideo.back")}
        </button>
      </div>
      {result?.song.hasVideo && <span class="muted small">{t("findVideo.has")}</span>}
      {error && <div class="notice">{error}</div>}
      {!result && !error && (
        <div class="row muted">
          <Icon name="loader" class="spin" /> {t("findVideo.searching")}
        </div>
      )}
      {result && result.candidates.length === 0 && <div class="notice">{t("findVideo.none")}</div>}
      <ul class="grid" style={{ gap: 10, listStyle: "none", padding: 0, margin: 0 }}>
        {result?.candidates.map((candidate) => (
          <li key={candidate.id} class="row" style={{ gap: 12, alignItems: "flex-start" }}>
            <a href={candidate.url} target="_blank" rel="noopener" style={{ flex: "0 0 160px" }}>
              {candidate.thumbnail ? (
                <img src={candidate.thumbnail} alt="" style={{ width: 160, aspectRatio: "16 / 9", objectFit: "cover", borderRadius: 8 }} loading="lazy" />
              ) : (
                <div class="skeleton" style={{ width: 160, aspectRatio: "16 / 9" }} />
              )}
            </a>
            <div class="grow" style={{ minWidth: 0, display: "grid", gap: 4 }}>
              <span style={{ fontWeight: 600 }}>{candidate.title}</span>
              <span class="muted small">
                {candidate.channel ?? ""} {candidate.duration ? `· ${duration(candidate.duration)}` : ""}
                {candidate.official && <span class="badge"> {t("findVideo.official")}</span>}
              </span>
              <span class="muted small">{t("findVideo.score", { score: candidate.score })}</span>
              <div>
                <button class="btn primary" disabled={busy !== null} onClick={() => void add(candidate.url)}>
                  {busy === candidate.url ? <Icon name="loader" class="spin" /> : <Icon name="download" />} {t("findVideo.add")}
                </button>
              </div>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}
