"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { ClipboardList, Heart, MapPin, ShoppingBag, UserRound } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { buttonVariants } from "@/components/ui/button";
import { toNumber } from "@/modules/catalog/format";
import { useMeSummary } from "@/modules/store/api";

function Tile({ href, icon, label, value, testId }: { href: string; icon: React.ReactNode; label: string; value: string; testId: string }) {
  return (
    <Link href={href} className="flex min-h-11 items-center gap-3 rounded-card border border-line bg-card p-4 transition-colors hover:border-primary" data-testid={testId}>
      <span className="flex size-11 shrink-0 items-center justify-center rounded-full bg-primary-soft text-primary-dark [&_svg]:size-5">{icon}</span>
      <span className="min-w-0">
        <span className="block text-sm text-muted">{label}</span>
        <span className="block text-xl font-semibold text-strong">{value}</span>
      </span>
    </Link>
  );
}

/** Counts and the default address for the account overview. The profile card above it stays a server component. */
export function AccountSummary() {
  const t = useTranslations("myaccount.overview");
  const summary = useMeSummary();

  if (summary.isPending) return <Skeleton className="h-32" />;
  if (summary.isError) return null;
  const s = summary.data;
  const address = s.defaultAddress;

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Tile href="/account/addresses" icon={<MapPin aria-hidden />} label={t("addresses")} value={String(toNumber(s.addressCount))} testId="tile-addresses" />
        <Tile href="/account/wishlist" icon={<Heart aria-hidden />} label={t("wishlist")} value={String(toNumber(s.wishlistCount))} testId="tile-wishlist" />
        <Tile href="/cart" icon={<ShoppingBag aria-hidden />} label={t("cart")} value={String(toNumber(s.cartItemCount))} testId="tile-cart" />
        <Tile href="/account/orders" icon={<ClipboardList aria-hidden />} label={t("orders")} value={String(toNumber(s.orderCount))} testId="tile-orders" />
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t("defaultAddress")}</CardTitle>
          <CardDescription>{address ? t("defaultAddressHelp") : t("noAddress")}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          {address ? (
            <p className="text-strong [overflow-wrap:anywhere]" data-testid="default-address">
              <span className="font-medium">{address.recipientName}</span>, {address.phone}
              <br />
              {[address.addressLine, address.area, address.city].filter(Boolean).join(", ")}
            </p>
          ) : null}
          <div className="flex flex-wrap gap-2">
            <Link href="/account/addresses" className={buttonVariants({ variant: "secondary" })}>
              <MapPin aria-hidden /> {address ? t("manageAddresses") : t("addAddress")}
            </Link>
            <Link href="/account/profile" className={buttonVariants({ variant: "secondary" })} data-testid="edit-profile-link">
              <UserRound aria-hidden /> {t("editProfile")}
            </Link>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
