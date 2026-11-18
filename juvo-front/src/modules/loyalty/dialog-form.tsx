"use client";

import * as React from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert } from "@/components/ui/surfaces";

/**
 * A form in a dialog: title, text, fields, an error line and Cancel / Submit. The body should be mounted only while the dialog is open
 * (callers render the dialog conditionally), so every opening starts with fresh fields.
 */
export function DialogForm({
  open,
  onOpenChange,
  title,
  description,
  submitLabel,
  cancelLabel,
  closeLabel,
  loading,
  error,
  destructive,
  size = "sm",
  onSubmit,
  children,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  submitLabel: string;
  cancelLabel: string;
  closeLabel: string;
  loading?: boolean;
  error?: string | null;
  destructive?: boolean;
  size?: "sm" | "md" | "lg";
  onSubmit: () => void;
  children?: React.ReactNode;
}) {
  return (
    <Dialog open={open} onOpenChange={(next) => (loading ? undefined : onOpenChange(next))}>
      <ModalContent size={size} closeLabel={closeLabel}>
        <form
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            onSubmit();
          }}
        >
          <DialogTitle className="font-display text-xl font-semibold text-strong">{title}</DialogTitle>
          {description ? <DialogDescription className="mt-2 text-muted">{description}</DialogDescription> : null}
          <div className="mt-4 space-y-4">
            {children}
            {error ? (
              <Alert variant="danger" data-testid="dialog-error">
                {error}
              </Alert>
            ) : null}
          </div>
          <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <DialogClose asChild>
              <Button type="button" variant="secondary" disabled={loading}>
                {cancelLabel}
              </Button>
            </DialogClose>
            <Button type="submit" variant={destructive ? "danger" : "primary"} loading={loading} data-testid="dialog-confirm">
              {submitLabel}
            </Button>
          </div>
        </form>
      </ModalContent>
    </Dialog>
  );
}
