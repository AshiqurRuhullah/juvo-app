"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Controller, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Power, PowerOff, Star } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { usePermissions } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { BRANCH_TYPES, useAllBranches, useBranch, useBranchAction, useBranchUsers, useCreateBranch, useReportError, useUpdateBranch, type BranchAction, type BranchDto } from "./api";

const MapPicker = dynamic(() => import("./map-picker"), { ssr: false, loading: () => <Skeleton className="h-72 w-full sm:h-80" /> });

/** Empty text means "no value". */
const blank = (value: string | undefined) => (value && value.trim() ? value.trim() : null);

/** Create a branch (no id) or edit one. Activate, deactivate and set default are on the edit page. */
export function BranchEditor({ id }: { id?: string }) {
  const t = useTranslations("ums.branches");
  const tn = useTranslations("nav");
  const router = useRouter();
  const report = useReportError();
  const { can } = usePermissions();
  const query = useBranch(id ?? "");
  const all = useAllBranches();
  const create = useCreateBranch();
  const update = useUpdateBranch(id ?? "");

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.branches"), href: "/admin/ums/branches" }, { label: id ? (query.data?.nameEn ?? "...") : t("new") }];

  if (id && query.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" />
      </>
    );
  }
  if (id && (query.isError || !query.data)) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFoundTitle")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFoundTitle") : t("loadFailed")}
          description={notFound ? t("notFoundBody") : undefined}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/ums/branches">{t("back")}</Link>
            </Button>
          }
        />
      </>
    );
  }

  const branch = query.data;
  const canEdit = id ? can("ums.branch.edit", "anywhere") : can("ums.branch.create", "anywhere");

  return (
    <>
      <PageHeader title={id ? (branch?.nameEn ?? "") : t("newTitle")} description={id ? branch?.code : t("newDescription")} crumbs={crumbs} actions={branch ? <BranchActions branch={branch} /> : undefined} />
      {branch ? (
        <div className="-mt-3 mb-5 flex flex-wrap gap-1.5">
          {branch.isActive ? <Badge variant="success">{t("active")}</Badge> : <Badge variant="outline">{t("inactive")}</Badge>}
          {branch.isDefault ? <Badge variant="brand">{t("default")}</Badge> : null}
          <Badge variant="neutral">{t(`types.${branch.type}`)}</Badge>
        </div>
      ) : null}

      <div className="space-y-4">
        <BranchForm
          key={branch ? `${branch.id}-${branch.nameEn}-${branch.latitude}-${branch.longitude}-${branch.addressLine}-${branch.phone}` : "new"}
          branch={branch}
          editable={canEdit}
          others={(all.data?.items ?? []).filter((b) => b.id !== id)}
          submitting={create.isPending || update.isPending}
          onSubmit={async (values) => {
            try {
              if (!id) {
                const created = await create.mutateAsync(values);
                toast.success(t("created"));
                router.push(`/admin/ums/branches/${created.id}`);
              } else {
                const { code, ...rest } = values;
                void code;
                await update.mutateAsync({ ...rest, defaultDeliveryZoneId: branch?.defaultDeliveryZoneId ?? null, courierStoreIds: values.courierStoreIds ?? branch?.courierStoreIds ?? null });
                toast.success(t("saved"));
              }
              return {};
            } catch (error) {
              return report(error);
            }
          }}
        />
        {id ? <BranchUsers id={id} /> : null}
      </div>
    </>
  );
}

type FormValues = {
  code: string;
  nameEn: string;
  nameBn: string;
  type: BranchDto["type"];
  parentBranchId: string;
  addressLine: string;
  phone: string;
  email: string;
  workingHours: string;
  timeZoneId: string;
  defaultCurrency: string;
  orderPrefix: string;
  pickupAddress: string;
  courierStoreIds: string;
  latitude: string;
  longitude: string;
};

function BranchForm({
  branch,
  editable,
  others,
  submitting,
  onSubmit,
}: {
  branch: BranchDto | undefined;
  editable: boolean;
  others: BranchDto[];
  submitting: boolean;
  onSubmit: (values: Parameters<ReturnType<typeof useCreateBranch>["mutateAsync"]>[0]) => Promise<Record<string, string>>;
}) {
  const t = useTranslations("ums.branches");
  const tc = useTranslations("ums.common");

  const schema = React.useMemo(
    () =>
      z.object({
        code: branch ? z.string() : z.string().trim().min(2, t("errors.codeRequired")).max(20).regex(/^[A-Za-z0-9-]+$/, t("errors.codeFormat")),
        nameEn: z.string().trim().min(1, t("errors.nameRequired")).max(150),
        nameBn: z.string().max(150),
        type: z.enum(BRANCH_TYPES as [string, ...string[]]),
        parentBranchId: z.string(),
        addressLine: z.string().max(300),
        phone: z.string().max(30),
        email: z.string().max(150).refine((v) => !v.trim() || z.string().email().safeParse(v.trim()).success, t("errors.emailInvalid")),
        workingHours: z.string().max(200),
        timeZoneId: z.string().trim().min(1, t("errors.timeZoneRequired")),
        defaultCurrency: z.string().trim().length(3, t("errors.currency")),
        orderPrefix: z.string().max(10),
        pickupAddress: z.string().max(300),
        courierStoreIds: z.string().max(200),
        latitude: z.string().refine((v) => !v.trim() || (Number.isFinite(Number(v)) && Math.abs(Number(v)) <= 90), t("errors.latitude")),
        longitude: z.string().refine((v) => !v.trim() || (Number.isFinite(Number(v)) && Math.abs(Number(v)) <= 180), t("errors.longitude")),
      }),
    [t, branch],
  );

  const { register, control, handleSubmit, formState, setError, setValue } = useForm<FormValues>({
    resolver: zodResolver(schema) as never,
    defaultValues: {
      code: branch?.code ?? "",
      nameEn: branch?.nameEn ?? "",
      nameBn: branch?.nameBn ?? "",
      type: branch?.type ?? "Shop",
      parentBranchId: branch?.parentBranchId ?? "",
      addressLine: branch?.addressLine ?? "",
      phone: branch?.phone ?? "",
      email: branch?.email ?? "",
      workingHours: branch?.workingHours ?? "",
      timeZoneId: branch?.timeZoneId ?? "Asia/Dhaka",
      defaultCurrency: branch?.defaultCurrency ?? "BDT",
      orderPrefix: branch?.orderPrefix ?? "",
      pickupAddress: branch?.pickupAddress ?? "",
      courierStoreIds: branch?.courierStoreIds ?? "",
      latitude: branch?.latitude != null ? String(branch.latitude) : "",
      longitude: branch?.longitude != null ? String(branch.longitude) : "",
    },
  });

  const [lat, lng] = useWatch({ control, name: ["latitude", "longitude"] });
  const latNum = lat.trim() && Number.isFinite(Number(lat)) ? Number(lat) : null;
  const lngNum = lng.trim() && Number.isFinite(Number(lng)) ? Number(lng) : null;

  const submit = handleSubmit(async (v) => {
    const failures = await onSubmit({
      code: v.code.trim().toUpperCase(),
      nameEn: v.nameEn.trim(),
      nameBn: blank(v.nameBn),
      type: v.type,
      parentBranchId: v.parentBranchId || null,
      addressLine: blank(v.addressLine),
      latitude: blank(v.latitude) ? Number(v.latitude) : null,
      longitude: blank(v.longitude) ? Number(v.longitude) : null,
      phone: blank(v.phone),
      email: blank(v.email),
      workingHours: blank(v.workingHours),
      timeZoneId: v.timeZoneId.trim(),
      defaultCurrency: v.defaultCurrency.trim().toUpperCase(),
      orderPrefix: blank(v.orderPrefix),
      pickupAddress: blank(v.pickupAddress),
      courierStoreIds: blank(v.courierStoreIds),
      defaultDeliveryZoneId: null,
    });
    for (const [field, message] of Object.entries(failures)) setError(field as keyof FormValues, { message });
  });

  const err = (name: keyof FormValues) => formState.errors[name]?.message as string | undefined;
  const ro = !editable;

  return (
    <form onSubmit={submit} noValidate className="space-y-4">
      {ro ? <Alert variant="info">{t("readOnly")}</Alert> : null}

      <Card>
        <CardHeader>
          <CardTitle>{t("sections.identity")}</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 md:grid-cols-2">
          <Field label={t("fields.code")} htmlFor="b-code" hint={branch ? t("codeLocked") : t("codeHint")} error={err("code")} required>
            <Input id="b-code" readOnly={!!branch || ro} autoCapitalize="characters" aria-invalid={!!err("code")} {...register("code")} />
          </Field>
          <Field label={t("fields.type")} htmlFor="b-type" required>
            <Controller
              control={control}
              name="type"
              render={({ field }) => (
                <Select
                  id="b-type"
                  value={field.value}
                  onValueChange={field.onChange}
                  disabled={ro}
                  options={BRANCH_TYPES.map((type) => ({ value: type, label: t(`types.${type}`) }))}
                  placeholder={t("fields.type")}
                  searchPlaceholder={tc("search")}
                  emptyText={tc("noMatch")}
                />
              )}
            />
          </Field>
          <Field label={t("fields.nameEn")} htmlFor="b-name" error={err("nameEn")} required>
            <Input id="b-name" readOnly={ro} aria-invalid={!!err("nameEn")} {...register("nameEn")} />
          </Field>
          <Field label={t("fields.nameBn")} htmlFor="b-name-bn" error={err("nameBn")}>
            <Input id="b-name-bn" lang="bn" readOnly={ro} {...register("nameBn")} />
          </Field>
          <Field label={t("fields.parent")} htmlFor="b-parent" hint={t("parentHint")}>
            <Controller
              control={control}
              name="parentBranchId"
              render={({ field }) => (
                <Select
                  id="b-parent"
                  value={field.value || "__none"}
                  onValueChange={(v) => field.onChange(v === "__none" ? "" : v)}
                  disabled={ro}
                  options={[{ value: "__none", label: t("noParent") }, ...others.map((b) => ({ value: b.id, label: `${b.code} · ${b.nameEn}` }))]}
                  placeholder={t("fields.parent")}
                  searchPlaceholder={tc("search")}
                  emptyText={tc("noMatch")}
                />
              )}
            />
          </Field>
          <Field label={t("fields.orderPrefix")} htmlFor="b-prefix" hint={t("orderPrefixHint")} error={err("orderPrefix")}>
            <Input id="b-prefix" readOnly={ro} autoCapitalize="characters" {...register("orderPrefix")} />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("sections.contact")}</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 md:grid-cols-2">
          <Field label={t("fields.phone")} htmlFor="b-phone" error={err("phone")}>
            <Input id="b-phone" type="tel" inputMode="tel" readOnly={ro} {...register("phone")} />
          </Field>
          <Field label={t("fields.email")} htmlFor="b-email" error={err("email")}>
            <Input id="b-email" type="email" inputMode="email" autoCapitalize="none" readOnly={ro} aria-invalid={!!err("email")} {...register("email")} />
          </Field>
          <Field label={t("fields.workingHours")} htmlFor="b-hours" hint={t("workingHoursHint")} error={err("workingHours")} className="md:col-span-2">
            <Input id="b-hours" readOnly={ro} {...register("workingHours")} />
          </Field>
          <Field label={t("fields.addressLine")} htmlFor="b-address" error={err("addressLine")} className="md:col-span-2">
            <Textarea id="b-address" rows={2} readOnly={ro} {...register("addressLine")} />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("sections.location")}</CardTitle>
          <CardDescription>{t("locationHint")}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("fields.latitude")} htmlFor="b-lat" error={err("latitude")}>
              <Input id="b-lat" inputMode="decimal" readOnly={ro} aria-invalid={!!err("latitude")} {...register("latitude")} />
            </Field>
            <Field label={t("fields.longitude")} htmlFor="b-lng" error={err("longitude")}>
              <Input id="b-lng" inputMode="decimal" readOnly={ro} aria-invalid={!!err("longitude")} {...register("longitude")} />
            </Field>
          </div>
          <MapPicker
            label={t("mapLabel")}
            latitude={latNum}
            longitude={lngNum}
            onChange={(a, b) => {
              if (ro) return;
              setValue("latitude", String(a), { shouldDirty: true, shouldValidate: true });
              setValue("longitude", String(b), { shouldDirty: true, shouldValidate: true });
            }}
          />
          <p className="text-sm text-muted">{t("mapHelp")}</p>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("sections.operations")}</CardTitle>
          <CardDescription>{t("operationsHint")}</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4 md:grid-cols-2">
          <Field label={t("fields.timeZone")} htmlFor="b-tz" error={err("timeZoneId")} required>
            <Input id="b-tz" readOnly={ro} aria-invalid={!!err("timeZoneId")} {...register("timeZoneId")} />
          </Field>
          <Field label={t("fields.currency")} htmlFor="b-cur" error={err("defaultCurrency")} required>
            <Input id="b-cur" readOnly={ro} maxLength={3} autoCapitalize="characters" aria-invalid={!!err("defaultCurrency")} {...register("defaultCurrency")} />
          </Field>
          <Field label={t("fields.pickupAddress")} htmlFor="b-pickup" hint={t("pickupHint")} error={err("pickupAddress")} className="md:col-span-2">
            <Textarea id="b-pickup" rows={2} readOnly={ro} {...register("pickupAddress")} />
          </Field>
          <Field label={t("fields.courierStoreIds")} htmlFor="b-courier" hint={t("courierHint")} error={err("courierStoreIds")} className="md:col-span-2">
            <Input id="b-courier" readOnly={ro} {...register("courierStoreIds")} />
          </Field>
        </CardContent>
      </Card>

      {!ro ? (
        <div className="sticky bottom-20 z-10 flex flex-col-reverse gap-2 rounded-card border border-line bg-card/95 p-3 shadow-pop backdrop-blur sm:flex-row sm:items-center sm:justify-end md:bottom-4">
          <Button variant="secondary" asChild>
            <Link href="/admin/ums/branches">{tc("cancel")}</Link>
          </Button>
          <Button type="submit" loading={submitting || formState.isSubmitting} disabled={!!branch && !formState.isDirty}>
            {branch ? t("save") : t("create")}
          </Button>
        </div>
      ) : null}
    </form>
  );
}

function BranchActions({ branch }: { branch: BranchDto }) {
  const t = useTranslations("ums.branches.actions");
  const tc = useTranslations("ums.common");
  const { can } = usePermissions();
  const report = useReportError();
  const action = useBranchAction();
  const [pending, setPending] = React.useState<BranchAction | null>(null);

  const items: { key: BranchAction; icon: React.ReactNode; allowed: boolean; destructive?: boolean }[] = [
    { key: branch.isActive ? "deactivate" : "activate", icon: branch.isActive ? <PowerOff aria-hidden /> : <Power aria-hidden />, allowed: can("ums.branch.deactivate", "anywhere"), destructive: branch.isActive },
    { key: "set-default", icon: <Star aria-hidden />, allowed: can("ums.branch.set-default", "anywhere") && branch.isActive && !branch.isDefault },
  ];
  const visible = items.filter((i) => i.allowed);
  if (visible.length === 0) return null;

  return (
    <>
      {visible.map((item) => (
        <Button key={item.key} variant="secondary" className={item.destructive ? "text-danger" : undefined} onClick={() => setPending(item.key)}>
          {item.icon} {t(`labels.${item.key}`)}
        </Button>
      ))}
      <ConfirmDialog
        open={pending !== null}
        onOpenChange={(open) => !open && setPending(null)}
        title={pending ? t(`confirm.${pending}.title`) : ""}
        description={pending ? t(`confirm.${pending}.body`, { name: branch.nameEn }) : ""}
        confirmLabel={pending ? t(`confirm.${pending}.button`) : ""}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive={pending === "deactivate"}
        loading={action.isPending}
        onConfirm={async () => {
          if (!pending) return;
          try {
            await action.mutateAsync({ id: branch.id, action: pending });
            toast.success(t(`done.${pending}`));
            setPending(null);
          } catch (error) {
            report(error);
            setPending(null);
          }
        }}
      />
    </>
  );
}

function BranchUsers({ id }: { id: string }) {
  const t = useTranslations("ums.branches.users");
  const query = useBranchUsers(id);
  const users = query.data ?? [];
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
        <CardDescription>{t("description")}</CardDescription>
      </CardHeader>
      <CardContent>
        {query.isLoading ? (
          <Skeleton className="h-16" />
        ) : users.length === 0 ? (
          <p className="text-muted">{t("empty")}</p>
        ) : (
          <ul className="divide-y divide-line rounded-card border border-line">
            {users.map((u) => (
              <li key={u.assignmentId} className="flex flex-wrap items-center gap-3 px-4 py-2">
                <span className="min-w-0 flex-1">
                  <Link href={`/admin/ums/users/${u.userId}`} className="inline-flex min-h-11 items-center font-semibold text-strong hover:text-primary hover:underline [overflow-wrap:anywhere]">
                    {u.fullName}
                  </Link>
                  <span className="block text-sm text-muted [overflow-wrap:anywhere]">{u.email}</span>
                </span>
                <Badge variant="brand">{u.roleName}</Badge>
                {!u.isActive ? <Badge variant="outline">{t("inactive")}</Badge> : null}
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
