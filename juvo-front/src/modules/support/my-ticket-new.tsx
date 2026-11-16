"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, CardContent } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import type { UploadedFile } from "@/components/data/attachment-picker";
import { useDateTime } from "@/components/data/list-kit";
import { api, ApiError, unwrap } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useQuery } from "@tanstack/react-query";
import { useMyOrders } from "@/modules/orders/api";
import { useCreateMyTicket } from "./api";
import { TicketAttachments, useCategoryOptions } from "./common";
import { MESSAGE_MAX, SUBJECT_MAX, hasErrors, validateMyTicket } from "./logic";

const NO_ORDER = "";

/** The customer's new request: subject, topic, optional order from their own orders, message and attachments. `orderId` pre-selects the order. */
export function MyTicketNew({ orderId }: { orderId?: string }) {
  const t = useTranslations("support.new");
  const tc = useTranslations("support.common");
  const router = useRouter();
  const problemMessage = useProblemMessage();
  const dateTime = useDateTime();
  const categories = useCategoryOptions();
  const create = useCreateMyTicket();
  const orders = useMyOrders({ page: 1, pageSize: 50 });
  const listed = orders.data?.items.find((o) => o.id === orderId);
  // The pre-selected order may be older than the 50 listed ones: ask for it on its own, only then.
  const lookup = !!orderId && orders.isSuccess && !listed;
  const single = useQuery({ queryKey: ["orders", "mine", orderId], enabled: lookup, retry: false, queryFn: async () => unwrap(await api.GET("/api/v1/me/orders/{id}", { params: { path: { id: orderId! } } })) });

  const [subject, setSubject] = React.useState("");
  const [message, setMessage] = React.useState("");
  const [category, setCategory] = React.useState(orderId ? "order" : "other");
  const [chosen, setChosen] = React.useState<string | null>(null);
  const [files, setFiles] = React.useState<UploadedFile[]>([]);
  const [errors, setErrors] = React.useState<ReturnType<typeof validateMyTicket>>({});
  const [problem, setProblem] = React.useState<string | null>(null);
  const sending = React.useRef(false);
  const [done, setDone] = React.useState(false);

  const preset = listed?.number ?? single.data?.number;
  const orderNumber = chosen ?? preset ?? NO_ORDER;

  const options = React.useMemo(() => {
    const rows = (orders.data?.items ?? []).map((o) => ({ value: o.number, label: o.number, hint: dateTime(o.placedAt) }));
    if (preset && !rows.some((r) => r.value === preset)) rows.unshift({ value: preset, label: preset, hint: "" });
    return [{ value: NO_ORDER, label: t("noOrder"), hint: "" }, ...rows];
  }, [orders.data, preset, dateTime, t]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    // One request at a time: a double click or a slow connection never files the ticket twice.
    if (sending.current || done) return;
    const found = validateMyTicket({ subject, message, category, orderNumber });
    setErrors(found);
    setProblem(null);
    if (hasErrors(found)) return;
    sending.current = true;
    try {
      const ticket = await create.mutateAsync({ subject, message, category, orderNumber, attachmentPaths: files.map((f) => f.path!).filter(Boolean) });
      setDone(true);
      toast.success(t("sent", { number: ticket.number }));
      router.push(`/account/support/${ticket.id}`);
    } catch (e) {
      setProblem(problemMessage(e instanceof ApiError ? e.problem : null));
      sending.current = false;
    }
  };

  const err = (key: "subject" | "message") => (errors[key] ? t(`errors.${errors[key]}`, { max: key === "subject" ? SUBJECT_MAX : MESSAGE_MAX }) : undefined);

  return (
    <Card>
      <CardContent className="pt-6">
        <form onSubmit={submit} noValidate className="max-w-2xl space-y-4" data-testid="my-ticket-form">
          {problem ? <Alert variant="danger">{problem}</Alert> : null}
          <Field label={t("subject")} htmlFor="mt-subject" required error={err("subject")}>
            <Input id="mt-subject" value={subject} maxLength={SUBJECT_MAX} onChange={(e) => setSubject(e.target.value)} aria-invalid={!!errors.subject} autoComplete="off" />
          </Field>
          <Field label={t("category")} htmlFor="mt-category">
            <Select id="mt-category" value={category} onValueChange={setCategory} options={categories} placeholder={t("category")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
          <Field label={t("order")} htmlFor="mt-order" hint={t("orderHint")}>
            <Select id="mt-order" value={orderNumber} onValueChange={(v) => setChosen(v)} options={options} placeholder={t("noOrder")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} disabled={orders.isPending} />
          </Field>
          <Field label={t("message")} htmlFor="mt-message" required error={err("message")} hint={t("messageHint")}>
            <Textarea id="mt-message" value={message} maxLength={MESSAGE_MAX} onChange={(e) => setMessage(e.target.value)} className="min-h-36" aria-invalid={!!errors.message} />
          </Field>
          <TicketAttachments scope="customer" idBase="mt" added={files} onAdded={setFiles} onError={(e) => setProblem(problemMessage(e instanceof ApiError ? e.problem : null))} disabled={create.isPending} />
          <Button type="submit" loading={create.isPending || done} data-testid="my-ticket-submit">
            {t("submit")}
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}
