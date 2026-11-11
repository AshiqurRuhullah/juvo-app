"use client";

import * as React from "react";
import { Area, AreaChart, CartesianGrid, Cell, ComposedChart, Line, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";

/** Chart pieces for the dashboard. Loaded lazily (Recharts is large) and drawn only in the browser. Colours are brand tokens. */

type DayRow = { label: string; sales: number; orders: number };

const axisTick = { fill: "var(--text-muted)", fontSize: 12 } as const;

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

/** Sales as a soft area and orders as a line on a second axis, for the last days. */
export function SalesChart({ data, salesLabel, ordersLabel, money }: { data: DayRow[]; salesLabel: string; ordersLabel: string; money: (value: number) => string }) {
  const id = React.useId().replace(/:/g, "");
  return (
    <ResponsiveContainer width="100%" height="100%">
      <ComposedChart data={data} margin={{ top: 8, right: 4, left: 0, bottom: 0 }} accessibilityLayer={false}>
        <defs>
          <linearGradient id={`fill-${id}`} x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="var(--brand-primary)" stopOpacity={0.22} />
            <stop offset="100%" stopColor="var(--brand-primary)" stopOpacity={0.02} />
          </linearGradient>
        </defs>
        <CartesianGrid vertical={false} stroke="var(--border)" strokeDasharray="3 4" />
        <XAxis dataKey="label" tick={axisTick} tickLine={false} axisLine={false} interval="preserveStartEnd" minTickGap={18} />
        <YAxis yAxisId="sales" tick={axisTick} tickLine={false} axisLine={false} width={44} tickFormatter={(v: number) => `${Math.round(v / 1000)}k`} />
        <YAxis yAxisId="orders" orientation="right" tick={axisTick} tickLine={false} axisLine={false} width={32} />
        <Tooltip
          cursor={{ stroke: "var(--border)" }}
          content={({ active, payload, label }) =>
            active && payload?.length ? (
              <TooltipBox
                title={String(label)}
                lines={[
                  { name: salesLabel, value: money(Number(payload.find((p) => p.dataKey === "sales")?.value ?? 0)), color: "var(--brand-primary)" },
                  { name: ordersLabel, value: String(payload.find((p) => p.dataKey === "orders")?.value ?? 0), color: "var(--info)" },
                ]}
              />
            ) : null
          }
        />
        <Area yAxisId="sales" type="monotone" dataKey="sales" stroke="var(--brand-primary)" strokeWidth={2} fill={`url(#fill-${id})`} activeDot={{ r: 4 }} isAnimationActive={false} />
        <Line yAxisId="orders" type="monotone" dataKey="orders" stroke="var(--info)" strokeWidth={2} strokeDasharray="5 4" dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
      </ComposedChart>
    </ResponsiveContainer>
  );
}

/** Orders by status as a ring. The legend next to it (in the view) carries the numbers, so colour is never the only signal. */
export function StatusDonut({ data }: { data: { name: string; value: number; color: string }[] }) {
  return (
    <ResponsiveContainer width="100%" height="100%">
      <PieChart accessibilityLayer={false}>
        <Pie data={data} dataKey="value" nameKey="name" innerRadius="64%" outerRadius="92%" paddingAngle={2} stroke="var(--surface-card)" strokeWidth={2} isAnimationActive={false}>
          {data.map((entry) => (
            <Cell key={entry.name} fill={entry.color} />
          ))}
        </Pie>
      </PieChart>
    </ResponsiveContainer>
  );
}

/** A tiny trend line for a KPI card. */
export function Sparkline({ values, color }: { values: number[]; color: string }) {
  const id = React.useId().replace(/:/g, "");
  const data = values.map((value, index) => ({ index, value }));
  return (
    <ResponsiveContainer width="100%" height="100%">
      <AreaChart data={data} margin={{ top: 4, right: 2, left: 2, bottom: 2 }} accessibilityLayer={false}>
        <defs>
          <linearGradient id={`spark-${id}`} x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor={color} stopOpacity={0.25} />
            <stop offset="100%" stopColor={color} stopOpacity={0} />
          </linearGradient>
        </defs>
        <XAxis dataKey="index" hide />
        <YAxis hide domain={["dataMin", "dataMax"]} />
        <Area type="monotone" dataKey="value" stroke={color} strokeWidth={2} fill={`url(#spark-${id})`} dot={false} isAnimationActive={false} />
      </AreaChart>
    </ResponsiveContainer>
  );
}
