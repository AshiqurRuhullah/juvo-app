import * as React from "react";
import { Slot } from "@radix-ui/react-slot";
import { cva, type VariantProps } from "class-variance-authority";
import { Loader2 } from "lucide-react";
import { cn } from "@/lib/utils";

/**
 * Buttons: solid brand colour (primary), outline (secondary), ghost for low emphasis, danger for destructive actions.
 * Every size is at least 44 px tall (touch friendly, SRS 3.3). Loading shows a spinner and blocks repeat clicks.
 */
const buttonVariants = cva(
  "inline-flex shrink-0 select-none items-center justify-center gap-2 whitespace-nowrap rounded-control font-medium transition-[background-color,box-shadow,border-color,color] duration-150 disabled:pointer-events-none disabled:opacity-50 aria-disabled:pointer-events-none aria-disabled:opacity-50 [&_svg]:size-5 [&_svg]:shrink-0",
  {
    variants: {
      variant: {
        primary: "bg-primary text-on-primary shadow-sm [@media(hover:hover)]:hover:bg-primary-dark active:bg-primary-dark",
        secondary: "border border-line bg-card text-strong shadow-[0_1px_0_rgb(60_40_30/0.03)] [@media(hover:hover)]:hover:border-strong/20 [@media(hover:hover)]:hover:bg-sunken active:bg-sunken",
        ghost: "text-strong [@media(hover:hover)]:hover:bg-sunken active:bg-sunken",
        danger: "bg-danger text-card shadow-sm [@media(hover:hover)]:hover:opacity-90 active:opacity-90",
        link: "min-h-0 px-0 text-primary underline-offset-4 [@media(hover:hover)]:hover:underline",
      },
      size: {
        md: "min-h-11 px-5 text-base",
        lg: "min-h-12 px-6 text-base",
        icon: "size-11",
      },
    },
    defaultVariants: { variant: "primary", size: "md" },
  },
);

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement>, VariantProps<typeof buttonVariants> {
  asChild?: boolean;
  loading?: boolean;
}

export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, asChild = false, loading = false, disabled, children, type, ...props }, ref) => {
    const classes = cn(buttonVariants({ variant, size }), className);
    if (asChild) {
      return (
        <Slot ref={ref} className={classes} {...props}>
          {children}
        </Slot>
      );
    }
    return (
      <button ref={ref} type={type ?? "button"} className={classes} disabled={disabled || loading} aria-busy={loading || undefined} {...props}>
        {loading ? <Loader2 className="animate-spin" aria-hidden /> : null}
        {children}
      </button>
    );
  },
);
Button.displayName = "Button";

export { buttonVariants };
