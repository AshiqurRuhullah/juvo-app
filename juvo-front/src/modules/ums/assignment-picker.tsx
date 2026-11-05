"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, type SelectOption } from "@/components/ui/select";
import { useAllBranches, useAllRoles } from "./api";

export const EVERYWHERE = "__everywhere";

/** Branch + role choice with an Add button. Used when creating a user and on the user's Access tab. */
export function AssignmentPicker({
  allowEverywhere,
  busy,
  onAdd,
  idPrefix,
}: {
  /** Only global-scope users can hold a role without a branch. */
  allowEverywhere: boolean;
  busy?: boolean;
  onAdd: (branchId: string | null, roleId: string) => void | Promise<void>;
  idPrefix: string;
}) {
  const t = useTranslations("ums.assignments");
  const branches = useAllBranches();
  const roles = useAllRoles();
  const [branch, setBranch] = React.useState<string>("");
  const [role, setRole] = React.useState<string>("");

  const branchOptions = React.useMemo<SelectOption[]>(() => {
    const options = (branches.data?.items ?? []).filter((b) => b.isActive).map((b) => ({ value: b.id, label: `${b.code} · ${b.nameEn}` }));
    return allowEverywhere ? [{ value: EVERYWHERE, label: t("everywhere") }, ...options] : options;
  }, [branches.data, allowEverywhere, t]);
  const roleOptions = React.useMemo<SelectOption[]>(() => (roles.data?.items ?? []).map((r) => ({ value: r.id, label: r.name, hint: r.description ?? undefined })), [roles.data]);

  const ready = !!branch && !!role;

  return (
    <div className="grid gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-end">
      <div className="space-y-1.5">
        <label htmlFor={`${idPrefix}-branch`} className="text-sm font-semibold text-strong">
          {t("branch")}
        </label>
        <Select id={`${idPrefix}-branch`} value={branch} onValueChange={setBranch} options={branchOptions} placeholder={t("chooseBranch")} searchPlaceholder={t("search")} emptyText={t("noMatch")} />
      </div>
      <div className="space-y-1.5">
        <label htmlFor={`${idPrefix}-role`} className="text-sm font-semibold text-strong">
          {t("role")}
        </label>
        <Select id={`${idPrefix}-role`} value={role} onValueChange={setRole} options={roleOptions} placeholder={t("chooseRole")} searchPlaceholder={t("search")} emptyText={t("noMatch")} />
      </div>
      <Button
        variant="secondary"
        disabled={!ready}
        loading={busy}
        onClick={async () => {
          await onAdd(branch === EVERYWHERE ? null : branch, role);
          setBranch("");
          setRole("");
        }}
      >
        <Plus aria-hidden /> {t("add")}
      </Button>
    </div>
  );
}
