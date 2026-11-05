"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Input, Switch } from "@/components/ui/form";
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { usePermissions } from "@/components/providers/session-provider";
import { useReportError, useUpdateUser, type UserDetail } from "./api";
import { useDateTime } from "./shared";

/** Name, phone and global scope (editable with ums.user.edit) next to the read-only account facts. */
export function UserOverview({ user }: { user: UserDetail }) {
  // A new key starts the form afresh when the saved values change (after a save, or an edit by someone else).
  return <OverviewContent key={`${user.fullName}|${user.phone}|${user.isGlobalScope}`} user={user} />;
}

function OverviewContent({ user }: { user: UserDetail }) {
  const t = useTranslations("ums.users.overview");
  const tu = useTranslations("ums.users");
  const dateTime = useDateTime();
  const report = useReportError();
  const update = useUpdateUser(user.id);
  const { can } = usePermissions();
  const editable = can("ums.user.edit", "anywhere");
  const [isGlobal, setIsGlobal] = React.useState(user.isGlobalScope);

  const schema = React.useMemo(() => z.object({ fullName: z.string().trim().min(1, t("nameRequired")).max(150), phone: z.string().trim().max(30).optional() }), [t]);
  const { register, handleSubmit, formState, setError } = useForm<z.infer<typeof schema>>({
    resolver: zodResolver(schema),
    defaultValues: { fullName: user.fullName, phone: user.phone ?? "" },
  });

  const onSubmit = handleSubmit(async (values) => {
    try {
      await update.mutateAsync({ fullName: values.fullName, phone: values.phone || null, isGlobalScope: isGlobal });
      toast.success(t("saved"));
    } catch (error) {
      for (const [field, message] of Object.entries(report(error))) setError(field as "fullName", { message });
    }
  });

  const dirty = formState.isDirty || isGlobal !== user.isGlobalScope;

  return (
    <div className="grid gap-4 lg:grid-cols-[3fr_2fr]">
      <Card>
        <CardHeader>
          <CardTitle>{t("details")}</CardTitle>
          <CardDescription>{editable ? t("detailsHint") : t("readOnly")}</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={onSubmit} noValidate className="space-y-4">
            <Field label={t("fullName")} htmlFor="uo-name" error={formState.errors.fullName?.message} required>
              <Input id="uo-name" readOnly={!editable} aria-invalid={!!formState.errors.fullName} {...register("fullName")} />
            </Field>
            <Field label={t("phone")} htmlFor="uo-phone" error={formState.errors.phone?.message}>
              <Input id="uo-phone" type="tel" inputMode="tel" readOnly={!editable} {...register("phone")} />
            </Field>
            <ChoiceRow htmlFor="uo-global">
              <Switch id="uo-global" checked={isGlobal} onCheckedChange={setIsGlobal} disabled={!editable} />
              <span>
                <span className="block font-medium">{tu("create.global")}</span>
                <span className="block text-sm text-muted">{tu("create.globalHint")}</span>
              </span>
            </ChoiceRow>
            {editable ? (
              <div className="flex justify-end">
                <Button type="submit" loading={update.isPending} disabled={!dirty}>
                  {t("save")}
                </Button>
              </div>
            ) : null}
          </form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("account")}</CardTitle>
        </CardHeader>
        <CardContent>
          <dl className="space-y-3">
            <Fact label={t("email")} value={user.email} />
            <Fact label={t("userName")} value={user.userName} />
            <Fact label={t("created")} value={dateTime(user.createdAt)} />
            <Fact label={t("lastLogin")} value={user.lastLoginAt ? dateTime(user.lastLoginAt) : tu("never")} />
            <div>
              <dt className="text-sm text-muted">{t("security")}</dt>
              <dd className="mt-1 flex flex-wrap gap-1.5">
                <Badge variant={user.twoFactorEnabled ? "success" : "outline"}>{user.twoFactorEnabled ? tu("twoFactorOn") : tu("twoFactorOff")}</Badge>
                {user.mustChangePassword ? <Badge variant="warning">{t("mustChange")}</Badge> : null}
                {user.isLockedOut ? <Badge variant="danger">{tu("locked")}</Badge> : null}
              </dd>
            </div>
          </dl>
        </CardContent>
      </Card>
    </div>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="font-medium text-strong [overflow-wrap:anywhere]">{value}</dd>
    </div>
  );
}
