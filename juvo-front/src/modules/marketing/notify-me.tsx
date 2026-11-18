"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { BellRing, CircleCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { signInUrl, useStoreSession } from "@/modules/store/store-session";
import { useNotifyMe } from "./api";
import { canNotify, looksLikeMobile, notifyErrorKey } from "./logic";

type Variant = { id: string; colorName: string | null; size: string | null; inStock: boolean; isPreorder?: boolean };

const label = (v: Variant) => [v.colorName, v.size].filter(Boolean).join(" / ");

/**
 * "Notify me when it is back" on a sold-out variant of the product page (FR-AC03, back in stock). A guest gives a mobile number, a
 * signed-in customer only confirms. Shown only when the chosen variant is sold out and cannot be pre-ordered; when no size is chosen yet
 * and everything is sold out, the customer picks which variant to wait for. The text says the message is sent once.
 */
export function NotifyMe({ slug, variants, variant }: { slug: string; variants: Variant[]; variant: Variant | undefined }) {
  const candidates = React.useMemo(() => variants.filter((v) => canNotify(v)), [variants]);
  const everythingSoldOut = variants.length > 0 && variants.every((v) => !v.inStock);
  const [picked, setPicked] = React.useState<string>("");

  const target = variant ? (canNotify(variant) ? variant : undefined) : everythingSoldOut ? (candidates.find((v) => v.id === picked) ?? (candidates.length === 1 ? candidates[0] : undefined)) : undefined;
  const needsPick = !variant && everythingSoldOut && candidates.length > 1;
  if (variant ? !canNotify(variant) : !(everythingSoldOut && candidates.length > 0)) return null;

  return <Form slug={slug} target={target} pickOptions={needsPick ? candidates : null} picked={picked} onPick={setPicked} />;
}

function Form({ slug, target, pickOptions, picked, onPick }: { slug: string; target: Variant | undefined; pickOptions: Variant[] | null; picked: string; onPick: (id: string) => void }) {
  const t = useTranslations("marketingStore.notify");
  const tc = useTranslations("marketingStore.common");
  const { customer } = useStoreSession();
  const notify = useNotifyMe(slug);
  const [mobile, setMobile] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [doneFor, setDone] = React.useState<null | { variantId: string; already: boolean }>(null);
  const done = doneFor && doneFor.variantId === target?.id ? doneFor : null;
  const sending = React.useRef(false);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (sending.current) return;
    setError(null);
    if (!target) {
      setError(t("pickVariant"));
      return;
    }
    if (!customer && !looksLikeMobile(mobile)) {
      setError(t("errors.contact"));
      return;
    }
    sending.current = true;
    try {
      const result = await notify.mutateAsync({ variantId: target.id, mobile: customer ? null : mobile.trim() });
      setDone({ variantId: target.id, already: result.alreadySubscribed });
    } catch (e) {
      const problem = e instanceof ApiError ? e.problem : null;
      setError(t(`errors.${notifyErrorKey(problem)}`));
    } finally {
      sending.current = false;
    }
  };

  if (done) {
    return (
      <div className="rounded-card border border-line bg-sunken p-4" role="status" data-testid="notify-done">
        <p className="flex items-start gap-2 font-medium text-strong">
          <CircleCheck className="mt-0.5 size-5 shrink-0 text-success" aria-hidden />
          <span>{done.already ? t("already") : t("success")}</span>
        </p>
        <p className="mt-1 ps-7 text-sm text-muted">{customer ? t("successCustomer") : t("successGuest")}</p>
      </div>
    );
  }

  return (
    <form onSubmit={submit} noValidate className="space-y-3 rounded-card border border-line bg-sunken p-4" data-testid="notify-me">
      <h2 className="flex items-center gap-2 font-semibold text-strong">
        <BellRing className="size-5" aria-hidden /> {t("title")}
      </h2>
      <p className="text-sm text-muted">{t("intro")}</p>
      {pickOptions ? (
        <Field label={t("whichVariant")} htmlFor="notify-variant" required>
          <Select id="notify-variant" value={picked} onValueChange={onPick} options={pickOptions.map((v) => ({ value: v.id, label: label(v) || t("thisItem") }))} placeholder={t("whichVariantPlaceholder")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
      ) : target && label(target) ? (
        <p className="text-sm text-strong">{t("forVariant", { variant: label(target) })}</p>
      ) : null}
      {!customer ? (
        <Field label={t("mobile")} htmlFor="notify-mobile" required hint={t("mobileHint")}>
          <Input id="notify-mobile" type="tel" inputMode="tel" autoComplete="tel" value={mobile} onChange={(e) => setMobile(e.target.value)} aria-invalid={!!error} />
        </Field>
      ) : (
        <p className="text-sm text-muted">{t("signedIn")}</p>
      )}
      {error ? <Alert variant="danger">{error}</Alert> : null}
      <Button type="submit" loading={notify.isPending} data-testid="notify-submit">
        <BellRing aria-hidden /> {t("submit")}
      </Button>
      {!customer ? (
        <p className="text-sm text-muted">
          {t("haveAccount")}{" "}
          <Link href={signInUrl(`/product/${slug}`)} className="font-medium text-primary underline-offset-4 hover:underline">
            {t("signIn")}
          </Link>
        </p>
      ) : null}
    </form>
  );
}
