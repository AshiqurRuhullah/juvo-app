"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { ClipboardList } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { usePermissions } from "@/components/providers/session-provider";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions, VariantText } from "@/modules/inventory/common";
import { useSuggestions, type Suggestions } from "./api";
import { useMoney2 } from "./common";
import { suggestionToLine } from "./logic";
import { usePurchasingPermissions } from "./permissions";
import { SEED_KEY, type PoSeed } from "./seed";

type Group = Suggestions["groups"][number];

/** Reorder suggestions of one branch: variants at or below their reorder level, grouped by the supplier they were last ordered from. */
export function SuggestionsView() {
  const t = useTranslations("purchasing.suggest");
  const tc = useTranslations("purchasing.common");
  const router = useRouter();
  const money = useMoney2();
  const perms = usePurchasingPermissions();
  const branches = useBranchOptions();
  const { activeBranchId } = usePermissions();
  const [chosen, setChosen] = React.useState<string>("");
  const branchId = chosen || (activeBranchId && branches.some((b) => b.value === activeBranchId) ? activeBranchId : branches[0]?.value) || "";
  const query = useSuggestions(branchId || undefined);

  const start = (group: Group) => {
    const seed: PoSeed = {
      branchId,
      supplierId: group.supplierId ?? "",
      lines: group.lines.map((l) => ({
        variantId: l.variantId,
        productId: l.productId,
        productName: l.productName,
        sku: l.sku,
        colorName: l.colorName ?? null,
        size: l.size ?? null,
        ...suggestionToLine({ suggestedQuantity: toNumber(l.suggestedQuantity), lastUnitCost: l.lastUnitCost }),
      })),
    };
    try {
      sessionStorage.setItem(SEED_KEY, JSON.stringify(seed));
    } catch {
      // Private mode: the draft opens empty and the lines can be added by hand.
    }
    router.push("/admin/purchasing/orders/new?seed=1");
  };

  return (
    <div className="space-y-4">
      <p className="text-muted">{t("intro")}</p>
      {branches.length > 1 ? (
        <Field label={tc("branch")} htmlFor="sg-branch" className="max-w-sm">
          <Select id="sg-branch" value={branchId} onValueChange={setChosen} options={branches} placeholder={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
      ) : null}
      {query.isLoading ? <Skeleton className="h-40" aria-busy="true" /> : null}
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {query.data && query.data.groups.length === 0 ? (
        <Card>
          <EmptyState icon={<ClipboardList />} title={t("emptyTitle")} description={t("emptyText")} />
        </Card>
      ) : null}
      {query.data?.groups.map((group) => (
        <Card key={group.supplierId ?? "none"} data-testid="suggestion-group">
          <CardHeader className="flex-row flex-wrap items-center justify-between gap-2">
            <CardTitle>{group.supplierName ?? t("noSupplier")}</CardTitle>
            {perms.poCreate ? (
              <Button onClick={() => start(group)} data-testid="start-draft">
                {t("startDraft")}
              </Button>
            ) : null}
          </CardHeader>
          <CardContent>
            <ul className="divide-y divide-line">
              {group.lines.map((l) => (
                <li key={l.variantId} className="flex flex-col gap-1 py-3 sm:flex-row sm:items-center sm:justify-between">
                  <div className="min-w-0">
                    <p className="font-semibold text-strong [overflow-wrap:anywhere]">{l.productName}</p>
                    <VariantText sku={l.sku} colorName={l.colorName} size={l.size} />
                  </div>
                  <p className="text-sm tabular-nums text-muted">
                    {t("stockLine", { available: toNumber(l.available), reorder: toNumber(l.reorderLevel), onOrder: toNumber(l.onOrder) })}
                  </p>
                  <p className="text-sm font-semibold tabular-nums text-strong">
                    {t("suggested", { count: toNumber(l.suggestedQuantity) })}
                    {l.lastUnitCost !== null && l.lastUnitCost !== undefined ? <span className="ms-2 font-normal text-muted">{t("lastCost", { cost: money(l.lastUnitCost) })}</span> : null}
                  </p>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      ))}
    </div>
  );
}
