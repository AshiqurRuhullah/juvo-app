"use client";

import * as React from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Clock } from "lucide-react";
import { cn } from "@/lib/utils";
import { countdownParts, countdownSegments, tickStepMs } from "./logic";

/**
 * Ticks only as often as the shown numbers change: once a minute while more than an hour is left, once a second inside the last hour.
 * The next tick is aimed at the moment a number flips, and the timer stops for good after the end. Before the first tick (server
 * render and first paint) the clock is `null` and the component prints a placeholder of the same size, so nothing shifts.
 */
function useClock(endsAt: string | null | undefined): number | null {
  const [now, setNow] = React.useState<number | null>(null);
  React.useEffect(() => {
    const end = endsAt ? Date.parse(endsAt) : NaN;
    if (!Number.isFinite(end)) return;
    let timer: number | undefined;
    const tick = () => {
      const t = Date.now();
      setNow(t);
      const left = end - t;
      if (left < 0) return;
      const step = tickStepMs(left);
      timer = window.setTimeout(tick, (left % step) + 5);
    };
    // The first reading happens in a timer, not in the effect body, so the first paint stays identical to the server's.
    timer = window.setTimeout(tick, 0);
    return () => window.clearTimeout(timer);
  }, [endsAt]);
  return now;
}

/** True once the end time has passed on this device (null while unknown). */
export function useEnded(endsAt: string | null | undefined): boolean {
  const now = useClock(endsAt);
  return now !== null && (countdownParts(endsAt, now)?.ended ?? false);
}

/**
 * The flash-sale countdown. The visible numbers are hidden from screen readers (they would be read every second); a plain sentence with
 * the end date and time is read instead, and the element is a `timer` with no live region. When the time is up it prints "Offer ended"
 * once and calls `onEnded`, so the page can fetch the real prices again.
 */
export function Countdown({ endsAt, onEnded, size = "sm", className }: { endsAt: string | null | undefined; onEnded?: () => void; size?: "sm" | "lg"; className?: string }) {
  const t = useTranslations("storepromo.countdown");
  const format = useFormatter();
  const now = useClock(endsAt);
  const parts = now === null ? null : countdownParts(endsAt, now);
  const ended = parts?.ended ?? false;
  const firedFor = React.useRef<string | null>(null);

  React.useEffect(() => {
    if (ended && endsAt && firedFor.current !== endsAt) {
      firedFor.current = endsAt;
      onEnded?.();
    }
  }, [ended, endsAt, onEnded]);

  if (!endsAt || !Number.isFinite(Date.parse(endsAt))) return null;

  const end = new Date(endsAt);
  const when = format.dateTime(end, { dateStyle: "medium", timeStyle: "short", timeZone: "Asia/Dhaka" });
  const segments = parts && !ended ? countdownSegments(parts) : [];
  const pad = (n: number) => format.number(n, { minimumIntegerDigits: 2, useGrouping: false });

  return (
    <span
      role="timer"
      data-testid="countdown"
      data-state={parts === null ? "pending" : ended ? "ended" : "running"}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-control bg-primary-soft px-2 font-medium tabular-nums text-primary-dark",
        size === "lg" ? "min-h-9 text-base" : "min-h-7 text-xs",
        // The width of a typical reading is reserved so the line does not grow when the numbers appear.
        !ended && (size === "lg" ? "min-w-[15rem]" : "min-w-[10.5rem]"),
        className,
      )}
    >
      <Clock className={size === "lg" ? "size-4" : "size-3.5"} aria-hidden />
      <span aria-hidden>
        {ended ? (
          t("ended")
        ) : parts === null ? (
          <>
            {t("endsIn")} <span className="opacity-60">--</span>
          </>
        ) : (
          <>
            {t("endsIn")}{" "}
            {segments.map((s) => (
              <span key={s.unit} className="me-1 inline-block">
                {s.unit === "d" ? format.number(s.value) : pad(s.value)}
                {t(`unit.${s.unit}`)}
              </span>
            ))}
          </>
        )}
      </span>
      <span className="sr-only">{ended ? t("endedSr") : t("endsAt", { when })}</span>
    </span>
  );
}
