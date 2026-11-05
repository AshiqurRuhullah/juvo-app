"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { ChevronDown } from "lucide-react";
import { Checkbox, Input } from "@/components/ui/form";
import { Badge, Skeleton } from "@/components/ui/surfaces";
import { usePermissionCatalog, type PermissionCatalog } from "./api";

type Module = PermissionCatalog["modules"][number];

const humanize = (value: string) => value.replaceAll("-", " ");

/**
 * Permission matrix: one section per module, a "select all" box per module (tri-state) and per resource, then one box per action.
 * The role editor passes the chosen codes in and gets the new set back. Sections open by themselves while searching.
 */
export function PermissionMatrix({
  value,
  onChange,
  readOnly,
  disabledCodes,
}: {
  value: ReadonlySet<string>;
  onChange: (next: Set<string>) => void;
  readOnly?: boolean;
  /** Codes the person editing cannot grant (the API forbids escalation); shown disabled. */
  disabledCodes?: ReadonlySet<string>;
}) {
  const t = useTranslations("ums.matrix");
  const tm = useTranslations("ums.modules");
  const catalog = usePermissionCatalog();
  const [filter, setFilter] = React.useState("");
  const [open, setOpen] = React.useState<Set<string>>(new Set());

  const needle = filter.trim().toLowerCase();
  const modules = React.useMemo(() => {
    const list = catalog.data?.modules ?? [];
    if (!needle) return list;
    return list
      .map((m) => ({ ...m, permissions: m.permissions.filter((p) => p.code.includes(needle) || p.description.toLowerCase().includes(needle)) }))
      .filter((m) => m.permissions.length > 0);
  }, [catalog.data, needle]);

  if (catalog.isLoading) return <Skeleton className="h-64" />;

  const toggleCodes = (codes: string[], on: boolean) => {
    const next = new Set(value);
    for (const code of codes) {
      if (disabledCodes?.has(code)) continue;
      if (on) next.add(code);
      else next.delete(code);
    }
    onChange(next);
  };

  const moduleLabel = (name: string) => (tm.has(name) ? tm(name) : name);
  const total = catalog.data?.total ?? 0;

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <div className="min-w-56 flex-1 sm:max-w-sm">
          <label htmlFor="pm-filter" className="sr-only">
            {t("filter")}
          </label>
          <Input id="pm-filter" type="search" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder={t("filter")} />
        </div>
        <Badge variant="brand">{t("selected", { count: value.size, total })}</Badge>
        {!needle ? (
          <button
            type="button"
            className="min-h-11 rounded-control px-2 text-sm font-semibold text-primary hover:underline"
            onClick={() => setOpen(open.size > 0 ? new Set() : new Set(modules.map((m) => m.module)))}
          >
            {open.size > 0 ? t("collapseAll") : t("expandAll")}
          </button>
        ) : null}
      </div>

      {modules.length === 0 ? <p className="py-6 text-center text-muted">{t("noMatch")}</p> : null}

      <div className="space-y-2">
        {modules.map((module) => {
          const codes = module.permissions.map((p) => p.code);
          const count = codes.filter((c) => value.has(c)).length;
          const state: boolean | "indeterminate" = count === 0 ? false : count === codes.length ? true : "indeterminate";
          const expanded = !!needle || open.has(module.module);
          const panelId = `pm-${module.module}`;
          return (
            <section key={module.module} className="rounded-card border border-line bg-card">
              <div className="flex items-center gap-2 ps-3 pe-1">
                {!readOnly ? (
                  <label htmlFor={`pm-all-${module.module}`} className="flex size-11 shrink-0 cursor-pointer items-center justify-center">
                    <Checkbox
                      id={`pm-all-${module.module}`}
                      checked={state}
                      onCheckedChange={(on) => toggleCodes(codes, on === true)}
                      aria-label={t("selectModule", { module: moduleLabel(module.module) })}
                    />
                  </label>
                ) : null}
                <button
                  type="button"
                  aria-expanded={expanded}
                  aria-controls={panelId}
                  onClick={() => setOpen((old) => (old.has(module.module) ? new Set([...old].filter((m) => m !== module.module)) : new Set(old).add(module.module)))}
                  className="flex min-h-12 flex-1 items-center justify-between gap-3 rounded-control px-2 text-start"
                >
                  <span className="font-semibold text-strong">{moduleLabel(module.module)}</span>
                  <span className="flex items-center gap-2">
                    <Badge variant={count > 0 ? "brand" : "outline"}>
                      {count}/{codes.length}
                    </Badge>
                    <ChevronDown className={`size-5 text-muted transition-transform ${expanded ? "rotate-180" : ""}`} aria-hidden />
                  </span>
                </button>
              </div>
              {expanded ? (
                <div id={panelId} className="grid gap-x-6 gap-y-4 border-t border-line p-4 md:grid-cols-2">
                  <ResourceGroups module={module} value={value} readOnly={readOnly} disabledCodes={disabledCodes} toggleCodes={toggleCodes} />
                </div>
              ) : null}
            </section>
          );
        })}
      </div>
    </div>
  );
}

function ResourceGroups({
  module,
  value,
  readOnly,
  disabledCodes,
  toggleCodes,
}: {
  module: Module;
  value: ReadonlySet<string>;
  readOnly?: boolean;
  disabledCodes?: ReadonlySet<string>;
  toggleCodes: (codes: string[], on: boolean) => void;
}) {
  const t = useTranslations("ums.matrix");
  const resources = React.useMemo(() => {
    const map = new Map<string, Module["permissions"]>();
    for (const p of module.permissions) map.set(p.resource, [...(map.get(p.resource) ?? []), p]);
    return [...map.entries()];
  }, [module]);

  return (
    <>
      {resources.map(([resource, items]) => {
        const codes = items.map((i) => i.code);
        const count = codes.filter((c) => value.has(c)).length;
        const state: boolean | "indeterminate" = count === 0 ? false : count === codes.length ? true : "indeterminate";
        return (
          <fieldset key={resource} className="min-w-0 space-y-1">
            <legend className="mb-1 flex items-center gap-1">
              {!readOnly ? (
                <label htmlFor={`pm-all-${module.module}-${resource}`} className="flex size-11 shrink-0 cursor-pointer items-center justify-center">
                  <Checkbox
                    id={`pm-all-${module.module}-${resource}`}
                    checked={state}
                    onCheckedChange={(on) => toggleCodes(codes, on === true)}
                    aria-label={t("selectResource", { resource: humanize(resource) })}
                  />
                </label>
              ) : null}
              <span className="font-sans text-sm font-semibold uppercase tracking-wide text-muted">{humanize(resource)}</span>
            </legend>
            <ul className="space-y-0">
              {items.map((p) => {
                const checked = value.has(p.code);
                const blocked = disabledCodes?.has(p.code) ?? false;
                const id = `pm-${p.code}`;
                return (
                  <li key={p.code}>
                    <label htmlFor={id} className={`flex min-h-11 items-center gap-3 rounded-control px-1 ${readOnly || blocked ? "" : "cursor-pointer [@media(hover:hover)]:hover:bg-sunken"}`}>
                      <Checkbox id={id} checked={checked} disabled={readOnly || blocked} onCheckedChange={(on) => toggleCodes([p.code], on === true)} />
                      <span className="min-w-0">
                        <span className="block font-medium capitalize text-strong">{humanize(p.action)}</span>
                        <span className="block truncate text-xs text-muted" title={p.code}>
                          {p.code}
                        </span>
                      </span>
                    </label>
                  </li>
                );
              })}
            </ul>
          </fieldset>
        );
      })}
    </>
  );
}
