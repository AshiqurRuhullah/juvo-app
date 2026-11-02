"use client";

import { useCallback, useEffect, useState, useSyncExternalStore } from "react";

/** True when the CSS media query matches. False during server rendering, then corrected on the client. */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (notify: () => void) => {
      const list = window.matchMedia(query);
      list.addEventListener("change", notify);
      return () => list.removeEventListener("change", notify);
    },
    [query],
  );
  return useSyncExternalStore(
    subscribe,
    () => window.matchMedia(query).matches,
    () => false,
  );
}

/** Phones: below the 768 px breakpoint. */
export function useIsMobile() {
  return useMediaQuery("(max-width: 767.98px)");
}

/** A value that follows `value` after it has stopped changing for `delay` ms (search boxes). */
export function useDebounced<T>(value: T, delay = 300): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const id = setTimeout(() => setDebounced(value), delay);
    return () => clearTimeout(id);
  }, [value, delay]);
  return debounced;
}

/** Calls the handler when the pressed key combination happens anywhere (for example Ctrl or Cmd + K). */
export function useHotkey(matches: (event: KeyboardEvent) => boolean, handler: (event: KeyboardEvent) => void) {
  useEffect(() => {
    const listener = (event: KeyboardEvent) => {
      if (matches(event)) handler(event);
    };
    window.addEventListener("keydown", listener);
    return () => window.removeEventListener("keydown", listener);
  }, [matches, handler]);
}
