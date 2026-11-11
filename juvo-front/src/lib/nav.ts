import {
  BadgeDollarSign,
  Boxes,
  ChartNoAxesColumn,
  ClipboardList,
  FileText,
  Gauge,
  Globe,
  Headset,
  Landmark,
  Layers,
  LogIn,
  Megaphone,
  PackageSearch,
  Receipt,
  RotateCcw,
  Settings,
  Shield,
  ShieldAlert,
  ShoppingBag,
  ShoppingCart,
  Shirt,
  Store,
  Tag,
  Truck,
  UserCog,
  Users,
  Wallet,
  type LucideIcon,
} from "lucide-react";
import { can, type AccessView, type PermissionScope } from "@/lib/auth/permissions";

export type NavItem = {
  id: string;
  /** Message key under "nav" in src/messages. */
  label: string;
  href: string;
  icon: LucideIcon;
  /** Permission needed to see the item. No permission = every signed-in staff member. */
  permission?: string;
  /** "active" = in the active branch, "anywhere" = global features (users, roles, catalog ...). */
  scope?: PermissionScope;
  /** False until the screen exists (a later step). Shown muted with a "Soon" tag instead of a dead link. */
  ready: boolean;
  /** One of the few items on the phone's bottom bar. */
  bottom?: boolean;
};

export type NavGroup = {
  id: string;
  label: string;
  /** Icon of the parent row (shown when the group has more than one item; a one-item group is a plain link with the item's icon). */
  icon: LucideIcon;
  items: NavItem[];
};

/** A group with a single item is shown as one top-level link; the menu has no empty parent row for it. */
export const isLeafGroup = (group: NavGroup) => group.items.length === 1;

/**
 * The admin menu, grouped by module (master prompt 5.3A). A user sees only the items their permissions allow in the active
 * branch; empty groups disappear. Each later step flips `ready` and adds the page.
 */
export const ADMIN_NAV: NavGroup[] = [
  {
    id: "overview",
    label: "overview",
    icon: Gauge,
    items: [{ id: "dashboard", label: "dashboard", href: "/admin", icon: Gauge, ready: true, bottom: true }],
  },
  {
    id: "sales",
    label: "sales",
    icon: ShoppingCart,
    items: [
      { id: "orders", label: "orders", href: "/admin/orders", icon: ClipboardList, permission: "orders.order.view", ready: true, bottom: true },
      { id: "social-orders", label: "socialOrders", href: "/admin/orders/new", icon: Store, permission: "orders.order.create", ready: true },
      { id: "fraud", label: "fraud", href: "/admin/fraud", icon: ShieldAlert, permission: "fraud.flag.view", ready: true },
      { id: "returns", label: "returns", href: "/admin/returns", icon: RotateCcw, permission: "returns.return.view", ready: true },
    ],
  },
  {
    id: "catalog",
    label: "catalog",
    icon: Shirt,
    items: [
      { id: "products", label: "products", href: "/admin/products", icon: ShoppingBag, permission: "catalog.product.view", scope: "anywhere", ready: true, bottom: true },
      { id: "categories", label: "categories", href: "/admin/categories", icon: Layers, permission: "catalog.category.view", scope: "anywhere", ready: true },
      { id: "inventory", label: "inventory", href: "/admin/inventory", icon: Boxes, permission: "inventory.stock.view", ready: true },
      { id: "purchasing", label: "purchasing", href: "/admin/purchasing", icon: PackageSearch, permission: "purchasing.po.view", ready: false },
    ],
  },
  {
    id: "customers",
    label: "customers",
    icon: Users,
    items: [{ id: "customers", label: "customers", href: "/admin/customers", icon: Users, permission: "customers.customer.view", scope: "anywhere", ready: false, bottom: true }],
  },
  {
    id: "delivery",
    label: "delivery",
    icon: Truck,
    items: [
      { id: "zones", label: "zones", href: "/admin/delivery/zones", icon: Globe, permission: "delivery.zone.view", scope: "anywhere", ready: false },
      { id: "couriers", label: "couriers", href: "/admin/couriers", icon: Truck, permission: "couriers.courier.view", ready: false },
    ],
  },
  {
    id: "money",
    label: "money",
    icon: Wallet,
    items: [
      { id: "payments", label: "payments", href: "/admin/payments", icon: Wallet, permission: "payments.payment.view", ready: false },
      { id: "tax", label: "taxInvoices", href: "/admin/tax", icon: Receipt, permission: "tax.invoice.view", ready: false },
      { id: "expenses", label: "expenses", href: "/admin/accounting/expenses", icon: Landmark, permission: "accounting.expense.view", ready: false },
    ],
  },
  {
    id: "marketing",
    label: "marketing",
    icon: Megaphone,
    items: [
      { id: "promotions", label: "promotions", href: "/admin/promotions", icon: Tag, permission: "promotions.promotion.view", scope: "anywhere", ready: false },
      { id: "campaigns", label: "campaigns", href: "/admin/marketing/campaigns", icon: Megaphone, permission: "marketing.campaign.view", scope: "anywhere", ready: false },
    ],
  },
  {
    id: "content",
    label: "content",
    icon: FileText,
    items: [{ id: "cms", label: "cms", href: "/admin/cms", icon: FileText, permission: "cms.page.view", scope: "anywhere", ready: false }],
  },
  {
    id: "insights",
    label: "insights",
    icon: ChartNoAxesColumn,
    items: [
      { id: "reports", label: "reports", href: "/admin/reports", icon: ChartNoAxesColumn, permission: "reports.sales.view", ready: false },
      { id: "support", label: "support", href: "/admin/support", icon: Headset, permission: "support.ticket.view", ready: false },
    ],
  },
  {
    id: "system",
    label: "system",
    icon: Settings,
    items: [{ id: "settings", label: "settings", href: "/admin/settings", icon: Settings, permission: "settings.system.view", scope: "anywhere", ready: false }],
  },
  {
    id: "ums",
    label: "ums",
    icon: UserCog,
    items: [
      { id: "users", label: "users", href: "/admin/ums/users", icon: UserCog, permission: "ums.user.view", scope: "anywhere", ready: true },
      { id: "roles", label: "roles", href: "/admin/ums/roles", icon: Shield, permission: "ums.role.view", scope: "anywhere", ready: true },
      { id: "branches", label: "branches", href: "/admin/ums/branches", icon: BadgeDollarSign, permission: "ums.branch.view", scope: "anywhere", ready: true },
      { id: "audit", label: "audit", href: "/admin/ums/audit", icon: FileText, permission: "ums.audit.view", scope: "anywhere", ready: true },
      { id: "sign-ins", label: "signIns", href: "/admin/ums/sign-ins", icon: LogIn, permission: "ums.sign-in.view", scope: "anywhere", ready: true },
    ],
  },
];

/** Customer account menu. Real customer screens arrive in Steps 7 and 8. */
export const ACCOUNT_NAV: NavGroup[] = [
  {
    id: "account",
    label: "account",
    icon: Gauge,
    items: [
      { id: "account-home", label: "accountHome", href: "/account", icon: Gauge, ready: true, bottom: true },
      { id: "account-orders", label: "myOrders", href: "/account/orders", icon: ClipboardList, ready: true, bottom: true },
      { id: "account-addresses", label: "addresses", href: "/account/addresses", icon: Globe, ready: true, bottom: true },
      { id: "account-wishlist", label: "wishlist", href: "/account/wishlist", icon: Tag, ready: true },
      { id: "account-returns", label: "returns", href: "/account/returns", icon: RotateCcw, ready: true },
    ],
  },
];

/** Groups with only the items this user may see in the active branch. Empty groups are removed. */
export function visibleNav(groups: NavGroup[], access: AccessView): NavGroup[] {
  return groups
    .map((group) => ({
      ...group,
      items: group.items.filter((item) => !item.permission || can(access, item.permission, item.scope ?? "active")),
    }))
    .filter((group) => group.items.length > 0);
}

export function flattenNav(groups: NavGroup[]): NavItem[] {
  return groups.flatMap((g) => g.items);
}

/** Finds the item whose route matches the current path best (longest prefix). */
export function activeItem(groups: NavGroup[], pathname: string): NavItem | undefined {
  return flattenNav(groups)
    .filter((item) => (item.href === "/admin" || item.href === "/account" ? pathname === item.href : pathname === item.href || pathname.startsWith(item.href + "/")))
    .sort((a, b) => b.href.length - a.href.length)[0];
}
