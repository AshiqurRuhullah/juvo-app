import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { TransferNew } from "@/modules/inventory/transfer-new";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("inventory.newTransfer");
  return { title: t("title") };
}

export default async function NewTransferPage() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "inventory.transfer.request", "anywhere");
  return <TransferNew />;
}
