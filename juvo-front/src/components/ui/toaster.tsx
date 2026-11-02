"use client";

import { Toaster as Sonner, toast } from "sonner";
import { useIsMobile } from "@/lib/hooks";

export { toast };

/** Short success or error messages: top right on desktop, bottom centre on phones (SRS 3.3). */
export function Toaster() {
  const isMobile = useIsMobile();
  return <Sonner position={isMobile ? "bottom-center" : "top-right"} closeButton duration={5000} offset={{ top: 16, right: 16, bottom: 80, left: 16 }} />;
}
