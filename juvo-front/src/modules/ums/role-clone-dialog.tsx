"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, DialogTrigger, ModalContent } from "@/components/ui/overlays";
import { toast } from "@/components/ui/toaster";
import { useCloneRole, useReportError, type RoleDetail, type RoleListItem } from "./api";

/** Copies a role and its permissions under a new name, then opens the copy. */
export function CloneRoleDialog({ source, children }: { source: Pick<RoleDetail | RoleListItem, "id" | "name">; children: React.ReactElement }) {
  const t = useTranslations("ums.roles.cloneDialog");
  const tc = useTranslations("ums.common");
  const router = useRouter();
  const report = useReportError();
  const clone = useCloneRole();
  const [open, setOpen] = React.useState(false);
  const [name, setName] = React.useState("");
  const [error, setError] = React.useState<string | undefined>();

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!name.trim()) {
      setError(t("nameRequired"));
      return;
    }
    try {
      const copy = await clone.mutateAsync({ id: source.id, name: name.trim() });
      toast.success(t("done"));
      setOpen(false);
      router.push(`/admin/ums/roles/${copy.id}`);
    } catch (e) {
      setError(report(e).name);
    }
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (next) {
          setName(t("defaultName", { name: source.name }));
          setError(undefined);
        }
      }}
    >
      <DialogTrigger asChild>{children}</DialogTrigger>
      <ModalContent size="sm" closeLabel={tc("close")}>
        <form onSubmit={submit} noValidate className="space-y-4">
          <div>
            <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title", { name: source.name })}</DialogTitle>
            <DialogDescription className="mt-1 text-muted">{t("description")}</DialogDescription>
          </div>
          <Field label={t("name")} htmlFor="clone-name" error={error} required>
            <Input id="clone-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={100} autoFocus aria-invalid={!!error} />
          </Field>
          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <Button type="button" variant="secondary" onClick={() => setOpen(false)}>
              {tc("cancel")}
            </Button>
            <Button type="submit" loading={clone.isPending}>
              {t("submit")}
            </Button>
          </div>
        </form>
      </ModalContent>
    </Dialog>
  );
}
