import * as React from "react";
import Image, { type ImageProps } from "next/image";
import { cva, type VariantProps } from "class-variance-authority";
import { AlertCircle, CheckCircle2, Info, TriangleAlert } from "lucide-react";
import { cn } from "@/lib/utils";

/** Cards: 16 px radius, soft layered shadow, lift on hover only where hover exists. */
export const Card = React.forwardRef<HTMLDivElement, React.HTMLAttributes<HTMLDivElement> & { interactive?: boolean }>(
  ({ className, interactive, ...props }, ref) => (
    <div
      ref={ref}
      className={cn(
        "rounded-card border border-line bg-card shadow-card",
        interactive && "hover-lift transition-transform duration-200 [@media(hover:hover)]:hover:-translate-y-0.5",
        className,
      )}
      {...props}
    />
  ),
);
Card.displayName = "Card";

export const CardHeader = ({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) => (
  <div className={cn("flex flex-col gap-1 p-5 pb-3 sm:p-6 sm:pb-3", className)} {...props} />
);
export const CardTitle = ({ className, ...props }: React.HTMLAttributes<HTMLHeadingElement>) => (
  <h2 className={cn("font-display text-xl font-semibold text-strong", className)} {...props} />
);
export const CardDescription = ({ className, ...props }: React.HTMLAttributes<HTMLParagraphElement>) => (
  <p className={cn("text-sm text-muted", className)} {...props} />
);
export const CardContent = ({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) => (
  <div className={cn("p-5 pt-2 sm:p-6 sm:pt-3", className)} {...props} />
);
export const CardFooter = ({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) => (
  <div className={cn("flex flex-wrap items-center gap-3 border-t border-line p-5 sm:p-6", className)} {...props} />
);

/** Badges for status and storefront labels (New, Sale, Low stock, Sold out, Free delivery). Meaning is in the text, not only the colour. */
const badgeVariants = cva("inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-semibold leading-5", {
  variants: {
    variant: {
      neutral: "bg-sunken text-strong",
      brand: "bg-primary-soft text-primary-dark",
      success: "bg-sunken text-success ring-1 ring-inset ring-success/30",
      warning: "bg-sunken text-warning ring-1 ring-inset ring-warning/30",
      danger: "bg-sunken text-danger ring-1 ring-inset ring-danger/30",
      info: "bg-sunken text-info ring-1 ring-inset ring-info/30",
      sale: "bg-primary text-on-primary",
      outline: "border border-line text-muted",
    },
  },
  defaultVariants: { variant: "neutral" },
});

export function Badge({ className, variant, ...props }: React.HTMLAttributes<HTMLSpanElement> & VariantProps<typeof badgeVariants>) {
  return <span className={cn(badgeVariants({ variant }), className)} {...props} />;
}

export function Skeleton({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div aria-hidden className={cn("skeleton rounded-control", className)} {...props} />;
}

/** Inline message with icon and text (so colour is never the only signal). */
const alertVariants = cva("flex items-start gap-3 rounded-control border p-4 text-sm", {
  variants: {
    variant: {
      info: "border-info/30 bg-sunken text-strong [&>svg]:text-info",
      success: "border-success/30 bg-sunken text-strong [&>svg]:text-success",
      warning: "border-warning/40 bg-sunken text-strong [&>svg]:text-warning",
      danger: "border-danger/30 bg-sunken text-strong [&>svg]:text-danger",
    },
  },
  defaultVariants: { variant: "info" },
});

const alertIcons = { info: Info, success: CheckCircle2, warning: TriangleAlert, danger: AlertCircle } as const;

export function Alert({
  variant = "info",
  title,
  children,
  className,
  ...props
}: React.HTMLAttributes<HTMLDivElement> & VariantProps<typeof alertVariants> & { title?: string }) {
  const Icon = alertIcons[variant ?? "info"];
  return (
    <div role={variant === "danger" ? "alert" : "status"} className={cn(alertVariants({ variant }), className)} {...props}>
      <Icon className="mt-0.5 size-5 shrink-0" aria-hidden />
      <div className="min-w-0 space-y-0.5">
        {title ? <p className="font-semibold">{title}</p> : null}
        <div className="text-muted [overflow-wrap:anywhere]">{children}</div>
      </div>
    </div>
  );
}

export function EmptyState({
  icon,
  title,
  description,
  action,
  className,
}: {
  icon?: React.ReactNode;
  title: string;
  description?: string;
  action?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("flex flex-col items-center gap-3 px-6 py-12 text-center", className)}>
      {icon ? <div className="flex size-14 items-center justify-center rounded-full bg-primary-soft text-primary-dark [&_svg]:size-7">{icon}</div> : null}
      <h3 className="font-display text-lg font-semibold text-strong">{title}</h3>
      {description ? <p className="max-w-sm text-muted">{description}</p> : null}
      {action ? <div className="pt-1">{action}</div> : null}
    </div>
  );
}

/**
 * Fixed 4:5 portrait frame for fashion photos (SRS 3.3): the image is cropped to fit, shows a shimmer until it loads and an
 * inner hairline border. Pass `blurDataURL` (the API provides one per image) for a blurred preview.
 */
export function ImageFrame({
  className,
  alt,
  ratio = "4/5",
  ...props
}: Omit<ImageProps, "fill" | "className"> & { className?: string; ratio?: "4/5" | "1/1" | "16/9" }) {
  const aspect = { "4/5": "aspect-[4/5]", "1/1": "aspect-square", "16/9": "aspect-video" }[ratio];
  return (
    <div className={cn("skeleton relative overflow-hidden rounded-card", aspect, className)}>
      <Image alt={alt} fill sizes="(min-width: 1024px) 25vw, (min-width: 768px) 33vw, 50vw" className="object-cover" {...props} />
      <span className="pointer-events-none absolute inset-0 rounded-card ring-1 ring-inset ring-black/5" aria-hidden />
    </div>
  );
}
