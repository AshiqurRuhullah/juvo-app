"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Scale, SlidersHorizontal, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { ChoiceRow, Field, Switch, Textarea } from "@/components/ui/form";
import {
  Dialog,
  DialogDescription,
  DialogTitle,
  ModalContent,
} from "@/components/ui/overlays";
import { RadioGroup, RadioItem } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { Can } from "@/components/providers/session-provider";
import {
  FilterSelect,
  SearchBox,
  useListState,
  useTableLabels,
} from "@/components/data/list-kit";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import {
  MANUAL_REASONS,
  REASON_SIGN,
  useAdjustStock,
  useSetReorderLevel,
  useStock,
  type ManualReason,
  type StockRow,
} from "./api";
import { QtyInput, VariantText, useBranchOptions } from "./common";

/** Stock per variant and branch (module V): the numbers, the low-stock flag and the two things staff do with them. */
export function StockList() {
  const t = useTranslations("inventory.stock");
  const tc = useTranslations("inventory.common");
  const labels = useTableLabels();
  const branches = useBranchOptions();
  const state = useListState<{ branchId?: string; low?: string }>({}, 20, [
    { id: "product", desc: false },
  ]);
  const [adjusting, setAdjusting] = React.useState<StockRow | null>(null);
  const [levelFor, setLevelFor] = React.useState<StockRow | null>(null);

  const query = useStock({
    page: state.page,
    pageSize: state.pageSize,
    sort: state.sort,
    search: state.search,
    branchId: state.filters.branchId,
    lowOnly: state.filters.low === "1",
  });

  const columns = React.useMemo<ColumnDef<StockRow, unknown>[]>(
    () => [
      {
        id: "product",
        header: t("columns.product"),
        meta: { primary: true },
        cell: ({ row }) => (
          <div className="min-w-0 py-1">
            <Link
              href={`/admin/products/${row.original.productId}`}
              className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline"
            >
              <span className="[overflow-wrap:anywhere]">
                {row.original.productName}
              </span>
            </Link>
            <VariantText
              sku={row.original.sku}
              colorName={row.original.colorName}
              size={row.original.size}
            />
          </div>
        ),
      },
      {
        id: "branch",
        header: t("columns.branch"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <Badge variant="outline">{row.original.branchCode}</Badge>
        ),
      },
      {
        id: "onHand",
        header: t("columns.onHand"),
        meta: { align: "end", secondary: true },
        cell: ({ row }) => (
          <span className="tabular-nums">{toNumber(row.original.onHand)}</span>
        ),
      },
      {
        id: "reserved",
        header: t("columns.reserved"),
        enableSorting: false,
        meta: { align: "end", secondary: true },
        cell: ({ row }) => (
          <span className="tabular-nums text-muted">
            {toNumber(row.original.reserved)}
          </span>
        ),
      },
      {
        id: "available",
        header: t("columns.available"),
        meta: { align: "end", secondary: true },
        cell: ({ row }) => (
          <span className="flex items-center justify-end gap-2">
            {row.original.isLow ? (
              <Badge variant="warning">
                <TriangleAlert className="size-3.5" aria-hidden /> {t("low")}
              </Badge>
            ) : null}
            <span className="font-semibold tabular-nums">
              {toNumber(row.original.available)}
            </span>
          </span>
        ),
      },
      {
        id: "threshold",
        header: t("columns.threshold"),
        enableSorting: false,
        meta: { align: "end", hideOnMobile: true },
        cell: ({ row }) =>
          toNumber(row.original.reorderLevel) > 0 ? (
            <span className="tabular-nums">
              {toNumber(row.original.reorderLevel)}
            </span>
          ) : (
            <span className="text-muted">{t("noThreshold")}</span>
          ),
      },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <span className="flex flex-col items-stretch gap-2 md:flex-row md:flex-wrap md:justify-end">
            <Can permission="inventory.stock.adjust" scope="anywhere">
              <Button
                variant="secondary"
                onClick={() => setAdjusting(row.original)}
                aria-label={t("adjustFor", {
                  sku: row.original.sku,
                  branch: row.original.branchCode,
                })}
              >
                <SlidersHorizontal aria-hidden /> {t("adjust")}
              </Button>
            </Can>
            <Can permission="inventory.reorder.edit" scope="anywhere">
              <Button
                variant="ghost"
                onClick={() => setLevelFor(row.original)}
                aria-label={t("levelFor", {
                  sku: row.original.sku,
                  branch: row.original.branchCode,
                })}
              >
                <Scale aria-hidden /> {t("setLevel")}
              </Button>
            </Can>
          </span>
        ),
      },
    ],
    [t],
  );

  const filtersActive = !!(
    state.searchText ||
    state.filters.branchId ||
    state.filters.low
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
        <SearchBox
          className="lg:w-80"
          value={state.searchText}
          onChange={state.setSearchText}
          placeholder={t("searchPlaceholder")}
          label={t("search")}
        />
        <div className="grid flex-1 gap-3 sm:grid-cols-2">
          {branches.length > 1 ? (
            <FilterSelect
              value={state.filters.branchId}
              onChange={(v) => state.setFilter("branchId", v)}
              options={branches}
              allLabel={tc("anyBranch")}
              label={tc("branch")}
              searchPlaceholder={tc("search")}
              emptyText={tc("noMatch")}
            />
          ) : null}
          <ChoiceRow htmlFor="stock-low">
            <Switch
              id="stock-low"
              checked={state.filters.low === "1"}
              onCheckedChange={(on) =>
                state.setFilter("low", on ? "1" : undefined)
              }
            />
            {t("lowOnly")}
          </ChoiceRow>
        </div>
        {filtersActive ? (
          <Button variant="ghost" onClick={state.resetFilters}>
            {tc("clear")}
          </Button>
        ) : null}
      </div>

      {query.isError ? (
        <Alert variant="danger">{tc("loadFailed")}</Alert>
      ) : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(r) => `${r.branchId}-${r.variantId}`}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={Number(query.data?.total ?? 0)}
        onPageChange={state.setPage}
        sorting={state.sorting}
        onSortingChange={state.setSorting}
      />

      <AdjustDialog row={adjusting} onClose={() => setAdjusting(null)} />
      <ReorderDialog row={levelFor} onClose={() => setLevelFor(null)} />
    </div>
  );
}

/** Add or remove stock of one variant in one branch with a reason. The reason decides the sign, except for a correction. */
function AdjustDialog({
  row,
  onClose,
}: {
  row: StockRow | null;
  onClose: () => void;
}) {
  const tc = useTranslations("inventory.common");
  const [busy, setBusy] = React.useState(false);
  if (!row) return null;
  // The form is mounted fresh for every row that is opened, so it never shows the previous row's values.
  return (
    <Dialog
      open
      onOpenChange={(open) => (!open && !busy ? onClose() : undefined)}
    >
      <ModalContent size="md" closeLabel={tc("close")}>
        <AdjustForm
          key={`${row.branchId}-${row.variantId}`}
          row={row}
          onClose={onClose}
          onBusy={setBusy}
        />
      </ModalContent>
    </Dialog>
  );
}

function AdjustForm({
  row,
  onClose,
  onBusy,
}: {
  row: StockRow;
  onClose: () => void;
  onBusy: (busy: boolean) => void;
}) {
  const t = useTranslations("inventory.adjust");
  const tr = useTranslations("inventory.reasons");
  const tc = useTranslations("inventory.common");
  const report = useReportError();
  const adjust = useAdjustStock();
  const [reason, setReason] = React.useState<ManualReason>("Correction");
  const [direction, setDirection] = React.useState<"add" | "remove">("add");
  const [quantity, setQuantity] = React.useState("");
  const [note, setNote] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => onBusy(adjust.isPending), [adjust.isPending, onBusy]);

  const sign = REASON_SIGN[reason];
  const effective = sign === "either" ? direction : sign;
  const amount = Number(quantity || 0);
  const onHand = toNumber(row.onHand);
  const available = toNumber(row.available);
  const next = effective === "add" ? onHand + amount : onHand - amount;
  const tooMuch = effective === "remove" && amount > available;

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!quantity || amount < 1) {
      setError(t("errors.quantityRequired"));
      return;
    }
    if (tooMuch) {
      setError(t("errors.tooMuch", { count: available }));
      return;
    }
    setError(null);
    try {
      await adjust.mutateAsync({
        branchId: row.branchId,
        reason,
        note: note.trim() || null,
        lines: [
          {
            variantId: row.variantId,
            quantityChange: effective === "add" ? amount : -amount,
          },
        ],
      });
      toast.success(t("done"));
      onClose();
    } catch (e) {
      report(e);
    }
  };

  return (
    <>
      <form onSubmit={submit} noValidate className="space-y-5">
        <div>
          <DialogTitle className="font-display text-xl font-semibold text-strong">
            {t("title")}
          </DialogTitle>
          <DialogDescription className="mt-1 text-muted [overflow-wrap:anywhere]">
            {t("for", {
              product: row.productName,
              sku: row.sku,
              branch: row.branchCode,
            })}
          </DialogDescription>
        </div>
        <p className="rounded-control bg-sunken px-4 py-3 text-sm text-strong">
          {t("current", {
            onHand,
            reserved: toNumber(row.reserved),
            available,
          })}
        </p>

        <Field label={t("reason")} htmlFor="adj-reason" required>
          <Select
            id="adj-reason"
            value={reason}
            onValueChange={(v) => setReason(v as ManualReason)}
            options={MANUAL_REASONS.map((r) => ({
              value: r,
              label: tr(r),
              hint: t(`sign.${REASON_SIGN[r]}`),
            }))}
            placeholder={t("reason")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
          />
        </Field>
        <p className="-mt-3 text-sm text-muted">{t(`hint.${reason}`)}</p>

        {sign === "either" ? (
          <fieldset className="space-y-1">
            <legend className="text-sm font-semibold text-strong">
              {t("direction")}
            </legend>
            <RadioGroup
              value={direction}
              onValueChange={(v) => setDirection(v as "add" | "remove")}
              className="sm:grid-cols-2"
            >
              <ChoiceRow htmlFor="adj-add">
                <RadioItem id="adj-add" value="add" />
                {t("add")}
              </ChoiceRow>
              <ChoiceRow htmlFor="adj-remove">
                <RadioItem id="adj-remove" value="remove" />
                {t("remove")}
              </ChoiceRow>
            </RadioGroup>
          </fieldset>
        ) : null}

        <Field
          label={t("quantity")}
          htmlFor="adj-qty"
          error={error ?? undefined}
          hint={t("quantityHint")}
          required
        >
          <QtyInput
            id="adj-qty"
            value={quantity}
            onValueChange={setQuantity}
            aria-invalid={!!error}
            aria-describedby={error ? "adj-qty-error" : "adj-qty-hint"}
          />
        </Field>
        {amount > 0 ? (
          <p className="-mt-3 text-sm text-strong" aria-live="polite">
            {t("willBecome", { count: Math.max(0, next) })}
          </p>
        ) : null}

        <Field label={t("note")} htmlFor="adj-note" hint={t("noteHint")}>
          <Textarea
            id="adj-note"
            value={note}
            maxLength={300}
            onChange={(e) => setNote(e.target.value)}
            className="min-h-20"
          />
        </Field>

        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button
            variant="secondary"
            onClick={onClose}
            disabled={adjust.isPending}
          >
            {tc("cancel")}
          </Button>
          <Button type="submit" loading={adjust.isPending}>
            {t("submit")}
          </Button>
        </div>
      </form>
    </>
  );
}

/** The low-stock threshold of one variant in one branch (0 turns the alert off). */
function ReorderDialog({
  row,
  onClose,
}: {
  row: StockRow | null;
  onClose: () => void;
}) {
  const tc = useTranslations("inventory.common");
  const [busy, setBusy] = React.useState(false);
  if (!row) return null;
  return (
    <Dialog
      open
      onOpenChange={(open) => (!open && !busy ? onClose() : undefined)}
    >
      <ModalContent size="sm" closeLabel={tc("close")}>
        <ReorderForm
          key={`${row.branchId}-${row.variantId}`}
          row={row}
          onClose={onClose}
          onBusy={setBusy}
        />
      </ModalContent>
    </Dialog>
  );
}

function ReorderForm({
  row,
  onClose,
  onBusy,
}: {
  row: StockRow;
  onClose: () => void;
  onBusy: (busy: boolean) => void;
}) {
  const t = useTranslations("inventory.reorder");
  const tc = useTranslations("inventory.common");
  const report = useReportError();
  const save = useSetReorderLevel();
  const [level, setLevel] = React.useState(String(toNumber(row.reorderLevel)));
  React.useEffect(() => onBusy(save.isPending), [save.isPending, onBusy]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    try {
      await save.mutateAsync({
        branchId: row.branchId,
        variantId: row.variantId,
        reorderLevel: Number(level || 0),
      });
      toast.success(t("done"));
      onClose();
    } catch (e) {
      report(e);
    }
  };

  return (
    <>
      <form onSubmit={submit} noValidate className="space-y-5">
        <div>
          <DialogTitle className="font-display text-xl font-semibold text-strong">
            {t("title")}
          </DialogTitle>
          <DialogDescription className="mt-1 text-muted [overflow-wrap:anywhere]">
            {t("for", { sku: row.sku, branch: row.branchCode })}
          </DialogDescription>
        </div>
        <Field label={t("level")} htmlFor="rl-level" hint={t("hint")} required>
          <QtyInput id="rl-level" value={level} onValueChange={setLevel} />
        </Field>
        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button
            variant="secondary"
            onClick={onClose}
            disabled={save.isPending}
          >
            {tc("cancel")}
          </Button>
          <Button type="submit" loading={save.isPending}>
            {t("submit")}
          </Button>
        </div>
      </form>
    </>
  );
}
