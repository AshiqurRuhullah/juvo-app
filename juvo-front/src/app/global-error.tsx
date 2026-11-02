"use client";

/** Last resort when even the root layout fails. Plain, self-contained markup (no providers are available here). */
export default function GlobalError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  return (
    <html lang="en">
      <body style={{ fontFamily: "system-ui, sans-serif", display: "grid", minHeight: "100dvh", placeItems: "center", margin: 0, background: "#FAF7F3", color: "#1C0807" }}>
        <main style={{ textAlign: "center", padding: "2rem", maxWidth: "28rem" }}>
          {/* eslint-disable-next-line @next/next/no-img-element -- the image component is not available if the root layout failed */}
          <img src="/brand/logo-badge-256.png" alt="Juvo BD" width={72} height={72} style={{ borderRadius: "50%" }} />
          <h1 style={{ fontSize: "1.75rem" }}>Something went wrong</h1>
          <p style={{ color: "#625755" }}>We could not show this page. Please try again in a moment.</p>
          <button onClick={reset} style={{ minHeight: 44, padding: "0 1.5rem", borderRadius: 12, border: 0, background: "#DB0100", color: "#fff", fontWeight: 600, fontSize: "1rem" }}>
            Try again
          </button>
        </main>
      </body>
    </html>
  );
}
