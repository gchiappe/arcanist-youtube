import type { ComponentChildren } from "preact";
import type { AudioFormat, AudioQuality, Status } from "./api";
import { help, t } from "./i18n";
import type { LinkState } from "./link";

/** How songs are saved, remembered between visits. */
export interface Choices {
  format: AudioFormat;
  quality: AudioQuality;
  libraryId: string;
  artwork: boolean;
  square: boolean;
}

const KEY = "arcanist.youtube.choices";

export function loadOptions(): Choices {
  const defaults: Choices = { format: "ORIGINAL", quality: "BEST", libraryId: "", artwork: true, square: true };
  try {
    return { ...defaults, ...(JSON.parse(localStorage.getItem(KEY) ?? "{}") as Partial<Choices>) };
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

export function Options(props: {
  status: Status;
  choices: Choices;
  setChoices: (change: (choices: Choices) => Choices) => void;
  links: LinkState[];
  children?: ComponentChildren;
}) {
  const { status, choices, setChoices } = props;
  const set = <K extends keyof Choices>(key: K, value: Choices[K]) => setChoices((c) => ({ ...c, [key]: value }));
  const converting = choices.format !== "ORIGINAL" && choices.format !== "FLAC";

  return (
    <div class="card">
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
      {props.children}
    </div>
  );
}
