import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { DesignGallery } from "@/components/layout/design-gallery";
import { PageHeader } from "@/components/layout/page-header";

export const metadata: Metadata = { title: "Design system", robots: { index: false, follow: false } };

/** Component gallery for designers, developers and the UI checks. Not available in production unless ENABLE_DESIGN_PAGE=true. */
export default function DesignPage() {
  if (process.env.NODE_ENV === "production" && process.env.ENABLE_DESIGN_PAGE !== "true") notFound();
  return (
    <main id="main" className="mx-auto max-w-7xl px-4 py-8 sm:px-6">
      <PageHeader title="Design system" description="Every component on the brand tokens. Light and dark themes follow the device or the theme switch." />
      <DesignGallery />
    </main>
  );
}
