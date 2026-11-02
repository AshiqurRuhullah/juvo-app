"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { Building2, Check, ChevronDown, Layers } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/overlays";
import { toast } from "@/components/ui/toaster";
import { postJson } from "@/lib/api/post";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { usePermissions, useSession } from "@/components/providers/session-provider";

/**
 * The active branch. Every list, widget and report follows it: it is sent to the API as X-Branch-Id, which validates it against
 * the user's assignments on each request. Hidden when there is only one branch to choose from (the single-branch setup).
 */
export function BranchSwitcher() {
  const { me } = useSession();
  const { can } = usePermissions();
  const router = useRouter();
  const queryClient = useQueryClient();
  const t = useTranslations("shell");
  const message = useProblemMessage();
  const [busy, setBusy] = React.useState(false);

  const branches = me.branches;
  const canPickAll = !!me.staff?.isGlobalScope || can("ums.branch.switch-all", "anywhere");
  if (branches.length <= 1 && !canPickAll) return null;
  if (branches.length <= 1) return null;

  const active = branches.find((b) => b.id === me.activeBranchId);
  const needsChoice = !active && !me.isAllBranches;
  const label = me.isAllBranches ? t("allBranches") : (active?.nameEn ?? t("chooseBranch"));
  const shortLabel = me.isAllBranches ? t("allShort") : (active?.code ?? t("chooseShort"));

  const choose = async (branch: string) => {
    setBusy(true);
    const result = await postJson("/api/bff/session/branch", { branch });
    setBusy(false);
    if (!result.ok) {
      toast.error(message(result.problem));
      return;
    }
    queryClient.clear(); // cached data belongs to the previous branch
    router.refresh(); // server components reload with the new X-Branch-Id
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant={needsChoice ? "primary" : "secondary"} className="min-w-0 max-w-[11rem] shrink gap-2 px-3 sm:max-w-[16rem]" disabled={busy} aria-label={`${t("branch")}: ${label}`}>
          {me.isAllBranches ? <Layers aria-hidden /> : <Building2 aria-hidden />}
          <span className="truncate sm:hidden">{shortLabel}</span>
          <span className="hidden truncate sm:inline">{label}</span>
          <ChevronDown className="!size-4" aria-hidden />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent className="min-w-64" align="start">
        <DropdownMenuLabel className="px-3 py-1.5 text-xs font-semibold uppercase tracking-wide text-muted">{t("branch")}</DropdownMenuLabel>
        {canPickAll ? (
          <>
            <DropdownMenuItem onSelect={() => choose("all")}>
              <Layers aria-hidden />
              <span className="flex-1">{t("allBranches")}</span>
              {me.isAllBranches ? <Check className="!text-primary" aria-hidden /> : null}
            </DropdownMenuItem>
            <DropdownMenuSeparator />
          </>
        ) : null}
        {branches.map((branch) => (
          <DropdownMenuItem key={branch.id} onSelect={() => choose(branch.id)}>
            <Building2 aria-hidden />
            <span className="min-w-0 flex-1">
              <span className="block truncate">{branch.nameEn}</span>
              <span className="block text-xs text-muted">{branch.code}</span>
            </span>
            {branch.id === me.activeBranchId ? <Check className="!text-primary" aria-hidden /> : null}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
