"use client";

import * as React from "react";
import * as DialogPrimitive from "@radix-ui/react-dialog";
import * as TooltipPrimitive from "@radix-ui/react-tooltip";
import * as DropdownPrimitive from "@radix-ui/react-dropdown-menu";
import * as PopoverPrimitive from "@radix-ui/react-popover";
import { X } from "lucide-react";
import { cn } from "@/lib/utils";

/* ---- Dialog / Modal / Sheet ---------------------------------------------------------------------------------------------
 * One Radix Dialog gives focus trapping, Escape, outside click and background scroll lock. Variants:
 *  - Modal: centred card on tablets and desktops, a full-screen sheet on phones (SRS 5.3A).
 *  - Sheet: slides from the left (navigation drawer) or the bottom (filters, pickers).
 */
export const Dialog = DialogPrimitive.Root;
export const DialogTrigger = DialogPrimitive.Trigger;
export const DialogClose = DialogPrimitive.Close;
export const DialogTitle = DialogPrimitive.Title;
export const DialogDescription = DialogPrimitive.Description;

const Overlay = React.forwardRef<React.ComponentRef<typeof DialogPrimitive.Overlay>, React.ComponentPropsWithoutRef<typeof DialogPrimitive.Overlay>>(
  ({ className, ...props }, ref) => (
    <DialogPrimitive.Overlay ref={ref} className={cn("anim-overlay fixed inset-0 z-50 bg-black/50 backdrop-blur-[2px]", className)} {...props} />
  ),
);
Overlay.displayName = "DialogOverlay";

export const CloseButton = ({ label, className }: { label: string; className?: string }) => (
  <DialogPrimitive.Close
    aria-label={label}
    className={cn("tap absolute end-2 top-2 flex items-center justify-center rounded-control text-muted [@media(hover:hover)]:hover:bg-sunken [@media(hover:hover)]:hover:text-strong", className)}
  >
    <X className="size-5" aria-hidden />
  </DialogPrimitive.Close>
);

export const ModalContent = React.forwardRef<
  React.ComponentRef<typeof DialogPrimitive.Content>,
  React.ComponentPropsWithoutRef<typeof DialogPrimitive.Content> & { closeLabel: string; size?: "sm" | "md" | "lg" }
>(({ className, children, closeLabel, size = "md", ...props }, ref) => (
  <DialogPrimitive.Portal>
    <Overlay />
    <DialogPrimitive.Content
      ref={ref}
      className={cn(
        "anim-bottom fixed inset-0 z-50 flex flex-col overflow-y-auto bg-card p-5 pt-14 safe-bottom focus:outline-none",
        "md:anim-pop md:inset-auto md:start-1/2 md:top-1/2 md:max-h-[85dvh] md:w-[calc(100%-2rem)] md:-translate-x-1/2 md:-translate-y-1/2 md:rounded-card md:border md:border-line md:p-6 md:pt-6 md:shadow-pop rtl:md:translate-x-1/2",
        size === "sm" && "md:max-w-md",
        size === "md" && "md:max-w-xl",
        size === "lg" && "md:max-w-3xl",
        className,
      )}
      {...props}
    >
      {children}
      <CloseButton label={closeLabel} />
    </DialogPrimitive.Content>
  </DialogPrimitive.Portal>
));
ModalContent.displayName = "ModalContent";

export const SheetContent = React.forwardRef<
  React.ComponentRef<typeof DialogPrimitive.Content>,
  React.ComponentPropsWithoutRef<typeof DialogPrimitive.Content> & { side?: "left" | "bottom"; closeLabel?: string }
>(({ className, children, side = "left", closeLabel, ...props }, ref) => (
  <DialogPrimitive.Portal>
    <Overlay />
    <DialogPrimitive.Content
      ref={ref}
      className={cn(
        "fixed z-50 flex flex-col bg-card shadow-pop focus:outline-none",
        side === "left" && "anim-left inset-y-0 start-0 w-[min(88vw,22rem)] rtl:anim-left",
        side === "bottom" && "anim-bottom inset-x-0 bottom-0 max-h-[85dvh] rounded-t-card border-t border-line safe-bottom",
        className,
      )}
      {...props}
    >
      {children}
      {closeLabel ? <CloseButton label={closeLabel} /> : null}
    </DialogPrimitive.Content>
  </DialogPrimitive.Portal>
));
SheetContent.displayName = "SheetContent";

/* ---- Tooltip -------------------------------------------------------------------------------------------------------------- */
export const TooltipProvider = TooltipPrimitive.Provider;

export function Tooltip({ content, children, side = "right" }: { content: React.ReactNode; children: React.ReactElement; side?: "top" | "right" | "bottom" | "left" }) {
  return (
    <TooltipPrimitive.Root delayDuration={250}>
      <TooltipPrimitive.Trigger asChild>{children}</TooltipPrimitive.Trigger>
      <TooltipPrimitive.Portal>
        <TooltipPrimitive.Content
          side={side}
          sideOffset={8}
          className="anim-fade z-[60] hidden max-w-60 rounded-lg bg-secondary px-3 py-1.5 text-sm font-medium text-accent shadow-pop [@media(hover:hover)]:block"
        >
          {content}
        </TooltipPrimitive.Content>
      </TooltipPrimitive.Portal>
    </TooltipPrimitive.Root>
  );
}

/* ---- Dropdown menu (user menu, branch switcher, row actions) --------------------------------------------------------------- */
export const DropdownMenu = DropdownPrimitive.Root;
export const DropdownMenuTrigger = DropdownPrimitive.Trigger;
export const DropdownMenuLabel = DropdownPrimitive.Label;
export const DropdownMenuGroup = DropdownPrimitive.Group;

export const DropdownMenuContent = React.forwardRef<
  React.ComponentRef<typeof DropdownPrimitive.Content>,
  React.ComponentPropsWithoutRef<typeof DropdownPrimitive.Content>
>(({ className, sideOffset = 8, align = "end", ...props }, ref) => (
  <DropdownPrimitive.Portal>
    <DropdownPrimitive.Content
      ref={ref}
      sideOffset={sideOffset}
      align={align}
      collisionPadding={12}
      className={cn("anim-fade z-[60] max-h-[min(70dvh,28rem)] min-w-56 max-w-[calc(100vw-1.5rem)] overflow-y-auto rounded-card border border-line bg-card p-1.5 shadow-pop scroll-thin", className)}
      {...props}
    />
  </DropdownPrimitive.Portal>
));
DropdownMenuContent.displayName = "DropdownMenuContent";

export const DropdownMenuItem = React.forwardRef<
  React.ComponentRef<typeof DropdownPrimitive.Item>,
  React.ComponentPropsWithoutRef<typeof DropdownPrimitive.Item> & { inset?: boolean }
>(({ className, inset, ...props }, ref) => (
  <DropdownPrimitive.Item
    ref={ref}
    className={cn(
      "flex min-h-11 cursor-pointer select-none items-center gap-3 rounded-control px-3 text-base text-strong outline-none data-[disabled]:pointer-events-none data-[disabled]:opacity-50 data-[highlighted]:bg-sunken [&_svg]:size-5 [&_svg]:shrink-0 [&_svg]:text-muted",
      inset && "ps-10",
      className,
    )}
    {...props}
  />
));
DropdownMenuItem.displayName = "DropdownMenuItem";

export const DropdownMenuSeparator = ({ className }: { className?: string }) => <DropdownPrimitive.Separator className={cn("-mx-1.5 my-1.5 h-px bg-line", className)} />;

/* ---- Popover -------------------------------------------------------------------------------------------------------------- */
export const Popover = PopoverPrimitive.Root;
export const PopoverTrigger = PopoverPrimitive.Trigger;
export const PopoverAnchor = PopoverPrimitive.Anchor;

export const PopoverContent = React.forwardRef<React.ComponentRef<typeof PopoverPrimitive.Content>, React.ComponentPropsWithoutRef<typeof PopoverPrimitive.Content>>(
  ({ className, align = "start", sideOffset = 6, ...props }, ref) => (
    <PopoverPrimitive.Portal>
      <PopoverPrimitive.Content
        ref={ref}
        align={align}
        sideOffset={sideOffset}
        collisionPadding={12}
        className={cn("anim-fade z-[60] w-[var(--radix-popover-trigger-width)] min-w-64 max-w-[calc(100vw-1.5rem)] rounded-card border border-line bg-card p-1.5 shadow-pop", className)}
        {...props}
      />
    </PopoverPrimitive.Portal>
  ),
);
PopoverContent.displayName = "PopoverContent";
