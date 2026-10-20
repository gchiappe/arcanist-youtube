// The app's side of Arcanist's host protocol (docs/developers/extensions.md in Arcanist): the player that opened
// the app (its parent frame, or the window that opened this tab) gives the session, the language and its look.
// Opened on its own, the app restores the session with the refresh cookie, as Arcanist's own clients do.

export interface HostTheme {
  scheme?: "light" | "dark";
  background?: string;
  foreground?: string;
  muted?: string;
  surface?: string;
  border?: string;
  accent?: string;
  accentForeground?: string;
  radius?: string;
  font?: string;
}

interface SessionMessage {
  type: "arcanist:session";
  token: string;
  expiresAt?: string;
  language?: string;
  theme?: HostTheme;
}

type Listener = () => void;

const origin = location.origin;

/** The window of the player that opened the app, if any. */
function hostWindow(): Window | null {
  if (window.parent && window.parent !== window) return window.parent;
  if (window.opener && !window.opener.closed) return window.opener as Window;
  return null;
}

class Host {
  token: string | null = null;
  language = navigator.language || "en";
  theme: HostTheme | null = null;
  /** A player answered: it handles the session (the app never refreshes it then). */
  embedded = false;

  private listeners = new Set<Listener>();
  private waiting: Array<(ok: boolean) => void> = [];

  constructor() {
    window.addEventListener("message", (event: MessageEvent) => {
      if (event.origin !== origin) return;
      const data = event.data as SessionMessage | null;
      if (!data || data.type !== "arcanist:session" || typeof data.token !== "string") return;
      this.embedded = true;
      this.token = data.token;
      if (data.language) this.language = data.language;
      if (data.theme) this.theme = data.theme;
      applyTheme(this.theme);
      this.waiting.splice(0).forEach((resolve) => resolve(true));
      this.listeners.forEach((listener) => listener());
    });
  }

  onChange(listener: Listener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private post(message: Record<string, unknown>): boolean {
    const target = hostWindow();
    if (!target) return false;
    try {
      target.postMessage(message, origin);
      return true;
    } catch {
      return false;
    }
  }

  private nextSession(timeoutMs: number): Promise<boolean> {
    return new Promise((resolve) => {
      this.waiting.push(resolve);
      setTimeout(() => {
        const index = this.waiting.indexOf(resolve);
        if (index >= 0) {
          this.waiting.splice(index, 1);
          resolve(false);
        }
      }, timeoutMs);
    });
  }

  /** Gets the session: from the player, or (alone) from the refresh cookie. */
  async start(): Promise<boolean> {
    applyTheme(null);
    if (this.post({ type: "arcanist:hello", version: 1 }) && (await this.nextSession(1200))) return true;
    return this.refreshAlone();
  }

  /** The token was refused: a new one from the player, or from the refresh cookie when alone. */
  async renew(): Promise<boolean> {
    if (this.embedded) {
      const answer = this.nextSession(5000);
      this.post({ type: "arcanist:token-expired" });
      return answer;
    }
    return this.refreshAlone();
  }

  private refreshing: Promise<boolean> | null = null;

  private refreshAlone(): Promise<boolean> {
    this.refreshing ??= fetch("/rest/v1/auth/refresh", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "same-origin",
      body: JSON.stringify({ useCookie: true }),
    })
      .then(async (response) => {
        if (!response.ok) return false;
        const body = (await response.json()) as { accessToken?: string };
        this.token = body.accessToken ?? null;
        this.listeners.forEach((listener) => listener());
        return this.token !== null;
      })
      .catch(() => false)
      .finally(() => (this.refreshing = null));
    return this.refreshing;
  }

  /** Asks the player to show something of its own; false when there is no player. */
  open(target: "uploads" | "track", id?: string): boolean {
    return this.post({ type: "arcanist:open", target, id });
  }

  /** Asks the player to play songs of the library (after what plays now); false when there is no player. */
  play(trackIds: string[]): boolean {
    return this.post({ type: "arcanist:play", trackIds });
  }

  notify(kind: "info" | "success" | "error", text: string): void {
    this.post({ type: "arcanist:notify", kind, text });
  }
}

export const host = new Host();

const variables: Array<[keyof HostTheme, string]> = [
  ["background", "--bg"],
  ["foreground", "--fg"],
  ["muted", "--muted"],
  ["surface", "--surface"],
  ["border", "--border"],
  ["accent", "--accent"],
  ["accentForeground", "--accent-fg"],
  ["radius", "--radius"],
  ["font", "--font"],
];

/** The player's look as CSS variables; what it does not say keeps the app's own (light or dark). */
function applyTheme(theme: HostTheme | null): void {
  const root = document.documentElement;
  const dark = theme?.scheme ? theme.scheme === "dark" : matchMedia("(prefers-color-scheme: dark)").matches;
  root.dataset.scheme = dark ? "dark" : "light";
  for (const [key, name] of variables) {
    const value = theme?.[key];
    if (typeof value === "string" && value.length < 200) root.style.setProperty(name, value);
    else root.style.removeProperty(name);
  }
}

matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => applyTheme(host.theme));
