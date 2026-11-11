"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { Heart } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/overlays";
import { cn } from "@/lib/utils";
import { useAddToWishlist, useRemoveFromWishlist, useWishlist } from "./api";
import { useStoreError } from "./hooks";
import { signInUrl, useStoreSession } from "./store-session";
import { toast } from "@/components/ui/toaster";

const base = "tap flex size-11 shrink-0 items-center justify-center rounded-full bg-card/95 text-strong shadow-sm transition-colors [@media(hover:hover)]:hover:text-primary";

/**
 * The wishlist heart. A signed-in customer saves or removes the product (state comes from one shared wishlist query); a visitor
 * gets a small note with a link to sign in and come straight back to this page.
 */
export function WishlistHeart({ productId, name, className }: { productId: string; name: string; className?: string }) {
  const t = useTranslations("store.wishlist");
  const { customer } = useStoreSession();
  const pathname = usePathname();
  const list = useWishlist(customer);
  const add = useAddToWishlist();
  const remove = useRemoveFromWishlist();
  const storeError = useStoreError();
  const saved = customer && (list.data ?? []).some((item) => item.productId === productId);

  if (!customer) {
    return (
      <Popover>
        <PopoverTrigger asChild>
          <button type="button" aria-label={t("add", { name })} className={cn(base, className)}>
            <Heart className="size-5" aria-hidden />
          </button>
        </PopoverTrigger>
        <PopoverContent align="end" className="w-64 space-y-2 p-4 text-sm">
          <p className="font-semibold text-strong">{t("guestTitle")}</p>
          <p className="text-muted">{t("guestBody")}</p>
          <Link href={signInUrl(pathname)} className="inline-flex min-h-11 items-center font-semibold text-primary underline-offset-4 hover:underline">
            {t("signIn")}
          </Link>
        </PopoverContent>
      </Popover>
    );
  }

  const toggle = async () => {
    try {
      if (saved) {
        await remove.mutateAsync(productId);
        toast.success(t("removed"));
      } else {
        await add.mutateAsync(productId);
        toast.success(t("saved"));
      }
    } catch (error) {
      toast.error(storeError(error));
    }
  };

  return (
    <button
      type="button"
      aria-pressed={saved}
      aria-label={saved ? t("remove", { name }) : t("add", { name })}
      disabled={add.isPending || remove.isPending}
      onClick={() => void toggle()}
      className={cn(base, saved && "text-primary", className)}
    >
      <Heart className={cn("size-5", saved && "fill-current")} aria-hidden />
    </button>
  );
}
