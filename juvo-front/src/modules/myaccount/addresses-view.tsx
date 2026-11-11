"use client";

import * as React from "react";
import { useLocale, useTranslations } from "next-intl";
import { MapPin, Pencil, Plus, Star, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Alert, Badge, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useCartErrorText } from "@/modules/cart/use-cart-error";
import { useAddresses, useCountries, useDeleteAddress, useSetDefaultAddress, type Address } from "@/modules/store/api";
import { AddressDialog } from "./address-dialog";

function AddressCard({ address, countryName, onEdit, onDelete }: { address: Address; countryName: string; onEdit: () => void; onDelete: () => void }) {
  const t = useTranslations("myaccount.addresses");
  const makeDefault = useSetDefaultAddress();
  const errorText = useCartErrorText();
  const abroad = address.countryCode !== "BD";
  const lines = [address.addressLine, address.landmark, address.area, address.city, address.stateProvince, address.postalCode, abroad ? countryName : null].filter(Boolean);

  return (
    <Card className="flex flex-col gap-3 p-4" data-testid="address-card">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="brand">{t(`labels.${address.label === "Office" || address.label === "Other" ? address.label : "Home"}`)}</Badge>
        {address.isDefault ? <Badge variant="success">{t("default")}</Badge> : null}
        {address.pinOutsideCountry ? <Badge variant="warning">{t("pinOutside")}</Badge> : null}
      </div>
      <div className="min-w-0 flex-1 text-strong [overflow-wrap:anywhere]">
        <p className="font-medium">{address.recipientName}</p>
        <p className="text-sm text-muted">{address.phone}</p>
        <p className="mt-1">{lines.join(", ")}</p>
        {address.latitude != null ? (
          <p className="mt-1 flex items-center gap-1 text-sm text-muted">
            <MapPin className="size-4" aria-hidden /> {t("pinSaved")}
          </p>
        ) : null}
      </div>
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" onClick={onEdit} aria-label={t("editAria", { name: address.recipientName })}>
          <Pencil aria-hidden /> {t("edit")}
        </Button>
        {!address.isDefault ? (
          <Button
            variant="secondary"
            loading={makeDefault.isPending}
            onClick={() => makeDefault.mutate(address.id, { onSuccess: () => toast.success(t("defaultSet")), onError: (failure) => toast.error(errorText(failure)) })}
          >
            <Star aria-hidden /> {t("setDefault")}
          </Button>
        ) : null}
        <Button variant="ghost" className="text-danger hover:text-danger" onClick={onDelete} aria-label={t("deleteAria", { name: address.recipientName })}>
          <Trash2 aria-hidden /> {t("delete")}
        </Button>
      </div>
    </Card>
  );
}

export function AddressesView() {
  const t = useTranslations("myaccount.addresses");
  const locale = useLocale();
  const errorText = useCartErrorText();
  const addresses = useAddresses();
  const countries = useCountries();
  const remove = useDeleteAddress();
  const [editing, setEditing] = React.useState<Address | null>(null);
  const [open, setOpen] = React.useState(false);
  const [deleting, setDeleting] = React.useState<Address | null>(null);

  if (addresses.isPending) {
    return (
      <div className="grid gap-4 md:grid-cols-2">
        <Skeleton className="h-48" />
        <Skeleton className="h-48" />
      </div>
    );
  }
  if (addresses.isError) return <Alert variant="danger">{errorText(addresses.error)}</Alert>;

  const list = addresses.data;
  const countryName = (code: string) => {
    const c = countries.data?.find((x) => x.code === code);
    return c ? (locale === "bn" && c.nameBn ? c.nameBn : c.nameEn) : code;
  };
  const add = () => {
    setEditing(null);
    setOpen(true);
  };

  const confirmDelete = async () => {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast.success(t("deleted"));
      setDeleting(null);
    } catch (failure) {
      toast.error(errorText(failure));
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={add} data-testid="add-address">
          <Plus aria-hidden /> {t("add")}
        </Button>
      </div>
      {list.length === 0 ? (
        <Card>
          <EmptyState icon={<MapPin aria-hidden />} title={t("empty.title")} description={t("empty.text")} action={<Button onClick={add}>{t("add")}</Button>} />
        </Card>
      ) : (
        <ul className="grid gap-4 md:grid-cols-2" aria-label={t("listLabel")}>
          {list.map((address) => (
            <li key={address.id} className="flex">
              <AddressCard
                address={address}
                countryName={countryName(address.countryCode)}
                onEdit={() => {
                  setEditing(address);
                  setOpen(true);
                }}
                onDelete={() => setDeleting(address)}
              />
            </li>
          ))}
        </ul>
      )}

      <AddressDialog open={open} onOpenChange={setOpen} address={editing} first={list.length === 0} />
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(next) => !next && setDeleting(null)}
        title={t("deleteTitle")}
        description={t("deleteText", { name: deleting?.recipientName ?? "" })}
        confirmLabel={t("delete")}
        cancelLabel={t("cancel")}
        closeLabel={t("close")}
        destructive
        loading={remove.isPending}
        onConfirm={confirmDelete}
      />
    </div>
  );
}
