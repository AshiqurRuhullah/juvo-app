"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ApiError, api, unwrap } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";
import { apiQuery, filenameFrom, type ReportFilters, type ReportName } from "./logic";

/** Typed reads of the report endpoints (juvo-be ReportsEndpoints) through the BFF, and the file download of the exports. */
export type SalesReport = Schemas["SalesReportDto"];
export type SalesRow = Schemas["SalesRowDto"];
export type OrdersReport = Schemas["OrdersReportDto"];
export type InventoryReport = Schemas["InventoryReportDto"];
export type FinanceReport = Schemas["FinanceReportDto"];
export type FinanceBucket = Schemas["FinanceBucketDto"];
export type CourierReport = Schemas["CourierReportDto"];
export type CourierRow = Schemas["CourierRowDto"];
export type CustomersReport = Schemas["CustomersReportDto"];
export type PnlReport = Schemas["PnlDto"];
export type PnlBucket = Schemas["PnlBucketDto"];
export type DashboardData = Schemas["DashboardDto"];

export const REPORT_KEYS = { all: ["reports"] as const };

const query = (filters: ReportFilters, extra?: Record<string, string | number | undefined>) => ({ params: { query: apiQuery(filters, extra) as never } });

/** One request per filter change, never polled. The previous result stays on screen while the next one loads. */
const options = { placeholderData: keepPreviousData, staleTime: 30_000, refetchOnWindowFocus: false } as const;

export const useSalesReport = (f: ReportFilters, enabled: boolean) =>
  useQuery({ queryKey: [...REPORT_KEYS.all, "sales", f], enabled, ...options, queryFn: async () => unwrap(await api.GET("/api/v1/reports/sales", query(f))) });
export const useOrdersReport = (f: ReportFilters, enabled: boolean) =>
  useQuery({ queryKey: [...REPORT_KEYS.all, "orders", f], enabled, ...options, queryFn: async () => unwrap(await api.GET("/api/v1/reports/orders", query(f))) });
export const useInventoryReport = (f: ReportFilters, enabled: boolean) =>
  useQuery({ queryKey: [...REPORT_KEYS.all, "inventory", f], enabled, ...options, queryFn: async () => unwrap(await api.GET("/api/v1/reports/inventory", query(f, { limit: 100 }))) });
export const useFinanceReport = (f: ReportFilters, enabled: boolean) =>
  useQuery({ queryKey: [...REPORT_KEYS.all, "finance", f], enabled, ...options, queryFn: async () => unwrap(await api.GET("/api/v1/reports/finance", query(f))) });
export const useCourierReport = (f: ReportFilters, enabled: boolean) =>
  useQuery({ queryKey: [...REPORT_KEYS.all, "courier", f], enabled, ...options, queryFn: async () => unwrap(await api.GET("/api/v1/reports/courier", query(f))) });
export const useCustomersReport = (f: ReportFilters, enabled: boolean) =>
  useQuery({ queryKey: [...REPORT_KEYS.all, "customers", f], enabled, ...options, queryFn: async () => unwrap(await api.GET("/api/v1/reports/customers", query(f, { limit: 20 }))) });
export const usePnl = (f: ReportFilters, enabled: boolean) =>
  useQuery({ queryKey: [...REPORT_KEYS.all, "pnl", f], enabled, ...options, queryFn: async () => unwrap(await api.GET("/api/v1/accounting/pnl", query(f))) });

/** The dashboard's only call. `branchId` follows the active branch. */
export const useDashboard = (branchId: string | undefined) =>
  useQuery({
    queryKey: [...REPORT_KEYS.all, "dashboard", branchId ?? null],
    staleTime: 30_000,
    refetchOnWindowFocus: false,
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/reports/dashboard", { params: { query: (branchId ? { branchId } : {}) as never } })),
  });

/**
 * Downloads a file from a BFF address and saves it under the name the server chose. A refusal (for example cost access missing)
 * throws an ApiError carrying the API's problem, so the caller shows the translated text. `init` lets a caller use POST (privacy export).
 */
export async function downloadFile(url: string, fallbackName: string, init?: RequestInit): Promise<{ name: string; size: number; type: string }> {
  let response: Response;
  try {
    response = await fetch(url, { credentials: "same-origin", ...init });
  } catch {
    throw new ApiError({ status: 0, code: "NETWORK" });
  }
  if (!response.ok) {
    const body: unknown = await response.json().catch(() => ({}));
    throw new ApiError(toProblem(body, response.status));
  }
  const blob = await response.blob();
  const name = filenameFrom(response.headers.get("content-disposition"), fallbackName);
  const href = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = href;
  link.download = name;
  link.rel = "noopener";
  link.style.display = "none";
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(href), 10_000);
  return { name, size: blob.size, type: blob.type };
}

export const reportFallbackName = (report: ReportName, format: string) => `${report}-report.${format}`;
