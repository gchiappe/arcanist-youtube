import { useEffect, useState } from "preact/hooks";
import { api, ApiError, type MyYouTube } from "./api";
import { Dialog } from "./dialog";
import { errorText, list, t } from "./i18n";
import { Icon } from "./icons";

/** Seconds the notice stays on screen before it can be accepted (the server checks a little less). */
const READING_SECONDS = 10;

/**
 * The responsible use notice. Shown before anything else until the user accepts it (once per user, kept by the server)
 * and again whenever they open it from the footer. While it must be read, the button counts down.
 */
export function Terms(props: { mustAccept: boolean; onAccepted: (me: MyYouTube) => void; onClose: () => void }) {
  const { mustAccept, onAccepted, onClose } = props;
  const [left, setLeft] = useState(mustAccept ? READING_SECONDS : 0);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (left <= 0) return;
    const timer = setTimeout(() => setLeft((s) => s - 1), 1000);
    return () => clearTimeout(timer);
  }, [left]);

  const accept = async () => {
    setSending(true);
    setError(null);
    try {
      onAccepted(await api.acceptTerms());
    } catch (e) {
      setError(errorText(e instanceof ApiError ? e.code : "unknown"));
    } finally {
      setSending(false);
    }
  };

  // Until it is accepted, closing it is not a way around it: the app stays behind it.
  const close = mustAccept ? () => setError(t("terms.left")) : onClose;
  return (
    <Dialog
      title={t("terms.title")}
      onClose={close}
      role="alertdialog"
      actions={
        mustAccept ? (
          <button class="btn primary" disabled={left > 0 || sending} onClick={() => void accept()} aria-live="polite">
            {sending && <Icon name="loader" class="spin" />}
            {left > 0 ? t("terms.wait", { seconds: left }) : t("terms.accept")}
          </button>
        ) : (
          <button class="btn primary" onClick={onClose}>
            {t("close")}
          </button>
        )
      }
    >
      <p>{t("terms.intro")}</p>
      <ul class="terms">
        {list("terms.lines").map((line) => (
          <li key={line}>{line}</li>
        ))}
      </ul>
      {error && <div class="notice error">{error}</div>}
    </Dialog>
  );
}
