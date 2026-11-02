import type { MetadataRoute } from "next";

/** Installable web app (SRS 2.5: the site is the mobile app). Icons are generated from the logo. */
export default function manifest(): MetadataRoute.Manifest {
  return {
    name: "Juvo BD",
    short_name: "Juvo",
    description: "Fashion, shoes, dresses and purses from Juvo BD.",
    start_url: "/",
    display: "standalone",
    background_color: "#FAF7F3",
    theme_color: "#DB0100",
    icons: [
      { src: "/brand/icon-192.png", sizes: "192x192", type: "image/png" },
      { src: "/brand/icon-512.png", sizes: "512x512", type: "image/png" },
      { src: "/brand/icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
    ],
  };
}
