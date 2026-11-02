"use client";

import * as React from "react";
import { cn } from "@/lib/utils";

type Tab = { id: string; label: string };

/** Accessible tabs (arrow keys, roving tabindex). Each tab button is 44 px tall. */
export function Tabs({
  tabs,
  value,
  onValueChange,
  label,
  className,
  children,
}: {
  tabs: Tab[];
  value: string;
  onValueChange: (id: string) => void;
  label: string;
  className?: string;
  children: (active: string) => React.ReactNode;
}) {
  const refs = React.useRef<Record<string, HTMLButtonElement | null>>({});

  const onKeyDown = (event: React.KeyboardEvent) => {
    const index = tabs.findIndex((t) => t.id === value);
    let next = index;
    if (event.key === "ArrowRight") next = (index + 1) % tabs.length;
    else if (event.key === "ArrowLeft") next = (index - 1 + tabs.length) % tabs.length;
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = tabs.length - 1;
    else return;
    event.preventDefault();
    const id = tabs[next]!.id;
    onValueChange(id);
    refs.current[id]?.focus();
  };

  return (
    <div className={className}>
      <div role="tablist" aria-label={label} onKeyDown={onKeyDown} className="grid auto-cols-fr grid-flow-col gap-1 rounded-control bg-sunken p-1">
        {tabs.map((tab) => {
          const selected = tab.id === value;
          return (
            <button
              key={tab.id}
              ref={(el) => {
                refs.current[tab.id] = el;
              }}
              id={`tab-${tab.id}`}
              role="tab"
              type="button"
              aria-selected={selected}
              aria-controls={`panel-${tab.id}`}
              tabIndex={selected ? 0 : -1}
              onClick={() => onValueChange(tab.id)}
              className={cn(
                "min-h-11 rounded-lg px-3 text-base font-semibold transition-colors",
                selected ? "bg-card text-strong shadow-sm" : "text-muted [@media(hover:hover)]:hover:text-strong",
              )}
            >
              {tab.label}
            </button>
          );
        })}
      </div>
      <div role="tabpanel" id={`panel-${value}`} aria-labelledby={`tab-${value}`} className="pt-5">
        {children(value)}
      </div>
    </div>
  );
}
