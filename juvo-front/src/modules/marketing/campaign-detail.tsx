"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { ArrowLeft, CalendarClock, Eye, FlaskConical, Send, Trash2, Ban, Save } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input, RadioGroup, RadioItem, ChoiceRow } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { PageHeader } from "@/components/layout/page-header";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber, useMoney } from "@/modules/catalog/format";
import {
  useCampaign,
  useCampaignPreview,
  useCampaignRecipients,
  useCampaignResults,
  useCancelCampaign,
  useDeleteCampaign,
  useSaveCampaign,
  useScheduleCampaign,
  useSendCampaign,
  useSendTest,
  type Campaign,
  type CampaignPreview,
  type CampaignRecipient,
} from "./api";
import { CampaignFormFields } from "./campaign-form";
import { CampaignStatusBadge, ChannelBadge, Stat, useMarketingPermissions, useReasonLabel } from "./common";
import {
  allowedActions,
  dhakaInputToOffset,
  emptyCampaignForm,
  hasCampaignErrors,
  instantToDhakaInput,
  scheduleProblem,
  validateCampaignForm,
  type CampaignErrors,
  type CampaignForm,
} from "./logic";

const formOf = (c: Campaign): CampaignForm => ({
  name: c.name,
  segmentId: c.segmentId,
  channel: c.channel === "Email" ? "Email" : "Sms",
  subjectEn: c.subjectEn ?? "",
  subjectBn: c.subjectBn ?? "",
  bodyEn: c.bodyEn,
  bodyBn: c.bodyBn ?? "",
});

/** A new campaign: the form only. After saving, the person lands on the campaign page to preview, test, schedule or send. */
export function NewCampaignView() {
  const t = useTranslations("marketing.campaigns");
  const router = useRouter();
  const problemMessage = useProblemMessage();
  const save = useSaveCampaign();
  const [form, setForm] = React.useState<CampaignForm>(emptyCampaignForm);
  const [errors, setErrors] = React.useState<CampaignErrors>({});
  const [serverErrors, setServerErrors] = React.useState<Record<string, string>>({});
  const [problem, setProblem] = React.useState<string | null>(null);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const found = validateCampaignForm(form);
    setErrors(found);
    setServerErrors({});
    setProblem(null);
    if (hasCampaignErrors(found)) return;
    try {
      const made = await save.mutateAsync({ form });
      toast.success(t("created"));
      router.push(`/admin/marketing/campaigns/${made.id}`);
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.errors) setServerErrors(fieldErrors(p));
      setProblem(problemMessage(p));
    }
  };

  return (
    <form onSubmit={submit} noValidate className="max-w-4xl space-y-5" data-testid="campaign-form">
      {problem ? <Alert variant="danger">{problem}</Alert> : null}
      <CampaignFormFields form={form} onChange={setForm} errors={errors} serverErrors={serverErrors} />
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button asChild variant="secondary">
          <Link href="/admin/marketing/campaigns">{t("back")}</Link>
        </Button>
        <Button type="submit" loading={save.isPending} data-testid="campaign-save">
          <Save aria-hidden /> {t("saveDraft")}
        </Button>
      </div>
    </form>
  );
}

/** One campaign: its state, the editor (while it can still change), preview, test, schedule, send, cancel, recipients and results. */
export function CampaignDetailView({ id }: { id: string }) {
  const t = useTranslations("marketing.campaigns");
  const tn = useTranslations("nav");
  const query = useCampaign(id);
  const campaign = query.data;
  const crumbs = [
    { label: tn("items.dashboard"), href: "/admin" },
    { label: t("title"), href: "/admin/marketing/campaigns" },
    { label: campaign?.name ?? t("detailCrumb") },
  ];
  if (query.isError) {
    return (
      <>
        <PageHeader title={t("detailCrumb")} crumbs={crumbs} />
        <Alert variant="danger">{t("loadFailed")}</Alert>
      </>
    );
  }
  if (!campaign) {
    return (
      <>
        <PageHeader title={t("detailCrumb")} crumbs={crumbs} />
        <Skeleton className="h-64" aria-busy="true" />
      </>
    );
  }
  return (
    <>
      <PageHeader title={campaign.name} crumbs={crumbs} />
      <CampaignBody campaign={campaign} />
    </>
  );
}

function CampaignBody({ campaign }: { campaign: Campaign }) {
  const t = useTranslations("marketing.campaigns");
  const router = useRouter();
  const dateTime = useDateTime();
  const problemMessage = useProblemMessage();
  const perms = useMarketingPermissions();
  const save = useSaveCampaign();
  const preview = useCampaignPreview();
  const send = useSendCampaign();
  const cancel = useCancelCampaign();
  const remove = useDeleteCampaign();
  const started = !!campaign.startedAt;
  const actions = allowedActions(campaign.status, started);
  const editable = actions.includes("edit") && perms.campaignCreate;

  const [form, setForm] = React.useState<CampaignForm>(() => formOf(campaign));
  const [baseline, setBaseline] = React.useState(() => JSON.stringify(formOf(campaign)));
  const dirty = JSON.stringify(form) !== baseline;
  const [errors, setErrors] = React.useState<CampaignErrors>({});
  const [serverErrors, setServerErrors] = React.useState<Record<string, string>>({});
  const [problem, setProblem] = React.useState<string | null>(null);
  const [previewData, setPreviewData] = React.useState<CampaignPreview | null>(null);
  const [asking, setAsking] = React.useState(false);
  const [confirmSend, setConfirmSend] = React.useState<CampaignPreview | null>(null);
  const [dialog, setDialog] = React.useState<"schedule" | "test" | "cancel" | "delete" | null>(null);

  const saveForm = async () => {
    const found = validateCampaignForm(form);
    setErrors(found);
    setServerErrors({});
    setProblem(null);
    if (hasCampaignErrors(found)) return false;
    try {
      const saved = await save.mutateAsync({ id: campaign.id, form });
      setBaseline(JSON.stringify(formOf(saved)));
      setPreviewData(null);
      toast.success(t("saved"));
      return true;
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.errors) setServerErrors(fieldErrors(p));
      setProblem(problemMessage(p));
      return false;
    }
  };

  const showPreview = async () => {
    try {
      setPreviewData(await preview.mutateAsync(campaign.id));
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  // "Send now" first asks the server what sending would do right now, and states that number in the confirmation.
  const askSend = async () => {
    setAsking(true);
    try {
      const data = await preview.mutateAsync(campaign.id);
      setPreviewData(data);
      setConfirmSend(data);
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    } finally {
      setAsking(false);
    }
  };

  const doSend = async () => {
    try {
      await send.mutateAsync(campaign.id);
      toast.success(t("sendStarted"));
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    }
    setConfirmSend(null);
  };

  const doCancel = async () => {
    try {
      await cancel.mutateAsync(campaign.id);
      toast.success(t("cancelled"));
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    }
    setDialog(null);
  };

  const doDelete = async () => {
    try {
      await remove.mutateAsync(campaign.id);
      toast.success(t("deleted"));
      router.push("/admin/marketing/campaigns");
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
      setDialog(null);
    }
  };

  const can = (a: ReturnType<typeof allowedActions>[number]) => actions.includes(a);
  const blockedByDirty = dirty && editable;

  return (
    <div className="space-y-6" data-testid="campaign-detail">
      <div className="flex flex-wrap items-center gap-3">
        <Button asChild variant="ghost">
          <Link href="/admin/marketing/campaigns">
            <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
          </Link>
        </Button>
        <CampaignStatusBadge status={campaign.status} />
        <ChannelBadge channel={campaign.channel} />
        <span className="text-sm text-muted">{t("segmentLabel", { name: campaign.segmentName ?? "-" })}</span>
      </div>

      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2 lg:grid-cols-4">
        <Meta label={t("meta.created")} value={`${dateTime(campaign.createdAt)}${campaign.createdByName ? ` · ${campaign.createdByName}` : ""}`} />
        {campaign.scheduledAt ? <Meta label={t("meta.scheduled")} value={campaign.scheduledAtDhaka ? `${campaign.scheduledAtDhaka}` : dateTime(campaign.scheduledAt)} testId="meta-scheduled" /> : null}
        {campaign.startedAt ? <Meta label={t("meta.started")} value={dateTime(campaign.startedAt)} /> : null}
        {campaign.completedAt ? <Meta label={t("meta.completed")} value={dateTime(campaign.completedAt)} /> : null}
        {campaign.cancelledAt ? <Meta label={t("meta.cancelled")} value={dateTime(campaign.cancelledAt)} /> : null}
      </dl>

      {campaign.status === "Sending" && campaign.deferredByQuietHours ? <Alert variant="warning" data-testid="quiet-hours-alert">{t("deferredByQuietHours")}</Alert> : null}
      {campaign.status === "Scheduled" ? <Alert variant="info">{t("scheduledInfo", { when: campaign.scheduledAtDhaka ?? dateTime(campaign.scheduledAt) })}</Alert> : null}

      {/* Actions */}
      {perms.campaignCreate || perms.campaignSend || perms.campaignView ? (
        <div className="flex flex-wrap gap-2" role="group" aria-label={t("actions")}>
          {editable ? (
            <Button onClick={() => void saveForm()} loading={save.isPending} disabled={!dirty} data-testid="campaign-save">
              <Save aria-hidden /> {t("save")}
            </Button>
          ) : null}
          <Button variant="secondary" onClick={() => void showPreview()} loading={preview.isPending && !asking} disabled={blockedByDirty} data-testid="campaign-preview">
            <Eye aria-hidden /> {t("preview")}
          </Button>
          {can("test") && perms.campaignCreate ? (
            <Button variant="secondary" onClick={() => setDialog("test")} disabled={blockedByDirty} data-testid="campaign-test">
              <FlaskConical aria-hidden /> {t("sendTest")}
            </Button>
          ) : null}
          {can("schedule") && perms.campaignSend ? (
            <Button variant="secondary" onClick={() => setDialog("schedule")} disabled={blockedByDirty} data-testid="campaign-schedule">
              <CalendarClock aria-hidden /> {campaign.status === "Scheduled" ? t("reschedule") : t("schedule")}
            </Button>
          ) : null}
          {can("sendNow") && perms.campaignSend ? (
            <Button onClick={() => void askSend()} loading={asking} disabled={blockedByDirty || preview.isPending} data-testid="campaign-send">
              <Send aria-hidden /> {t("sendNow")}
            </Button>
          ) : null}
          {can("cancel") && perms.campaignSend ? (
            <Button variant="secondary" onClick={() => setDialog("cancel")} data-testid="campaign-cancel">
              <Ban aria-hidden /> {t("cancel")}
            </Button>
          ) : null}
          {can("delete") && perms.campaignCreate ? (
            <Button variant="ghost" onClick={() => setDialog("delete")} data-testid="campaign-delete">
              <Trash2 aria-hidden /> {t("delete")}
            </Button>
          ) : null}
        </div>
      ) : null}
      {blockedByDirty ? <p className="text-sm text-muted">{t("saveFirst")}</p> : null}

      {/* The message */}
      <section aria-label={t("message")} className="space-y-4 rounded-card border border-line bg-card p-4 sm:p-5">
        <h2 className="font-display text-lg font-semibold text-strong">{t("message")}</h2>
        {problem ? <Alert variant="danger">{problem}</Alert> : null}
        {editable ? (
          <form
            noValidate
            onSubmit={(e) => {
              e.preventDefault();
              void saveForm();
            }}
          >
            <CampaignFormFields form={form} onChange={setForm} errors={errors} serverErrors={serverErrors} />
          </form>
        ) : (
          <ReadOnlyMessage campaign={campaign} />
        )}
      </section>

      {previewData ? <PreviewPanel data={previewData} channel={campaign.channel} /> : null}

      {campaign.recipientsReady || started ? <ResultsPanel campaign={campaign} /> : null}
      {campaign.recipientsReady ? <RecipientsPanel campaignId={campaign.id} /> : null}

      {/* Dialogs */}
      <ConfirmDialog
        open={!!confirmSend}
        onOpenChange={(open) => !open && setConfirmSend(null)}
        title={t("sendConfirmTitle")}
        description={confirmSend ? t("sendConfirmDescription", { count: toNumber(confirmSend.recipients) }) : ""}
        confirmLabel={t("sendConfirm", { count: toNumber(confirmSend?.recipients) })}
        cancelLabel={t("keep")}
        closeLabel={t("close")}
        loading={send.isPending}
        onConfirm={doSend}
      >
        {confirmSend ? (
          <div className="space-y-2 text-sm" data-testid="send-confirm-details">
            <p className="text-strong" data-testid="send-confirm-count">
              {t("sendConfirmCount", { count: toNumber(confirmSend.recipients), members: toNumber(confirmSend.segmentMembers) })}
            </p>
            <p className="text-muted">{t("sendConfirmSkipped", { consent: toNumber(confirmSend.skippedNoConsent), contact: toNumber(confirmSend.skippedNoContact), cap: toNumber(confirmSend.skippedFrequencyCap) })}</p>
            {confirmSend.smsDeferred ? <Alert variant="warning">{t("sendConfirmQuiet")}</Alert> : null}
            {toNumber(confirmSend.recipients) === 0 ? <Alert variant="warning">{t("sendConfirmNobody")}</Alert> : null}
          </div>
        ) : null}
      </ConfirmDialog>

      <ConfirmDialog
        open={dialog === "cancel"}
        onOpenChange={(open) => !open && setDialog(null)}
        title={t("cancelTitle")}
        description={campaign.status === "Sending" ? t("cancelSendingDescription") : t("cancelDescription")}
        confirmLabel={t("cancelConfirm")}
        cancelLabel={t("keep")}
        closeLabel={t("close")}
        destructive
        loading={cancel.isPending}
        onConfirm={doCancel}
      />
      <ConfirmDialog
        open={dialog === "delete"}
        onOpenChange={(open) => !open && setDialog(null)}
        title={t("deleteTitle")}
        description={t("deleteDescription")}
        confirmLabel={t("delete")}
        cancelLabel={t("keep")}
        closeLabel={t("close")}
        destructive
        loading={remove.isPending}
        onConfirm={doDelete}
      />
      {dialog === "schedule" ? <ScheduleDialog campaign={campaign} onClose={() => setDialog(null)} /> : null}
      {dialog === "test" ? <TestDialog campaign={campaign} onClose={() => setDialog(null)} /> : null}
    </div>
  );
}

function Meta({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return (
    <div>
      <dt className="text-muted">{label}</dt>
      <dd className="font-medium text-strong" data-testid={testId}>
        {value}
      </dd>
    </div>
  );
}

function ReadOnlyMessage({ campaign }: { campaign: Campaign }) {
  const t = useTranslations("marketing.campaigns.form");
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      {campaign.channel === "Email" ? (
        <>
          <ReadOnlyText label={t("subjectEn")} text={campaign.subjectEn} />
          <ReadOnlyText label={t("subjectBn")} text={campaign.subjectBn} />
        </>
      ) : null}
      <ReadOnlyText label={t("bodyEn")} text={campaign.bodyEn} />
      <ReadOnlyText label={t("bodyBn")} text={campaign.bodyBn} />
    </div>
  );
}

function ReadOnlyText({ label, text }: { label: string; text: string | null | undefined }) {
  return (
    <div>
      <p className="text-sm font-medium text-muted">{label}</p>
      <p className="mt-1 whitespace-pre-line rounded-control bg-sunken p-3 text-strong [overflow-wrap:anywhere]">{text || "-"}</p>
    </div>
  );
}

/* ---- Preview ---------------------------------------------------------------------------------------------------------------- */

function PreviewPanel({ data, channel }: { data: CampaignPreview; channel: string }) {
  const t = useTranslations("marketing.campaigns.previewPanel");
  const tl = useTranslations("marketing.languages");
  return (
    <section aria-label={t("title")} className="space-y-4 rounded-card border border-line bg-card p-4 sm:p-5" data-testid="campaign-preview-panel">
      <h2 className="font-display text-lg font-semibold text-strong">{t("title")}</h2>
      <p className="text-sm text-muted">{t("hint")}</p>
      <ul className="grid grid-cols-2 gap-2 md:grid-cols-5">
        <li><Stat testId="preview-members" label={t("members")} value={toNumber(data.segmentMembers)} /></li>
        <li><Stat testId="preview-recipients" label={t("recipients")} value={toNumber(data.recipients)} /></li>
        <li><Stat testId="preview-noconsent" label={t("noConsent")} value={toNumber(data.skippedNoConsent)} /></li>
        <li><Stat testId="preview-nocontact" label={t("noContact")} value={toNumber(data.skippedNoContact)} /></li>
        <li><Stat testId="preview-cap" label={t("frequencyCap")} value={toNumber(data.skippedFrequencyCap)} /></li>
      </ul>
      {data.smsDeferred ? <Alert variant="warning">{t("quietNow")}</Alert> : null}
      {data.unknownPlaceholders.length > 0 ? <Alert variant="danger">{t("unknownPlaceholders", { names: data.unknownPlaceholders.map((p) => `{${p}}`).join(", ") })}</Alert> : null}
      <div className="grid gap-4 lg:grid-cols-2">
        {data.samples.map((sample) => (
          <article key={sample.language} className="rounded-control border border-line p-3" data-testid={`preview-sample-${sample.language}`} lang={sample.language}>
            <p className="flex flex-wrap items-center justify-between gap-2 text-sm font-semibold text-strong">
              <span>{tl.has(sample.language) ? tl(sample.language) : sample.language}</span>
              <Badge variant="outline">{t("characters", { count: toNumber(sample.characters) })}</Badge>
            </p>
            {channel === "Email" && sample.subject ? <p className="mt-2 font-medium text-strong [overflow-wrap:anywhere]">{sample.subject}</p> : null}
            <p className="mt-2 whitespace-pre-line text-sm text-strong [overflow-wrap:anywhere]">{sample.body}</p>
          </article>
        ))}
      </div>
    </section>
  );
}

/* ---- Schedule and test ------------------------------------------------------------------------------------------------------ */

function ScheduleDialog({ campaign, onClose }: { campaign: Campaign; onClose: () => void }) {
  const t = useTranslations("marketing.campaigns.scheduleDialog");
  const tc = useTranslations("marketing.common");
  const problemMessage = useProblemMessage();
  const schedule = useScheduleCampaign();
  const [value, setValue] = React.useState(() => (campaign.scheduledAt ? instantToDhakaInput(campaign.scheduledAt) : ""));
  const [error, setError] = React.useState<string | null>(null);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const problem = scheduleProblem(value);
    if (problem) {
      setError(t(`errors.${problem}`));
      return;
    }
    try {
      await schedule.mutateAsync({ id: campaign.id, scheduledAt: dhakaInputToOffset(value)! });
      toast.success(t("done", { when: value.replace("T", " ") }));
      onClose();
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      setError(p?.errors ? Object.values(fieldErrors(p))[0]! : problemMessage(p));
    }
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <ModalContent size="sm" closeLabel={tc("close")} data-testid="schedule-dialog">
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{campaign.channel === "Sms" ? t("descriptionSms") : t("description")}</DialogDescription>
        <form onSubmit={submit} noValidate className="mt-4 space-y-4">
          <Field label={t("when")} htmlFor="schedule-at" required hint={t("hint")} error={error ?? undefined}>
            <Input id="schedule-at" type="datetime-local" value={value} onChange={(e) => { setValue(e.target.value); setError(null); }} aria-invalid={!!error} />
          </Field>
          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <DialogClose asChild>
              <Button variant="secondary" type="button">{tc("cancel")}</Button>
            </DialogClose>
            <Button type="submit" loading={schedule.isPending} data-testid="schedule-confirm">
              {t("confirm")}
            </Button>
          </div>
        </form>
      </ModalContent>
    </Dialog>
  );
}

function TestDialog({ campaign, onClose }: { campaign: Campaign; onClose: () => void }) {
  const t = useTranslations("marketing.campaigns.testDialog");
  const tc = useTranslations("marketing.common");
  const tl = useTranslations("marketing.languages");
  const problemMessage = useProblemMessage();
  const test = useSendTest();
  const [language, setLanguage] = React.useState<"en" | "bn">("en");
  const [error, setError] = React.useState<string | null>(null);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    try {
      const sent = await test.mutateAsync({ id: campaign.id, language });
      toast.success(t("sent", { recipient: sent.recipient }));
      onClose();
    } catch (e) {
      setError(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <ModalContent size="sm" closeLabel={tc("close")} data-testid="test-dialog">
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{campaign.channel === "Email" ? t("descriptionEmail") : t("descriptionSms")}</DialogDescription>
        <form onSubmit={submit} className="mt-4 space-y-4">
          {error ? <Alert variant="danger">{error}</Alert> : null}
          <RadioGroup aria-label={t("language")} value={language} onValueChange={(v) => setLanguage(v === "bn" ? "bn" : "en")}>
            {(["en", "bn"] as const).map((l) => (
              <ChoiceRow key={l} htmlFor={`test-lang-${l}`}>
                <RadioItem value={l} id={`test-lang-${l}`} />
                <span>{tl(l)}</span>
              </ChoiceRow>
            ))}
          </RadioGroup>
          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <DialogClose asChild>
              <Button variant="secondary" type="button">{tc("cancel")}</Button>
            </DialogClose>
            <Button type="submit" loading={test.isPending} data-testid="test-confirm">
              {t("confirm")}
            </Button>
          </div>
        </form>
      </ModalContent>
    </Dialog>
  );
}

/* ---- Results and recipients ------------------------------------------------------------------------------------------------- */

function ResultsPanel({ campaign }: { campaign: Campaign }) {
  const t = useTranslations("marketing.campaigns.results");
  const reasonLabel = useReasonLabel();
  const money = useMoney();
  const results = useCampaignResults(campaign.id, true, campaign.status === "Sending");
  const r = results.data;
  return (
    <section aria-label={t("title")} className="space-y-4 rounded-card border border-line bg-card p-4 sm:p-5" data-testid="campaign-results">
      <h2 className="font-display text-lg font-semibold text-strong">{t("title")}</h2>
      {results.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      {!r ? (
        <Skeleton className="h-24" aria-busy="true" />
      ) : (
        <>
          <ul className="grid grid-cols-2 gap-2 md:grid-cols-4 xl:grid-cols-8">
            <li><Stat testId="result-recipients" label={t("recipients")} value={toNumber(r.recipients)} /></li>
            <li><Stat testId="result-pending" label={t("pending")} value={toNumber(r.pending)} hint={t("pendingHint")} /></li>
            <li><Stat testId="result-inflight" label={t("inFlight")} value={toNumber(r.inFlight)} hint={t("inFlightHint")} /></li>
            <li><Stat testId="result-sent" label={t("sent")} value={toNumber(r.sent)} /></li>
            <li><Stat testId="result-delivered" label={t("delivered")} value={toNumber(r.delivered)} hint={t("deliveredHint")} /></li>
            <li><Stat testId="result-failed" label={t("failed")} value={toNumber(r.failed)} /></li>
            <li><Stat testId="result-skipped" label={t("skipped")} value={toNumber(r.skipped)} /></li>
            <li><Stat testId="result-unsubscribed" label={t("unsubscribed")} value={toNumber(r.unsubscribed)} /></li>
          </ul>
          {r.skippedByReason.length > 0 ? (
            <div>
              <h3 className="text-sm font-semibold text-strong">{t("skippedByReason")}</h3>
              <ul className="mt-1 flex flex-wrap gap-2" data-testid="skipped-by-reason">
                {r.skippedByReason.map((x) => (
                  <li key={x.reason}>
                    <Badge variant="neutral">
                      {reasonLabel(x.reason)}: {toNumber(x.count)}
                    </Badge>
                  </li>
                ))}
              </ul>
            </div>
          ) : null}
          <div className="rounded-control bg-sunken p-3" data-testid="result-attribution">
            <h3 className="text-sm font-semibold text-strong">{t("attribution")}</h3>
            <p className="mt-1 text-strong">
              <span className="text-xl font-bold tabular-nums" data-testid="attributed-orders">{toNumber(r.attribution.orders)}</span> {t("orders")} ·{" "}
              <span className="text-xl font-bold tabular-nums" data-testid="attributed-revenue">{money(r.attribution.revenue)}</span> {t("revenue")}
            </p>
            <p className="mt-1 text-sm text-muted">{t("attributionHint", { days: toNumber(r.attribution.windowDays) })}</p>
          </div>
        </>
      )}
    </section>
  );
}

const RECIPIENT_STATUSES = ["Pending", "Queued", "Skipped"] as const;

function RecipientsPanel({ campaignId }: { campaignId: string }) {
  const t = useTranslations("marketing.campaigns.recipients");
  const tc = useTranslations("marketing.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const reasonLabel = useReasonLabel();
  const tdel = useTranslations("marketing.delivery");
  const state = useListState<{ status?: string; reason?: string }>({});
  const query = useCampaignRecipients(campaignId, { page: state.page, pageSize: state.pageSize, status: state.filters.status, reason: state.filters.reason, search: state.search });

  const columns = React.useMemo<ColumnDef<CampaignRecipient, unknown>[]>(
    () => [
      { id: "name", header: t("columns.name"), enableSorting: false, meta: { primary: true }, cell: ({ row }) => <span className="font-medium text-strong [overflow-wrap:anywhere]">{row.original.name ?? "-"}</span> },
      { id: "contact", header: t("columns.contact"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="text-sm [overflow-wrap:anywhere]">{row.original.contact ?? "-"}</span> },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant={row.original.status === "Skipped" ? "neutral" : row.original.status === "Queued" ? "success" : "info"}>{t(`status.${row.original.status}`)}</Badge> },
      { id: "reason", header: t("columns.reason"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="text-sm text-muted">{row.original.skipReason ? reasonLabel(row.original.skipReason) : "-"}</span> },
      { id: "delivery", header: t("columns.delivery"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-sm text-muted">{row.original.deliveryStatus ? (tdel.has(row.original.deliveryStatus) ? tdel(row.original.deliveryStatus) : row.original.deliveryStatus) : "-"}</span> },
      { id: "processed", header: t("columns.processed"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.processedAt)}</span> },
    ],
    [t, reasonLabel, tdel, dateTime],
  );

  return (
    <section aria-label={t("title")} className="space-y-3 rounded-card border border-line bg-card p-4 sm:p-5" data-testid="campaign-recipients">
      <h2 className="font-display text-lg font-semibold text-strong">{t("title")}</h2>
      <p className="text-sm text-muted">{t("maskedHint")}</p>
      <div className="grid gap-3 sm:grid-cols-3">
        <SearchBox value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={RECIPIENT_STATUSES.map((s) => ({ value: s, label: t(`status.${s}`) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect
          value={state.filters.reason}
          onChange={(v) => state.setFilter("reason", v)}
          options={["NoConsent", "NoContact", "FrequencyCap", "ChannelOff", "Cancelled", "Inactive"].map((r) => ({ value: r, label: reasonLabel(r) }))}
          allLabel={t("anyReason")}
          label={t("columns.reason")}
          searchPlaceholder={tc("search")}
          emptyText={tc("noMatch")}
        />
      </div>
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={query.data?.items ?? []} loading={query.isLoading} getRowId={(x) => x.customerId} labels={labels} page={state.page} pageSize={state.pageSize} total={toNumber(query.data?.total)} onPageChange={state.setPage} />
    </section>
  );
}
