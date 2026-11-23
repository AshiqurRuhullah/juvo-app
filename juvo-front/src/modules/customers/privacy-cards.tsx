"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CircleCheck, CircleX, Download, ShieldAlert, ShieldCheck, Trash2, UserX } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Textarea, Input } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Pagination } from "@/components/ui/pagination";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useDateTime, usePaginationLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { PAGE_SIZE } from "@/lib/pagination";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { exportCustomerData, useAnonymiseCustomer, useConsentHistory, type AnonymiseResult } from "./api";
import {
  REASON_MAX,
  canAnonymise,
  confirmMatches,
  isBlockingKind,
  privacyErrorKind,
  reasonProblem,
  toConsentRow,
  type ConsentRow,
  type PrivacyErrorKind,
} from "./privacy-logic";

/** Consent history, data export and anonymisation for one customer (docs/privacy-api.md). The page decides who sees which card. */

const REPLACED = ["profile", "orders", "text", "signins", "audit"] as const;
const DELETED = ["addresses", "wishlist", "sessions", "backInStock", "attachments"] as const;
const KEPT = ["invoices", "orders", "money", "audit"] as const;

/** Said once at the top of the page after the customer was anonymised (the date is known only when it happened in this session). */
export function AnonymisedBanner({ anonymisedAt }: { anonymisedAt: string | null }) {
  const t = useTranslations("privacy.banner");
  const dateTime = useDateTime();
  return (
    <Alert variant="warning" data-testid="anonymised-banner" title={anonymisedAt ? t("title", { date: dateTime(anonymisedAt) }) : t("titleNoDate")}>
      {t("body")}
    </Alert>
  );
}

function ConsentRowItem({ row, masked }: { row: ConsentRow; masked: boolean }) {
  const t = useTranslations("privacy.history");
  const dateTime = useDateTime();
  const given = row.change === "given" || row.change === "firstGiven";
  return (
    <li className="rounded-card border border-line p-4" data-testid="consent-history-row">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="flex flex-wrap items-center gap-2 font-semibold text-strong">
          <span>{t(`channel.${row.channel}`)}</span>
          <Badge variant={given ? "success" : "outline"}>
            {given ? <CircleCheck className="size-3.5" aria-hidden /> : <CircleX className="size-3.5" aria-hidden />}
            {t(`change.${row.change}`)}
          </Badge>
        </p>
        <time className="text-sm text-muted" dateTime={row.at}>
          {dateTime(row.at)}
        </time>
      </div>
      {row.coversWhatsApp ? <p className="mt-1 text-sm text-muted">{t("whatsapp")}</p> : null}
      <dl className="mt-2 grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
        <div className="min-w-0">
          <dt className="text-muted">{t("source")}</dt>
          <dd className="font-medium text-strong [overflow-wrap:anywhere]">
            {t(`sourceKind.${row.sourceKind}`)}
            <span className="block font-mono text-xs font-normal text-muted" data-testid="consent-source">
              {row.source}
            </span>
          </dd>
        </div>
        <div className="min-w-0">
          <dt className="text-muted">{t("ip")}</dt>
          <dd className="font-mono font-medium text-strong [overflow-wrap:anywhere]" data-testid="consent-ip" title={masked ? t("ipMasked") : undefined}>
            {row.ip ?? <span className="font-sans font-normal text-muted">{t("noIp")}</span>}
          </dd>
        </div>
      </dl>
    </li>
  );
}

/** The consent log of the customer, newest first, ten per page. Needs customers.customer.view (the page only mounts it then). */
export function ConsentHistoryCard({ id }: { id: string }) {
  const t = useTranslations("privacy.history");
  const query = useConsentHistory(id, true);
  const pagerLabels = usePaginationLabels();
  const [page, setPage] = React.useState(1);
  const rows = React.useMemo(() => (query.data?.items ?? []).map(toConsentRow), [query.data]);
  const visible = rows.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);
  return (
    <Card data-testid="consent-history">
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
        <p className="mt-1 text-sm text-muted">{t("description")}</p>
      </CardHeader>
      <CardContent className="space-y-3">
        {query.isPending ? (
          <Skeleton className="h-24" aria-busy="true" />
        ) : query.isError || !query.data ? (
          <p className="text-sm text-muted" data-testid="history-failed">
            {t("loadFailed")}
          </p>
        ) : rows.length === 0 ? (
          <p className="text-muted" data-testid="history-empty">
            {t("empty")}
          </p>
        ) : (
          <>
            {query.data.piiMasked ? <p className="text-sm text-muted">{t("ipMasked")}</p> : null}
            <ul className="space-y-3">
              {visible.map((row) => (
                <ConsentRowItem key={row.id} row={row} masked={query.data.piiMasked} />
              ))}
            </ul>
            {rows.length > PAGE_SIZE ? <Pagination page={page} total={rows.length} onPageChange={setPage} labels={pagerLabels} /> : null}
          </>
        )}
      </CardContent>
    </Card>
  );
}

function Section({ title, items, testId }: { title: string; items: readonly string[]; testId: string }) {
  return (
    <section className="space-y-1" data-testid={testId}>
      <h3 className="text-sm font-semibold text-strong">{title}</h3>
      <ul className="list-disc space-y-0.5 ps-5 text-sm text-muted">
        {items.map((label) => (
          <li key={label}>{label}</li>
        ))}
      </ul>
    </section>
  );
}

/** The message and, for the two blocking refusals, the system's own detail line under it. */
function ProblemAlert({ error, kind, testId }: { error: ApiError; kind: PrivacyErrorKind; testId: string }) {
  const t = useTranslations("privacy.anonymise");
  const message = useProblemMessage();
  const detail = error.problem.detail;
  return (
    <Alert variant="danger" data-testid={testId} title={isBlockingKind(kind) ? t("blockedTitle") : undefined}>
      <span className="block">{message(error.problem)}</span>
      {kind === "openWork" ? <span className="mt-1 block">{t("blockedOpen")}</span> : null}
      {kind === "openWork" && detail ? (
        <span className="mt-1 block text-muted" data-testid="blocked-detail">
          {t("blockedDetail")}: {detail}
        </span>
      ) : null}
    </Alert>
  );
}

function AnonymiseResultView({ result, onClose }: { result: AnonymiseResult; onClose: () => void }) {
  const t = useTranslations("privacy.anonymise");
  const money = useMoney();
  const already = result.alreadyAnonymised;
  const forfeitedPoints = toNumber(result.forfeitedPoints);
  const forfeitedCredit = toNumber(result.forfeitedCredit);
  return (
    <div className="mt-4 space-y-4" data-testid="anonymise-result">
      <Alert variant="success" title={already ? t("alreadyTitle") : t("doneTitle")}>
        {already ? t("alreadyBody") : t("doneBody", { orders: toNumber(result.scrubbed.orders), returns: toNumber(result.scrubbed.returns), tickets: toNumber(result.scrubbed.tickets) })}
        {!already && (forfeitedPoints > 0 || forfeitedCredit > 0) ? <span className="mt-1 block">{t("forfeited", { points: forfeitedPoints, credit: money(forfeitedCredit) })}</span> : null}
      </Alert>
      <div className="flex justify-end">
        <Button onClick={onClose} data-testid="anonymise-close">
          {t("close")}
        </Button>
      </div>
    </div>
  );
}

/** The deliberate flow: lists what changes, asks for a reason and the customer id, and enables the button only when both are right. */
function AnonymiseDialog({ id, open, onOpenChange, onDone }: { id: string; open: boolean; onOpenChange: (open: boolean) => void; onDone: (at: string) => void }) {
  const t = useTranslations("privacy.anonymise");
  const tc = useTranslations("customers.common");
  const mutation = useAnonymiseCustomer(id);
  const [reason, setReason] = React.useState("");
  const [confirm, setConfirm] = React.useState("");
  const [touched, setTouched] = React.useState({ reason: false, confirm: false });
  const [error, setError] = React.useState<ApiError | null>(null);
  const [result, setResult] = React.useState<AnonymiseResult | null>(null);
  const reasonRef = React.useRef<HTMLTextAreaElement>(null);

  const problem = reasonProblem(reason);
  const matches = confirmMatches(confirm, id);
  const ready = canAnonymise({ reason, confirm, customerId: id, busy: mutation.isPending });
  const kind = privacyErrorKind(error?.problem);

  const submit = async () => {
    setError(null);
    try {
      const done = await mutation.mutateAsync({ reason: reason.trim(), confirm: confirm.trim() });
      setResult(done);
      onDone(done.anonymisedAt);
    } catch (e) {
      setError(e instanceof ApiError ? e : new ApiError({ status: 0, code: "NETWORK" }));
    }
  };

  const close = () => onOpenChange(false);

  return (
    <Dialog open={open} onOpenChange={(next) => (mutation.isPending ? undefined : onOpenChange(next))}>
      <ModalContent
        size="lg"
        closeLabel={tc("close")}
        // The first thing focused is the reason box: never the destructive button.
        onOpenAutoFocus={(event) => {
          event.preventDefault();
          reasonRef.current?.focus();
        }}
        data-testid="anonymise-dialog"
      >
        <DialogTitle className="flex items-center gap-2 font-display text-xl font-semibold text-strong">
          <ShieldAlert className="size-6 shrink-0 text-danger" aria-hidden />
          {t("title")}
        </DialogTitle>
        <DialogDescription className="mt-2 font-medium text-strong">{t("irreversible")}</DialogDescription>
        {result ? (
          <AnonymiseResultView result={result} onClose={close} />
        ) : (
          <form
            className="mt-4 space-y-4"
            onSubmit={(event) => {
              event.preventDefault();
              if (ready) void submit();
            }}
          >
            <div className="grid gap-4 md:grid-cols-3">
              <Section title={t("replacedTitle")} items={REPLACED.map((k) => t(`replaced.${k}`))} testId="anonymise-replaced" />
              <Section title={t("deletedTitle")} items={DELETED.map((k) => t(`deleted.${k}`))} testId="anonymise-deleted" />
              <Section title={t("keptTitle")} items={KEPT.map((k) => t(`kept.${k}`))} testId="anonymise-kept" />
            </div>
            <ul className="space-y-1 text-sm">
              <li className="font-medium text-strong" data-testid="kept-summary">
                {t("keptSummary")}
              </li>
              <li className="text-muted">{t("balances")}</li>
              <li className="text-muted">{t("again")}</li>
            </ul>

            <Field
              label={t("reason")}
              htmlFor="anonymise-reason"
              required
              hint={t("reasonHint")}
              error={touched.reason && problem ? (problem === "tooShort" ? t("reasonShort") : t("reasonLong")) : undefined}
            >
              <Textarea
                id="anonymise-reason"
                ref={reasonRef}
                value={reason}
                rows={3}
                onChange={(e) => setReason(e.target.value)}
                onBlur={() => setTouched((s) => ({ ...s, reason: true }))}
                aria-invalid={touched.reason && !!problem}
                aria-describedby={touched.reason && problem ? "anonymise-reason-error" : "anonymise-reason-hint"}
              />
              <p className={reason.trim().length > REASON_MAX ? "text-sm font-medium text-danger" : "text-sm text-muted"} aria-live="off">
                {t("count", { count: [...reason.trim()].length, max: REASON_MAX })}
              </p>
            </Field>

            <Field label={t("confirmLabel")} htmlFor="anonymise-confirm" required error={touched.confirm && confirm.trim() && !matches ? t("confirmWrong") : undefined}>
              <p className="text-sm text-muted">
                {t("confirmHint")}: <code className="select-all font-mono text-strong [overflow-wrap:anywhere]" data-testid="anonymise-id">{id}</code>
              </p>
              <Input
                id="anonymise-confirm"
                value={confirm}
                onChange={(e) => setConfirm(e.target.value)}
                onBlur={() => setTouched((s) => ({ ...s, confirm: true }))}
                autoComplete="off"
                autoCapitalize="off"
                spellCheck={false}
                dir="ltr"
                aria-invalid={touched.confirm && !!confirm.trim() && !matches}
              />
            </Field>

            {error ? <ProblemAlert error={error} kind={kind} testId={isBlockingKind(kind) ? "anonymise-blocked" : "anonymise-error"} /> : null}

            <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
              <DialogClose asChild>
                <Button variant="secondary" disabled={mutation.isPending}>
                  {tc("cancel")}
                </Button>
              </DialogClose>
              <Button type="submit" variant="danger" disabled={!ready} loading={mutation.isPending} data-testid="anonymise-submit">
                <Trash2 aria-hidden /> {t("submit")}
              </Button>
            </div>
          </form>
        )}
      </ModalContent>
    </Dialog>
  );
}

/** The confirm step before a data export; a refusal such as the hourly limit is said plainly inside the dialog. */
function ExportDialog({ id, open, onOpenChange }: { id: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  const t = useTranslations("privacy.export");
  const tc = useTranslations("customers.common");
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<ApiError | null>(null);

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      await exportCustomerData(id);
      toast.success(t("done"));
      onOpenChange(false);
    } catch (e) {
      setError(e instanceof ApiError ? e : new ApiError({ status: 0, code: "NETWORK" }));
    } finally {
      setBusy(false);
    }
  };

  return (
    <ConfirmDialog
      open={open}
      onOpenChange={(next) => {
        if (!next) setError(null);
        onOpenChange(next);
      }}
      title={t("title")}
      description={t("description")}
      confirmLabel={t("confirm")}
      cancelLabel={tc("cancel")}
      closeLabel={tc("close")}
      loading={busy}
      onConfirm={run}
    >
      {error ? <ProblemAlert error={error} kind={privacyErrorKind(error.problem)} testId="export-error" /> : null}
    </ConfirmDialog>
  );
}

/** The two privacy actions, each only for the staff member who holds its permission. An anonymised customer gets a note instead. */
export function PrivacyCard({ id, canExport, canAnonymise: mayAnonymise, anonymised, onAnonymised }: { id: string; canExport: boolean; canAnonymise: boolean; anonymised: boolean; onAnonymised: (at: string) => void }) {
  const t = useTranslations("privacy");
  const [exportOpen, setExportOpen] = React.useState(false);
  const [anonymiseOpen, setAnonymiseOpen] = React.useState(false);
  return (
    <Card data-testid="privacy-card">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ShieldCheck className="size-5 text-muted" aria-hidden />
          {t("card.title")}
        </CardTitle>
        <p className="mt-1 text-sm text-muted">{t("card.description")}</p>
      </CardHeader>
      <CardContent className="space-y-5">
        {anonymised ? (
          <p className="flex items-start gap-2 text-sm text-muted" data-testid="privacy-anonymised-note">
            <UserX className="mt-0.5 size-4 shrink-0" aria-hidden />
            {t("card.anonymisedNote")}
          </p>
        ) : (
          <>
            {canExport ? (
              <div className="space-y-2">
                <p className="text-sm text-muted">{t("export.help")}</p>
                <Button variant="secondary" onClick={() => setExportOpen(true)} data-testid="privacy-export">
                  <Download aria-hidden /> {t("export.button")}
                </Button>
                <ExportDialog id={id} open={exportOpen} onOpenChange={setExportOpen} />
              </div>
            ) : null}
            {mayAnonymise ? (
              <div className="space-y-2">
                <p className="text-sm text-muted">{t("anonymise.help")}</p>
                <Button variant="danger" onClick={() => setAnonymiseOpen(true)} data-testid="privacy-anonymise">
                  <UserX aria-hidden /> {t("anonymise.button")}
                </Button>
              </div>
            ) : null}
          </>
        )}
      </CardContent>
      {/* Outside the branches above so the result stays on screen after the card switches to its anonymised note. */}
      {anonymiseOpen && mayAnonymise ? <AnonymiseDialog id={id} open={anonymiseOpen} onOpenChange={setAnonymiseOpen} onDone={onAnonymised} /> : null}
    </Card>
  );
}
