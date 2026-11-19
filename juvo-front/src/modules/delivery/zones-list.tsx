"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { FileUp, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Tabs } from "@/components/ui/tabs";
import { Alert } from "@/components/ui/surfaces";
import { SearchBox, useTableLabels } from "@/components/data/list-kit";
import { PAGE_SIZE } from "@/lib/pagination";
import { useMoney } from "@/modules/catalog/format";
import { useZones, type Zone } from "./api";
import { ActiveBadge, FallbackBadge, KindBadge, useDeliveryRights } from "./common";
import { CountriesView } from "./countries-view";
import { matchesName, zoneSummaryParts } from "./logic";

const UpazilaImport = dynamic(() => import("./upazila-import"), { ssr: false });

/** Delivery zones for staff: the zone list (cards on phones, shared pager) and, with the country permission, the international country list. */
export function ZonesList() {
  const t = useTranslations("delivery.list");
  const rights = useDeliveryRights();
  const [tab, setTab] = React.useState("zones");
  const [importOpen, setImportOpen] = React.useState(false);
  const tabs = [{ id: "zones", label: t("tabZones") }, ...(rights.countryView ? [{ id: "countries", label: t("tabCountries") }] : [])];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-muted">{t("subtitle")}</p>
        <div className="flex flex-wrap gap-2">
          {rights.edit ? (
            <Button variant="secondary" onClick={() => setImportOpen(true)} data-testid="open-import">
              <FileUp aria-hidden /> {t("importUpazilas")}
            </Button>
          ) : null}
          {rights.create ? (
            <Button asChild data-testid="zone-new">
              <Link href="/admin/delivery/zones/new">
                <Plus aria-hidden /> {t("newZone")}
              </Link>
            </Button>
          ) : null}
        </div>
      </div>
      {importOpen ? <UpazilaImport open={importOpen} onOpenChange={setImportOpen} /> : null}
      {tabs.length > 1 ? (
        <Tabs tabs={tabs} value={tab} onValueChange={setTab} label={t("tabsLabel")}>
          {(active) => (active === "countries" ? <CountriesView /> : <ZoneTable />)}
        </Tabs>
      ) : (
        <ZoneTable />
      )}
    </div>
  );
}

function ZoneTable() {
  const t = useTranslations("delivery.list");
  const tc = useTranslations("delivery.common");
  const labels = useTableLabels();
  const money = useMoney();
  const query = useZones();
  const [searchText, setSearchText] = React.useState("");
  const [page, setPage] = React.useState(1);
    const filtered = React.useMemo(() => (query.data ?? []).filter((z) => matchesName(z, searchText) || z.code.toLowerCase().includes(searchText.trim().toLowerCase())), [query.data, searchText]);
  const rows = React.useMemo(() => filtered.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE), [filtered, page]);

  const columns = React.useMemo<ColumnDef<Zone, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="block min-w-0">
            <Link href={`/admin/delivery/zones/${row.original.id}`} className="inline-flex min-h-11 min-w-24 items-center rounded-control font-semibold text-strong [overflow-wrap:anywhere] hover:text-primary hover:underline" data-testid="zone-link">
              {row.original.nameEn}
            </Link>
            <span className="block font-mono text-xs text-muted">{row.original.code}</span>
          </span>
        ),
      },
      {
        id: "kind",
        header: t("columns.kind"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="flex flex-wrap gap-1.5">
            <KindBadge kind={row.original.kind} />
            {row.original.isFallback ? <FallbackBadge /> : null}
          </span>
        ),
      },
      {
        id: "areas",
        header: t("columns.areas"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => {
          const z = row.original;
          return (
            <span data-testid="zone-areas">
              {z.kind === "Domestic" ? t("areaCount", { count: z.areas.length }) : z.countryCodes.length ? z.countryCodes.join(", ") : <span className="text-muted">{t("noCountries")}</span>}
            </span>
          );
        },
      },
      {
        id: "rate",
        header: t("columns.rate"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => {
          const p = zoneSummaryParts(row.original);
          return (
            <span className="block text-sm" data-testid="zone-rate">
              <span className="block font-medium text-strong">{t("baseFee", { amount: money(p.base) })}</span>
              <span className="block text-muted">
                {p.free === null ? t("noFree") : t("freeFrom", { amount: money(p.free) })}
                {" · "}
                {t("slabCount", { count: p.slabCount })}
              </span>
            </span>
          );
        },
      },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <ActiveBadge isActive={row.original.isActive} /> },
      {
        id: "actions",
        header: "",
        enableSorting: false,
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/delivery/zones/${row.original.id}`} aria-label={t("open", { name: row.original.nameEn })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, money],
  );

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox
          className="sm:col-span-2"
          value={searchText}
          onChange={(v) => {
            setSearchText(v);
            setPage(1);
          }}
          placeholder={t("searchPlaceholder")}
          label={t("search")}
        />
      </div>
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={rows} loading={query.isLoading} getRowId={(z) => z.id} labels={labels} page={page} pageSize={PAGE_SIZE} total={filtered.length} onPageChange={setPage} />
    </div>
  );
}
