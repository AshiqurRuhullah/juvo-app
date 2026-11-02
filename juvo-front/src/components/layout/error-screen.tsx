import Link from "next/link";
import { Logo } from "./logo";
import { Button } from "@/components/ui/button";

/** Friendly full-page message with the logo, for 401, 403, 404 and 500. Plain language, one clear next step. */
export function ErrorScreen({
  code,
  title,
  body,
  primary,
  secondary,
  reference,
}: {
  code: string;
  title: string;
  body: string;
  primary: { label: string; href?: string; onClick?: () => void };
  secondary?: { label: string; href: string };
  /** Correlation id the user can quote to support. */
  reference?: string;
}) {
  return (
    <main id="main" className="flex min-h-dvh flex-col items-center justify-center gap-6 px-6 py-12 text-center">
      <Logo size={72} wordmark={false} href="/" label="Juvo BD" priority />
      <div className="space-y-3">
        <p className="font-display text-6xl font-semibold text-primary" aria-hidden>
          {code}
        </p>
        <h1 className="text-2xl font-semibold sm:text-3xl">{title}</h1>
        <p className="mx-auto max-w-md text-muted">{body}</p>
        {reference ? <p className="text-xs text-muted">Reference: {reference}</p> : null}
      </div>
      <div className="flex flex-wrap items-center justify-center gap-3">
        {primary.href ? (
          <Button asChild size="lg">
            <Link href={primary.href}>{primary.label}</Link>
          </Button>
        ) : (
          <Button size="lg" onClick={primary.onClick}>
            {primary.label}
          </Button>
        )}
        {secondary ? (
          <Button asChild size="lg" variant="secondary">
            <Link href={secondary.href}>{secondary.label}</Link>
          </Button>
        ) : null}
      </div>
    </main>
  );
}
