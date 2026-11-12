"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Pencil, Eye } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge, Card, Skeleton } from "@/components/ui/surfaces";
import { SearchBox } from "@/components/data/list-kit";
import { useTemplates, type Template } from "./api";
import { groupTemplates } from "./format";
import { TemplateEditor } from "./template-editor";

/** All templates grouped by code; each channel and language is one row with its version and an edit (or view) button. */
export function TemplatesPanel({ canEdit }: { canEdit: boolean }) {
  const t = useTranslations("notifications");
  const query = useTemplates();
  const [search, setSearch] = React.useState("");
  const [editing, setEditing] = React.useState<Template | null>(null);

  const groups = React.useMemo(() => {
    const needle = search.trim().toLowerCase();
    return groupTemplates(query.data ?? []).filter((g) => !needle || g.code.includes(needle) || g.description.toLowerCase().includes(needle));
  }, [query.data, search]);

  if (query.isLoading) return <Skeleton className="h-64" aria-busy />;
  if (query.isError) return <Alert variant="danger">{t("common.loadFailed")}</Alert>;

  return (
    <div className="space-y-4">
      <SearchBox className="lg:w-80" value={search} onChange={setSearch} placeholder={t("templates.searchPlaceholder")} label={t("templates.search")} />
      {groups.length === 0 ? <p className="text-muted">{t("common.noMatch")}</p> : null}
      <ul className="space-y-4" aria-label={t("tabs.templates")}>
        {groups.map((g) => (
          <li key={g.code}>
            <Card className="space-y-3 p-4 sm:p-5" data-testid="template-group" data-code={g.code}>
              <div className="flex flex-wrap items-center gap-2">
                <h3 className="font-mono text-base font-semibold text-strong">{g.code}</h3>
                <Badge variant={g.category === "Marketing" ? "warning" : "neutral"}>{t(`category.${g.category}`)}</Badge>
              </div>
              <p className="text-sm text-muted">{g.description}</p>
              <ul className="divide-y divide-line">
                {g.variants.map((v) => (
                  <li key={`${v.channel}-${v.language}`} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2" data-testid="template-row" data-variant={`${v.channel}-${v.language}`}>
                    <span className="min-w-0 flex-1 basis-40">
                      <span className="font-medium text-strong">
                        {t(`channel.${v.channel}`)} · {t(`language.${v.language}`)}
                      </span>
                      <span className="ms-2 text-sm text-muted">{t("templates.version", { n: Number(v.version) })}</span>
                    </span>
                    <span className="flex flex-wrap gap-1.5">
                      <Badge variant={v.isActive ? "success" : "neutral"}>{v.isActive ? t("templates.active") : t("templates.inactive")}</Badge>
                      <Badge variant="outline">{v.isCustom ? t("templates.custom") : t("templates.builtIn")}</Badge>
                    </span>
                    <Button variant="secondary" onClick={() => setEditing(v)} aria-label={t(canEdit ? "templates.editFor" : "templates.viewFor", { code: g.code, channel: t(`channel.${v.channel}`), language: t(`language.${v.language}`) })}>
                      {canEdit ? <Pencil aria-hidden /> : <Eye aria-hidden />} {canEdit ? t("templates.edit") : t("templates.view")}
                    </Button>
                  </li>
                ))}
              </ul>
            </Card>
          </li>
        ))}
      </ul>

      <Dialog open={!!editing} onOpenChange={(open) => (open ? undefined : setEditing(null))}>
        <ModalContent size="lg" closeLabel={t("common.close")}>
          {editing ? (
            <>
              <DialogTitle className="font-display text-xl font-semibold text-strong">{t("editor.title", { code: editing.code })}</DialogTitle>
              <DialogDescription className="mb-4 mt-1 text-muted">
                {t(`channel.${editing.channel}`)} · {t(`language.${editing.language}`)} · {editing.description}
              </DialogDescription>
              <TemplateEditor template={editing} canEdit={canEdit} onDone={() => setEditing(null)} />
            </>
          ) : null}
        </ModalContent>
      </Dialog>
    </div>
  );
}
