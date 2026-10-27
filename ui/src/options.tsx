import type { ComponentChildren } from "preact";
import { VIDEO_HEIGHTS, type AudioFormat, type AudioQuality, type Status } from "./api";
import { help, t } from "./i18n";
import type { LinkState } from "./link";

/** How songs are saved, remembered between visits. */
export interface Choices {
  format: AudioFormat;
  quality: AudioQuality;
  libraryId: string;
  artwork: boolean;
  square: boolean;
  /** The audio only, the audio and its music video, or only the music video (for songs the library has). */
  videoMode: VideoMode;
  /** The highest picture of music videos; 0 leaves it to the server. */
  videoHeight: number;
}

export type VideoMode = "AUDIO" | "ALSO" | "ONLY";

const KEY = "arcanist.youtube.choices";

export function loadOptions(): Choices {
  const defaults: Choices = { format: "ORIGINAL", quality: "BEST", libraryId: "", artwork: true, square: true, videoMode: "AUDIO", videoHeight: 0 };
  try {
    const stored = JSON.parse(localStorage.getItem(KEY) ?? "{}") as Partial<Choices> & { video?: boolean };
    // Before 0.6 there was only "also its music video".
    if (stored.videoMode === undefined && stored.video) stored.videoMode = "ALSO";
    delete stored.video;
    return { ...defaults, ...stored };
  } catch {
    return defaults;
  }
}

export function saveOptions(choices: Choices): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(choices));
  } catch {
    // Without storage, the choices last this visit.
  }
}

const FORMATS: AudioFormat[] = ["ORIGINAL", "OPUS", "MP3", "M4A", "FLAC"];
const QUALITIES: AudioQuality[] = ["BEST", "HIGH", "MEDIUM"];
const MODES: VideoMode[] = ["AUDIO", "ALSO", "ONLY"];

/** "1080p (Full HD)" and the like. */
export function heightName(height: number): string {
  const names: Record<number, string> = { 2160: "2160p (4K UHD)", 1440: "1440p (QHD)", 1080: "1080p (Full HD)", 720: "720p (HD)" };
  return names[height] ?? `${height}p`;
}

/** The quality of music videos: the server's, or one chosen. */
export function VideoHeightSelect(props: { id: string; status: Status; value: number; onChange: (height: number) => void }) {
  return (
    <div class="field">
      <label for={props.id}>{t("videoHeight")}</label>
      <select id={props.id} class="select" value={String(props.value)} onChange={(e) => props.onChange(Number((e.target as HTMLSelectElement).value))}>
        <option value="0">{t("videoHeightServer", { height: heightName(props.status.settings.videoHeight ?? 1080) })}</option>
        {VIDEO_HEIGHTS.map((h) => (
          <option key={h} value={String(h)}>
            {heightName(h)}
          </option>
        ))}
      </select>
    </div>
  );
}

export function Options(props: {
  status: Status;
  choices: Choices;
  setChoices: (change: (choices: Choices) => Choices) => void;
  links: LinkState[];
  children?: ComponentChildren;
}) {
  const { status, choices, setChoices } = props;
  const set = <K extends keyof Choices>(key: K, value: Choices[K]) => setChoices((c) => ({ ...c, [key]: value }));
  // Servers without music videos only take the audio.
  const mode: VideoMode = status.musicVideos ? choices.videoMode : "AUDIO";
  const audio = mode !== "ONLY";
  const converting = audio && choices.format !== "ORIGINAL" && choices.format !== "FLAC";

  return (
    <div class="card">
      {status.musicVideos && (
        <div class="field">
          <span class="label">{t("videoMode")}</span>
          <div class="segments" role="radiogroup">
            {MODES.map((m) => (
              <button key={m} role="radio" aria-pressed={mode === m} aria-checked={mode === m} title={t(`videoModes.${m}Help`)} onClick={() => set("videoMode", m)}>
                {t(`videoModes.${m}`)}
              </button>
            ))}
          </div>
          <span class="muted small">{t(`videoModes.${mode}Help`)}</span>
        </div>
      )}

      {audio && (
        <div class="field">
          <span class="label">{t("format")}</span>
          <div class="segments" role="radiogroup">
            {FORMATS.map((format) => (
              <button
                key={format}
                role="radio"
                aria-pressed={choices.format === format}
                aria-checked={choices.format === format}
                disabled={!status.formats.includes(format)}
                title={status.formats.includes(format) ? help(`formats.${format}`) : t("noFfmpeg")}
                onClick={() => set("format", format)}
              >
                {t(`formats.${format}`)}
              </button>
            ))}
          </div>
          <span class="muted small">{status.formats.length === 1 ? t("noFfmpeg") : help(`formats.${choices.format}`)}</span>
        </div>
      )}

      <div class="grid2">
        {converting && (
          <div class="field">
            <label for="quality">{t("quality")}</label>
            <select id="quality" class="select" value={choices.quality} onChange={(e) => set("quality", (e.target as HTMLSelectElement).value as AudioQuality)}>
              {QUALITIES.map((q) => (
                <option key={q} value={q}>
                  {t(`qualities.${q}`)}
                </option>
              ))}
            </select>
          </div>
        )}
        {mode !== "AUDIO" && <VideoHeightSelect id="video-height" status={status} value={choices.videoHeight} onChange={(h) => set("videoHeight", h)} />}
        <div class="field">
          <label for="library">{t("library")}</label>
          <select
            id="library"
            class="select"
            value={choices.libraryId}
            disabled={status.libraries.length === 0}
            onChange={(e) => set("libraryId", (e.target as HTMLSelectElement).value)}
          >
            {status.libraries.map((library) => (
              <option key={library.id} value={library.id}>
                {library.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      {audio && (
        <div class="row" style={{ gap: 18 }}>
          <label class="check">
            <input type="checkbox" checked={choices.artwork} onChange={(e) => set("artwork", (e.target as HTMLInputElement).checked)} />
            {t("artwork")}
          </label>
          {choices.artwork && (
            <label class="check">
              <input type="checkbox" checked={choices.square} onChange={(e) => set("square", (e.target as HTMLInputElement).checked)} />
              {t("square")}
            </label>
          )}
        </div>
      )}
      {props.children}
    </div>
  );
}
