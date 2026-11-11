"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { CalendarClock, ImagePlus, Minus, Plus, RotateCcw, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, RadioGroup, RadioItem, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, CardContent, CardDescription, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { cn } from "@/lib/utils";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useStoreProduct, useStoreProducts } from "@/modules/store/api";
import { useDateTime } from "@/components/data/list-kit";
import { uploadReturnPhoto, useCreateReturn, useEligibility, type ReturnableLine } from "./api";
import { Thumb, VariantLine, useLocalName } from "./common";
import {
  PHOTO_MAX_COUNT,
  PHOTO_TYPES,
  buildLines,
  clampQuantity,
  defaultFault,
  digitsOnly,
  estimateRefund,
  maxQuantity,
  newIdempotencyKey,
  photoProblem,
  photoSizeProblem,
  type PhotoProblem,
  type ReturnKind,
} from "./logic";

type PhotoItem = { id: string; name: string; preview: string; state: "uploading" | "done" | "error"; path?: string; error?: PhotoProblem | "upload" };

/** Reads the pixel size of a picture in the browser (to apply the 1000 px rule before uploading). */
async function readSize(file: File): Promise<{ width: number; height: number } | null> {
  try {
    if (typeof createImageBitmap === "function") {
      const bitmap = await createImageBitmap(file);
      const size = { width: bitmap.width, height: bitmap.height };
      bitmap.close();
      return size;
    }
  } catch {
    // fall through to an <img> element
  }
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const image = new Image();
    image.onload = () => {
      resolve({ width: image.naturalWidth, height: image.naturalHeight });
      URL.revokeObjectURL(url);
    };
    image.onerror = () => {
      resolve(null);
      URL.revokeObjectURL(url);
    };
    image.src = url;
  });
}

function QuantityStepper({ value, max, onChange, label }: { value: number; max: number; onChange: (value: number) => void; label: string }) {
  const t = useTranslations("returns.request");
  return (
    <div className="inline-flex items-center gap-1" role="group" aria-label={label}>
      <Button variant="secondary" size="icon" aria-label={t("decrease")} disabled={value <= 0} onClick={() => onChange(value - 1)}>
        <Minus aria-hidden />
      </Button>
      <input
        type="text"
        inputMode="numeric"
        pattern="[0-9]*"
        autoComplete="off"
        aria-label={label}
        value={String(value)}
        onChange={(e) => onChange(clampQuantity(digitsOnly(e.target.value), max))}
        className="h-11 w-14 rounded-control border border-line bg-card text-center text-base font-semibold tabular-nums text-strong"
      />
      <Button variant="secondary" size="icon" aria-label={t("increase")} disabled={value >= max} onClick={() => onChange(value + 1)}>
        <Plus aria-hidden />
      </Button>
    </div>
  );
}

/** The replacement choices for one exchange line: variants of the same product that are in stock (the same variant by default). */
function ExchangeVariant({ line, value, onChange, idPrefix }: { line: ReturnableLine; value: string; onChange: (variantId: string) => void; idPrefix: string }) {
  const t = useTranslations("returns.request");
  const tc = useTranslations("returns.common");
  const search = useStoreProducts({ q: line.name.slice(0, 60), pageSize: 24 });
  const card = search.data?.items.find((p) => p.id === line.productId);
  const detail = useStoreProduct(card?.slug ?? "");
  const variants = detail.data?.variants ?? [];
  const options = variants
    .filter((v) => v.inStock || v.id === line.variantId)
    .map((v) => ({ value: v.id, label: [v.sku, v.colorName, v.size].filter(Boolean).join(" · "), hint: v.id === line.variantId ? t("sameItem") : undefined, disabled: !v.inStock }));
  const loading = search.isPending || (!!card && detail.isPending);

  return (
    <Field label={t("replacement")} htmlFor={`${idPrefix}-variant`} hint={options.length <= 1 && !loading ? t("replacementSame") : t("replacementHint")}>
      {loading ? (
        <Skeleton className="h-12" aria-busy="true" />
      ) : (
        <Select
          id={`${idPrefix}-variant`}
          value={value}
          onValueChange={onChange}
          options={options.length > 0 ? options : [{ value: line.variantId, label: [line.sku, line.colorName, line.size].filter(Boolean).join(" · ") }]}
          placeholder={t("replacement")}
          searchPlaceholder={tc("search")}
          emptyText={tc("noMatch")}
          disabled={options.length <= 1}
        />
      )}
    </Field>
  );
}

/** The customer's return or exchange request for one delivered order (FR-F04, FR-L03..L08). */
export function RequestFlow({ orderId }: { orderId: string }) {
  const t = useTranslations("returns.request");
  const tc = useTranslations("returns.common");
  const locale = useLocale();
  const router = useRouter();
  const money = useMoney();
  const dateTime = useDateTime();
  const localName = useLocalName();
  const problemMessage = useProblemMessage();
  const eligibility = useEligibility(orderId);
  const create = useCreateReturn();

  const [quantities, setQuantities] = React.useState<Record<string, number>>({});
  const [kind, setKind] = React.useState<ReturnKind>("Return");
  const [variants, setVariants] = React.useState<Record<string, string | null>>({});
  const [reasonCode, setReasonCode] = React.useState("");
  const [details, setDetails] = React.useState("");
  const [photos, setPhotos] = React.useState<PhotoItem[]>([]);
  const [problem, setProblem] = React.useState<string | null>(null);
  const [errors, setErrors] = React.useState<{ items?: boolean; reason?: boolean }>({});
  const fileInput = React.useRef<HTMLInputElement>(null);
  const key = React.useRef<string | null>(null);
  const previews = React.useRef<Set<string>>(new Set());

  // Blob addresses of the previews are released when the page goes away.
  React.useEffect(() => {
    const set = previews.current;
    return () => set.forEach((url) => URL.revokeObjectURL(url));
  }, []);

  const data = eligibility.data;
  const lines = React.useMemo(() => data?.items ?? [], [data]);
  const originalVariant = React.useMemo(() => Object.fromEntries(lines.map((l) => [l.orderItemId, l.variantId])), [lines]);
  const selected = lines.filter((l) => (quantities[l.orderItemId] ?? 0) > 0);
  const estimate = estimateRefund(lines, quantities);
  const uploading = photos.some((p) => p.state === "uploading");

  const addPhotos = async (files: FileList | null) => {
    if (!files) return;
    const room = PHOTO_MAX_COUNT - photos.filter((p) => p.state !== "error").length;
    const picked = Array.from(files);
    if (picked.length > room) toast.error(t("photoLimit", { count: PHOTO_MAX_COUNT }));
    for (const file of picked.slice(0, Math.max(0, room))) {
      const id = `${file.name}-${file.size}-${Math.random().toString(36).slice(2, 8)}`;
      const preview = URL.createObjectURL(file);
      previews.current.add(preview);
      const base = { id, name: file.name, preview };
      const early = photoProblem(file);
      if (early) {
        setPhotos((old) => [...old, { ...base, state: "error", error: early }]);
        continue;
      }
      setPhotos((old) => [...old, { ...base, state: "uploading" }]);
      const size = await readSize(file);
      const tooSmall = size ? photoSizeProblem(size.width, size.height) : null;
      if (tooSmall) {
        setPhotos((old) => old.map((p) => (p.id === id ? { ...p, state: "error", error: tooSmall } : p)));
        continue;
      }
      try {
        const done = await uploadReturnPhoto(file);
        setPhotos((old) => old.map((p) => (p.id === id ? { ...p, state: "done", path: done.path } : p)));
      } catch (e) {
        const reason = e instanceof ApiError ? problemMessage(e.problem) : t("photoUploadFailed");
        setPhotos((old) => old.map((p) => (p.id === id ? { ...p, state: "error", error: "upload" } : p)));
        toast.error(reason);
      }
    }
    if (fileInput.current) fileInput.current.value = "";
  };

  const removePhoto = (id: string) => {
    setPhotos((old) => {
      const gone = old.find((p) => p.id === id);
      if (gone) {
        URL.revokeObjectURL(gone.preview);
        previews.current.delete(gone.preview);
      }
      return old.filter((p) => p.id !== id);
    });
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setProblem(null);
    const next = { items: selected.length === 0, reason: reasonCode === "" };
    setErrors(next);
    if (next.items || next.reason || uploading) return;
    key.current ??= newIdempotencyKey();
    try {
      const created = await create.mutateAsync({
        key: key.current,
        body: {
          orderId,
          kind,
          reasonCode,
          details,
          items: buildLines(kind, quantities, variants, originalVariant).map((l) => ({ orderItemId: l.orderItemId, quantity: l.quantity, exchangeVariantId: l.exchangeVariantId })),
          photoPaths: photos.filter((p) => p.state === "done" && p.path).map((p) => p.path!),
        },
      });
      toast.success(t("sent", { number: created.number }));
      router.push(`/account/returns/${created.id}`);
    } catch (e) {
      const apiProblem = e instanceof ApiError ? e.problem : null;
      setProblem(problemMessage(apiProblem));
      // A definite refusal ends this attempt; after a lost connection the same key is reused so a retry cannot file it twice.
      if (apiProblem && apiProblem.status && apiProblem.status >= 400 && apiProblem.status < 500) key.current = null;
      if (apiProblem?.code?.startsWith("RETURN.")) void eligibility.refetch();
    }
  };

  if (eligibility.isPending) {
    return (
      <div className="space-y-4" aria-busy="true">
        <Skeleton className="h-28" />
        <Skeleton className="h-64" />
      </div>
    );
  }
  if (eligibility.isError || !data) {
    const notFound = eligibility.error instanceof ApiError && eligibility.error.problem.status === 404;
    return (
      <EmptyState
        icon={<RotateCcw aria-hidden />}
        title={notFound ? t("orderNotFound") : tc("loadFailed")}
        action={
          <Button asChild variant="secondary">
            <Link href="/account/returns">{t("myReturns")}</Link>
          </Button>
        }
      />
    );
  }

  const reasons = data.reasons;
  const exchangeOffered = data.kinds.includes("Exchange");
  const reasonLabel = (r: { labelEn: string; labelBn: string }) => (locale === "bn" ? r.labelBn : r.labelEn);
  const sellerFault = reasonCode ? defaultFault(reasonCode) === "Seller" : false;

  return (
    <form onSubmit={submit} noValidate className="space-y-6" aria-label={t("title")}>
      <Card>
        <CardHeader>
          <CardTitle>{t("orderTitle", { number: data.orderNumber })}</CardTitle>
          {data.deliveredAt ? (
            <CardDescription>
              {t("delivered", { date: dateTime(data.deliveredAt) })}
            </CardDescription>
          ) : null}
        </CardHeader>
        <CardContent className="space-y-3">
          {data.eligible && data.deadline ? (
            <p className="flex items-start gap-2 text-strong" data-testid="return-deadline">
              <CalendarClock className="mt-0.5 size-5 shrink-0 text-muted" aria-hidden />
              <span>{t("deadline", { date: dateTime(data.deadline), days: toNumber(data.windowDays) })}</span>
            </p>
          ) : null}
          {!data.eligible ? (
            <Alert variant="danger" title={t("notEligible")} data-testid="not-eligible">
              {problemMessage({ code: data.reasonCode ?? undefined, status: 409 })}
            </Alert>
          ) : null}
        </CardContent>
      </Card>

      {problem ? (
        <Alert variant="danger" title={t("failed")} data-testid="request-problem">
          {problem}
        </Alert>
      ) : null}

      {data.eligible ? (
        <>
          <Card>
            <CardHeader>
              <CardTitle>{t("itemsTitle")}</CardTitle>
              <CardDescription>{t("itemsHelp")}</CardDescription>
            </CardHeader>
            <CardContent>
              <ul className="divide-y divide-line" aria-label={t("itemsTitle")}>
                {lines.map((line) => {
                  const max = maxQuantity(line);
                  const qty = quantities[line.orderItemId] ?? 0;
                  const name = localName(line.name, line.nameBn);
                  return (
                    <li key={line.orderItemId} className="space-y-3 py-4 first:pt-0 last:pb-0" data-testid="return-line">
                      <div className="flex items-start gap-3">
                        <Thumb src={line.imageUrl} size={64} />
                        <div className="min-w-0 flex-1">
                          <p className="font-semibold text-strong [overflow-wrap:anywhere]">{name}</p>
                          <VariantLine sku={line.sku} colorName={line.colorName} size={line.size} />
                          <p className="mt-1 text-sm text-muted">
                            {t("lineFacts", { bought: toNumber(line.quantity), already: toNumber(line.alreadyReturned), price: money(line.refundPerUnit) })}
                          </p>
                        </div>
                      </div>
                      {max > 0 ? (
                        <div className="flex flex-wrap items-center justify-between gap-3">
                          <p className="text-sm text-muted">{t("canReturn", { count: max })}</p>
                          <QuantityStepper
                            value={qty}
                            max={max}
                            label={t("quantityFor", { name })}
                            onChange={(value) => {
                              setQuantities((old) => ({ ...old, [line.orderItemId]: clampQuantity(value, max) }));
                              setErrors((old) => ({ ...old, items: false }));
                            }}
                          />
                        </div>
                      ) : (
                        <p className="text-sm font-medium text-danger-ink">{t(`blocked.${line.blockedReason ?? "NON_RETURNABLE"}`)}</p>
                      )}
                      {kind === "Exchange" && qty > 0 ? (
                        <ExchangeVariant line={line} idPrefix={line.orderItemId} value={variants[line.orderItemId] ?? line.variantId} onChange={(v) => setVariants((old) => ({ ...old, [line.orderItemId]: v }))} />
                      ) : null}
                    </li>
                  );
                })}
              </ul>
              {errors.items ? (
                <p role="alert" className="mt-3 text-sm font-medium text-danger">
                  {t("itemsRequired")}
                </p>
              ) : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t("kindTitle")}</CardTitle>
            </CardHeader>
            <CardContent>
              <RadioGroup value={kind} onValueChange={(v) => setKind(v as ReturnKind)} aria-label={t("kindTitle")}>
                {(["Return", ...(exchangeOffered ? ["Exchange"] : [])] as ReturnKind[]).map((k) => (
                  <label key={k} htmlFor={`kind-${k}`} className="flex min-h-11 cursor-pointer items-start gap-3 py-1.5 text-strong">
                    <RadioItem id={`kind-${k}`} value={k} className="mt-2" />
                    <span>
                      <span className="block font-semibold">{t(`kind.${k}`)}</span>
                      <span className="block text-sm text-muted">{t(`kindHelp.${k}`)}</span>
                    </span>
                  </label>
                ))}
              </RadioGroup>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t("reasonTitle")}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <fieldset>
                <legend className="sr-only">{t("reasonTitle")}</legend>
                <RadioGroup
                  value={reasonCode}
                  onValueChange={(v) => {
                    setReasonCode(v);
                    setErrors((old) => ({ ...old, reason: false }));
                  }}
                  aria-label={t("reasonTitle")}
                >
                  {reasons.map((r) => (
                    <label key={r.code} htmlFor={`reason-${r.code}`} className="flex min-h-11 cursor-pointer items-center gap-3 text-strong">
                      <RadioItem id={`reason-${r.code}`} value={r.code} />
                      <span>{reasonLabel(r)}</span>
                    </label>
                  ))}
                </RadioGroup>
              </fieldset>
              {errors.reason ? (
                <p role="alert" className="text-sm font-medium text-danger">
                  {t("reasonRequired")}
                </p>
              ) : null}
              {reasonCode ? <p className="text-sm text-muted">{sellerFault ? t("faultSeller") : t("faultCustomer")}</p> : null}
              <Field label={t("details")} htmlFor="return-details" hint={t("detailsHint")}>
                <Textarea id="return-details" value={details} maxLength={500} onChange={(e) => setDetails(e.target.value)} className="min-h-24" />
              </Field>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t("photosTitle")}</CardTitle>
              <CardDescription>{t("photosHelp", { count: PHOTO_MAX_COUNT })}</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <input ref={fileInput} id="return-photo-input" data-testid="return-photo-input" type="file" hidden multiple accept={PHOTO_TYPES.join(",")} onChange={(e) => void addPhotos(e.target.files)} />
              <Button variant="secondary" disabled={photos.filter((p) => p.state !== "error").length >= PHOTO_MAX_COUNT} onClick={() => fileInput.current?.click()}>
                <ImagePlus aria-hidden /> {t("addPhotos")}
              </Button>
              {photos.length > 0 ? (
                <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3" aria-label={t("photosTitle")}>
                  {photos.map((photo, index) => (
                    <li key={photo.id} className="space-y-1.5" data-testid="return-photo">
                      <div className="relative">
                        {/* eslint-disable-next-line @next/next/no-img-element -- a blob: preview of a file the customer just chose */}
                        <img src={photo.preview} alt={t("photoAlt", { number: index + 1 })} className={cn("aspect-square w-full rounded-control border border-line object-cover", photo.state !== "done" && "opacity-60")} />
                        <Button variant="secondary" size="icon" className="absolute end-1 top-1 bg-card" aria-label={t("removePhoto", { number: index + 1 })} onClick={() => removePhoto(photo.id)}>
                          <X aria-hidden />
                        </Button>
                      </div>
                      <p className={cn("text-sm", photo.state === "error" ? "font-medium text-danger" : "text-muted")} role={photo.state === "error" ? "alert" : undefined}>
                        {photo.state === "uploading" ? t("uploading") : photo.state === "done" ? t("uploaded") : t(`photoError.${photo.error ?? "upload"}`)}
                      </p>
                    </li>
                  ))}
                </ul>
              ) : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t("nextTitle")}</CardTitle>
              <CardDescription>{t("nextHelp")}</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <ol className="list-decimal space-y-1.5 ps-5 text-strong">
                {(["review", "pickup", "check", kind === "Return" ? "refund" : "replacement"] as const).map((step) => (
                  <li key={step}>{t(`steps.${step}`)}</li>
                ))}
              </ol>
              {kind === "Return" ? (
                <p className="flex items-baseline justify-between gap-3 rounded-control bg-sunken p-3">
                  <span className="text-muted">{t("estimate")}</span>
                  <span className="text-xl font-bold tabular-nums text-strong" data-testid="refund-estimate">
                    {money(estimate)}
                  </span>
                </p>
              ) : (
                <p className="rounded-control bg-sunken p-3 text-strong">{t("exchangeNote")}</p>
              )}
              <p className="text-sm text-muted">{t("estimateNote")}</p>
            </CardContent>
          </Card>

          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <Button asChild variant="secondary">
              <Link href="/account/returns">{tc("cancel")}</Link>
            </Button>
            <Button type="submit" loading={create.isPending} disabled={uploading}>
              {t("submit")}
            </Button>
          </div>
        </>
      ) : null}
    </form>
  );
}
