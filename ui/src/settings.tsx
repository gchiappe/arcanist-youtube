import { useState } from "preact/hooks";
import { api, ApiError, type MyYouTube } from "./api";
import { Dialog } from "./dialog";
import { errorText, t } from "./i18n";
import { Icon } from "./icons";
import type { Toast } from "./main";

/**
 * The user's own settings: play each song when it arrives, and YouTube cookies of their own (used for their links
 * instead of the server's, never shown back). In a dialog over the app.
 */
export function MySettings(props: { me: MyYouTube; onChange: (me: MyYouTube) => void; onClose: () => void; show: (toast: Toast) => void }) {
  const { me, onChange, onClose, show } = props;
  const [cookies, setCookies] = useState("");
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<MyYouTube>, done?: string) => {
    setBusy(true);
    try {
      onChange(await action());
      if (done) show({ text: done });
    } catch (error) {
      show({ text: errorText(error instanceof ApiError ? error.code : "unknown"), error: true });
    } finally {
      setBusy(false);
    }
  };

  const readFile = async (file: File | undefined) => {
    if (file) setCookies(await file.text());
  };

  return (
    <Dialog
      title={t("mine.title")}
      onClose={onClose}
      actions={
        <button type="button" class="btn" onClick={onClose}>
          {t("done")}
        </button>
      }
    >
      <label class="check">
        <input
          type="checkbox"
          checked={me.playWhenDone}
          disabled={busy}
          onChange={(e) => void run(() => api.setPreferences((e.target as HTMLInputElement).checked))}
        />
        <span>
          {t("mine.playWhenDone")}
          <span class="muted small block">{t("mine.playWhenDoneHelp")}</span>
        </span>
      </label>

      <div class="field">
        <span class="label">{t("mine.cookies")}</span>
        <span class="muted small">{me.hasCookies ? t("mine.cookiesSaved") : t("mine.cookiesNone")}</span>
        <span class="muted small">{t("mine.cookiesHelp")}</span>
        <textarea
          class="input"
          rows={4}
          placeholder="# Netscape HTTP Cookie File"
          value={cookies}
          onInput={(e) => setCookies((e.target as HTMLTextAreaElement).value)}
        />
        <div class="row">
          <label class="btn small">
            <Icon name="upload" /> {t("mine.cookiesFile")}
            <input type="file" accept=".txt,text/plain" hidden onChange={(e) => void readFile((e.target as HTMLInputElement).files?.[0])} />
          </label>
          <span class="grow" />
          {me.hasCookies && (
            <button class="btn small ghost" disabled={busy} onClick={() => void run(() => api.removeMyCookies(), t("mine.cookiesRemoved"))}>
              {t("mine.cookiesRemove")}
            </button>
          )}
          <button
            class="btn small primary"
            disabled={busy || !cookies.trim()}
            onClick={() =>
              void run(async () => {
                const result = await api.saveMyCookies(cookies);
                setCookies("");
                return result;
              }, t("mine.cookiesSavedNow"))
            }
          >
            {t("mine.cookiesSave")}
          </button>
        </div>
      </div>
    </Dialog>
  );
}
