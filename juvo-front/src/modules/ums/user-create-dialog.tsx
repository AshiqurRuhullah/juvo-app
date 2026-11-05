"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { UserPlus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Input, PasswordInput, Switch } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, DialogTrigger, ModalContent } from "@/components/ui/overlays";
import { toast } from "@/components/ui/toaster";
import { AssignmentPicker } from "./assignment-picker";
import { useAllBranches, useAllRoles, useCreateUser, useReportError } from "./api";
import { SecretReveal } from "./secret-reveal";

type Assignment = { branchId: string | null; roleId: string };

/** New staff account. Leave the password empty and the API makes a temporary one, shown once; the person must change it at first sign-in. */
export function UserCreateDialog() {
  const t = useTranslations("ums.users.create");
  const tc = useTranslations("ums.common");
  const report = useReportError();
  const create = useCreateUser();
  const branches = useAllBranches();
  const roles = useAllRoles();

  const [open, setOpen] = React.useState(false);
  const [assignments, setAssignments] = React.useState<Assignment[]>([]);
  const [isGlobal, setIsGlobal] = React.useState(false);
  const [created, setCreated] = React.useState<{ id: string; name: string; password: string | null } | null>(null);

  const schema = React.useMemo(
    () =>
      z.object({
        fullName: z.string().trim().min(1, t("errors.nameRequired")).max(150),
        email: z.string().trim().min(1, t("errors.emailRequired")).email(t("errors.emailInvalid")),
        userName: z.string().trim().max(100).optional(),
        phone: z.string().trim().max(30).optional(),
        password: z.string().optional(),
      }),
    [t],
  );
  const form = useForm<z.infer<typeof schema>>({ resolver: zodResolver(schema), defaultValues: { fullName: "", email: "", userName: "", phone: "", password: "" } });
  const { register, handleSubmit, formState, setError, reset } = form;

  const close = (next: boolean) => {
    setOpen(next);
    if (!next) {
      reset();
      setAssignments([]);
      setIsGlobal(false);
      setCreated(null);
    }
  };

  const branchName = (id: string | null) => (id === null ? tc("everywhere") : (branches.data?.items.find((b) => b.id === id)?.code ?? id));
  const roleName = (id: string) => roles.data?.items.find((r) => r.id === id)?.name ?? id;

  const onSubmit = handleSubmit(async (values) => {
    try {
      const result = await create.mutateAsync({
        fullName: values.fullName,
        email: values.email,
        userName: values.userName || null,
        phone: values.phone || null,
        password: values.password || null,
        isGlobalScope: isGlobal,
        assignments: assignments.map((a) => ({ branchId: a.branchId, roleId: a.roleId })),
      });
      toast.success(t("created"));
      setCreated({ id: result.user.id, name: result.user.fullName, password: result.temporaryPassword ?? null });
    } catch (error) {
      for (const [field, message] of Object.entries(report(error))) setError(field as "email", { message });
    }
  });

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogTrigger asChild>
        <Button>
          <UserPlus aria-hidden /> {t("button")}
        </Button>
      </DialogTrigger>
      <ModalContent size="lg" closeLabel={tc("close")}>
        {created ? (
          <div className="space-y-5">
            <DialogTitle className="font-display text-xl font-semibold text-strong">{t("doneTitle", { name: created.name })}</DialogTitle>
            <DialogDescription className="text-muted">{created.password ? t("doneWithPassword") : t("doneNoPassword")}</DialogDescription>
            {created.password ? <SecretReveal label={t("temporaryPassword")} value={created.password} /> : null}
            <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
              <Button variant="secondary" onClick={() => close(false)}>
                {tc("close")}
              </Button>
              <Button asChild>
                <Link href={`/admin/ums/users/${created.id}`}>{t("openUser")}</Link>
              </Button>
            </div>
          </div>
        ) : (
          <form onSubmit={onSubmit} noValidate className="space-y-5">
            <div>
              <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
              <DialogDescription className="mt-1 text-muted">{t("description")}</DialogDescription>
            </div>

            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={t("fullName")} htmlFor="cu-name" error={formState.errors.fullName?.message} required>
                <Input id="cu-name" autoComplete="off" aria-invalid={!!formState.errors.fullName} {...register("fullName")} />
              </Field>
              <Field label={t("email")} htmlFor="cu-email" error={formState.errors.email?.message} required>
                <Input id="cu-email" type="email" inputMode="email" autoCapitalize="none" autoComplete="off" aria-invalid={!!formState.errors.email} {...register("email")} />
              </Field>
              <Field label={t("userName")} htmlFor="cu-user" hint={t("userNameHint")} error={formState.errors.userName?.message}>
                <Input id="cu-user" autoCapitalize="none" autoComplete="off" {...register("userName")} />
              </Field>
              <Field label={t("phone")} htmlFor="cu-phone" error={formState.errors.phone?.message}>
                <Input id="cu-phone" type="tel" inputMode="tel" autoComplete="off" {...register("phone")} />
              </Field>
              <Field label={t("password")} htmlFor="cu-pass" hint={t("passwordHint")} error={formState.errors.password?.message} className="sm:col-span-2">
                <PasswordInput id="cu-pass" autoComplete="new-password" showLabel={tc("show")} hideLabel={tc("hide")} {...register("password")} />
              </Field>
            </div>

            <ChoiceRow htmlFor="cu-global">
              <Switch id="cu-global" checked={isGlobal} onCheckedChange={setIsGlobal} />
              <span>
                <span className="block font-medium">{t("global")}</span>
                <span className="block text-sm text-muted">{t("globalHint")}</span>
              </span>
            </ChoiceRow>

            <fieldset className="space-y-3">
              <legend className="mb-1 font-semibold text-strong">{t("assignments")}</legend>
              {assignments.length > 0 ? (
                <ul className="flex flex-wrap gap-2">
                  {assignments.map((a, index) => (
                    <li key={`${a.branchId}-${a.roleId}`} className="inline-flex items-center gap-1 rounded-full bg-primary-soft py-0.5 ps-3 pe-1 text-sm font-medium text-primary-dark">
                      {branchName(a.branchId)} · {roleName(a.roleId)}
                      <button type="button" className="inline-flex size-11 items-center justify-center rounded-full" aria-label={t("removeAssignment", { branch: branchName(a.branchId), role: roleName(a.roleId) })} onClick={() => setAssignments((list) => list.filter((_, i) => i !== index))}>
                        <X className="size-4" aria-hidden />
                      </button>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="text-sm text-muted">{t("noAssignments")}</p>
              )}
              <AssignmentPicker
                idPrefix="cu"
                allowEverywhere={isGlobal}
                onAdd={(branchId, roleId) => setAssignments((list) => (list.some((a) => a.branchId === branchId && a.roleId === roleId) ? list : [...list, { branchId, roleId }]))}
              />
            </fieldset>

            <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
              <Button type="button" variant="secondary" onClick={() => close(false)}>
                {tc("cancel")}
              </Button>
              <Button type="submit" loading={create.isPending}>
                {t("submit")}
              </Button>
            </div>
          </form>
        )}
      </ModalContent>
    </Dialog>
  );
}
