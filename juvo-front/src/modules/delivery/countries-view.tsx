"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Lock, Pencil, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { ChoiceRow, Field, Input, Switch } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { SearchBox, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { PAGE_SIZE } from "@/lib/pagination";
import { useCountries, useSaveCountry, useZones, type Country, type Zone } from "./api";
import { ActiveBadge, useDeliveryRights } from "./common";
import { countryToForm, countryToRequest, emptyCountryForm, matchesName, validateCountry, type CountryErrors, type CountryForm } from "./logic";

/** The international country list: which countries the shop delivers to and which international zone prices each. Bangladesh is priced by area and cannot be switched off. */
export function CountriesView() {
  const t = useTranslations("delivery.countries");
  const tc = useTranslations("delivery.common");
  const labels = useTableLabels();
  const rights = useDeliveryRights();
  const query = useCountries(true);
  const zones = useZones();
  const [searchText, setSearchText] = React.useState("");
  const [page, setPage] = React.useState(1);
  const [editing, setEditing] = React.useState<Country | "new" | null>(null);
    const zoneName = React.useMemo(() => new Map((zones.data ?? []).map((z) => [z.id, z.nameEn])), [zones.data]);
  const filtered = React.useMemo(() => (query.data ?? []).filter((c) => matchesName(c, searchText) || c.code.toLowerCase() === searchText.trim().toLowerCase()), [query.data, searchText]);
  const rows = React.useMemo(() => filtered.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE), [filtered, page]);

  const columns = React.useMemo<ColumnDef<Country, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.country"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="block min-w-0 [overflow-wrap:anywhere]" data-testid="country-name">
            <span className="font-semibold text-strong">{row.original.nameEn}</span>
            {row.original.nameBn ? <span className="block text-xs text-muted">{row.original.nameBn}</span> : null}
          </span>
        ),
      },
      { id: "code", header: t("columns.code"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.code} · +{row.original.phoneCode}</span> },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <ActiveBadge isActive={row.original.isEnabled} /> },
      {
        id: "zone",
        header: t("columns.zone"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) =>
          row.original.code === "BD" ? (
            <span className="text-muted">{t("byArea")}</span>
          ) : row.original.zoneId ? (
            <span>{zoneName.get(row.original.zoneId) ?? t("unknownZone")}</span>
          ) : (
            <Badge variant="warning">{t("noZone")}</Badge>
          ),
      },
      {
        id: "actions",
        header: "",
        enableSorting: false,
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) =>
          rights.countryEnable ? (
            <Button variant="secondary" onClick={() => setEditing(row.original)} aria-label={t("edit", { name: row.original.nameEn })} data-testid="country-edit">
              <Pencil aria-hidden /> {t("editShort")}
            </Button>
          ) : null,
      },
    ],
    [t, zoneName, rights.countryEnable],
  );

  return (
    <div className="space-y-4" data-testid="countries">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-muted">{t("subtitle")}</p>
        {rights.countryEnable ? (
          <Button onClick={() => setEditing("new")} data-testid="country-new">
            <Plus aria-hidden /> {t("add")}
          </Button>
        ) : null}
      </div>
      {!rights.countryEnable ? (
        <Alert variant="info" data-testid="countries-read-only">
          <span className="flex items-start gap-2">
            <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>{t("readOnly")}</span>
          </span>
        </Alert>
      ) : null}
      <SearchBox
        className="max-w-xl"
        value={searchText}
        onChange={(v) => {
          setSearchText(v);
          setPage(1);
        }}
        placeholder={t("searchPlaceholder")}
        label={t("search")}
      />
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={rows} loading={query.isLoading} getRowId={(c) => c.id} labels={labels} page={page} pageSize={PAGE_SIZE} total={filtered.length} onPageChange={setPage} />
      <CountryDialog target={editing} zones={zones.data ?? []} onClose={() => setEditing(null)} />
    </div>
  );
}

function CountryDialog({ target, zones, onClose }: { target: Country | "new" | null; zones: readonly Zone[]; onClose: () => void }) {
  const t = useTranslations("delivery.countries");
  const tc = useTranslations("delivery.common");
  return (
    <Dialog open={target !== null} onOpenChange={(open) => (open ? undefined : onClose())}>
      <ModalContent size="md" closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{target === "new" || target === null ? t("addTitle") : t("editTitle", { name: target.nameEn })}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{t("dialogDescription")}</DialogDescription>
        {target !== null ? <CountryBody country={target === "new" ? null : target} zones={zones} close={onClose} /> : null}
      </ModalContent>
    </Dialog>
  );
}

function CountryBody({ country, zones, close }: { country: Country | null; zones: readonly Zone[]; close: () => void }) {
  const t = useTranslations("delivery.countries");
  const te = useTranslations("delivery.countries.errors");
  const tc = useTranslations("delivery.common");
  const problemMessage = useProblemMessage();
  const save = useSaveCountry();
  const isNew = !country;
  const bd = country?.code === "BD";
  const [values, setValues] = React.useState<CountryForm>(() => (country ? countryToForm(country) : emptyCountryForm()));
  const [errors, setErrors] = React.useState<CountryErrors>({});
  const [failure, setFailure] = React.useState<string | null>(null);
  const set = <K extends keyof CountryForm>(key: K, value: CountryForm[K]) => setValues((old) => ({ ...old, [key]: value }));
  const international = zones.filter((z) => z.kind === "International");
  const err = (key: keyof CountryErrors) => (errors[key] ? te(errors[key]!) : undefined);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const found = validateCountry(values, isNew);
    setErrors(found);
    setFailure(null);
    if (Object.keys(found).length) return;
    try {
      await save.mutateAsync({ id: country?.id, body: countryToRequest(values) });
      toast.success(isNew ? t("added") : t("saved"));
      close();
    } catch (e) {
      setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-4">
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("code")} htmlFor="co-code" required hint={t("codeHint")} error={err("code")}>
          <Input id="co-code" value={values.code} maxLength={2} disabled={!isNew} onChange={(e) => set("code", e.target.value.toUpperCase())} aria-invalid={!!errors.code} autoComplete="off" />
        </Field>
        <Field label={t("phoneCode")} htmlFor="co-phone" required hint={t("phoneHint")} error={err("phoneCode")}>
          <Input id="co-phone" inputMode="numeric" value={values.phoneCode} maxLength={9} onChange={(e) => set("phoneCode", e.target.value)} aria-invalid={!!errors.phoneCode} autoComplete="off" />
        </Field>
        <Field label={t("nameEn")} htmlFor="co-name" required error={err("nameEn")}>
          <Input id="co-name" value={values.nameEn} maxLength={100} onChange={(e) => set("nameEn", e.target.value)} aria-invalid={!!errors.nameEn} autoComplete="off" />
        </Field>
        <Field label={t("nameBn")} htmlFor="co-name-bn">
          <Input id="co-name-bn" value={values.nameBn} maxLength={100} onChange={(e) => set("nameBn", e.target.value)} autoComplete="off" />
        </Field>
      </div>
      {bd ? (
        <Alert variant="info">{t("bdNote")}</Alert>
      ) : (
        <>
          <Field label={t("zone")} htmlFor="co-zone" hint={t("zoneHint")} error={err("zoneId")}>
            <Select
              id="co-zone"
              value={values.zoneId || "__none"}
              onValueChange={(v) => set("zoneId", v === "__none" ? "" : v)}
              options={[{ value: "__none", label: t("noZone") }, ...international.map((z) => ({ value: z.id, label: z.nameEn, hint: z.isActive ? undefined : t("zoneOff") }))]}
              placeholder={t("zone")}
              searchPlaceholder={tc("search")}
              emptyText={tc("noMatch")}
              invalid={!!errors.zoneId}
            />
          </Field>
          <ChoiceRow htmlFor="co-enabled">
            <Switch id="co-enabled" checked={values.isEnabled} onCheckedChange={(v) => set("isEnabled", v)} />
            {t("enabled")}
          </ChoiceRow>
        </>
      )}
      {failure ? (
        <Alert variant="danger" data-testid="country-form-error">
          {failure}
        </Alert>
      ) : null}
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={save.isPending}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" loading={save.isPending} data-testid="country-save">
          {isNew ? t("add") : t("save")}
        </Button>
      </div>
    </form>
  );
}
