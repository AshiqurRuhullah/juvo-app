"use client";

import * as React from "react";
import { Area, Bar, BarChart, CartesianGrid, ComposedChart, Line, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";

/** Chart pieces of the report screens. Loaded lazily (Recharts is large) and drawn only in the browser. Colours are brand tokens. */

const axisTick = { fill: "var(--text-muted)", fontSize: 12 } as const;

export type Series = { key: string; label: string; color: string; kind?: "area" | "line" | "bar"; dashed?: boolean; percent?: boolean };

type Row = Record<string, string | number | null>;

function TooltipBox({ title, lines }: { title: string; lines: { name: string; value: string; color: string }[] }) {
  return (
    <div className="rounded-control border border-line bg-card px-3 py-2 text-sm shadow-pop">
      <p className="mb-1 font-medium text-strong">{title}</p>
      <ul className="space-y-0.5">
        {lines.map((line) => (
          <li key={line.name} className="flex items-center gap-2 text-muted">
            <span className="size-2.5 rounded-full" style={{ backgroundColor: line.color }} aria-hidden />
            {line.name}: <span className="font-medium tabular-nums text-strong">{line.value}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

const compact = (v: number) => (Math.abs(v) >= 1000 ? `${Math.round(v / 100) / 10}k` : String(v));

function tooltip(series: Series[], money: (n: number) => string, percent: (n: number) => string) {
  return function Content({ active, payload, label }: { active?: boolean; payload?: readonly { dataKey?: string | number | ((obj: never) => unknown); value?: unknown }[]; label?: unknown }) {
    if (!active || !payload?.length) return null;
    return (
      <TooltipBox
        title={String(label)}
        lines={series
          .map((s) => {
            const raw = payload.find((p) => p.dataKey === s.key)?.value;
            return raw === undefined || raw === null ? null : { name: s.label, value: s.percent ? percent(Number(raw)) : money(Number(raw)), color: s.color };
          })
          .filter((x): x is { name: string; value: string; color: string } => x !== null)}
      />
    );
  };
}

/** Values over time: the first series is a soft area, the others lines. Missing values (hidden cost) leave a gap, not a zero. */
export function TrendChart({ data, series, money, percent }: { data: Row[]; series: Series[]; money: (n: number) => string; percent: (n: number) => string }) {
  const id = React.useId().replace(/:/g, "");
  return (
    <ResponsiveContainer width="100%" height="100%">
      <ComposedChart data={data} margin={{ top: 8, right: 8, left: 0, bottom: 0 }} accessibilityLayer={false}>
        <defs>
          <linearGradient id={`fill-${id}`} x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor={series[0]?.color ?? "var(--brand-primary)"} stopOpacity={0.22} />
            <stop offset="100%" stopColor={series[0]?.color ?? "var(--brand-primary)"} stopOpacity={0.02} />
          </linearGradient>
        </defs>
        <CartesianGrid vertical={false} stroke="var(--border)" strokeDasharray="3 4" />
        <XAxis dataKey="label" tick={axisTick} tickLine={false} axisLine={false} interval="preserveStartEnd" minTickGap={24} />
        <YAxis tick={axisTick} tickLine={false} axisLine={false} width={48} tickFormatter={compact} />
        <Tooltip cursor={{ stroke: "var(--border)" }} content={tooltip(series, money, percent)} />
        {series.map((s, i) =>
          i === 0 ? (
            <Area key={s.key} type="monotone" dataKey={s.key} stroke={s.color} strokeWidth={2} fill={`url(#fill-${id})`} activeDot={{ r: 4 }} isAnimationActive={false} connectNulls={false} />
          ) : (
            <Line key={s.key} type="monotone" dataKey={s.key} stroke={s.color} strokeWidth={2} strokeDasharray={s.dashed ? "5 4" : undefined} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} connectNulls={false} />
          ),
        )}
      </ComposedChart>
    </ResponsiveContainer>
  );
}

/** Bars side by side per label; `horizontal` for long labels (products, couriers). Values are money or percentages per series. */
export function BarsChart({ data, series, money, percent, horizontal }: { data: Row[]; series: Series[]; money: (n: number) => string; percent: (n: number) => string; horizontal?: boolean }) {
  const anyPercent = series.some((s) => s.percent);
  return (
    <ResponsiveContainer width="100%" height="100%">
      <BarChart data={data} layout={horizontal ? "vertical" : "horizontal"} margin={{ top: 8, right: 12, left: 0, bottom: 0 }} accessibilityLayer={false}>
        <CartesianGrid horizontal={!horizontal} vertical={!!horizontal} stroke="var(--border)" strokeDasharray="3 4" />
        {horizontal ? (
          <>
            <XAxis type="number" tick={axisTick} tickLine={false} axisLine={false} tickFormatter={anyPercent ? (v: number) => `${v}%` : compact} />
            <YAxis type="category" dataKey="label" tick={axisTick} tickLine={false} axisLine={false} width={110} />
          </>
        ) : (
          <>
            <XAxis dataKey="label" tick={axisTick} tickLine={false} axisLine={false} interval="preserveStartEnd" minTickGap={16} />
            <YAxis tick={axisTick} tickLine={false} axisLine={false} width={48} tickFormatter={anyPercent ? (v: number) => `${v}%` : compact} />
          </>
        )}
        <Tooltip cursor={{ fill: "var(--surface-sunken)" }} content={tooltip(series, money, percent)} />
        {series.map((s) => (
          <Bar key={s.key} dataKey={s.key} fill={s.color} radius={horizontal ? [0, 4, 4, 0] : [4, 4, 0, 0]} maxBarSize={36} isAnimationActive={false} />
        ))}
      </BarChart>
    </ResponsiveContainer>
  );
}
