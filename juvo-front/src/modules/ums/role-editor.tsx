"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Copy, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { usePermissions, useSession } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useCreateRole, useDeleteRole, usePermissionCatalog, useReportError, useRole, useSetRolePermissions, useUpdateRole } from "./api";
import { CloneRoleDialog } from "./role-clone-dialog";
import { PermissionMatrix } from "./permission-matrix";

/** Create a role (no id) or edit one: name, description and the permission matrix. */
export function RoleEditor({ id }: { id?: string }) {
  const t = useTranslations("ums.roles");
  const tn = useTranslations("nav");
  const tc = useTranslations("ums.common");
  const router = useRouter();
  const report = useReportError();
  const { me } = useSession();
  const { can } = usePermissions();

  const role = useRole(id ?? "");
  const catalog = usePermissionCatalog();
  const create = useCreateRole();
  const update = useUpdateRole(id ?? "");
  const setPermissions = useSetRolePermissions(id ?? "");
  const remove = useDeleteRole();

  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [selected, setSelected] = React.useState<Set<string>>(new Set());
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [deleting, setDeleting] = React.useState(false);
  const loaded = React.useRef<string | null>(null);

  // Fill the form once per loaded version of the role (after a save the server copy is the truth).
  const data = role.data;
  const dataKey = data ? `${data.id}|${data.name}|${data.description}|${[...data.permissions].sort().join(",")}` : null;
  React.useEffect(() => {
    if (data && dataKey && loaded.current !== dataKey) {
      loaded.current = dataKey;
      setName(data.name);
      setDescription(data.description ?? "");
      setSelected(new Set(data.permissions));
    }
  }, [data, dataKey]);

  // You can only hand out permissions you hold yourself (the API refuses escalation); the rest are shown disabled.
  const disabledCodes = React.useMemo(() => {
    if (me.hasAllPermissions) return new Set<string>();
    const held = new Set<string>([...me.globalPermissions, ...Object.values(me.branchPermissions).flat()]);
    const all = (catalog.data?.modules ?? []).flatMap((m) => m.permissions.map((p) => p.code));
    return new Set(all.filter((code) => !held.has(code)));
  }, [me, catalog.data]);

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.roles"), href: "/admin/ums/roles" }, { label: id ? (data?.name ?? "...") : t("new") }];

  if (id && role.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" />
      </>
    );
  }
  if (id && (role.isError || !data)) {
    const notFound = role.error instanceof ApiError && role.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFoundTitle")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFoundTitle") : t("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/ums/roles">{t("back")}</Link>
            </Button>
          }
        />
      </>
    );
  }

  const isSuperAdmin = data?.isSuperAdmin ?? false;
  const isSystem = data?.isSystem ?? false;
  const canEdit = id ? can("ums.role.edit", "anywhere") && !isSuperAdmin : can("ums.role.create", "anywhere");
  const canAssign = id ? can("ums.role.assign-permissions", "anywhere") && !isSuperAdmin : can("ums.role.create", "anywhere");
  const nameLocked = isSystem; // system roles keep their name

  const sameSet = (a: ReadonlySet<string>, b: readonly string[]) => a.size === b.length && b.every((c) => a.has(c));
  const dirty = !id || name !== data?.name || description !== (data?.description ?? "") || !sameSet(selected, data?.permissions ?? []);

  const save = async () => {
    setErrors({});
    if (!name.trim()) {
      setErrors({ name: t("nameRequired") });
      return;
    }
    try {
      if (!id) {
        const created = await create.mutateAsync({ name: name.trim(), description: description.trim() || null, permissions: [...selected] });
        toast.success(t("created"));
        router.push(`/admin/ums/roles/${created.id}`);
        return;
      }
      if (data && (name !== data.name || description !== (data.description ?? "")) && canEdit) {
        await update.mutateAsync({ name: name.trim(), description: description.trim() || null });
      }
      if (data && !sameSet(selected, data.permissions) && canAssign) {
        await setPermissions.mutateAsync([...selected]);
      }
      toast.success(t("saved"));
    } catch (error) {
      const fields = report(error);
      setErrors(fields);
    }
  };

  const busy = create.isPending || update.isPending || setPermissions.isPending;

  return (
    <>
      <PageHeader
        title={id ? (data?.name ?? "") : t("newTitle")}
        description={id ? (data?.description ?? undefined) : t("newDescription")}
        crumbs={crumbs}
        actions={
          id && data ? (
            <>
              {can("ums.role.clone", "anywhere") ? (
                <CloneRoleDialog source={data}>
                  <Button variant="secondary">
                    <Copy aria-hidden /> {t("clone")}
                  </Button>
                </CloneRoleDialog>
              ) : null}
              {can("ums.role.delete", "anywhere") && !isSystem ? (
                <Button variant="secondary" className="text-danger" onClick={() => setDeleting(true)}>
                  <Trash2 aria-hidden /> {t("delete")}
                </Button>
              ) : null}
            </>
          ) : undefined
        }
      />

      {id && data ? (
        <div className="-mt-3 mb-5 flex flex-wrap gap-1.5">
          {isSuperAdmin ? <Badge variant="danger">{t("superAdmin")}</Badge> : isSystem ? <Badge variant="info">{t("system")}</Badge> : <Badge variant="outline">{t("custom")}</Badge>}
          <Badge variant="neutral">{t("users", { count: data.userCount })}</Badge>
        </div>
      ) : null}

      <div className="space-y-4">
        {isSuperAdmin ? (
          <Alert variant="info" title={t("superAdminTitle")}>
            {t("superAdminBody")}
          </Alert>
        ) : null}
        {isSystem && !isSuperAdmin ? <Alert variant="info">{t("systemNote")}</Alert> : null}
        {disabledCodes.size > 0 && canAssign ? <Alert variant="info">{t("escalationNote")}</Alert> : null}

        <Card>
          <CardHeader>
            <CardTitle>{t("details")}</CardTitle>
          </CardHeader>
          <CardContent className="grid gap-4 md:grid-cols-2">
            <Field label={t("name")} htmlFor="role-name" error={errors.name} required>
              <Input id="role-name" value={name} onChange={(e) => setName(e.target.value)} readOnly={!canEdit || nameLocked} maxLength={100} aria-invalid={!!errors.name} />
            </Field>
            <Field label={t("description")} htmlFor="role-desc" error={errors.description}>
              <Textarea id="role-desc" rows={2} value={description} onChange={(e) => setDescription(e.target.value)} readOnly={!canEdit} maxLength={500} />
            </Field>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("permissions")}</CardTitle>
            <CardDescription>{t("permissionsHint")}</CardDescription>
          </CardHeader>
          <CardContent>
            {isSuperAdmin ? <p className="text-muted">{t("superAdminAll")}</p> : <PermissionMatrix value={selected} onChange={setSelected} readOnly={!canAssign} disabledCodes={disabledCodes} />}
          </CardContent>
        </Card>

        {(canEdit || canAssign) && !isSuperAdmin ? (
          <div className="sticky bottom-20 z-10 flex flex-col-reverse gap-2 rounded-card border border-line bg-card/95 p-3 shadow-pop backdrop-blur sm:flex-row sm:items-center sm:justify-end md:bottom-4">
            <span className="text-sm text-muted sm:me-auto" aria-live="polite">
              {dirty ? t("unsaved") : t("upToDate")}
            </span>
            <Button variant="secondary" asChild>
              <Link href="/admin/ums/roles">{tc("cancel")}</Link>
            </Button>
            <Button onClick={() => void save()} loading={busy} disabled={!dirty}>
              {id ? t("save") : t("create")}
            </Button>
          </div>
        ) : null}
      </div>

      <ConfirmDialog
        open={deleting}
        onOpenChange={setDeleting}
        title={t("deleteTitle")}
        description={t("deleteBody", { name: data?.name ?? "" })}
        confirmLabel={t("deleteConfirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={remove.isPending}
        onConfirm={async () => {
          if (!id) return;
          try {
            await remove.mutateAsync(id);
            toast.success(t("deleted"));
            router.push("/admin/ums/roles");
          } catch (error) {
            report(error);
            setDeleting(false);
          }
        }}
      />
    </>
  );
}

