"use client";

import * as React from "react";
import { MAX_BULK, pageCheck, toggleId, togglePage } from "./logic";

/** Ticked rows of a list, kept across pages, never more than the API's bulk limit. `full` is true after a tick was refused for the limit. */
export function useSelection(limit: number = MAX_BULK) {
  const [selected, setSelected] = React.useState<ReadonlySet<string>>(() => new Set());
  const [full, setFull] = React.useState(false);

  const toggle = React.useCallback(
    (id: string) => {
      const r = toggleId(selected, id, limit);
      setSelected(r.next);
      setFull(r.full);
    },
    [selected, limit],
  );
  const togglePageIds = React.useCallback(
    (ids: readonly string[], on: boolean) => {
      const r = togglePage(selected, ids, on, limit);
      setSelected(r.next);
      setFull(r.full);
    },
    [selected, limit],
  );
  const clear = React.useCallback(() => {
    setSelected(new Set());
    setFull(false);
  }, []);

  return { selected, count: selected.size, full: full || selected.size >= limit, refused: full, toggle, togglePageIds, clear, pageState: (ids: readonly string[]) => pageCheck(selected, ids), limit };
}
