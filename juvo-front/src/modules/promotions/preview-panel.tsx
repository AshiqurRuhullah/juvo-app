"use client";

import * as React from "react";
import { useLocale, useTranslations } from "next-intl";
import { Eye, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { VariantPicker, VariantText, type PickedVariant } from "@/modules/inventory/common";
import { usePreview, type PreviewResult } from "./api";
import type { Channel } from "./logic";
import { CHANNELS } from "./logic";

// The shared picker greys out added variants with a visible text that differs from its button name; adding twice is ignored here instead.
const NONE_CHOSEN: ReadonlySet<string> = new Set();

type SampleLine = PickedVariant & { quantity: string };

/**
 * "What would a customer see": prices a sample cart with exactly this promotion (a draft included, schedule ignored) through the
 * same engine as the shop. Nothing is saved or counted. The person picks sample products; nothing is made up.
 */
export function PreviewPanel({ promotionId }: { promotionId: string }) {
  const t = useTranslations("promotions.preview");
  const problem = useProblemMessage();
  const [lines, setLines] = React.useState<SampleLine[]>([]);
  const [channel, setChannel] = React.useState<Channel>("Web");
  const [result, setResult] = React.useState<PreviewResult | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const preview = usePreview();

  const run = async () => {
    setError(null);
    try {
      const answer = await preview.mutateAsync({
        lines: lines.map((l) => ({ variantId: l.variantId, sku: null, quantity: Math.max(1, toNumber(l.quantity)) })),
        couponCode: null,
        promotionIds: [promotionId],
        at: null,
        channel,
      });
      setResult(answer);
    } catch (e) {
      setResult(null);
      setError(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <section className="space-y-4" aria-labelledby="preview-title" data-testid="preview-panel">
      <div>
        <h2 id="preview-title" className="font-display text-xl font-semibold text-strong">
          {t("title")}
        </h2>
        <p className="text-sm text-muted">{t("intro")}</p>
      </div>
      <VariantPicker idPrefix="preview" chosen={NONE_CHOSEN} onAdd={(v) => setLines((old) => (old.some((l) => l.variantId === v.variantId) ? old : [...old, { ...v, quantity: "1" }]))} />
      {lines.length > 0 ? (
        <ul className="divide-y divide-line rounded-control border border-line" aria-label={t("sampleCart")}>
          {lines.map((l) => (
            <li key={l.variantId} className="flex flex-wrap items-center gap-2 px-3 py-1.5" data-testid="sample-line">
              <span className="min-w-0 flex-1 [overflow-wrap:anywhere]">
                <span className="block font-medium text-strong">{l.productName}</span>
                <VariantText sku={l.sku} colorName={l.colorName} size={l.size} className="text-sm text-muted" />
              </span>
              <label className="flex items-center gap-2 text-sm text-muted">
                {t("quantity")}
                <Input
                  className="w-20 text-center"
                  inputMode="numeric"
                  value={l.quantity}
                  aria-label={t("quantityOf", { sku: l.sku })}
                  onChange={(e) => setLines((old) => old.map((x) => (x.variantId === l.variantId ? { ...x, quantity: e.target.value.replace(/[^0-9]/g, "") } : x)))}
                />
              </label>
              <Button variant="ghost" size="icon" onClick={() => setLines((old) => old.filter((x) => x.variantId !== l.variantId))} aria-label={t("removeLine", { sku: l.sku })}>
                <Trash2 aria-hidden />
              </Button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-sm text-muted">{t("noLines")}</p>
      )}
      <div className="flex flex-wrap items-end gap-3">
        <div role="group" aria-label={t("channel")} className="flex flex-wrap gap-2">
          {CHANNELS.map((c) => (
            <button
              key={c}
              type="button"
              aria-pressed={channel === c}
              onClick={() => setChannel(c)}
              className={`min-h-11 rounded-control border px-3 font-medium ${channel === c ? "border-primary bg-primary-soft text-strong" : "border-line bg-card text-muted"}`}
            >
              {t(`channels.${c}`)}
            </button>
          ))}
        </div>
        <Button onClick={() => void run()} disabled={lines.length === 0} loading={preview.isPending} data-testid="preview-run">
          <Eye aria-hidden /> {t("run")}
        </Button>
      </div>
      {error ? <Alert variant="danger">{error}</Alert> : null}
      {result ? <PreviewResultView result={result} /> : null}
    </section>
  );
}

function PreviewResultView({ result }: { result: PreviewResult }) {
  const t = useTranslations("promotions.preview");
  const money = useMoney();
  const locale = useLocale();
  const bn = locale === "bn";
  const notes = bn ? result.explanation.notesBn : result.explanation.notesEn;
  const savings = toNumber(result.explanation.savings);
  return (
    <div className="space-y-3 rounded-card border border-line bg-card p-4" data-testid="preview-result" aria-live="polite">
      <h3 className="font-semibold text-strong">{t("resultTitle")}</h3>
      <ul className="divide-y divide-line">
        {result.lines.map((l) => {
          const list = toNumber(l.listPrice);
          const unit = toNumber(l.unitPrice);
          return (
            <li key={l.variantId} className="flex flex-wrap items-baseline justify-between gap-2 py-1.5" data-testid="preview-line">
              <span className="text-sm text-muted">{t("lineQty", { count: toNumber(l.quantity) })}</span>
              <span className="tabular-nums">
                {list > unit ? <s className="me-2 text-muted">{money(list)}</s> : null}
                <span className="font-semibold text-strong" data-testid="preview-unit">
                  {money(unit)}
                </span>
                <span className="ms-2 text-sm text-muted">
                  {t("lineTotal")} {money(l.netTotal)}
                </span>
                {toNumber(l.orderDiscount) > 0 ? <span className="ms-2 text-sm text-success-ink">{t("orderDiscount", { amount: money(l.orderDiscount) })}</span> : null}
              </span>
            </li>
          );
        })}
      </ul>
      <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 text-sm">
        <dt className="text-muted">{t("listSubtotal")}</dt>
        <dd className="tabular-nums">{money(result.totals.listSubtotal)}</dd>
        <dt className="text-muted">{t("promotionDiscount")}</dt>
        <dd className="tabular-nums" data-testid="preview-discount">
          {money(result.totals.promotionDiscount)}
        </dd>
        <dt className="font-semibold text-strong">{t("goodsTotal")}</dt>
        <dd className="font-semibold tabular-nums text-strong" data-testid="preview-total">
          {money(result.totals.goodsTotal)}
        </dd>
        <dt className="text-muted">{t("savings")}</dt>
        <dd className="tabular-nums">{money(savings)}</dd>
      </dl>
      {result.promotions.length === 0 ? <Alert variant="warning">{t("notApplied")}</Alert> : null}
      {result.promotions.length > 0 ? (
        <ul className="space-y-1 text-sm" aria-label={t("applied")}>
          {result.promotions.map((p) => (
            <li key={p.id} data-testid="preview-applied">
              <span className="font-medium text-strong">{bn ? p.labelBn || p.labelEn : p.labelEn}</span> <span className="text-muted">{money(p.discount)}</span>
            </li>
          ))}
        </ul>
      ) : null}
      {result.freeDelivery.length > 0 ? (
        <ul className="space-y-1 text-sm" aria-label={t("freeDelivery")}>
          {result.freeDelivery.map((o) => (
            <li key={o.promotionId} data-testid="preview-free-delivery">
              {bn ? o.labelBn || o.labelEn : o.labelEn}: {o.qualifies ? t("qualifies") : t("needsMore", { amount: money(o.amountToQualify) })}
            </li>
          ))}
        </ul>
      ) : null}
      {notes.length > 0 ? (
        <ul className="list-disc space-y-1 ps-5 text-sm text-muted" aria-label={t("notes")}>
          {notes.map((n) => (
            <li key={n}>{n}</li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
