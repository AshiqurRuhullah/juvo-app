"use client";

import dynamic from "next/dynamic";
import { Skeleton } from "@/components/ui/surfaces";
import type { ReportName } from "./logic";

const loading = () => <Skeleton className="h-96" />;

// Each report is its own chunk: a page loads only the report it shows.
const VIEWS = {
  sales: dynamic(() => import("./sales-view").then((m) => m.SalesView), { loading }),
  orders: dynamic(() => import("./orders-view").then((m) => m.OrdersView), { loading }),
  inventory: dynamic(() => import("./inventory-view").then((m) => m.InventoryView), { loading }),
  finance: dynamic(() => import("./finance-view").then((m) => m.FinanceView), { loading }),
  courier: dynamic(() => import("./courier-view").then((m) => m.CourierView), { loading }),
  customers: dynamic(() => import("./customers-view").then((m) => m.CustomersView), { loading }),
} as const;

export function ReportSwitch({ report }: { report: ReportName }) {
  const View = VIEWS[report];
  return <View />;
}
