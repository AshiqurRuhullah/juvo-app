"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Archive, Copy, PowerOff, Rocket, Save } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Alert, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber } from "@/modules/catalog/format";
import { useCreatePromotion, usePromotion, usePromotionAction, useUpdatePromotion, type PromotionAction, type PromotionDetail } from "./api";
import { StatusBadge, usePromotionPermissions } from "./common";
import { allowedActions, emptyForm, formFromDetail, toRequest, validateForm, TYPE_RULES, type PromotionForm } from "./logic";
import { BasicsSection, CampaignSection, OfferSection, ScheduleSection, StackingSection, TargetsSection, type Errors } from "./promotion-fields";
import { PreviewPanel } from "./preview-panel";
import { PromotionResults } from "./report-view";

const same = (a: PromotionForm, b: PromotionForm) => JSON.stringify(a) === JSON.stringify(b);

/** Fields the API can name in a validation error, with the text shown next to the field (the API's own English wording is never shown). */
const SERVER_FIELDS = ["nameEn", "nameBn", "discountKind", "discountValue", "maxDiscount", "targets", "endsAt", "startsAt", "slug", "bundlePrice", "buyQuantity", "getQuantity", "minCartValue", "zoneCodes", "titleEn", "bannerCategoryId", "usageLimit", "priority", "channels"];

/** Create (no id) or edit one promotion of any type: details, targets, schedule, stacking, campaign page, preview and results. */
export function PromotionEditor({ id }: { id?: string }) {
  const t = useTranslations("promotions.editor");
  const tn = useTranslations("nav");
  const tl = useTranslations("promotions.list");
  const query = usePromotion(id ?? "");
  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.promotions"), href: "/admin/promotions" }, { label: id ? (query.data?.nameEn ?? "...") : t("newCrumb") }];

  if (id && query.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-96" aria-busy="true" />
      </>
    );
  }
  if (id && (query.isError || !query.data)) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFoundTitle")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFoundBody") : t("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/promotions">{tl("backToList")}</Link>
            </Button>
          }
        />
      </>
    );
  }
  return <EditorBody key={query.data?.id ?? "new"} detail={query.data} crumbs={crumbs} />;
}

type Dialog = "activate" | "deactivate" | "archive" | null;

function EditorBody({ detail, crumbs }: { detail: PromotionDetail | undefined; crumbs: { label: string; href?: string }[] }) {
  const t = useTranslations("promotions.editor");
  const ta = useTranslations("promotions.actions");
  const tc = useTranslations("promotions.common");
  const tt = useTranslations("promotions.type");
  const problemMessage = useProblemMessage();
  const router = useRouter();
  const perms = usePromotionPermissions();
  const create = useCreatePromotion();
  const update = useUpdatePromotion(detail?.id ?? "");
  const act = usePromotionAction(detail?.id ?? "");

  const [form, setForm] = React.useState<PromotionForm>(() => (detail ? formFromDetail(detail) : emptyForm()));
  const [errors, setErrors] = React.useState<Errors>({});
  const [failure, setFailure] = React.useState<string | null>(null);
  const [dialog, setDialog] = React.useState<Dialog>(null);
  const [dialogError, setDialogError] = React.useState<string | null>(null);

  const isNew = !detail;
  const status = detail?.status ?? "Draft";
  const actions = detail
    ? allowedActions(status, perms)
    : { save: perms.create, activate: false, deactivate: false, archive: false, duplicate: false, upload: false };
  const locked = !actions.save;
  const saved = React.useMemo(() => (detail ? formFromDetail(detail) : null), [detail]);
  const dirty = saved ? !same(form, saved) : true;
  const rule = TYPE_RULES[form.type];

  const set = React.useCallback(<K extends keyof PromotionForm>(key: K, value: PromotionForm[K]) => {
    setForm((old) => ({ ...old, [key]: value }));
    setErrors((old) => (old[key as string] ? { ...old, [key as string]: undefined } : old));
  }, []);

  const clientErrors = (): Errors => {
    const found = validateForm(form);
    const texts: Errors = {};
    for (const [field, key] of Object.entries(found)) texts[field] = t(`errors.${key}`);
    return texts;
  };

  /** Shows what the API named (translated, field by field) and the code-based message for anything else. */
  const showProblem = (e: unknown, into: (text: string) => void) => {
    const problem = e instanceof ApiError ? e.problem : null;
    if (problem?.errors) {
      const named = fieldErrors(problem);
      const mapped: Errors = {};
      for (const field of Object.keys(named)) if (SERVER_FIELDS.includes(field)) mapped[field] = t(`serverField.${field}`);
      if (Object.keys(mapped).length) {
        setErrors((old) => ({ ...old, ...mapped }));
        into(problemMessage({ ...problem, code: problem.code ?? "GENERAL.VALIDATION" }));
        return;
      }
    }
    into(problemMessage(problem));
  };

  const save = async () => {
    setFailure(null);
    const found = clientErrors();
    setErrors(found);
    if (Object.keys(found).length) {
      setFailure(t("fixFields"));
      return;
    }
    try {
      const body = toRequest(form);
      if (isNew) {
        const created = await create.mutateAsync(body);
        toast.success(t("created"));
        router.replace(`/admin/promotions/${created.id}`);
      } else {
        const updated = await update.mutateAsync(body);
        setForm(formFromDetail(updated));
        toast.success(t("saved"));
      }
    } catch (e) {
      showProblem(e, setFailure);
    }
  };

  const run = async (action: PromotionAction) => {
    setDialogError(null);
    try {
      const result = await act.mutateAsync(action);
      if (action === "duplicate") {
        toast.success(ta("duplicated"));
        router.push(`/admin/promotions/${result.id}`);
        return;
      }
      setForm(formFromDetail(result));
      setDialog(null);
      toast.success(ta(`${action}Done`));
    } catch (e) {
      if (action === "duplicate") toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
      else showProblem(e, setDialogError);
    }
  };

  const title = detail ? detail.nameEn : t("newTitle");
  const busy = create.isPending || update.isPending || act.isPending;

  return (
    <>
      <PageHeader
        title={title}
        crumbs={crumbs}
        description={detail ? tt(detail.type) : undefined}
        actions={
          <>
            {actions.duplicate && detail ? (
              <Button variant="secondary" onClick={() => void run("duplicate")} disabled={busy} data-testid="promotion-duplicate">
                <Copy aria-hidden /> {ta("duplicate")}
              </Button>
            ) : null}
            {actions.deactivate ? (
              <Button variant="secondary" onClick={() => { setDialogError(null); setDialog("deactivate"); }} disabled={busy} data-testid="promotion-deactivate">
                <PowerOff aria-hidden /> {ta("deactivate")}
              </Button>
            ) : null}
            {actions.archive ? (
              <Button variant="secondary" onClick={() => { setDialogError(null); setDialog("archive"); }} disabled={busy} data-testid="promotion-archive">
                <Archive aria-hidden /> {ta("archive")}
              </Button>
            ) : null}
            {actions.activate ? (
              <Button onClick={() => { setDialogError(null); setDialog("activate"); }} disabled={busy || dirty} data-testid="promotion-activate">
                <Rocket aria-hidden /> {ta("activate")}
              </Button>
            ) : null}
          </>
        }
      />

      <div className="space-y-4">
        {detail ? (
          <div className="flex flex-wrap items-center gap-3" data-testid="promotion-state">
            <StatusBadge status={detail.status} />
            <span className="text-sm text-muted">{ta(`statusText.${detail.status}`)}</span>
            <span className="text-sm text-muted tabular-nums">{t("usedLine", { used: toNumber(detail.usedCount), limit: detail.usageLimit === null ? t("unlimited") : toNumber(detail.usageLimit) })}</span>
          </div>
        ) : null}
        {detail?.isArchived ? <Alert variant="warning">{t("archivedNote")}</Alert> : null}
        {!detail?.isArchived && !actions.save ? <Alert variant="info">{t("readOnly")}</Alert> : null}
        {actions.activate && dirty ? <Alert variant="info">{t("saveBeforeActivate")}</Alert> : null}

        <form
          noValidate
          onSubmit={(e) => {
            e.preventDefault();
            if (!locked) void save();
          }}
          className="space-y-4"
          data-testid="promotion-form"
          aria-label={title}
        >
          <fieldset disabled={locked || busy} className="m-0 min-w-0 space-y-4 border-0 p-0">
            <BasicsSection form={form} set={set} errors={errors} isNew={isNew} detail={detail} />
            <OfferSection form={form} set={set} errors={errors} isNew={isNew} detail={detail} />
            <TargetsSection form={form} set={set} errors={errors} isNew={isNew} detail={detail} />
            <ScheduleSection form={form} set={set} errors={errors} isNew={isNew} detail={detail} />
            <StackingSection form={form} set={set} errors={errors} isNew={isNew} detail={detail} />
            {rule.campaign ? <CampaignSection form={form} set={set} errors={errors} isNew={isNew} detail={detail} /> : null}
          </fieldset>

          {failure ? (
            <Alert variant="danger" data-testid="promotion-failure">
              {failure}
            </Alert>
          ) : null}
          {!locked ? (
            <div className="flex flex-wrap items-center gap-3">
              <Button type="submit" loading={create.isPending || update.isPending} data-testid="promotion-save">
                <Save aria-hidden /> {isNew ? t("saveDraft") : t("saveChanges")}
              </Button>
              <Button asChild variant="ghost">
                <Link href="/admin/promotions">{tc("cancel")}</Link>
              </Button>
              {!isNew && dirty ? <span className="text-sm text-muted">{t("unsaved")}</span> : null}
            </div>
          ) : null}
        </form>

        {detail ? (
          <div className="space-y-8 pt-4">
            <PreviewPanel promotionId={detail.id} />
            <PromotionResults id={detail.id} />
          </div>
        ) : (
          <Alert variant="info">{t("previewAfterSave")}</Alert>
        )}
      </div>

      <ConfirmDialog
        open={dialog === "activate"}
        onOpenChange={(open) => !open && setDialog(null)}
        title={ta("activateTitle")}
        description={ta("activateBody")}
        confirmLabel={ta("activate")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={act.isPending}
        onConfirm={() => run("activate")}
      >
        {dialogError ? <Alert variant="danger">{dialogError}</Alert> : null}
      </ConfirmDialog>
      <ConfirmDialog
        open={dialog === "deactivate"}
        onOpenChange={(open) => !open && setDialog(null)}
        title={ta("deactivateTitle")}
        description={ta("deactivateBody")}
        confirmLabel={ta("deactivate")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={act.isPending}
        onConfirm={() => run("deactivate")}
      >
        {dialogError ? <Alert variant="danger">{dialogError}</Alert> : null}
      </ConfirmDialog>
      <ConfirmDialog
        open={dialog === "archive"}
        onOpenChange={(open) => !open && setDialog(null)}
        title={ta("archiveTitle")}
        description={ta("archiveBody")}
        confirmLabel={ta("archive")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={act.isPending}
        onConfirm={() => run("archive")}
      >
        {dialogError ? <Alert variant="danger">{dialogError}</Alert> : null}
      </ConfirmDialog>
    </>
  );
}
