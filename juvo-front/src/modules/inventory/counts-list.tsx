"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { ClipboardCheck, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Textarea } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, DialogTrigger, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { Can, useSession } from "@/components/providers/session-provider";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { COUNT_STATUSES, useCounts, useStartCount, type CountStatus, type StockCountSummary } from "./api";
import { CountStatusBadge, VariantPicker, VariantText, useBranchOptions, type PickedVariant } from "./common";

/** Stocktakes of the branches this person may view. */
export function CountsList() {
  const t = useTranslations("inventory.counts");
  const ts = useTranslations("inventory.countStatus");
  const tc = useTranslations("inventory.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const branches = useBranchOptions();
  const state = useListState<{ status?: string; branchId?: string }>({}, 20);

  const query = useCounts({ page: state.page, pageSize: state.pageSize, search: state.search, status: state.filters.status as CountStatus | undefined, branchId: state.filters.branchId });

  const columns = React.useMemo<ColumnDef<StockCountSummary, unknown>[]>(
    () => [
      {
        id: "number",
        header: t("columns.number"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/inventory/counts/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="font-mono">{row.original.number}</span>
          </Link>
        ),
      },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant="outline">{row.original.branchCode}</Badge> },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <CountStatusBadge status={row.original.status} /> },
      {
        id: "progress",
        header: t("columns.progress"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <span className="text-muted">{t("progress", { counted: toNumber(row.original.countedCount), lines: toNumber(row.original.lineCount) })}</span>,
      },
      {
        id: "difference",
        header: t("columns.difference"),
        enableSorting: false,
        meta: { align: "end", hideOnMobile: true },
        cell: ({ row }) => <span className="tabular-nums">{t("differenceCell", { lines: toNumber(row.original.differenceLines), total: toNumber(row.original.totalDifference) })}</span>,
      },
      { id: "createdAt", header: t("columns.created"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.createdAt)}</span> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/inventory/counts/${row.original.id}`} aria-label={t("open", { number: row.original.number })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, dateTime],
  );

  const filtersActive = !!(state.searchText || state.filters.status || state.filters.branchId);

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
        <SearchBox className="lg:w-80" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <div className="grid flex-1 gap-3 sm:grid-cols-2">
          <FilterSelect
            value={state.filters.status}
            onChange={(v) => state.setFilter("status", v)}
            options={COUNT_STATUSES.map((s) => ({ value: s, label: ts(s) }))}
            allLabel={t("anyStatus")}
            label={t("columns.status")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
          />
          {branches.length > 1 ? (
            <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          ) : null}
        </div>
        <div className="flex gap-2">
          {filtersActive ? (
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          ) : null}
          <Can permission="inventory.count.create" scope="anywhere">
            <StartCountDialog />
          </Can>
        </div>
      </div>

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(x) => x.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={Number(query.data?.total ?? 0)}
        onPageChange={state.setPage}
      />
    </div>
  );
}

/** Start a count of one branch: every stocked variant, or just the variants chosen here. */
function StartCountDialog() {
  const t = useTranslations("inventory.counts.start");
  const tc = useTranslations("inventory.common");
  const router = useRouter();
  const report = useReportError();
  const { me } = useSession();
  const branches = useBranchOptions();
  const start = useStartCount();
  const [open, setOpen] = React.useState(false);
  const [branchId, setBranchId] = React.useState<string>(me.activeBranchId ?? "");
  const [note, setNote] = React.useState("");
  const [variants, setVariants] = React.useState<PickedVariant[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const chosen = React.useMemo(() => new Set(variants.map((v) => v.variantId)), [variants]);

  const close = (next: boolean) => {
    setOpen(next);
    if (!next) {
      setNote("");
      setVariants([]);
      setError(null);
    }
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!branchId) {
      setError(t("errors.branchRequired"));
      return;
    }
    setError(null);
    try {
      const created = await start.mutateAsync({ branchId, note: note.trim() || null, variantIds: variants.length > 0 ? variants.map((v) => v.variantId) : null });
      toast.success(t("created"));
      close(false);
      router.push(`/admin/inventory/counts/${created.summary.id}`);
    } catch (e) {
      report(e);
    }
  };

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogTrigger asChild>
        <Button>
          <ClipboardCheck aria-hidden /> {t("button")}
        </Button>
      </DialogTrigger>
      <ModalContent size="lg" closeLabel={tc("close")}>
        <form onSubmit={submit} noValidate className="space-y-5">
          <div>
            <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
            <DialogDescription className="mt-1 text-muted">{t("description")}</DialogDescription>
          </div>
          <Field label={tc("branch")} htmlFor="sc-branch" error={error ?? undefined} required>
            <Select id="sc-branch" value={branchId} onValueChange={setBranchId} options={branches} placeholder={t("chooseBranch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!error} />
          </Field>
          <Field label={t("note")} htmlFor="sc-note" hint={t("noteHint")}>
            <Textarea id="sc-note" value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} className="min-h-20" />
          </Field>

          <div className="space-y-3">
            <p className="text-sm font-semibold text-strong">{t("scope")}</p>
            <p className="text-sm text-muted">{variants.length === 0 ? t("scopeAll") : t("scopeSome", { count: variants.length })}</p>
            <VariantPicker chosen={chosen} idPrefix="sc" sourceBranchId={branchId || undefined} onAdd={(v) => setVariants((old) => (old.some((x) => x.variantId === v.variantId) ? old : [...old, v]))} />
            {variants.length > 0 ? (
              <ul className="divide-y divide-line rounded-control border border-line" aria-label={t("chosen")}>
                {variants.map((v) => (
                  <li key={v.variantId} className="flex items-center justify-between gap-2 p-2 ps-3">
                    <div className="min-w-0">
                      <p className="font-semibold text-strong [overflow-wrap:anywhere]">{v.productName}</p>
                      <VariantText sku={v.sku} colorName={v.colorName} size={v.size} />
                    </div>
                    <Button variant="ghost" size="icon" aria-label={t("remove", { sku: v.sku })} onClick={() => setVariants((old) => old.filter((x) => x.variantId !== v.variantId))}>
                      <Trash2 aria-hidden />
                    </Button>
                  </li>
                ))}
              </ul>
            ) : null}
          </div>

          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <Button variant="secondary" onClick={() => close(false)} disabled={start.isPending}>
              {tc("cancel")}
            </Button>
            <Button type="submit" loading={start.isPending}>
              {t("submit")}
            </Button>
          </div>
        </form>
      </ModalContent>
    </Dialog>
  );
}
