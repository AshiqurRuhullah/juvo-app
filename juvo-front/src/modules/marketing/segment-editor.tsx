"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Copy, Plus, Trash2, Users, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox, ChoiceRow, Field, Input, RadioGroup, RadioItem, Switch, Textarea } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useDebounced } from "@/lib/hooks";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber } from "@/modules/catalog/format";
import { useDivisions } from "@/modules/store/api";
import { useAllDistricts, useCopySegment, usePreviewCount, useProductSearch, useSaveSegment, type Segment } from "./api";
import { useMarketingPermissions } from "./common";
import {
  NAME_MAX,
  ORDER_STATUSES,
  RULE_FIELDS,
  RULE_TYPES,
  SEGMENT_RULES_MAX,
  changeRuleType,
  definitionOf,
  emptyRule,
  emptySegmentForm,
  fieldKey,
  hasFormErrors,
  newRuleKey,
  segmentFormFromApi,
  segmentToRequest,
  splitSegmentErrors,
  validateSegmentForm,
  type FormErrors,
  type RuleField,
  type RuleForm,
  type RuleType,
  type SegmentForm,
} from "./logic";

type Props = { open: boolean; onOpenChange: (open: boolean) => void; segment: Segment | null };

/** The segment rule builder. A built-in segment opens read-only with a "copy" button; a saved one is edited; null makes a new one. */
export default function SegmentEditorDialog({ open, onOpenChange, segment }: Props) {
  const t = useTranslations("marketing.segments");
  const tc = useTranslations("marketing.common");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="lg" closeLabel={tc("close")} data-testid="segment-dialog">
        <DialogTitle className="font-display text-xl font-semibold text-strong">{segment ? (segment.isBuiltIn ? t("viewTitle", { name: segment.name }) : t("editTitle")) : t("newTitle")}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">{segment?.isBuiltIn ? t("builtInHint") : t("editorHint")}</DialogDescription>
        <Body segment={segment} onClose={() => onOpenChange(false)} />
      </ModalContent>
    </Dialog>
  );
}

function Body({ segment, onClose }: { segment: Segment | null; onClose: () => void }) {
  const t = useTranslations("marketing.segments");
  const tc = useTranslations("marketing.common");
  const problemMessage = useProblemMessage();
  const perms = useMarketingPermissions();
  const save = useSaveSegment();
  const copy = useCopySegment();
  const builtIn = !!segment?.isBuiltIn;
  const readOnly = builtIn || !perms.segmentEdit;
  const [form, setForm] = React.useState<SegmentForm>(() => (segment ? segmentFormFromApi(segment) : emptySegmentForm()));
  const [errors, setErrors] = React.useState<FormErrors>({ fields: {} });
  const [serverFields, setServerFields] = React.useState<Record<string, string>>({});
  const [serverOther, setServerOther] = React.useState<string[]>([]);
  const [nameError, setNameError] = React.useState<string | null>(null);
  const [problem, setProblem] = React.useState<string | null>(null);

  const setRule = (key: string, update: (rule: RuleForm) => RuleForm) => {
    setForm((f) => ({ ...f, rules: f.rules.map((r) => (r.key === key ? update(r) : r)) }));
    setServerFields({});
  };

  // The live size of the rules as they stand: only when the rules are complete, after a short pause.
  const complete = React.useMemo(() => Object.keys(validateSegmentForm({ ...form, name: "x" }).fields).length === 0 && form.rules.length > 0, [form]);
  const definition = React.useMemo(() => (complete && perms.segmentView ? definitionOf(form) : null), [complete, form, perms.segmentView]);
  const settled = useDebounced(definition, 500);
  const preview = usePreviewCount(settled);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const found = validateSegmentForm(form);
    setErrors(found);
    setServerFields({});
    setServerOther([]);
    setNameError(null);
    setProblem(null);
    if (hasFormErrors(found)) return;
    try {
      await save.mutateAsync({ id: segment?.id, body: segmentToRequest(form) });
      toast.success(segment ? t("saved") : t("created"));
      onClose();
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.errors) {
        const all = fieldErrors(p);
        const split = splitSegmentErrors(all);
        setServerFields(split.fields);
        setServerOther(split.other);
        if (all.name) setNameError(all.name);
      }
      setProblem(problemMessage(p));
    }
  };

  const makeCopy = async () => {
    if (!segment) return;
    try {
      const made = await copy.mutateAsync(segment.id);
      toast.success(t("copied", { name: made.name }));
      onClose();
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <form onSubmit={submit} noValidate className="mt-4 space-y-5" data-testid="segment-form">
      <fieldset disabled={readOnly} className="min-w-0 space-y-5 border-0 p-0">
        {problem ? <Alert variant="danger">{problem}</Alert> : null}
        {serverOther.length > 0 ? (
          <Alert variant="danger">
            <ul className="list-disc ps-5">
              {serverOther.map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
          </Alert>
        ) : null}

        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("name")} htmlFor="seg-name" required error={errors.name ? t(`errors.${errors.name}`, { max: NAME_MAX }) : (nameError ?? undefined)}>
            <Input id="seg-name" value={form.name} maxLength={NAME_MAX} onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))} aria-invalid={!!errors.name || !!nameError} />
          </Field>
          <Field label={t("description")} htmlFor="seg-description">
            <Textarea id="seg-description" value={form.description} maxLength={300} onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))} className="min-h-12" />
          </Field>
        </div>

        <div className="space-y-1">
          <p className="text-sm font-medium text-strong">{t("combinator")}</p>
          <RadioGroup aria-label={t("combinator")} value={form.combinator} onValueChange={(v) => setForm((f) => ({ ...f, combinator: v === "or" ? "or" : "and" }))} className="sm:grid-cols-2">
            {(["and", "or"] as const).map((c) => (
              <ChoiceRow key={c} htmlFor={`comb-${c}`}>
                <RadioItem value={c} id={`comb-${c}`} />
                <span>
                  <span className="block font-medium">{t(`combinators.${c}`)}</span>
                  <span className="block text-sm text-muted">{t(`combinators.${c}Hint`)}</span>
                </span>
              </ChoiceRow>
            ))}
          </RadioGroup>
        </div>

        <ol className="space-y-3" aria-label={t("rules")}>
          {form.rules.map((rule, index) => (
            <li key={rule.key} className="rounded-card border border-line bg-sunken/40 p-3 sm:p-4" data-testid="rule-row">
              <RuleEditor
                index={index}
                rule={rule}
                errors={errors}
                serverFields={serverFields}
                readOnly={readOnly}
                canRemove={form.rules.length > 1}
                onChange={(update) => setRule(rule.key, update)}
                onRemove={() => setForm((f) => ({ ...f, rules: f.rules.filter((r) => r.key !== rule.key) }))}
              />
            </li>
          ))}
        </ol>
        {errors.rules ? <p role="alert" className="text-sm font-medium text-danger">{t(`errors.${errors.rules}`, { max: SEGMENT_RULES_MAX })}</p> : null}
        {!readOnly ? (
          <Button type="button" variant="secondary" disabled={form.rules.length >= SEGMENT_RULES_MAX} onClick={() => setForm((f) => ({ ...f, rules: [...f.rules, { ...emptyRule(), key: newRuleKey() }] }))} data-testid="add-rule">
            <Plus aria-hidden /> {t("addRule")}
          </Button>
        ) : null}
      </fieldset>

      {perms.segmentView ? <LiveCount loading={!!definition && (preview.isFetching || settled !== definition)} data={preview.data} error={preview.isError && !!settled} idle={!definition} /> : null}

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" variant="secondary" onClick={onClose}>
          {tc("close")}
        </Button>
        {builtIn && perms.segmentEdit ? (
          <Button type="button" onClick={() => void makeCopy()} loading={copy.isPending} data-testid="segment-copy-from-dialog">
            <Copy aria-hidden /> {t("copy")}
          </Button>
        ) : null}
        {!readOnly ? (
          <Button type="submit" loading={save.isPending} data-testid="segment-save">
            {t("save")}
          </Button>
        ) : null}
      </div>
    </form>
  );
}

function LiveCount({ loading, data, error, idle }: { loading: boolean; data: { total: number | string; reachable: { sms: number | string; email: number | string } } | undefined; error: boolean; idle: boolean }) {
  const t = useTranslations("marketing.segments");
  return (
    <div className="rounded-card border border-line bg-card p-3" aria-live="polite" data-testid="segment-live-count">
      <p className="flex items-center gap-2 text-sm font-medium text-strong">
        <Users className="size-4" aria-hidden /> {t("liveCount")}
      </p>
      {idle && !data ? (
        <p className="mt-1 text-sm text-muted">{t("liveIdle")}</p>
      ) : error ? (
        <p className="mt-1 text-sm text-danger-ink">{t("liveFailed")}</p>
      ) : loading && !data ? (
        <Skeleton className="mt-2 h-6 w-40" />
      ) : data ? (
        <p className={loading ? "mt-1 opacity-60" : "mt-1"}>
          <span className="text-xl font-bold tabular-nums text-strong" data-testid="live-total">
            {toNumber(data.total)}
          </span>{" "}
          <span className="text-muted">{t("liveCustomers")}</span>
          <span className="block text-sm text-muted">{t("liveReachable", { sms: toNumber(data.reachable.sms), email: toNumber(data.reachable.email) })}</span>
        </p>
      ) : null}
    </div>
  );
}

/* ---- One rule --------------------------------------------------------------------------------------------------------------- */

function RuleEditor({
  index,
  rule,
  errors,
  serverFields,
  readOnly,
  canRemove,
  onChange,
  onRemove,
}: {
  index: number;
  rule: RuleForm;
  errors: FormErrors;
  serverFields: Record<string, string>;
  readOnly: boolean;
  canRemove: boolean;
  onChange: (update: (rule: RuleForm) => RuleForm) => void;
  onRemove: () => void;
}) {
  const t = useTranslations("marketing.segments");
  const tr = useTranslations("marketing.segments.ruleTypes");
  const tf = useTranslations("marketing.segments.fields");
  const tc = useTranslations("marketing.common");
  const base = `rule-${rule.key}`;
  const visible = RULE_FIELDS[rule.type];

  const errorFor = (field: RuleField) => {
    const client = errors.fields[fieldKey(index, field)];
    return client ? t(`errors.${client}`) : serverFields[`${index}.${field}`];
  };
  const set = <K extends keyof RuleForm>(key: K, value: RuleForm[K]) => onChange((r) => ({ ...r, [key]: value }));
  const numberField = (field: "days" | "minOrders" | "minSpend" | "periodDays" | "idleHours", required: boolean) => (
    <Field label={tf(field)} htmlFor={`${base}-${field}`} required={required} hint={tf(`${field}Hint`)} error={errorFor(field)}>
      <Input id={`${base}-${field}`} inputMode={field === "minSpend" ? "decimal" : "numeric"} value={rule[field]} onChange={(e) => set(field, e.target.value)} aria-invalid={!!errorFor(field)} />
    </Field>
  );

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-end gap-3">
        <Field label={t("ruleNumber", { n: index + 1 })} htmlFor={`${base}-type`} className="min-w-0 flex-1 basis-56">
          <Select
            id={`${base}-type`}
            value={rule.type}
            onValueChange={(v) => onChange((r) => changeRuleType(r, v as RuleType))}
            options={RULE_TYPES.map((type) => ({ value: type, label: tr(type) }))}
            placeholder={t("ruleType")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
          />
        </Field>
        {canRemove && !readOnly ? (
          <Button type="button" variant="ghost" onClick={onRemove} aria-label={t("removeRule", { n: index + 1 })} data-testid="remove-rule">
            <Trash2 aria-hidden /> <span className="sm:inline">{t("remove")}</span>
          </Button>
        ) : null}
      </div>
      <p className="text-sm text-muted">{tr(`${rule.type}Hint`)}</p>

      <div className="grid gap-3 sm:grid-cols-2">
        {visible.includes("days") ? numberField("days", true) : null}
        {visible.includes("minOrders") ? numberField("minOrders", true) : null}
        {visible.includes("minSpend") ? numberField("minSpend", true) : null}
        {visible.includes("periodDays") ? numberField("periodDays", false) : null}
        {visible.includes("idleHours") ? numberField("idleHours", false) : null}
        {visible.includes("channel") ? (
          <Field label={tf("channel")} htmlFor={`${base}-channel`} required error={errorFor("channel")}>
            <Select
              id={`${base}-channel`}
              value={rule.channel}
              onValueChange={(v) => set("channel", v === "email" ? "email" : "sms")}
              options={[
                { value: "sms", label: tf("channelSms") },
                { value: "email", label: tf("channelEmail") },
              ]}
              placeholder={tf("channel")}
              searchPlaceholder={tc("search")}
              emptyText={tc("noMatch")}
            />
          </Field>
        ) : null}
        {visible.includes("granted") ? (
          <ChoiceRow htmlFor={`${base}-granted`} className="self-end">
            <Switch id={`${base}-granted`} checked={rule.granted} onCheckedChange={(v) => set("granted", v)} />
            <span>{rule.granted ? tf("grantedYes") : tf("grantedNo")}</span>
          </ChoiceRow>
        ) : null}
        {visible.includes("status") ? (
          <Field label={tf("status")} htmlFor={`${base}-status`} required error={errorFor("status")}>
            <Select
              id={`${base}-status`}
              value={rule.status}
              onValueChange={(v) => set("status", v)}
              options={ORDER_STATUSES.map((s) => ({ value: s, label: tf(`orderStatus.${s}`) }))}
              placeholder={tf("status")}
              searchPlaceholder={tc("search")}
              emptyText={tc("noMatch")}
            />
          </Field>
        ) : null}
      </div>

      {visible.includes("level") ? (
        <div className="space-y-2">
          <Field label={tf("level")} htmlFor={`${base}-level`} required>
            <RadioGroup
              id={`${base}-level`}
              value={rule.level}
              onValueChange={(v) => onChange((r) => ({ ...r, level: v === "division" ? "division" : "district", ids: [] }))}
              className="sm:grid-cols-2"
              aria-label={tf("level")}
            >
              {(["district", "division"] as const).map((level) => (
                <ChoiceRow key={level} htmlFor={`${base}-level-${level}`}>
                  <RadioItem value={level} id={`${base}-level-${level}`} />
                  <span>{tf(`level_${level}`)}</span>
                </ChoiceRow>
              ))}
            </RadioGroup>
          </Field>
          <PlacePicker idBase={base} level={rule.level} value={rule.ids} onChange={(ids) => set("ids", ids)} error={errorFor("ids")} />
        </div>
      ) : null}

      {visible.includes("productIds") ? <ProductPicker idBase={base} value={rule.productIds} onChange={(ids) => set("productIds", ids)} readOnly={readOnly} /> : null}
    </div>
  );
}

/** Divisions or districts as a list of checkboxes (a district can be searched by name). */
function PlacePicker({ idBase, level, value, onChange, error }: { idBase: string; level: "division" | "district"; value: string[]; onChange: (ids: string[]) => void; error?: string }) {
  const t = useTranslations("marketing.segments.fields");
  const divisions = useDivisions();
  const districts = useAllDistricts();
  const [filter, setFilter] = React.useState("");
  const options = React.useMemo(() => {
    const source = level === "division" ? (divisions.data ?? []) : (districts.data ?? []);
    return source.map((p) => ({ id: p.id, name: p.nameEn })).sort((a, b) => a.name.localeCompare(b.name));
  }, [level, divisions.data, districts.data]);
  const shown = options.filter((o) => o.name.toLowerCase().includes(filter.trim().toLowerCase()));
  const loading = level === "division" ? divisions.isPending : districts.isPending;
  const toggle = (id: string, on: boolean) => onChange(on ? [...value, id] : value.filter((x) => x !== id));

  return (
    <div className="space-y-2">
      <p className="text-sm font-medium text-strong">
        {level === "division" ? t("pickDivisions") : t("pickDistricts")} <span className="text-muted">({t("picked", { count: value.length })})</span>
      </p>
      {options.length > 8 ? <Input aria-label={t("filterPlaces")} placeholder={t("filterPlaces")} value={filter} onChange={(e) => setFilter(e.target.value)} /> : null}
      {loading ? (
        <Skeleton className="h-24" />
      ) : (
        <ul className="grid max-h-56 gap-x-4 overflow-y-auto rounded-control border border-line bg-card p-2 sm:grid-cols-2" aria-label={t("places")}>
          {shown.map((o) => (
            <li key={o.id}>
              <ChoiceRow htmlFor={`${idBase}-place-${o.id}`}>
                <Checkbox id={`${idBase}-place-${o.id}`} checked={value.includes(o.id)} onCheckedChange={(v) => toggle(o.id, v === true)} />
                <span>{o.name}</span>
              </ChoiceRow>
            </li>
          ))}
        </ul>
      )}
      {error ? (
        <p role="alert" className="text-sm font-medium text-danger">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/** Products a wishlist rule is limited to (optional). Search needs the catalog permission; without it the chosen ids stay as they are. */
function ProductPicker({ idBase, value, onChange, readOnly }: { idBase: string; value: string[]; onChange: (ids: string[]) => void; readOnly: boolean }) {
  const t = useTranslations("marketing.segments.fields");
  const perms = useMarketingPermissions();
  const [text, setText] = React.useState("");
  const search = useProductSearch(useDebounced(text, 350), perms.catalogView && !readOnly);
  // Names of the products picked in this session (an id loaded from a saved segment shows a short id: the API holds no name for it).
  const [names, setNames] = React.useState<Record<string, string>>({});
  const results = (search.data?.items ?? []).filter((p) => !value.includes(p.id));

  return (
    <div className="space-y-2">
      <p className="text-sm font-medium text-strong">{t("productsOptional")}</p>
      <p className="text-sm text-muted">{t("productsHint")}</p>
      {value.length > 0 ? (
        <ul className="flex flex-wrap gap-2" aria-label={t("chosenProducts")}>
          {value.map((id) => (
            <li key={id}>
              <Badge variant="brand" className="gap-1 ps-3 pe-1">
                {names[id] ?? id.slice(0, 8)}
                {!readOnly ? (
                  <button type="button" className="tap -my-3 flex items-center justify-center rounded-full" aria-label={t("removeProduct", { name: names[id] ?? id.slice(0, 8) })} onClick={() => onChange(value.filter((x) => x !== id))}>
                    <X className="size-4" aria-hidden />
                  </button>
                ) : null}
              </Badge>
            </li>
          ))}
        </ul>
      ) : null}
      {perms.catalogView && !readOnly ? (
        <>
          <Input id={`${idBase}-product-search`} type="search" aria-label={t("searchProducts")} placeholder={t("searchProducts")} value={text} onChange={(e) => setText(e.target.value)} autoComplete="off" />
          {results.length > 0 ? (
            <ul className="divide-y divide-line rounded-control border border-line bg-card">
              {results.map((p) => (
                <li key={p.id}>
                  <button type="button" className="flex min-h-11 w-full items-center justify-between gap-3 px-3 text-start text-strong hover:bg-sunken" onClick={() => {
                    setNames((old) => ({ ...old, [p.id]: p.nameEn }));
                    onChange([...value, p.id]);
                  }}>
                    <span className="[overflow-wrap:anywhere]">{p.nameEn}</span>
                    <Plus className="size-4 shrink-0" aria-hidden />
                  </button>
                </li>
              ))}
            </ul>
          ) : null}
        </>
      ) : !readOnly ? (
        <p className="text-sm text-muted">{t("noCatalogPermission")}</p>
      ) : null}
    </div>
  );
}
