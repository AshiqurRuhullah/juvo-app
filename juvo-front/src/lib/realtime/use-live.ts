"use client";

import * as React from "react";
import { useQueryClient, type QueryKey } from "@tanstack/react-query";

/**
 * Live updates over the API's SignalR hub (docs/couriers-api.md section 10).
 *
 * The browser never holds the access token: it asks the BFF for a short hub token (`POST /api/bff/api/v1/realtime/token`, or the
 * guest variant with the order proof), then connects straight to the hub. The SignalR client (about 50 kB) is loaded only when a
 * screen asks for live updates, so pages that do not use it never download it. Events are at-least-once, so handlers must be
 * idempotent; the usual handler just refreshes the affected queries, which `useLiveRefresh` does with a short debounce.
 */

export const LIVE_EVENTS = ["OrderStatusChanged", "PaymentStatusChanged", "ShipmentEventAdded", "ReturnStatusChanged", "NewOrder"] as const;
export type LiveEventName = (typeof LIVE_EVENTS)[number];

/** Fields every event carries; the rest depends on the event and on whether the group is the customer's or the staff's. */
export type LiveEvent = { name: LiveEventName; orderId?: string; orderNumber?: string; at?: string; [key: string]: unknown };

export type LiveStatus = "off" | "connecting" | "live" | "reconnecting" | "offline";

export type GuestProof = { number: string; mobile: string } | { token: string };

export type LiveOptions = {
  /** staff: joins a branch group (and optional single orders); customer: joins own orders; guest: the one order of the proof. */
  mode: "staff" | "customer" | "guest";
  /** Staff: a branch id or `all`. Without it a staff screen only follows the orders it joins. */
  branch?: string | null;
  /** Orders to follow (customer, guest and staff). */
  orderIds?: string[];
  proof?: GuestProof;
  enabled?: boolean;
  onEvent: (event: LiveEvent) => void;
};

type HubToken = { token: string; expiresAt: string; hubPath: string; orderId?: string | null };

async function fetchToken(mode: LiveOptions["mode"], proof?: GuestProof): Promise<HubToken> {
  const guest = mode === "guest";
  const response = await fetch(guest ? "/api/bff/api/v1/store/orders/track/realtime-token" : "/api/bff/api/v1/realtime/token", {
    method: "POST",
    credentials: "same-origin",
    headers: guest ? { "Content-Type": "application/json" } : undefined,
    body: guest ? JSON.stringify(proof ?? {}) : undefined,
  });
  if (!response.ok) throw new Error(`realtime token ${response.status}`);
  return (await response.json()) as HubToken;
}

let hubBasePromise: Promise<string> | undefined;
/** The API origin for the socket, asked once from the BFF (a run-time setting, see api/bff/realtime/hub). */
function hubBase(): Promise<string> {
  hubBasePromise ??= fetch("/api/bff/realtime/hub", { credentials: "same-origin" })
    .then((r) => r.json() as Promise<{ url: string }>)
    .then((j) => j.url.replace(/\/+$/, ""))
    .catch((error) => {
      hubBasePromise = undefined;
      throw error;
    });
  return hubBasePromise;
}

/** Connects, joins the groups, reports the connection state. Reconnects (with a fresh token) and re-joins on its own. */
export function useLive(options: LiveOptions): LiveStatus {
  const { mode, branch, enabled = true } = options;
  const [status, setStatus] = React.useState<LiveStatus>("connecting");
  const handler = React.useRef(options.onEvent);
  React.useEffect(() => {
    handler.current = options.onEvent;
  });

  // Stable keys so a new array or object with the same content does not tear the connection down.
  const orderKey = (options.orderIds ?? []).slice().sort().join(",");
  const proofKey = options.proof ? JSON.stringify(options.proof) : "";
  const active = enabled && !(mode === "guest" && !proofKey);

  React.useEffect(() => {
    if (!active) return;
    let cancelled = false;
    let connection: import("@microsoft/signalr").HubConnection | undefined;
    const proof = proofKey ? (JSON.parse(proofKey) as GuestProof) : undefined;
    const orders = orderKey ? orderKey.split(",") : [];

    (async () => {
      try {
        setStatus("connecting");
        const [signalr, first, base] = await Promise.all([import("@microsoft/signalr"), fetchToken(mode, proof), hubBase()]);
        if (cancelled) return;
        let pending: HubToken | undefined = first;
        const built = new signalr.HubConnectionBuilder()
          .withUrl(base + first.hubPath, {
            accessTokenFactory: async () => {
              const next = pending ?? (await fetchToken(mode, proof));
              pending = undefined;
              return next.token;
            },
          })
          .withAutomaticReconnect([0, 2000, 5000, 10000, 20000, 30000])
          .configureLogging(signalr.LogLevel.None)
          .build();
        connection = built;

        for (const name of LIVE_EVENTS) built.on(name, (payload: Record<string, unknown>) => handler.current({ name, ...(payload ?? {}) } as LiveEvent));

        const join = async () => {
          if (mode === "staff" && branch) await built.invoke("JoinBranch", branch);
          // A guest's token names the one order of the proof (the tracking answer carries no order id): it must join that group itself.
          const follow = mode === "guest" && first.orderId ? [first.orderId] : orders;
          for (const id of follow) await built.invoke("JoinOrder", id);
        };
        built.onreconnecting(() => !cancelled && setStatus("reconnecting"));
        built.onreconnected(async () => {
          if (cancelled) return;
          try {
            await join(); // groups are lost on reconnect
            setStatus("live");
            // Events can be missed while offline: tell the screen to reload what it shows.
            handler.current({ name: "OrderStatusChanged", at: new Date().toISOString(), resync: true });
          } catch {
            setStatus("offline");
          }
        });
        built.onclose(() => !cancelled && setStatus("offline"));

        await built.start();
        if (cancelled) return;
        await join();
        if (!cancelled) setStatus("live");
      } catch {
        if (!cancelled) setStatus("offline");
      }
    })();

    return () => {
      cancelled = true;
      void connection?.stop();
    };
  }, [active, mode, branch, orderKey, proofKey]);

  return active ? status : "off";
}

/**
 * The common case: when any event arrives, refresh the given query roots once (events come in bursts, so refreshes are merged
 * within `delayMs`). Returns the connection state for a small "Live" indicator.
 */
export function useLiveRefresh(
  options: Omit<LiveOptions, "onEvent"> & { queryKeys: QueryKey[]; events?: LiveEventName[]; delayMs?: number; onEvent?: (event: LiveEvent) => void },
): LiveStatus {
  const queryClient = useQueryClient();
  const { queryKeys, events, delayMs = 600, onEvent, ...rest } = options;
  const keysRef = React.useRef(queryKeys);
  const eventsRef = React.useRef(events);
  const timer = React.useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  React.useEffect(() => {
    keysRef.current = queryKeys;
    eventsRef.current = events;
  });
  React.useEffect(() => () => clearTimeout(timer.current), []);

  return useLive({
    ...rest,
    onEvent: (event) => {
      onEvent?.(event);
      if (eventsRef.current && !eventsRef.current.includes(event.name)) return;
      clearTimeout(timer.current);
      timer.current = setTimeout(() => {
        for (const key of keysRef.current) void queryClient.invalidateQueries({ queryKey: key });
      }, delayMs);
    },
  });
}
