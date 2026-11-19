import type { Metadata } from "next";
import { contentMetadata, ContentPageView } from "@/modules/content/content-page";

export const generateMetadata = (): Promise<Metadata> => contentMetadata("terms");

export default function Page() {
  return <ContentPageView pageKey="terms" />;
}
