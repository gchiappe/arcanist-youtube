import type { ComponentChildren } from "preact";
import { useEffect, useId, useRef } from "preact/hooks";
import { t } from "./i18n";
import { Icon } from "./icons";

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * A modal dialog over the app: focus moves into it and stays there (Tab cycles), Escape or a click outside closes
 * it, and focus goes back to what opened it.
 */
export function Dialog(props: {
  title: string;
  onClose: () => void;
  children: ComponentChildren;
  /** The buttons at its foot. */
  actions?: ComponentChildren;
  /** "alert" for a question that needs an answer (a confirmation). */
  role?: "dialog" | "alertdialog";
  size?: "small" | "medium";
}) {
  const { title, onClose, children, actions, role = "dialog", size = "medium" } = props;
  const panel = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const close = useRef(onClose);
  close.current = onClose;

  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    const box = panel.current!;
    // The first field, or the dialog itself (so a confirmation is not answered by a stray Enter).
    const first = role === "dialog" ? box.querySelector<HTMLElement>(`.dialog-body :is(${FOCUSABLE})`) : null;
    (first ?? box).focus();

    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.stopPropagation();
        close.current();
        return;
      }
      if (event.key !== "Tab") return;
      const items = [...box.querySelectorAll<HTMLElement>(FOCUSABLE)].filter((e) => e.offsetParent !== null || e === document.activeElement);
      if (items.length === 0) {
        event.preventDefault();
        box.focus();
        return;
      }
      const firstItem = items[0];
      const lastItem = items[items.length - 1];
      if (event.shiftKey && (document.activeElement === firstItem || document.activeElement === box)) {
        event.preventDefault();
        lastItem.focus();
      } else if (!event.shiftKey && document.activeElement === lastItem) {
        event.preventDefault();
        firstItem.focus();
      }
    };
    document.addEventListener("keydown", onKey, true);
    const overflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.removeEventListener("keydown", onKey, true);
      document.body.style.overflow = overflow;
      if (opener?.isConnected) opener.focus();
    };
  }, [role]);

  return (
    <div
      class="overlay"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <div ref={panel} class={`dialog ${size}`} role={role} aria-modal="true" aria-labelledby={titleId} tabIndex={-1}>
        <div class="dialog-head row">
          <h2 id={titleId} class="grow">
            {title}
          </h2>
          <button type="button" class="btn ghost icon" title={t("close")} aria-label={t("close")} onClick={onClose}>
            <Icon name="x" />
          </button>
        </div>
        <div class="dialog-body">{children}</div>
        {actions && <div class="dialog-actions row">{actions}</div>}
      </div>
    </div>
  );
}
