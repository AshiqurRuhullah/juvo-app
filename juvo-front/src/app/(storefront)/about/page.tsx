import type { Metadata } from "next";
import { contentMetadata, ContentPageView } from "@/modules/content/content-page";

export const generateMetadata = (): Promise<Metadata> => contentMetadata("about");

export default function Page() {
  return <ContentPageView pageKey="about" />;
}
