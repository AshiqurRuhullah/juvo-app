"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Plus, Search, Trash2, UserCheck, UserPlus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Input, Label, RadioGroup, RadioItem, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import { useDebounced } from "@/lib/hooks";
import { useProduct, useProducts } from "@/modules/catalog/api";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { QtyInput } from "@/modules/inventory/common";
import { addressErrors, emptyDraft, placeOf, toCheckoutAddress, type AddressDraft } from "./address";
import { AddressFields } from "./address-fields";
import { DESK_SOURCES, problemOf, useCreateManualOrder, useCustomerLookup, useDeskDeliveryQuote, useOrderErrorText, type OrderSource } from "./api";
import { AmountList, Section, useSourceLabel } from "./common";
import { useOrderPermissions } from "./permissions";
import { cleanMobile, deskTotals, isBdMobile, newKey } from "./status";

type DeskLine = { variantId: string; productId: string; sku: string; name: string; detail: string; price: number; quantity: number };

/** Search the catalog by name or SKU, open a product, add one of its variants. Prices are the shop's current selling price. */
function ItemPicker({ chosen, onAdd }: { chosen: ReadonlySet<string>; onAdd: (line: Omit<DeskLine, "quantity">) => void }) {
  const t = useTranslations("orders.desk.items");
  const money = useMoney();
  const [text, setText] = React.useState("");
  const term = useDebounced(text.trim(), 350);
  const [productId, setProductId] = React.useState<string | null>(null);
  const list = useProducts({ page: 1, pageSize: 8, search: term, status: "Published" });
  const product = useProduct(productId ?? "");
  const items = list.data?.items ?? [];
  const picked = items.find((p) => p.id === productId);

  return (
    <div className="space-y-3">
      <Field label={t("search")} htmlFor="desk-search" hint={t("hint")}>
        <div className="relative">
          <Search className="pointer-events-none absolute start-3.5 top-1/2 size-5 -translate-y-1/2 text-muted" aria-hidden />
          <Input id="desk-search" type="search" className="ps-11" value={text} autoComplete="off" onChange={(e) => (setText(e.target.value), setProductId(null))} placeholder={t("placeholder")} />
        </div>
      </Field>
      {term.length >= 2 && !productId ? (
        list.isLoading ? (
          <Skeleton className="h-20" aria-busy />
        ) : items.length === 0 ? (
          <p className="text-sm text-muted">{t("none", { term })}</p>
        ) : (
          <ul className="divide-y divide-line overflow-hidden rounded-control border border-line" aria-label={t("results")}>
            {items.map((p) => (
              <li key={p.id}>
                <button type="button" onClick={() => setProductId(p.id)} className="flex min-h-11 w-full items-center justify-between gap-3 bg-card px-4 py-2 text-start [@media(hover:hover)]:hover:bg-sunken" data-testid="desk-product">
                  <span className="min-w-0 font-semibold text-strong [overflow-wrap:anywhere]">{p.nameEn}</span>
                  <span className="shrink-0 text-sm text-muted">{money(p.effectivePrice)}</span>
                </button>
              </li>
            ))}
          </ul>
        )
      ) : null}
      {productId && picked ? (
        <div className="space-y-2 rounded-control border border-line p-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="font-semibold text-strong [overflow-wrap:anywhere]">{picked.nameEn}</p>
            <Button variant="ghost" onClick={() => setProductId(null)}>
              {t("other")}
            </Button>
          </div>
          {product.isLoading ? <Skeleton className="h-16" aria-busy /> : null}
          <ul className="divide-y divide-line">
            {(product.data?.variants ?? [])
              .filter((v) => v.isActive)
              .map((v) => {
                const added = chosen.has(v.id);
                const price = v.priceOverride != null ? toNumber(v.priceOverride) : toNumber(picked.effectivePrice);
                return (
                  <li key={v.id} className="flex flex-wrap items-center justify-between gap-2 py-1.5" data-sku={v.sku}>
                    <span className="min-w-0 text-sm [overflow-wrap:anywhere]">
                      <span className="font-mono text-strong">{v.sku}</span>
                      <span className="text-muted"> {[v.colorName, v.size].filter(Boolean).join(" · ")} · {money(price)}</span>
                    </span>
                    <Button
                      variant="secondary"
                      disabled={added}
                      aria-label={t("addVariant", { sku: v.sku })}
                      onClick={() => onAdd({ variantId: v.id, productId: picked.id, sku: v.sku, name: picked.nameEn, detail: [v.colorName, v.size].filter(Boolean).join(" · "), price })}
                    >
                      <Plus aria-hidden /> {added ? t("added") : t("add")}
                    </Button>
                  </li>
                );
              })}
          </ul>
        </div>
      ) : null}
    </div>
  );
}

/**
 * The social order desk (module S): staff turn a Facebook, WhatsApp, phone or walk-in request into an order. Source, customer (found by
 * mobile or created), address, items, discount and delivery override (only with their permissions), payment and notes. The API
 * works out the real amounts when the order is saved; the figures shown here use the shop's prices and delivery charge so the
 * person can read the customer a number first. The new order starts as Placed and opens on its detail page.
 */
export function OrderDesk() {
  const t = useTranslations("orders.desk");
  const tc = useTranslations("orders.common");
  const router = useRouter();
  const money = useMoney();
  const { me } = useSession();
  const perms = useOrderPermissions();
  const errorText = useOrderErrorText();
  const sourceLabel = useSourceLabel();
  const create = useCreateManualOrder();

  const branchOptions = me.branches.filter((b) => b.isActive).map((b) => ({ value: b.id, label: `${b.code} · ${b.nameEn}` }));
  const [source, setSource] = React.useState<OrderSource>("Facebook");
  const [branchId, setBranchId] = React.useState(me.activeBranchId && !me.isAllBranches ? me.activeBranchId : "");
  const [mobile, setMobile] = React.useState("");
  const [name, setName] = React.useState("");
  const [email, setEmail] = React.useState("");
  const [draft, setDraft] = React.useState<AddressDraft>(emptyDraft);
  const [lines, setLines] = React.useState<DeskLine[]>([]);
  const [method, setMethod] = React.useState("COD");
  const [discount, setDiscount] = React.useState("");
  const [override, setOverride] = React.useState("");
  const [customerNote, setCustomerNote] = React.useState("");
  const [internalNote, setInternalNote] = React.useState("");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [duplicateOpen, setDuplicateOpen] = React.useState(false);
  const attempt = React.useRef<{ fingerprint: string; key: string } | null>(null);

  const validMobile = isBdMobile(mobile);
  const lookup = useCustomerLookup(cleanMobile(mobile), validMobile && canAnywhere(me, "customers.customer.view"));
  const place = React.useMemo(() => placeOf(draft), [draft]);
  const quoteItems = React.useMemo(() => lines.map((l) => ({ variantId: l.variantId, quantity: l.quantity })), [lines]);
  const delivery = useDeskDeliveryQuote(place, quoteItems);
  const discountValue = Math.max(0, Number(discount) || 0);
  const deliveryValue = override.trim() !== "" && perms.overrideDelivery ? Math.max(0, Number(override) || 0) : toNumber(delivery.data?.total);
  const totals = deskTotals({ lines, discount: discountValue, delivery: deliveryValue });
  const customer = lookup.data;

  const addLine = (line: Omit<DeskLine, "quantity">) => setLines((old) => (old.some((l) => l.variantId === line.variantId) ? old : [...old, { ...line, quantity: 1 }]));
  const setQty = (variantId: string, value: string) => setLines((old) => old.map((l) => (l.variantId === variantId ? { ...l, quantity: Math.max(1, Math.min(Number(value) || 1, 999)) } : l)));

  const submit = async (confirmDuplicate = false) => {
    setFormError(null);
    const next: Record<string, string> = {};
    if (!validMobile) next.mobile = t("errors.mobile");
    const found = addressErrors(draft, { needPhone: false });
    if (found.recipient) next.recipient = t("errors.recipient");
    if (found.phone) next.phone = t("errors.phone");
    if (found.district) next.district = t("errors.district");
    if (found.country) next.country = t("errors.country");
    if (found.city) next.city = t("errors.city");
    if (found.line) next.line = t("errors.line");
    if (lines.length === 0) next.lines = t("errors.lines");
    setErrors(next);
    if (Object.keys(next).length) return setFormError(t("errors.fix"));

    const address = toCheckoutAddress(draft);
    if (!address.phone) address.phone = cleanMobile(mobile);
    const body = {
      customerMobile: cleanMobile(mobile),
      customerName: name.trim() || draft.recipientName.trim() || null,
      customerEmail: email.trim() || null,
      source,
      branchId: branchId || null,
      address,
      lines: lines.map((l) => ({ variantId: l.variantId, sku: null, quantity: l.quantity })),
      paymentMethod: method,
      couponCode: null,
      discount: perms.applyDiscount && discountValue > 0 ? discountValue : null,
      deliveryChargeOverride: perms.overrideDelivery && override.trim() !== "" ? deliveryValue : null,
      customerNote: customerNote.trim() || null,
      internalNote: internalNote.trim() || null,
      idempotencyKey: null as string | null,
      confirmDuplicate,
    };
    const fingerprint = JSON.stringify([body.customerMobile, body.lines, body.paymentMethod, body.branchId, body.discount, body.deliveryChargeOverride]);
    if (attempt.current?.fingerprint !== fingerprint) attempt.current = { fingerprint, key: newKey() };
    body.idempotencyKey = attempt.current.key;

    try {
      const order = await create.mutateAsync(body);
      toast.success(t("created", { number: order.number }));
      router.push(`/admin/orders/${order.id}`);
    } catch (failure) {
      const problem = problemOf(failure);
      if (problem?.code === "ORDER.DUPLICATE_SUSPECTED") return setDuplicateOpen(true);
      const reason = problem?.code === "ORDER.CHECKOUT_BLOCKED" && problem.detail ? ` ${problem.detail}` : "";
      setFormError(errorText(failure) + reason);
    }
  };

  const chosen = new Set(lines.map((l) => l.variantId));

  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-6 lg:grid-cols-[1fr_400px] lg:gap-8">
      <form
        className="min-w-0 space-y-5"
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit(false);
        }}
        aria-label={t("title")}
      >
        <Section title={t("source.title")} id="desk-source">
          <RadioGroup value={source} onValueChange={(v) => setSource(v as OrderSource)} className="grid grid-cols-2 gap-2 sm:grid-cols-5" aria-label={t("source.title")}>
            {DESK_SOURCES.map((s) => (
              <div key={s} className="flex min-h-12 items-center gap-2 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
                <RadioItem id={`desk-source-${s}`} value={s} />
                <Label htmlFor={`desk-source-${s}`} className="flex min-h-11 flex-1 cursor-pointer items-center">
                  {sourceLabel(s)}
                </Label>
              </div>
            ))}
          </RadioGroup>
          {branchOptions.length > 1 ? (
            <Field label={t("branch")} htmlFor="desk-branch" hint={t("branchHint")} className="mt-4">
              <Select
                id="desk-branch"
                value={branchId || "__auto"}
                onValueChange={(v) => setBranchId(v === "__auto" ? "" : v)}
                options={[{ value: "__auto", label: t("branchAuto") }, ...branchOptions]}
                placeholder={tc("choose")}
                searchPlaceholder={tc("search")}
                emptyText={tc("noMatch")}
              />
            </Field>
          ) : null}
        </Section>

        <Section title={t("customer.title")} id="desk-customer">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("customer.mobile")} htmlFor="desk-mobile" required hint={t("customer.mobileHint")} error={errors.mobile}>
              <Input id="desk-mobile" type="tel" inputMode="tel" autoComplete="off" value={mobile} onChange={(e) => setMobile(e.target.value)} aria-invalid={!!errors.mobile} />
            </Field>
            <Field label={t("customer.name")} htmlFor="desk-name">
              <Input id="desk-name" value={name} onChange={(e) => setName(e.target.value)} autoComplete="off" />
            </Field>
            <Field label={t("customer.email")} htmlFor="desk-email" className="sm:col-span-2">
              <Input id="desk-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="off" />
            </Field>
          </div>
          {validMobile ? (
            <div className="mt-3" aria-live="polite" data-testid="customer-lookup">
              {customer ? (
                <Alert variant="success">
                  <span className="flex items-center gap-2">
                    <UserCheck className="size-4" aria-hidden /> {t("customer.found", { name: customer.fullName ?? customer.mobile })}
                    <Button variant="ghost" onClick={() => setName(customer.fullName ?? "")}>
                      {t("customer.useName")}
                    </Button>
                  </span>
                </Alert>
              ) : lookup.isFetching ? null : (
                <p className="flex items-center gap-2 text-sm text-muted">
                  <UserPlus className="size-4" aria-hidden /> {t("customer.new")}
                </p>
              )}
            </div>
          ) : null}
        </Section>

        <Section title={t("address.title")} id="desk-address">
          <AddressFields value={draft} onChange={setDraft} errors={errors} idPrefix="desk" showPin={false} phoneRequired={false} />
        </Section>

        <Section title={t("items.title")} id="desk-items">
          <ItemPicker chosen={chosen} onAdd={addLine} />
          {errors.lines ? (
            <p role="alert" className="mt-3 text-sm font-medium text-danger">
              {errors.lines}
            </p>
          ) : null}
          {lines.length > 0 ? (
            <ul className="mt-4 divide-y divide-line border-t border-line" aria-label={t("items.chosen")} data-testid="desk-lines">
              {lines.map((l) => (
                <li key={l.variantId} className="flex flex-wrap items-center gap-3 py-3" data-sku={l.sku}>
                  <div className="min-w-0 flex-1 basis-44">
                    <p className="font-medium text-strong [overflow-wrap:anywhere]">{l.name}</p>
                    <p className="text-sm text-muted">
                      <span className="font-mono">{l.sku}</span> {l.detail ? `· ${l.detail}` : ""} · {money(l.price)}
                    </p>
                  </div>
                  <div className="w-24">
                    <label htmlFor={`qty-${l.variantId}`} className="sr-only">
                      {t("items.quantity", { sku: l.sku })}
                    </label>
                    <QtyInput id={`qty-${l.variantId}`} value={String(l.quantity)} onValueChange={(v) => setQty(l.variantId, v)} />
                  </div>
                  <p className="w-24 text-end font-semibold text-strong">{money(l.price * l.quantity)}</p>
                  <Button variant="ghost" size="icon" aria-label={t("items.remove", { sku: l.sku })} onClick={() => setLines((old) => old.filter((x) => x.variantId !== l.variantId))}>
                    <Trash2 aria-hidden />
                  </Button>
                </li>
              ))}
            </ul>
          ) : null}
        </Section>

        <Section title={t("payment.title")} id="desk-payment">
          <RadioGroup value={method} onValueChange={setMethod} className="grid gap-2 sm:grid-cols-2" aria-label={t("payment.title")}>
            {(["COD", "SANDBOX"] as const).map((m) => (
              <div key={m} className="flex min-h-12 items-start gap-3 rounded-control border border-line bg-card p-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
                <RadioItem id={`desk-pay-${m}`} value={m} className="mt-0.5" />
                <Label htmlFor={`desk-pay-${m}`} className="flex-1 cursor-pointer font-normal">
                  <span className="block font-semibold text-strong">{t(`payment.${m}`)}</span>
                  <span className="block text-sm text-muted">{t(`payment.help.${m}`)}</span>
                </Label>
              </div>
            ))}
          </RadioGroup>
          <p className="mt-3 text-sm text-muted">{t("payment.advanceNote")}</p>
        </Section>

        <Section title={t("notes.title")} id="desk-notes">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("notes.customer")} htmlFor="desk-note-customer" hint={t("notes.customerHint")}>
              <Textarea id="desk-note-customer" rows={3} maxLength={500} value={customerNote} onChange={(e) => setCustomerNote(e.target.value)} />
            </Field>
            <Field label={t("notes.internal")} htmlFor="desk-note-internal" hint={t("notes.internalHint")}>
              <Textarea id="desk-note-internal" rows={3} maxLength={500} value={internalNote} onChange={(e) => setInternalNote(e.target.value)} />
            </Field>
          </div>
        </Section>
      </form>

      <aside className="min-w-0 lg:sticky lg:top-24 lg:self-start" aria-label={t("summary.title")}>
        <Card className="space-y-4 p-4 sm:p-5">
          <h2 className="text-lg font-semibold text-strong">{t("summary.title")}</h2>
          {perms.applyDiscount ? (
            <Field label={t("summary.discount")} htmlFor="desk-discount" hint={t("summary.discountHint")}>
              <Input id="desk-discount" inputMode="decimal" value={discount} onChange={(e) => setDiscount(e.target.value.replace(/[^\d.]/g, ""))} />
            </Field>
          ) : null}
          {perms.overrideDelivery ? (
            <Field label={t("summary.override")} htmlFor="desk-override" hint={t("summary.overrideHint", { charge: money(delivery.data?.total ?? 0) })}>
              <Input id="desk-override" inputMode="decimal" value={override} onChange={(e) => setOverride(e.target.value.replace(/[^\d.]/g, ""))} />
            </Field>
          ) : null}
          <AmountList
            className="border-t border-line pt-4"
            rows={[
              { key: "subtotal", label: t("summary.subtotal"), value: totals.subtotal, always: true },
              { key: "discount", label: t("summary.discountRow"), value: totals.discount, negative: true },
              { key: "delivery", label: t("summary.delivery"), value: totals.delivery, always: !!place && lines.length > 0, text: place && lines.length > 0 ? undefined : t("summary.needAddress") },
              { key: "total", label: t("summary.total"), value: totals.grandTotal, strong: true },
            ]}
          />
          <p className="text-sm text-muted" data-testid="desk-estimate-note">
            {t("summary.estimate")}
          </p>
          {formError ? (
            <Alert variant="danger" data-testid="desk-error">
              {formError}
            </Alert>
          ) : null}
          <Button size="lg" className="w-full" loading={create.isPending} onClick={() => void submit(false)} data-testid="desk-save">
            {t("save")}
          </Button>
        </Card>
      </aside>

      <ConfirmDialog
        open={duplicateOpen}
        onOpenChange={setDuplicateOpen}
        title={t("duplicate.title")}
        description={t("duplicate.text")}
        confirmLabel={t("duplicate.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={create.isPending}
        onConfirm={async () => {
          setDuplicateOpen(false);
          await submit(true);
        }}
      />
    </div>
  );
}
