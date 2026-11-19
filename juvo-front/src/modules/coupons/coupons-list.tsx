"use client";

import * as React from "react";
import { useFormatter, useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Lock, Pencil, Plus, Power, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, SearchBox, useDateTime, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { PAGE_SIZE, clampPage, pageCount } from "@/lib/pagination";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { MAX_PAGES, useCoupons, useSaveCoupon, type Coupon } from "./api";
import { StatusBadge, useCouponProblem, useCouponRights, useStatusOptions } from "./common";
import { CouponDialog } from "./coupon-dialog";
import { DeleteCouponDialog } from "./delete-dialog";
import { couponStatus, filterCoupons, usageFigures, withActive } from "./logic";

/** `/admin/coupons`: every coupon with its real status, uses and rules; create, edit, switch on or off and delete follow the staff member's rights. */
export function CouponsList() {
  const t = useTranslations("coupons.list");
  const tc = useTranslations("coupons.common");
  const labels = useTableLabels();
  const money = useMoney();
  const format = useFormatter();
  const dateTime = useDateTime();
  const rights = useCouponRights();
  const problem = useCouponProblem();
  const statusOptions = useStatusOptions();
  const query = useCoupons();
  const save = useSaveCoupon();
  const [searchText, setSearchText] = React.useState("");
  const [status, setStatus] = React.useState<string | undefined>(undefined);
  const [page, setPage] = React.useState(1);
  const [editing, setEditing] = React.useState<Coupon | "new" | null>(null);
  const [deleting, setDeleting] = React.useState<Coupon | null>(null);
  const [now, setNow] = React.useState(() => Date.now());
  React.useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 30_000);
    return () => window.clearInterval(timer);
  }, []);

  const all = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const filtered = React.useMemo(() => filterCoupons(all, searchText, status, now), [all, searchText, status, now]);
  const current = clampPage(page, pageCount(filtered.length));
  const rows = React.useMemo(() => filtered.slice((current - 1) * PAGE_SIZE, current * PAGE_SIZE), [filtered, current]);
  const filtersActive = !!(searchText || status);
  const truncated = (query.data?.total ?? 0) > all.length && all.length >= MAX_PAGES * 100;
  const canChange = rights.edit || rights.remove;

  const toggle = async (coupon: Coupon) => {
    const on = !coupon.isActive;
    try {
      await save.mutateAsync({ id: coupon.id, body: withActive(coupon, on) });
      toast.success(on ? t("switchedOn", { code: coupon.code }) : t("switchedOff", { code: coupon.code }));
    } catch (e) {
      toast.error(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const columns: ColumnDef<Coupon, unknown>[] = [
      {
        id: "code",
        header: t("columns.code"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="block max-w-xs [overflow-wrap:anywhere]">
            <span className="font-mono font-semibold text-strong" data-testid="coupon-code">
              {row.original.code}
            </span>
            {row.original.description ? <span className="block text-sm text-muted">{row.original.description}</span> : null}
          </span>
        ),
      },
      {
        id: "discount",
        header: t("columns.discount"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => {
          const c = row.original;
          const percent = c.kind === "Percent";
          return (
            <span className="block" data-testid="coupon-discount">
              <span className="font-semibold text-strong">{percent ? t("percentOff", { value: format.number(toNumber(c.value), { maximumFractionDigits: 2 }) }) : t("amountOff", { amount: money(c.value) })}</span>
              {percent && c.maxDiscount !== null ? <span className="block text-sm text-muted">{t("cap", { amount: money(c.maxDiscount) })}</span> : null}
            </span>
          );
        },
      },
      {
        id: "minimum",
        header: t("columns.minimum"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <span>{toNumber(row.original.minOrderValue) > 0 ? money(row.original.minOrderValue) : t("noMinimum")}</span>,
      },
      {
        id: "window",
        header: t("columns.window"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block text-sm text-muted">
            {row.original.startsAt || row.original.endsAt ? (
              <>
                <span className="block">{row.original.startsAt ? t("from", { when: dateTime(row.original.startsAt) }) : t("noStart")}</span>
                <span className="block">{row.original.endsAt ? t("until", { when: dateTime(row.original.endsAt) }) : t("noEnd")}</span>
              </>
            ) : (
              t("always")
            )}
          </span>
        ),
      },
      {
        id: "uses",
        header: t("columns.uses"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => {
          const u = usageFigures(row.original.usedCount, row.original.usageLimit);
          return (
            <span className="tabular-nums" data-testid="coupon-uses">
              {u.limit === null ? t("usedUnlimited", { used: u.used }) : t("usedOf", { used: u.used, limit: u.limit })}
            </span>
          );
        },
      },
      {
        id: "stacks",
        header: t("columns.stacks"),
        enableSorting: false,
        meta: { hideOnMobile: true },
        cell: ({ row }) => <span className="text-sm">{row.original.stacksWithPromotions ? t("stacksYes") : t("stacksNo")}</span>,
      },
      {
        id: "status",
        header: t("columns.status"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <StatusBadge status={couponStatus(row.original, now)} />,
      },
      {
        id: "actions",
        header: "",
        enableSorting: false,
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => {
          const c = row.original;
          if (!canChange) return null;
          return (
            <span className="flex flex-col items-stretch gap-2 md:flex-row md:flex-wrap md:justify-end">
              {rights.edit ? (
                <Button variant="secondary" onClick={() => setEditing(c)} aria-label={t("edit", { code: c.code })} data-testid="coupon-edit">
                  <Pencil aria-hidden /> {t("editShort")}
                </Button>
              ) : null}
              {rights.edit ? (
                <Button variant="secondary" onClick={() => void toggle(c)} disabled={save.isPending} aria-label={c.isActive ? t("switchOffLabel", { code: c.code }) : t("switchOnLabel", { code: c.code })} data-testid="coupon-toggle">
                  <Power aria-hidden /> {c.isActive ? t("switchOff") : t("switchOn")}
                </Button>
              ) : null}
              {rights.remove ? (
                <Button variant="secondary" onClick={() => setDeleting(c)} aria-label={t("delete", { code: c.code })} data-testid="coupon-delete">
                  <Trash2 aria-hidden /> {t("deleteShort")}
                </Button>
              ) : null}
            </span>
          );
        },
      },
  ];

  return (
    <div className="space-y-4" data-testid="coupons">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-muted">{t("subtitle")}</p>
        {rights.create ? (
          <Button onClick={() => setEditing("new")} data-testid="coupon-new">
            <Plus aria-hidden /> {t("new")}
          </Button>
        ) : null}
      </div>

      {!canChange ? (
        <Alert variant="info" data-testid="coupons-read-only">
          <span className="flex items-start gap-2">
            <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>{t("readOnly")}</span>
          </span>
        </Alert>
      ) : null}

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
        <FilterSelect
          value={status}
          onChange={(v) => {
            setStatus(v);
            setPage(1);
          }}
          options={statusOptions}
          allLabel={t("anyStatus")}
          label={t("columns.status")}
          searchPlaceholder={tc("search")}
          emptyText={tc("noMatch")}
        />
        {filtersActive ? (
          <div className="flex items-end">
            <Button
              variant="ghost"
              onClick={() => {
                setSearchText("");
                setStatus(undefined);
                setPage(1);
              }}
            >
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>
      <p className="text-sm text-muted">{t("timeNote")}</p>

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {truncated ? <Alert variant="warning">{t("truncated", { shown: all.length, total: query.data?.total ?? 0 })}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={rows}
        loading={query.isLoading}
        getRowId={(c) => c.id}
        labels={labels}
        page={current}
        pageSize={PAGE_SIZE}
        total={filtered.length}
        onPageChange={setPage}
      />

      <CouponDialog target={editing} onClose={() => setEditing(null)} />
      <DeleteCouponDialog coupon={deleting} canSwitchOff={rights.edit} onClose={() => setDeleting(null)} />
    </div>
  );
}
