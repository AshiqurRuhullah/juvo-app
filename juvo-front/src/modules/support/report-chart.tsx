"use client";

import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";

/** New and resolved tickets per day. Loaded lazily (Recharts is large); the view carries a text summary and a data table next to it. */
export type DayPoint = { label: string; created: number; resolved: number };

const tick = { fill: "var(--text-muted)", fontSize: 12 } as const;

export default function DailyChart({ data, createdLabel, resolvedLabel }: { data: DayPoint[]; createdLabel: string; resolvedLabel: string }) {
  return (
    <ResponsiveContainer width="100%" height="100%">
      <BarChart data={data} margin={{ top: 8, right: 4, left: 0, bottom: 0 }} accessibilityLayer={false}>
        <CartesianGrid vertical={false} stroke="var(--border)" strokeDasharray="3 4" />
        <XAxis dataKey="label" tick={tick} tickLine={false} axisLine={false} interval="preserveStartEnd" minTickGap={16} />
        <YAxis tick={tick} tickLine={false} axisLine={false} width={28} allowDecimals={false} />
        <Tooltip cursor={{ fill: "var(--surface-sunken)" }} contentStyle={{ background: "var(--surface-card)", border: "1px solid var(--border)", borderRadius: 8 }} />
        <Legend wrapperStyle={{ fontSize: 13 }} />
        <Bar dataKey="created" name={createdLabel} fill="var(--brand-primary)" radius={[3, 3, 0, 0]} isAnimationActive={false} />
        <Bar dataKey="resolved" name={resolvedLabel} fill="var(--info)" radius={[3, 3, 0, 0]} isAnimationActive={false} />
      </BarChart>
    </ResponsiveContainer>
  );
}
