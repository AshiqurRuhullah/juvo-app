import Link from "next/link";
import { ChevronRight } from "lucide-react";

export type Crumb = { label: string; href?: string };

/** Breadcrumb trail (Home › Women › Dresses). The last item is the current page. */
export function Breadcrumbs({ items, label }: { items: Crumb[]; label: string }) {
  return (
    <nav aria-label={label} className="mb-3">
      <ol className="flex flex-wrap items-center gap-x-1 text-sm text-muted">
        {items.map((item, index) => (
          <li key={`${item.label}-${index}`} className="flex items-center gap-1">
            {index > 0 ? <ChevronRight className="size-4 rtl:rotate-180" aria-hidden /> : null}
            {item.href ? (
              <Link href={item.href} className="inline-flex min-h-11 min-w-11 items-center justify-center hover:text-strong hover:underline">
                {item.label}
              </Link>
            ) : (
              <span aria-current="page" className="font-medium text-strong">
                {item.label}
              </span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}

/** Title block of a listing page. */
export function PageHeading({ title, subtitle }: { title: string; subtitle?: string }) {
  return (
    <div className="mb-6 space-y-1">
      <h1 className="text-3xl font-semibold sm:text-4xl">{title}</h1>
      {subtitle ? <p className="max-w-2xl text-muted">{subtitle}</p> : null}
    </div>
  );
}
