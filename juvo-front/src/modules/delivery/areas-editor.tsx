"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { ChevronDown, ChevronRight, Lock } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Checkbox, ChoiceRow, Switch } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, CardContent, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { SearchBox } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useDistricts, useDivisions, useSaveAreas, useUpazilas, useZones, type District, type Zone } from "./api";
import { fieldMessages, useLocalName } from "./common";
import {
  areaKey, draftFromAreas, draftToAreas, groupState, matchesName, movesFromOthers, ownersOf, sameAreas, setDistricts, setRemote, toggleUpazila,
  type AreaDraft, type Owners,
} from "./logic";

/**
 * Which districts and upazilas a domestic zone covers. A district row covers the whole district; an upazila row inside it is an
 * override that wins over the district row (the API's quote rule). The remote switch adds the zone's remote surcharge for that area.
 * An area another zone holds is marked with that zone's name, and saving moves it here (the API takes it from the other zone).
 */
export function AreasEditor({ zone, canEdit, onDirty }: { zone: Zone; canEdit: boolean; onDirty: (dirty: boolean) => void }) {
  const t = useTranslations("delivery.areas");
  const tc = useTranslations("delivery.common");
  const localName = useLocalName();
  const problemMessage = useProblemMessage();
  const divisions = useDivisions();
  const districts = useDistricts();
  const zones = useZones();
  const save = useSaveAreas(zone.id);
  const saved = React.useMemo(() => draftFromAreas(zone.areas), [zone.areas]);
  const [draft, setDraft] = React.useState<AreaDraft>(saved);
  const [search, setSearch] = React.useState("");
  const [divisionId, setDivisionId] = React.useState("");
  const [open, setOpen] = React.useState<Set<string>>(new Set());
  const [confirm, setConfirm] = React.useState(false);
  const [failure, setFailure] = React.useState<{ text: string; lines: string[] } | null>(null);
  const dirty = !sameAreas(draft, saved);

  React.useEffect(() => onDirty(dirty), [dirty, onDirty]);

  const owners = React.useMemo<Owners>(() => ownersOf(zones.data ?? [], zone.id), [zones.data, zone.id]);
  const moves = React.useMemo(() => movesFromOthers(draft, owners), [draft, owners]);
  const movedCount = moves.reduce((n, m) => n + m.count, 0);
  const areaList = React.useMemo(() => [...draft.values()], [draft]);
  const wholeCount = areaList.filter((a) => a.upazilaId === null).length;
  const upazilaCount = areaList.length - wholeCount;

  const byDivision = React.useMemo(() => {
    const list = (districts.data ?? []).filter((d) => (divisionId === "" || d.divisionId === divisionId) && matchesName(d, search));
    const groups = new Map<string, District[]>();
    for (const d of list) groups.set(d.divisionId, [...(groups.get(d.divisionId) ?? []), d]);
    return (divisions.data ?? []).filter((v) => groups.has(v.id)).map((v) => ({ division: v, items: groups.get(v.id)! }));
  }, [districts.data, divisions.data, divisionId, search]);
  const shownIds = React.useMemo(() => byDivision.flatMap((g) => g.items.map((d) => d.id)), [byDivision]);

  const doSave = async () => {
    setFailure(null);
    try {
      await save.mutateAsync(draftToAreas(draft));
      toast.success(t("saved"));
      setConfirm(false);
    } catch (e) {
      const problem = e instanceof ApiError ? e.problem : null;
      setFailure({ text: problemMessage(problem), lines: fieldMessages(problem) });
      setConfirm(false);
    }
  };

  const toggleOpen = (id: string) =>
    setOpen((old) => {
      const next = new Set(old);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  if (districts.isPending || divisions.isPending) return <Skeleton className="h-64" aria-busy="true" />;
  if (districts.isError || divisions.isError) return <Alert variant="danger">{tc("loadFailed")}</Alert>;

  return (
    <div className="space-y-4" data-testid="areas-editor">
      {!canEdit ? (
        <Alert variant="info" data-testid="areas-read-only">
          <span className="flex items-start gap-2">
            <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>{t("readOnly")}</span>
          </span>
        </Alert>
      ) : null}
      <Alert variant="info">{zone.isFallback ? t("howFallback") : t("how")}</Alert>

      <Card>
        <CardContent className="space-y-3 pt-5">
          <div className="grid gap-3 sm:grid-cols-2">
            <SearchBox value={search} onChange={setSearch} placeholder={t("searchPlaceholder")} label={t("search")} />
            <Select
              aria-label={t("division")}
              value={divisionId || "__all"}
              onValueChange={(v) => setDivisionId(v === "__all" ? "" : v)}
              options={[{ value: "__all", label: t("allDivisions") }, ...(divisions.data ?? []).map((d) => ({ value: d.id, label: localName(d) }))]}
              placeholder={t("division")}
              searchPlaceholder={tc("search")}
              emptyText={tc("noMatch")}
            />
          </div>
          {canEdit ? (
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="secondary" onClick={() => setDraft((d) => setDistricts(d, shownIds, true))} disabled={shownIds.length === 0} data-testid="areas-select-shown">
                {t("selectShown", { count: shownIds.length })}
              </Button>
              <Button type="button" variant="secondary" onClick={() => setDraft((d) => setDistricts(d, shownIds, false))} disabled={shownIds.length === 0} data-testid="areas-clear-shown">
                {t("clearShown")}
              </Button>
            </div>
          ) : null}
          <p className="font-medium text-strong" data-testid="areas-summary">
            {t("summary", { districts: wholeCount, upazilas: upazilaCount })}
          </p>
        </CardContent>
      </Card>

      {movedCount > 0 ? (
        <Alert variant="warning" title={t("movesTitle", { count: movedCount })} data-testid="areas-moves">
          <ul className="list-disc ps-5">
            {moves.map((m) => (
              <li key={m.zoneId}>{t("movesFrom", { zone: m.zoneName, count: m.count })}</li>
            ))}
          </ul>
        </Alert>
      ) : null}

      {byDivision.length === 0 ? <p className="text-muted">{t("noMatch")}</p> : null}
      {byDivision.map(({ division, items }) => {
        const ids = items.map((d) => d.id);
        const state = groupState(draft, ids);
        const selected = ids.filter((id) => draft.has(areaKey(id, null))).length;
        return (
          <Card key={division.id}>
            <CardContent className="pt-3">
              <div className="flex flex-wrap items-center justify-between gap-2 border-b border-line pb-2">
                <ChoiceRow htmlFor={`div-${division.id}`} className="font-semibold">
                  <Checkbox
                    id={`div-${division.id}`}
                    checked={state === "all" ? true : state === "some" ? "indeterminate" : false}
                    disabled={!canEdit}
                    onCheckedChange={() => setDraft((d) => setDistricts(d, ids, state !== "all"))}
                  />
                  {localName(division)}
                </ChoiceRow>
                <span className="text-sm text-muted">{t("divisionCount", { selected, total: ids.length })}</span>
              </div>
              <ul>
                {items.map((d) => (
                  <DistrictRow
                    key={d.id}
                    district={d}
                    draft={draft}
                    owners={owners}
                    canEdit={canEdit}
                    expanded={open.has(d.id)}
                    onToggleOpen={() => toggleOpen(d.id)}
                    onChange={setDraft}
                  />
                ))}
              </ul>
            </CardContent>
          </Card>
        );
      })}

      {failure ? (
        <Alert variant="danger" data-testid="areas-form-error">
          {failure.text}
          {failure.lines.map((l) => (
            <span key={l} className="block">
              {l}
            </span>
          ))}
        </Alert>
      ) : null}

      {canEdit ? (
        <div className="flex md:sticky md:bottom-0 md:z-10 -mx-1 flex-col-reverse gap-2 rounded-card border border-line bg-card p-3 shadow-pop sm:flex-row sm:items-center sm:justify-end">
          {dirty ? <Badge variant="warning">{tc("unsaved")}</Badge> : null}
          <Button type="button" variant="secondary" disabled={!dirty || save.isPending} onClick={() => { setDraft(saved); setFailure(null); }}>
            {tc("discard")}
          </Button>
          <Button type="button" disabled={!dirty} loading={save.isPending} onClick={() => (movedCount > 0 ? setConfirm(true) : void doSave())} data-testid="areas-save">
            {t("save")}
          </Button>
        </div>
      ) : null}

      <ConfirmDialog
        open={confirm}
        onOpenChange={setConfirm}
        title={t("confirmTitle", { count: movedCount })}
        description={t("confirmDescription", { zones: moves.map((m) => m.zoneName).join(", ") })}
        confirmLabel={t("confirmMove")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={save.isPending}
        onConfirm={doSave}
      />
    </div>
  );
}

function OwnerBadge({ owners, keyName }: { owners: Owners; keyName: string }) {
  const t = useTranslations("delivery.areas");
  const owner = owners.get(keyName);
  return owner ? (
    <Badge variant="info" data-testid="area-owner">
      {t("inZone", { zone: owner.zoneName })}
    </Badge>
  ) : null;
}

function DistrictRow({
  district, draft, owners, canEdit, expanded, onToggleOpen, onChange,
}: {
  district: District;
  draft: AreaDraft;
  owners: Owners;
  canEdit: boolean;
  expanded: boolean;
  onToggleOpen: () => void;
  onChange: React.Dispatch<React.SetStateAction<AreaDraft>>;
}) {
  const t = useTranslations("delivery.areas");
  const localName = useLocalName();
  const key = areaKey(district.id, null);
  const entry = draft.get(key);
  const overrides = [...draft.values()].filter((a) => a.districtId === district.id && a.upazilaId !== null).length;
  return (
    <li className="border-b border-line py-1 last:border-b-0" data-testid="district-row" data-district={district.nameEn}>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-0">
        <Button type="button" variant="ghost" size="icon" aria-expanded={expanded} aria-label={t("showUpazilas", { name: localName(district) })} onClick={onToggleOpen}>
          {expanded ? <ChevronDown aria-hidden /> : <ChevronRight className="rtl:rotate-180" aria-hidden />}
        </Button>
        <ChoiceRow htmlFor={`d-${district.id}`} className="min-w-0 flex-1 basis-48">
          <Checkbox id={`d-${district.id}`} checked={!!entry} disabled={!canEdit} onCheckedChange={(v) => onChange((d) => setDistricts(d, [district.id], v === true))} />
          <span className="min-w-0 [overflow-wrap:anywhere]">{localName(district)}</span>
        </ChoiceRow>
        <span className="flex flex-wrap items-center gap-1.5">
          <OwnerBadge owners={owners} keyName={key} />
          {overrides > 0 ? <Badge variant="neutral">{t("overrides", { count: overrides })}</Badge> : null}
        </span>
        {entry ? (
          <ChoiceRow htmlFor={`dr-${district.id}`}>
            <Switch id={`dr-${district.id}`} checked={entry.isRemote} disabled={!canEdit} onCheckedChange={(v) => onChange((d) => setRemote(d, district.id, null, v))} />
            {t("remote")}
          </ChoiceRow>
        ) : null}
      </div>
      {expanded ? <UpazilaList district={district} draft={draft} owners={owners} canEdit={canEdit} onChange={onChange} /> : null}
    </li>
  );
}

function UpazilaList({
  district, draft, owners, canEdit, onChange,
}: {
  district: District;
  draft: AreaDraft;
  owners: Owners;
  canEdit: boolean;
  onChange: React.Dispatch<React.SetStateAction<AreaDraft>>;
}) {
  const t = useTranslations("delivery.areas");
  const tc = useTranslations("delivery.common");
  const localName = useLocalName();
  const query = useUpazilas(district.id);
  if (query.isPending) return <Skeleton className="m-3 h-16" aria-busy="true" />;
  if (query.isError) return <Alert variant="danger">{tc("loadFailed")}</Alert>;
  const list = query.data ?? [];
  return (
    <div className="ms-6 rounded-card bg-sunken p-3 ps-4" data-testid="upazila-list">
      <p className="mb-1 text-sm text-muted">{t("upazilaHelp", { name: localName(district) })}</p>
      {list.length === 0 ? <p className="text-muted">{t("noUpazilas")}</p> : null}
      <ul className="grid gap-x-4 sm:grid-cols-2">
        {list.map((u) => {
          const k = areaKey(district.id, u.id);
          const entry = draft.get(k);
          return (
            <li key={u.id} className="flex flex-wrap items-center gap-x-3" data-testid="upazila-row">
              <ChoiceRow htmlFor={`u-${u.id}`} className="min-w-0 flex-1 basis-40">
                <Checkbox id={`u-${u.id}`} checked={!!entry} disabled={!canEdit} onCheckedChange={(v) => onChange((d) => toggleUpazila(d, district.id, u.id, v === true))} />
                <span className="min-w-0 [overflow-wrap:anywhere]">{localName(u)}</span>
              </ChoiceRow>
              <OwnerBadge owners={owners} keyName={k} />
              {entry ? (
                <ChoiceRow htmlFor={`ur-${u.id}`}>
                  <Switch id={`ur-${u.id}`} checked={entry.isRemote} disabled={!canEdit} onCheckedChange={(v) => onChange((d) => setRemote(d, district.id, u.id, v))} />
                  {t("remote")}
                </ChoiceRow>
              ) : null}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
