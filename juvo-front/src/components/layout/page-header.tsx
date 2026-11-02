import Link from "next/link";
import { ChevronRight } from "lucide-react";
import { cn } from "@/lib/utils";

export type Crumb = { label: string; href?: string };

/** Consistent top of every page: breadcrumb, title, description and the primary action. */
export function PageHeader({
  title,
  description,
  crumbs,
  actions,
  className,
}: {
  title: string;
  description?: string;
  crumbs?: Crumb[];
  actions?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("mb-6 space-y-2", className)}>
      {crumbs && crumbs.length > 0 ? (
        <nav aria-label="Breadcrumb">
          <ol className="flex flex-wrap items-center gap-1 text-sm text-muted">
            {crumbs.map((crumb, index) => (
              <li key={`${crumb.label}-${index}`} className="flex items-center gap-1">
                {index > 0 ? <ChevronRight className="size-4 rtl:rotate-180" aria-hidden /> : null}
                {crumb.href ? (
                  <Link href={crumb.href} className="inline-flex min-h-6 items-center rounded px-0.5 hover:text-strong hover:underline">
                    {crumb.label}
                  </Link>
                ) : (
                  <span aria-current="page" className="text-strong">
                    {crumb.label}
                  </span>
                )}
              </li>
            ))}
          </ol>
        </nav>
      ) : null}
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold sm:text-3xl [overflow-wrap:anywhere]">{title}</h1>
          {description ? <p className="mt-1 max-w-2xl text-muted">{description}</p> : null}
        </div>
        {actions ? <div className="flex flex-wrap items-center gap-2">{actions}</div> : null}
      </div>
    </div>
  );
}
