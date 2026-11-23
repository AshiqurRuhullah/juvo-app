/**
 * Pure rules for the installable web app (no browser objects, so they can be unit-tested). The service worker keeps its own copy of
 * the cache rules in public/sw.js (a public file cannot import TypeScript); tests/pwa-sw.test.ts checks that file directly.
 */

export const DAY_MS = 24 * 60 * 60 * 1000;
/** After a dismissal the install prompt stays away this long. */
export const DISMISS_DAYS = 30;
/** Visits needed before the prompt appears (a visit is one browser session). */
export const MIN_VISITS = 2;

/** Paths where neither the install prompt nor the update banner is shown (transactions and personal pages). */
const QUIET_PREFIXES = ["/checkout", "/cart", "/pay", "/order", "/track", "/login", "/first-login", "/forgot-password", "/reset-password", "/account", "/admin", "/offline"];

export function isQuietPath(pathname: string): boolean {
  return QUIET_PREFIXES.some((p) => pathname === p || pathname.startsWith(`${p}/`));
}

export interface InstallState {
  /** Number of separate browser sessions in which the shop was opened. */
  visits: number;
  /** Time of the last dismissal (ms since epoch), or null. */
  dismissedAt: number | null;
  /** The customer did something meaningful (for example put an item in the cart). */
  action: boolean;
}

export const EMPTY_STATE: InstallState = { visits: 0, dismissedAt: null, action: false };

/** The install prompt rule: second visit or a meaningful action, never while dismissed (30 days), never when already installed. */
export function shouldShowInstall(state: InstallState, now: number, installed: boolean): boolean {
  if (installed) return false;
  if (state.dismissedAt != null && now - state.dismissedAt < DISMISS_DAYS * DAY_MS) return false;
  return state.visits >= MIN_VISITS || state.action;
}

/** iPhone or iPad Safari (other iOS browsers cannot add to the home screen from their own menu in the same way). */
export function isIosSafari(userAgent: string): boolean {
  const ios = /iPhone|iPad|iPod/.test(userAgent);
  const otherBrowser = /CriOS|FxiOS|EdgiOS|OPiOS|GSA\//.test(userAgent);
  return ios && !otherBrowser && /Safari\//.test(userAgent);
}

export function parseState(raw: string | null): InstallState {
  if (!raw) return { ...EMPTY_STATE };
  try {
    const v = JSON.parse(raw) as Partial<InstallState>;
    return {
      visits: typeof v.visits === "number" && v.visits >= 0 ? Math.floor(v.visits) : 0,
      dismissedAt: typeof v.dismissedAt === "number" ? v.dismissedAt : null,
      action: v.action === true,
    };
  } catch {
    return { ...EMPTY_STATE };
  }
}

export const STORAGE_KEY = "juvo_pwa";
export const IOS_SEEN_KEY = "juvo_pwa_ios_seen";
export const VISIT_FLAG_KEY = "juvo_pwa_visit"; // sessionStorage: this session already counted

export function readState(): InstallState {
  try {
    return parseState(window.localStorage.getItem(STORAGE_KEY));
  } catch {
    return { ...EMPTY_STATE };
  }
}

export function writeState(state: InstallState): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  } catch {
    /* private window or blocked storage: the prompt simply works without memory */
  }
}

/** Counts this browser session once and returns the new state. */
export function countVisit(): InstallState {
  const state = readState();
  try {
    if (window.sessionStorage.getItem(VISIT_FLAG_KEY)) return state;
    window.sessionStorage.setItem(VISIT_FLAG_KEY, "1");
  } catch {
    return state;
  }
  const next = { ...state, visits: state.visits + 1 };
  writeState(next);
  return next;
}

/** Call after a meaningful action (adding to the cart); it makes the prompt eligible on the next page view. */
export function markMeaningfulAction(): void {
  const state = readState();
  if (!state.action) writeState({ ...state, action: true });
}

export function dismissInstall(now: number): InstallState {
  const next = { ...readState(), dismissedAt: now };
  writeState(next);
  return next;
}

export function iosHintSeen(): boolean {
  try {
    return window.localStorage.getItem(IOS_SEEN_KEY) === "1";
  } catch {
    return false;
  }
}

export function markIosHintSeen(): void {
  try {
    window.localStorage.setItem(IOS_SEEN_KEY, "1");
  } catch {
    /* ignore */
  }
}
