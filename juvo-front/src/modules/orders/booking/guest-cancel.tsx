"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Ban } from "lucide-react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Alert } from "@/components/ui/surfaces";
import { Field, Textarea } from "@/components/ui/form";
import { toast } from "@/components/ui/toaster";
import { api, unwrap } from "@/lib/api/client";
import { problemOf, useOrderErrorText, type TrackQuery, type Tracking } from "../api";

/** `POST /store/orders/track/cancel` with the same proof the tracking page used (number and mobile, or the private token). */
function useGuestCancel(query: TrackQuery) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (reason: string | null): Promise<Tracking> =>
      unwrap(
        await api.POST("/api/v1/store/orders/track/cancel", {
          body: { number: "number" in query ? query.number : null, mobile: "mobile" in query ? query.mobile : null, token: "token" in query ? query.token : null, reason },
        }),
      ),
    // The answer is the new tracking view: show it at once, no second request.
    onSuccess: (data) => queryClient.setQueryData(["orders", "track", query], data),
  });
}

/**
 * "Cancel my order" for a guest, offered while the API says the order can still be cancelled (it has not left the shop). The dialog
 * says plainly what happens; if the order moved on in the meantime the API refuses (ORDER.CANNOT_CANCEL) and the page is refreshed.
 */
export function GuestCancel({ query, number, paid }: { query: TrackQuery; number: string; paid: boolean }) {
  const t = useTranslations("booking.guestCancel");
  const tc = useTranslations("booking.common");
  const errorText = useOrderErrorText();
  const queryClient = useQueryClient();
  const cancel = useGuestCancel(query);
  const [open, setOpen] = React.useState(false);
  const [reason, setReason] = React.useState("");
  const [failure, setFailure] = React.useState<string | null>(null);

  const run = async () => {
    setFailure(null);
    try {
      await cancel.mutateAsync(reason.trim() || null);
      toast.success(t("done", { number }));
      setOpen(false);
      setReason("");
    } catch (error) {
      setFailure(errorText(error));
      // Too late (the order left the shop): reload so the page stops offering it.
      if (problemOf(error)?.code === "ORDER.CANNOT_CANCEL") void queryClient.invalidateQueries({ queryKey: ["orders", "track", query] });
    }
  };

  return (
    <>
      <Button
        variant="secondary"
        onClick={() => {
          setFailure(null);
          setOpen(true);
        }}
        data-testid="guest-cancel"
      >
        <Ban aria-hidden /> {t("button")}
      </Button>
      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        title={t("title", { number })}
        description={t("text")}
        confirmLabel={t("confirm")}
        cancelLabel={t("keep")}
        closeLabel={tc("close")}
        destructive
        loading={cancel.isPending}
        onConfirm={run}
      >
        <div className="space-y-3">
          {paid ? <Alert variant="warning">{t("paidNote")}</Alert> : null}
          <Field label={t("reason")} htmlFor="guest-cancel-reason" hint={tc("optional")}>
            <Textarea id="guest-cancel-reason" rows={3} maxLength={300} value={reason} onChange={(e) => setReason(e.target.value)} />
          </Field>
          {failure ? (
            <Alert variant="danger" data-testid="guest-cancel-error">
              {failure}
            </Alert>
          ) : null}
        </div>
      </ConfirmDialog>
    </>
  );
}
