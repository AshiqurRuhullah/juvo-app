"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { Input } from "@/components/ui/form";
import { toast } from "@/components/ui/toaster";
import { Can, usePermissions } from "@/components/providers/session-provider";
import { AssignmentPicker } from "./assignment-picker";
import { useAddAssignment, useEffectivePermissions, useRemoveAssignment, useReportError, type EffectivePermissions, type UserDetail } from "./api";

/** Which role the person holds in which branch, plus what that adds up to (effective permissions) for each branch. */
export function UserAccess({ user }: { user: UserDetail }) {
  const t = useTranslations("ums.access");
  const report = useReportError();
  const add = useAddAssignment(user.id);
  const remove = useRemoveAssignment(user.id);
  const { can } = usePermissions();
  const [removing, setRemoving] = React.useState<UserDetail["assignments"][number] | null>(null);
  const canAssign = can("ums.branch.assign-user", "anywhere");

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle>{t("title")}</CardTitle>
          <CardDescription>{t("description")}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {user.isGlobalScope ? <Alert variant="info">{t("globalNote")}</Alert> : null}

          {user.assignments.length === 0 ? (
            <p className="text-muted">{t("none")}</p>
          ) : (
            <ul className="divide-y divide-line rounded-card border border-line" aria-label={t("title")}>
              {user.assignments.map((a) => (
                <li key={a.assignmentId} className="flex flex-wrap items-center gap-3 px-4 py-2">
                  <span className="min-w-0 flex-1">
                    <span className="block font-semibold text-strong">{a.branchCode ?? t("everywhere")}</span>
                    <span className="block text-sm text-muted">{a.roleName}</span>
                  </span>
                  {canAssign ? (
                    <Button variant="ghost" onClick={() => setRemoving(a)} aria-label={t("remove", { branch: a.branchCode ?? t("everywhere"), role: a.roleName })}>
                      <Trash2 aria-hidden /> <span className="hidden sm:inline">{t("removeShort")}</span>
                    </Button>
                  ) : null}
                </li>
              ))}
            </ul>
          )}

          <Can permission="ums.branch.assign-user" scope="anywhere">
            <div className="border-t border-line pt-4">
              <h3 className="mb-3 font-sans text-sm font-semibold text-muted">{t("addTitle")}</h3>
              <AssignmentPicker
                idPrefix="ua"
                allowEverywhere={user.isGlobalScope}
                busy={add.isPending}
                onAdd={async (branchId, roleId) => {
                  try {
                    await add.mutateAsync({ branchId, roleId });
                    toast.success(t("added"));
                  } catch (error) {
                    report(error);
                  }
                }}
              />
            </div>
          </Can>
        </CardContent>
      </Card>

      <EffectiveViewer userId={user.id} assignmentCount={user.assignments.length} />

      <ConfirmDialog
        open={removing !== null}
        onOpenChange={(open) => !open && setRemoving(null)}
        title={t("removeTitle")}
        description={removing ? t("removeBody", { branch: removing.branchCode ?? t("everywhere"), role: removing.roleName }) : ""}
        confirmLabel={t("removeConfirm")}
        cancelLabel={t("cancel")}
        closeLabel={t("close")}
        destructive
        loading={remove.isPending}
        onConfirm={async () => {
          if (!removing) return;
          try {
            await remove.mutateAsync(removing.assignmentId);
            toast.success(t("removed"));
            setRemoving(null);
          } catch (error) {
            report(error);
          }
        }}
      />
    </div>
  );
}

/** "What can this person do in branch X?": the union of the permissions of their roles there, grouped by module. */
function EffectiveViewer({ userId, assignmentCount }: { userId: string; assignmentCount: number }) {
  const t = useTranslations("ums.access.effective");
  const query = useEffectivePermissions(userId);
  const [selected, setSelected] = React.useState<string | null>(null);
  const [filter, setFilter] = React.useState("");

  // Places where the person has nothing (for example "Everywhere" for someone with only branch roles) are left out.
  const entries = React.useMemo(() => (query.data ?? []).filter((e) => e.hasAllPermissions || e.permissions.length > 0), [query.data]);
  const keyOf = (e: EffectivePermissions) => e.branchId ?? "global";
  const current = entries.find((e) => keyOf(e) === selected) ?? entries[0];

  const grouped = React.useMemo(() => {
    const groups = new Map<string, string[]>();
    for (const code of current?.permissions ?? []) {
      if (filter && !code.toLowerCase().includes(filter.toLowerCase())) continue;
      const group = code.split(".")[0] ?? code;
      groups.set(group, [...(groups.get(group) ?? []), code]);
    }
    return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [current, filter]);

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
        <CardDescription>{t("description")}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {query.isLoading ? (
          <Skeleton className="h-24" />
        ) : entries.length === 0 ? (
          <p className="text-muted">{assignmentCount === 0 ? t("noneAssigned") : t("none")}</p>
        ) : (
          <>
            <div role="tablist" aria-label={t("pickBranch")} className="flex flex-wrap gap-2">
              {entries.map((e) => {
                const active = e === current;
                return (
                  <button
                    key={keyOf(e)}
                    type="button"
                    role="tab"
                    aria-selected={active}
                    onClick={() => setSelected(keyOf(e))}
                    className={`min-h-11 rounded-control border px-4 font-semibold ${active ? "border-primary bg-primary-soft text-primary-dark" : "border-line text-strong [@media(hover:hover)]:hover:bg-sunken"}`}
                  >
                    {e.branchCode ?? t("everywhere")}
                  </button>
                );
              })}
            </div>

            {current?.hasAllPermissions ? (
              <Alert variant="success" title={t("allTitle")}>
                {t("allBody")}
              </Alert>
            ) : (
              <>
                <div className="flex flex-wrap items-center gap-3">
                  <Badge variant="brand">{t("count", { count: current?.permissions.length ?? 0 })}</Badge>
                  <div className="min-w-48 flex-1 sm:max-w-xs">
                    <label htmlFor="eff-filter" className="sr-only">
                      {t("filter")}
                    </label>
                    <Input id="eff-filter" type="search" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder={t("filter")} />
                  </div>
                </div>
                {grouped.length === 0 ? (
                  <p className="text-muted">{t("noMatch")}</p>
                ) : (
                  <div className="grid gap-3 md:grid-cols-2">
                    {grouped.map(([group, codes]) => (
                      <section key={group} className="rounded-card border border-line p-3">
                        <h4 className="mb-2 font-sans text-sm font-semibold uppercase tracking-wide text-muted">{group}</h4>
                        <ul className="flex flex-wrap gap-1.5">
                          {codes.map((code) => (
                            <li key={code}>
                              <code className="rounded-md bg-sunken px-1.5 py-0.5 text-xs text-strong">{code.slice(group.length + 1)}</code>
                            </li>
                          ))}
                        </ul>
                      </section>
                    ))}
                  </div>
                )}
              </>
            )}
          </>
        )}
      </CardContent>
    </Card>
  );
}
