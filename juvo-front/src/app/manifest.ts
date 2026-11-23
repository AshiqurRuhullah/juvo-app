import type { MetadataRoute } from "next";
import { MANIFEST_BACKGROUND_COLOR, MANIFEST_THEME_COLOR } from "@/lib/pwa/colors";

/**
 * Installable web app (FR-AF07). Colours are the brand tokens in src/styles/tokens.css (brand-primary, brand-accent; a unit test
 * compares them). A manifest holds one language, so names and shortcut titles are English (the default); the pages themselves
 * follow the visitor's language cookie.
 */
export default function manifest(): MetadataRoute.Manifest {
  return {
    id: "/",
    name: "Juvo BD",
    short_name: "Juvo",
    description: "Fashion, shoes, dresses and purses from Juvo BD.",
    lang: "en",
    dir: "ltr",
    start_url: "/",
    scope: "/",
    display: "standalone",
    orientation: "portrait",
    categories: ["shopping", "lifestyle"],
    background_color: MANIFEST_BACKGROUND_COLOR,
    theme_color: MANIFEST_THEME_COLOR,
    icons: [
      { src: "/brand/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
      { src: "/brand/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
      { src: "/brand/icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
    ],
    shortcuts: [
      { name: "Shop", short_name: "Shop", url: "/shop", icons: [{ src: "/brand/icon-192.png", sizes: "192x192", type: "image/png" }] },
      { name: "Track order", short_name: "Track", url: "/track", icons: [{ src: "/brand/icon-192.png", sizes: "192x192", type: "image/png" }] },
      { name: "Cart", short_name: "Cart", url: "/cart", icons: [{ src: "/brand/icon-192.png", sizes: "192x192", type: "image/png" }] },
    ],
  };
}
