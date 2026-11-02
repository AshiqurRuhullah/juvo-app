import Image from "next/image";
import Link from "next/link";
import { cn } from "@/lib/utils";

/** The Juvo badge (generated from Logo/Official Logo.jpg by scripts/generate_brand_assets.py) with an optional wordmark. */
export function Logo({
  size = 40,
  wordmark = true,
  className,
  priority,
  href,
  label = "Juvo BD",
}: {
  size?: number;
  wordmark?: boolean;
  className?: string;
  priority?: boolean;
  href?: string;
  label?: string;
}) {
  const content = (
    <span className={cn("inline-flex items-center gap-2.5", className)}>
      <Image src="/brand/logo-badge.png" alt={wordmark ? "" : label} width={size} height={size} priority={priority} className="shrink-0 rounded-full" style={{ width: size, height: size }} />
      {wordmark ? <span className="font-display text-xl font-semibold tracking-tight text-strong">Juvo BD</span> : null}
    </span>
  );
  return href ? (
    <Link href={href} aria-label={label} className="inline-flex min-h-11 items-center rounded-control">
      {content}
    </Link>
  ) : (
    content
  );
}
