"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { KeyRound, LogOut, MoreVertical, Power, PowerOff, ShieldOff } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Dialog, DialogDescription, DialogTitle, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { Tabs } from "@/components/ui/tabs";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { usePermissions, useSession } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useReportError, useResetPassword, useUser, useUserAction, type UserAction, type UserDetail } from "./api";
import { SecretReveal } from "./secret-reveal";
import { UserAccess } from "./user-access";
import { UserOverview } from "./user-overview";
import { UserSessions, UserSignIns } from "./user-sessions";

type Pending = { kind: UserAction } | { kind: "reset-password" } | null;

export function UserDetailView({ id }: { id: string }) {
  const t = useTranslations("ums.users");
  const tn = useTranslations("nav");
  const query = useUser(id);
  const [tab, setTab] = React.useState("overview");
  const { can } = usePermissions();

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.users"), href: "/admin/ums/users" }, { label: query.data?.fullName ?? "..." }];

  if (query.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" />
      </>
    );
  }

  if (query.isError || !query.data) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("detail.notFoundTitle")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("detail.notFoundTitle") : t("loadFailed")}
          description={notFound ? t("detail.notFoundBody") : undefined}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/ums/users">{t("detail.back")}</Link>
            </Button>
          }
        />
      </>
    );
  }

  const user = query.data;
  const tabs = [
    { id: "overview", label: t("tabs.overview") },
    { id: "access", label: t("tabs.access") },
    ...(can("ums.session.view", "anywhere") ? [{ id: "sessions", label: t("tabs.sessions") }] : []),
    ...(can("ums.sign-in.view", "anywhere") ? [{ id: "signIns", label: t("tabs.signIns") }] : []),
  ];

  return (
    <>
      <PageHeader
        title={user.fullName}
        description={user.email}
        crumbs={crumbs}
        actions={<UserActions user={user} />}
      />
      <div className="-mt-3 mb-5 flex flex-wrap gap-1.5">
        {user.isActive ? <Badge variant="success">{t("active")}</Badge> : <Badge variant="outline">{t("inactive")}</Badge>}
        {user.isLockedOut ? <Badge variant="danger">{t("locked")}</Badge> : null}
        {user.isGlobalScope ? <Badge variant="info">{t("global")}</Badge> : null}
      </div>
      {!user.isActive ? (
        <Alert variant="warning" className="mb-4">
          {t("detail.inactiveNote")}
        </Alert>
      ) : null}

      <Tabs tabs={tabs} value={tab} onValueChange={setTab} label={t("tabs.label")}>
        {(active) =>
          active === "access" ? <UserAccess user={user} /> : active === "sessions" ? <UserSessions userId={user.id} /> : active === "signIns" ? <UserSignIns userId={user.id} /> : <UserOverview user={user} />
        }
      </Tabs>
    </>
  );
}

/** Account actions in one menu. Each risky one asks first; nobody can act on their own account (the API refuses it too). */
function UserActions({ user }: { user: UserDetail }) {
  const t = useTranslations("ums.users.actions");
  const tc = useTranslations("ums.common");
  const { me } = useSession();
  const { can } = usePermissions();
  const report = useReportError();
  const action = useUserAction(user.id);
  const reset = useResetPassword(user.id);
  const [pending, setPending] = React.useState<Pending>(null);
  const [temporary, setTemporary] = React.useState<string | null>(null);

  const isSelf = me.staff?.id === user.id;
  const items: { key: UserAction | "reset-password"; icon: React.ReactNode; allowed: boolean; destructive?: boolean }[] = [
    { key: user.isActive ? "deactivate" : "activate", icon: user.isActive ? <PowerOff aria-hidden /> : <Power aria-hidden />, allowed: can(user.isActive ? "ums.user.deactivate" : "ums.user.activate", "anywhere"), destructive: user.isActive },
    { key: "reset-password", icon: <KeyRound aria-hidden />, allowed: can("ums.user.reset-password", "anywhere") },
    { key: "force-logout", icon: <LogOut aria-hidden />, allowed: can("ums.user.force-logout", "anywhere") },
    { key: "reset-two-factor", icon: <ShieldOff aria-hidden />, allowed: can("ums.user.manage-two-factor", "anywhere") && user.twoFactorEnabled },
  ];
  const visible = items.filter((i) => i.allowed);
  if (isSelf || visible.length === 0) return null;

  const confirm = async () => {
    if (!pending) return;
    try {
      if (pending.kind === "reset-password") {
        const result = await reset.mutateAsync();
        setPending(null);
        setTemporary(result.temporaryPassword);
        return;
      }
      await action.mutateAsync(pending.kind);
      toast.success(t(`done.${pending.kind}`));
      setPending(null);
    } catch (error) {
      report(error);
    }
  };

  const busy = action.isPending || reset.isPending;

  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="secondary" aria-label={t("menu")}>
            <MoreVertical aria-hidden /> <span className="hidden sm:inline">{t("menu")}</span>
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="min-w-64">
          {visible.map((item) => (
            <DropdownMenuItem key={item.key} onSelect={() => setPending({ kind: item.key } as Pending)} className={item.destructive ? "text-danger" : undefined}>
              {item.icon}
              {t(`labels.${item.key}`)}
            </DropdownMenuItem>
          ))}
        </DropdownMenuContent>
      </DropdownMenu>

      <ConfirmDialog
        open={pending !== null}
        onOpenChange={(open) => !open && setPending(null)}
        title={pending ? t(`confirm.${pending.kind}.title`) : ""}
        description={pending ? t(`confirm.${pending.kind}.body`, { name: user.fullName }) : ""}
        confirmLabel={pending ? t(`confirm.${pending.kind}.button`) : ""}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive={pending?.kind === "deactivate" || pending?.kind === "force-logout" || pending?.kind === "reset-two-factor"}
        loading={busy}
        onConfirm={confirm}
      />

      <Dialog open={temporary !== null} onOpenChange={(open) => !open && setTemporary(null)}>
        <ModalContent size="sm" closeLabel={tc("close")}>
          <DialogTitle className="font-display text-xl font-semibold text-strong">{t("resetDone.title")}</DialogTitle>
          <DialogDescription className="mt-2 text-muted">{t("resetDone.body", { name: user.fullName })}</DialogDescription>
          <div className="mt-4">{temporary ? <SecretReveal label={t("resetDone.label")} value={temporary} /> : null}</div>
          <div className="mt-6 flex justify-end">
            <Button onClick={() => setTemporary(null)}>{tc("close")}</Button>
          </div>
        </ModalContent>
      </Dialog>
    </>
  );
}
