"use client";

import * as React from "react";
import Image from "next/image";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Banknote, FlaskConical, Lock, MapPin, ShoppingBag, Truck } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Checkbox, Field, Input, Label, RadioGroup, RadioItem, Textarea } from "@/components/ui/form";
import { Alert, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { mediaUrl, toNumber, useMoney } from "@/modules/catalog/format";
import { useCart, useMe } from "@/modules/store/api";
import { usePickName } from "@/modules/store/hooks";
import { useStoreSession, signInUrl } from "@/modules/store/store-session";
import { addressErrors, emptyDraft, placeOf, toCheckoutAddress, type AddressDraft } from "./address";
import { AddressFields } from "./address-fields";
import { useCheckoutQuote, useOrderErrorText, usePlaceOrder, useRequestOrderOtp, useSavedAddresses, type CheckoutQuote, type CheckoutRequest } from "./api";
import { AmountList, Section, useMethodLabel } from "./common";
import { cleanMobile, getDeviceId, isBdMobile, newKey } from "./status";

const NEW_ADDRESS = "__new";
export const ORDER_STORAGE_PREFIX = "juvo_order_";

/** Words for one problem the quote reports (out of stock, coupon ended ...). Unknown codes get a general sentence. */
function useProblemText() {
  const t = useTranslations("orders.checkout.problems");
  return (code: string) => {
    const key = code.replaceAll(".", "_");
    return t.has(key) ? t(key) : t("OTHER");
  };
}

/**
 * The checkout: one page, top to bottom. Contact, address, delivery, payment, note, then the review with totals. The server works
 * out every amount: the quote is asked again on each change, and the total shown is sent back as `expectedTotal` so a change
 * between looking and placing is caught (FR-D06). One idempotency key belongs to one attempt, so a retry never places twice.
 */
export function CheckoutView() {
  const t = useTranslations("orders.checkout");
  const router = useRouter();
  const money = useMoney();
  const { customer } = useStoreSession();
  const errorText = useOrderErrorText();
  const problemText = useProblemText();
  const methodLabel = useMethodLabel();
  const pick = usePickName();

  const cart = useCart();
  const saved = useSavedAddresses(customer);
  const me = useMe(customer);
  const place = usePlaceOrder();
  const requestOtp = useRequestOrderOtp();

  const [mobile, setMobile] = React.useState("");
  const [email, setEmail] = React.useState("");
  const [choice, setChoice] = React.useState<string | null>(null);
  const [draft, setDraft] = React.useState<AddressDraft>(emptyDraft);
  const [saveAddress, setSaveAddress] = React.useState(true);
  const [method, setMethod] = React.useState("COD");
  const [note, setNote] = React.useState("");
  const [terms, setTerms] = React.useState(false);
  const [otp, setOtp] = React.useState("");
  const [otpInfo, setOtpInfo] = React.useState<{ to: string; devCode: string | null } | null>(null);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [changedFrom, setChangedFrom] = React.useState<number | null>(null);
  const [duplicateOpen, setDuplicateOpen] = React.useState(false);
  const [placed, setPlaced] = React.useState(false);
  const attempt = React.useRef<{ fingerprint: string; key: string } | null>(null);

  const savedList = saved.data ?? [];
  const defaultId = savedList.find((a) => a.isDefault)?.id ?? savedList[0]?.id ?? null;
  const addressId = customer && savedList.length > 0 && choice !== NEW_ADDRESS ? (choice ?? defaultId) : null;
  const chosenSaved = savedList.find((a) => a.id === addressId);
  const usingNew = !addressId;

  const lines = React.useMemo(() => (cart.data?.lines ?? []).map((l) => `${l.variantId}:${toNumber(l.quantity)}`).join(","), [cart.data]);
  const place_ = React.useMemo(() => (usingNew ? placeOf(draft) : null), [usingNew, draft]);
  const guestMobile = !customer && isBdMobile(mobile) ? cleanMobile(mobile) : null;
  const quoteInput = React.useMemo(
    () => ({
      addressId,
      address: place_ ? { recipientName: "", phone: null, area: null, addressLine: "", landmark: null, stateProvince: null, city: null, postalCode: null, latitude: null, longitude: null, ...place_ } : null,
      paymentMethod: method,
      mobile: guestMobile,
    }),
    [addressId, place_, method, guestMobile],
  );
  const hasLines = (cart.data?.lines.length ?? 0) > 0;
  const quoteQuery = useCheckoutQuote(quoteInput, hasLines && !placed, lines);
  const quote: CheckoutQuote | undefined = quoteQuery.data;

  const methods = quote?.paymentMethods ?? [];
  const activeMethod = methods.find((m) => m.code === method && m.available)?.code ?? methods.find((m) => m.available)?.code ?? method;
  const needsOtp = !customer && !!quote?.otpRequired;
  const total = quote ? toNumber(quote.grandTotal) : 0;
  const advance = quote ? toNumber(quote.advanceRequired) : 0;

  if (cart.isPending) return <Skeleton className="h-96" aria-busy />;
  if (!hasLines && !placed) {
    return (
      <EmptyState
        icon={<ShoppingBag aria-hidden />}
        title={t("empty.title")}
        description={t("empty.text")}
        action={
          <Link href="/shop" className={buttonVariants({ size: "lg" })}>
            {t("empty.cta")}
          </Link>
        }
      />
    );
  }

  const sendCode = async () => {
    setErrors((e) => ({ ...e, otp: "", mobile: "" }));
    if (!isBdMobile(mobile)) return setErrors((e) => ({ ...e, mobile: t("errors.mobile") }));
    try {
      const result = await requestOtp.mutateAsync(cleanMobile(mobile));
      setOtpInfo({ to: result.sentTo, devCode: result.devCode ?? null });
    } catch (failure) {
      toast.error(errorText(failure));
    }
  };

  const submit = async (confirmDuplicate = false) => {
    setFormError(null);
    const next: Record<string, string> = {};
    if (!customer && !isBdMobile(mobile)) next.mobile = t("errors.mobile");
    if (usingNew) {
      const found = addressErrors(draft, { needPhone: true });
      if (found.recipient) next.recipient = t("errors.recipient");
      if (found.phone) next.phone = t("errors.phone");
      if (found.district) next.district = t("errors.district");
      if (found.country) next.country = t("errors.country");
      if (found.city) next.city = t("errors.city");
      if (found.line) next.line = t("errors.line");
    }
    if (needsOtp && !otp.trim()) next.otp = t("errors.otp");
    if (!terms) next.terms = t("errors.terms");
    setErrors(next);
    if (Object.keys(next).length) {
      setFormError(t("errors.fix"));
      return;
    }
    if (!quote?.canPlace) return setFormError(t("errors.cannotPlace"));

    const address = usingNew ? toCheckoutAddress(draft) : null;
    const recipient = usingNew ? draft.recipientName.trim() : (chosenSaved?.recipientName ?? "");
    const body: CheckoutRequest = {
      contact: { mobile: customer ? (me.data?.mobile ?? "") : cleanMobile(mobile), fullName: recipient || null, email: email.trim() || null },
      addressId,
      address,
      saveAddress: !!customer && usingNew && saveAddress,
      paymentMethod: activeMethod,
      note: note.trim() || null,
      acceptTerms: true,
      otpCode: needsOtp ? otp.trim() : null,
      confirmDuplicate,
      expectedTotal: total,
      idempotencyKey: null,
      deviceId: getDeviceId(),
    };
    // One key per attempt: the same basket, method and number keep it (a retry returns the same order); a changed basket gets a new one.
    const fingerprint = JSON.stringify([lines, activeMethod, body.contact.mobile, addressId]);
    if (attempt.current?.fingerprint !== fingerprint) attempt.current = { fingerprint, key: newKey() };

    try {
      const result = await place.mutateAsync({ body, key: attempt.current.key });
      setPlaced(true);
      try {
        window.sessionStorage.setItem(`${ORDER_STORAGE_PREFIX}${result.number}`, JSON.stringify(result));
      } catch {
        // Without storage the confirmation page still shows the number; only the one-time tracking link is lost.
      }
      router.push(`/order/confirmed/${encodeURIComponent(result.number)}`);
    } catch (failure) {
      const problem = failure instanceof ApiError ? failure.problem : null;
      const code = problem?.code ?? "";
      if (code === "ORDER.TOTAL_CHANGED") {
        setChangedFrom(total);
        await quoteQuery.refetch();
        return;
      }
      if (code === "ORDER.DUPLICATE_SUSPECTED") return setDuplicateOpen(true);
      if (code === "ORDER.STOCK_INSUFFICIENT" || code.startsWith("CART.") || code.startsWith("COUPON.")) {
        await Promise.all([quoteQuery.refetch(), cart.refetch()]);
        return setFormError(errorText(failure));
      }
      if (code === "ORDER.OTP_REQUIRED" || code.startsWith("AUTH.OTP")) return setErrors((e) => ({ ...e, otp: errorText(failure) }));
      if (code === "GENERAL.VALIDATION" && problem) {
        const found = fieldErrors(problem);
        const mapped: Record<string, string> = {};
        for (const [field, message] of Object.entries(found)) {
          if (field.includes("mobile")) mapped.mobile = message;
          else if (field.includes("districtId") || field.includes("upazilaId")) mapped.district = message;
          else if (field.includes("recipientName")) mapped.recipient = message;
          else if (field.includes("phone")) mapped.phone = message;
          else if (field.includes("addressLine")) mapped.line = message;
          else if (field.includes("countryCode")) mapped.country = message;
          else if (field.includes("city")) mapped.city = message;
        }
        setErrors((e) => ({ ...e, ...mapped }));
      }
      setFormError(errorText(failure));
    }
  };

  const quoteLines = quote?.lines ?? [];

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
        <Section title={t("contact.title")} id="co-contact">
          {customer ? (
            <p className="text-strong" data-testid="signed-in-contact">
              {t("contact.signedInAs", { mobile: me.data?.mobile ?? "..." })}
            </p>
          ) : (
            <div className="space-y-4">
              <p className="text-sm text-muted">{t("contact.guestHelp")}</p>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("contact.mobile")} htmlFor="co-mobile" required hint={t("contact.mobileHint")} error={errors.mobile}>
                  <Input id="co-mobile" type="tel" inputMode="tel" autoComplete="tel" value={mobile} onChange={(e) => setMobile(e.target.value)} aria-invalid={!!errors.mobile} />
                </Field>
                <Field label={t("contact.email")} htmlFor="co-email" hint={t("contact.emailHint")}>
                  <Input id="co-email" type="email" inputMode="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} />
                </Field>
              </div>
              <p className="text-sm text-muted">
                {t("contact.haveAccount")}{" "}
                <Link href={signInUrl("/checkout")} className="inline-flex min-h-11 min-w-11 items-center justify-center font-semibold text-primary underline-offset-4 hover:underline">
                  {t("contact.signIn")}
                </Link>
              </p>
            </div>
          )}
        </Section>

        <Section title={t("address.title")} id="co-address">
          {customer && savedList.length > 0 ? (
            <RadioGroup value={addressId ?? NEW_ADDRESS} onValueChange={(v) => setChoice(v)} className="mb-4 space-y-2" aria-label={t("address.saved")}>
              {savedList.map((a) => (
                <div key={a.id} className="flex items-start gap-3 rounded-control border border-line bg-card p-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
                  <RadioItem id={`co-addr-${a.id}`} value={a.id} className="mt-0.5" />
                  <Label htmlFor={`co-addr-${a.id}`} className="min-h-11 min-w-0 flex-1 cursor-pointer font-normal">
                    <span className="block font-semibold text-strong">
                      {a.recipientName} <span className="font-normal text-muted">{a.phone}</span>
                    </span>
                    <span className="block text-muted [overflow-wrap:anywhere]">{[a.addressLine, a.area, a.city].filter(Boolean).join(", ")}</span>
                  </Label>
                </div>
              ))}
              <div className="flex items-start gap-3 rounded-control border border-line bg-card p-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
                <RadioItem id="co-addr-new" value={NEW_ADDRESS} className="mt-0.5" />
                <Label htmlFor="co-addr-new" className="flex min-h-11 flex-1 cursor-pointer items-center font-semibold text-strong">
                  <MapPin className="me-2 size-5 text-primary" aria-hidden /> {t("address.useNew")}
                </Label>
              </div>
            </RadioGroup>
          ) : null}
          {usingNew ? (
            <div className="space-y-4">
              <AddressFields value={draft} onChange={setDraft} errors={errors} idPrefix="co" />
              {customer ? (
                <div className="flex min-h-11 items-center justify-between gap-3">
                  <Label htmlFor="co-save-address" className="flex min-h-11 flex-1 cursor-pointer items-center">
                    {t("address.save")}
                  </Label>
                  <Checkbox id="co-save-address" checked={saveAddress} onCheckedChange={(v) => setSaveAddress(v === true)} />
                </div>
              ) : null}
            </div>
          ) : null}
        </Section>

        <Section title={t("delivery.title")} id="co-delivery">
          <div aria-live="polite" data-testid="checkout-delivery">
            {quote?.delivery ? (
              <div className="space-y-1">
                <p className="flex items-center gap-2 font-semibold text-strong">
                  <Truck className="size-5 text-primary" aria-hidden /> {t("delivery.standard")}
                  <span className="ms-auto" data-testid="delivery-charge">
                    {quote.delivery.isFree ? t("delivery.free") : money(quote.deliveryCharge)}
                  </span>
                </p>
                <p className="text-sm text-muted">{t("delivery.days", { min: toNumber(quote.delivery.daysMin), max: toNumber(quote.delivery.daysMax) })}</p>
                {quote.delivery.dutiesPaidByReceiver ? <p className="text-sm text-muted">{t("delivery.duties")}</p> : null}
              </div>
            ) : quote?.deliveryError ? (
              <Alert variant="warning">{quote.deliveryError === "DELIVERY.NOT_DELIVERABLE" ? t("delivery.notDeliverable") : t("delivery.error")}</Alert>
            ) : (
              <p className="text-sm text-muted">{t("delivery.pick")}</p>
            )}
          </div>
        </Section>

        <Section title={t("payment.title")} id="co-payment">
          <RadioGroup value={activeMethod} onValueChange={setMethod} className="space-y-2" aria-label={t("payment.title")}>
            {(methods.length > 0 ? methods : [{ code: "COD", name: "Cash on delivery", available: true, reason: null }]).map((m) => (
              <div key={m.code} className="flex items-start gap-3 rounded-control border border-line bg-card p-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft has-[[data-disabled]]:opacity-60">
                <RadioItem id={`co-pay-${m.code}`} value={m.code} disabled={!m.available} className="mt-0.5" />
                <Label htmlFor={`co-pay-${m.code}`} className="min-h-11 min-w-0 flex-1 cursor-pointer font-normal">
                  <span className="flex items-center gap-2 font-semibold text-strong">
                    {m.code === "COD" ? <Banknote className="size-5 text-primary" aria-hidden /> : <FlaskConical className="size-5 text-primary" aria-hidden />}
                    {methodLabel(m.code)}
                  </span>
                  <span className="block text-sm text-muted">{m.available ? t(`payment.help.${m.code === "COD" ? "COD" : "ONLINE"}`) : (m.reason ?? t("payment.unavailable"))}</span>
                </Label>
              </div>
            ))}
          </RadioGroup>
          {advance > 0 ? (
            <Alert variant="warning" className="mt-4" title={t("advance.title")}>
              <span data-testid="advance-notice">{t("advance.text", { advance: money(advance), rest: money(quote!.payOnDelivery) })}</span>
            </Alert>
          ) : null}
        </Section>

        {needsOtp ? (
          <Section title={t("otp.title")} id="co-otp">
            <div className="space-y-4">
              <p className="text-sm text-muted">{t("otp.help")}</p>
              {otpInfo ? <Alert variant="success">{t("otp.sent", { to: otpInfo.to })}</Alert> : null}
              {otpInfo?.devCode ? (
                <div role="note" className="rounded-control border border-line bg-primary-soft p-4" data-testid="sandbox-code">
                  <p className="flex items-center gap-2 text-sm font-semibold text-strong">
                    <FlaskConical className="size-5 shrink-0 text-primary" aria-hidden /> {t("otp.sandboxTitle")}
                  </p>
                  <p className="mt-1 text-sm text-muted">{t("otp.sandboxBody")}</p>
                  <p className="mt-1 text-center text-3xl font-bold tracking-[0.3em] text-strong">{otpInfo.devCode}</p>
                </div>
              ) : null}
              <div className="flex flex-wrap items-end gap-3">
                <Field label={t("otp.code")} htmlFor="co-otp-code" required error={errors.otp} className="min-w-0 flex-1 basis-48">
                  <Input id="co-otp-code" inputMode="numeric" autoComplete="one-time-code" maxLength={8} value={otp} onChange={(e) => setOtp(e.target.value)} aria-invalid={!!errors.otp} />
                </Field>
                <Button type="button" variant="secondary" onClick={() => void sendCode()} loading={requestOtp.isPending}>
                  {otpInfo ? t("otp.resend") : t("otp.send")}
                </Button>
              </div>
            </div>
          </Section>
        ) : null}

        <Section title={t("note.title")} id="co-note">
          <Field label={t("note.label")} htmlFor="co-note-text" hint={t("note.hint")}>
            <Textarea id="co-note-text" rows={3} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
          </Field>
        </Section>
      </form>

      <aside className="min-w-0 lg:sticky lg:top-24 lg:self-start" aria-label={t("review.title")}>
        <Card className="space-y-4 p-4 sm:p-5">
          <h2 className="text-lg font-semibold text-strong">{t("review.title")}</h2>
          <ul className="divide-y divide-line" aria-label={t("review.items")} data-testid="checkout-lines">
            {quoteLines.map((line) => {
              const image = mediaUrl(line.imageUrl);
              return (
                <li key={line.variantId} className="flex gap-3 py-3" data-sku={line.sku}>
                  <span className="relative block aspect-[4/5] w-12 shrink-0 overflow-hidden rounded-control bg-sunken">{image ? <Image src={image} alt="" fill sizes="48px" className="object-cover" /> : null}</span>
                  <div className="min-w-0 flex-1">
                    <p className="line-clamp-2 text-sm font-medium text-strong [overflow-wrap:anywhere]">{pick(line.name, line.nameBn)}</p>
                    <p className="text-xs text-muted">{[line.colorName, line.size ? t("review.size", { size: line.size }) : null, `× ${toNumber(line.quantity)}`].filter(Boolean).join(" · ")}</p>
                    {line.problem ? (
                      <p className="mt-1 text-sm font-medium text-danger" data-testid="line-problem" role="alert">
                        {problemText(line.problem)}
                      </p>
                    ) : null}
                  </div>
                  <p className="shrink-0 text-sm font-semibold text-strong">{money(line.lineTotal)}</p>
                </li>
              );
            })}
          </ul>

          {quote ? (
            <AmountList
              className="border-t border-line pt-4"
              rows={[
                { key: "subtotal", label: t("review.subtotal", { count: toNumber(quote.itemCount) }), value: quote.subtotal, always: true },
                { key: "discount", label: t("review.discount"), value: quote.couponDiscount, negative: true },
                { key: "delivery", label: t("review.delivery"), value: quote.deliveryCharge, always: !!quote.delivery, text: quote.delivery ? (quote.delivery.isFree ? t("delivery.free") : undefined) : t("review.chooseAddress") },
                { key: "total", label: t("review.total"), value: quote.grandTotal, strong: true },
                ...(advance > 0
                  ? [
                      { key: "paynow", label: t("review.payNow"), value: quote.payNow, always: true },
                      { key: "ondelivery", label: t("review.onDelivery"), value: quote.payOnDelivery, always: true },
                    ]
                  : []),
              ]}
            />
          ) : quoteQuery.isError ? (
            <Alert variant="warning">{errorText(quoteQuery.error)}</Alert>
          ) : (
            <Skeleton className="h-28" />
          )}

          {quote && quote.problems.length > 0 ? (
            <Alert variant="warning" title={t("review.problems")}>
              <ul className="list-disc ps-4">
                {[...new Set(quote.problems)].map((p) => (
                  <li key={p}>{problemText(p)}</li>
                ))}
              </ul>
              <Link href="/cart" className="mt-1 inline-flex min-h-11 items-center font-semibold text-strong underline underline-offset-4">
                {t("review.backToCart")}
              </Link>
            </Alert>
          ) : null}

          {changedFrom !== null ? (
            <Alert variant="warning" title={t("changed.title")}>
              <span data-testid="total-changed">{t("changed.text", { from: money(changedFrom), to: money(total) })}</span>
            </Alert>
          ) : null}

          <div className="flex min-h-11 items-start gap-3">
            <Checkbox id="co-terms" checked={terms} onCheckedChange={(v) => setTerms(v === true)} aria-invalid={!!errors.terms} className="mt-0.5" />
            <Label htmlFor="co-terms" className="flex min-h-11 flex-1 cursor-pointer items-center font-normal">
              {t("terms")}
            </Label>
          </div>
          {errors.terms ? (
            <p role="alert" className="-mt-2 text-sm font-medium text-danger">
              {errors.terms}
            </p>
          ) : null}

          {formError ? (
            <Alert variant="danger" data-testid="checkout-error">
              {formError}
            </Alert>
          ) : null}

          <Button size="lg" className="w-full" loading={place.isPending} disabled={!quote || (quote.problems.length > 0 && !quote.canPlace)} onClick={() => void submit(false)} data-testid="place-order">
            <Lock aria-hidden /> {t("place")}
          </Button>
          <p className="text-center text-sm text-muted">{t("placeNote")}</p>
        </Card>
      </aside>

      <ConfirmDialog
        open={duplicateOpen}
        onOpenChange={setDuplicateOpen}
        title={t("duplicate.title")}
        description={t("duplicate.text")}
        confirmLabel={t("duplicate.confirm")}
        cancelLabel={t("duplicate.cancel")}
        closeLabel={t("duplicate.close")}
        loading={place.isPending}
        onConfirm={async () => {
          setDuplicateOpen(false);
          await submit(true);
        }}
      />
    </div>
  );
}
