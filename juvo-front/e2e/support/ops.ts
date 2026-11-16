import { expect } from "@playwright/test";
import { loadState } from "./env";
import { call } from "./cod";
import { adminAccess } from "./returns";

/**
 * Data for the purchasing and accounting specs, made through the real API (not the browser): suppliers, purchase orders at any stage,
 * accounts, categories and expenses. Products come from the sample catalogue.
 */
type Json = Record<string, unknown>;

export const unique = (prefix: string) => `${prefix} ${Math.random().toString(36).slice(2, 8)}`;
export const hoId = () => loadState().branches.ho.id;

export type Product = { id: string; nameEn: string; variants: { id: string; sku: string }[] };

let cached: Product | null = null;
let used = 0;
/** A published sample product with at least two stock-tracked variants (each call hands out the next one, so specs do not share stock). */
export async function sampleProduct(): Promise<Product> {
  const admin = await adminAccess();
  const list = await call<{ items: { id: string; nameEn: string }[] }>("GET", "/api/v1/catalog/products?Page=1&PageSize=60&status=Published", { token: admin, expect: 200 });
  const found: Product[] = [];
  for (const item of list.json.items) {
    const detail = await call<{ id: string; nameEn: string; variants: { id: string; sku: string; isActive: boolean; tracksStock: boolean }[] }>("GET", `/api/v1/catalog/products/${item.id}`, { token: admin });
    if (detail.status !== 200) continue;
    const variants = detail.json.variants.filter((v) => v.isActive && v.tracksStock).map((v) => ({ id: v.id, sku: v.sku }));
    // The picker searches by name: only a name that no other product shares finds exactly this product.
    if (variants.length >= 2 && list.json.items.filter((x) => x.nameEn === item.nameEn).length === 1) found.push({ id: detail.json.id, nameEn: detail.json.nameEn, variants });
  }
  expect(found.length, "the sample catalogue needs products with two stock-tracked variants").toBeGreaterThan(0);
  cached = found[used % found.length]!;
  used += 1;
  return cached;
}

export async function makeSupplier(name = unique("Supplier"), terms = 30): Promise<{ id: string; name: string }> {
  const made = await call<{ id: string }>("POST", "/api/v1/suppliers", { token: await adminAccess(), body: { name, contactName: "Karim Uddin", phone: "01711000000", paymentTermsDays: terms }, expect: 201 });
  return { id: made.json.id, name };
}

export type Line = { variantId: string; quantity: number; unitCost: number };

export type PoRef = { id: string; number: string; lines: { id: string; variantId: string; sku: string }[] };

export async function makePo(supplierId: string, lines: Line[], stage: "Draft" | "Sent" = "Draft"): Promise<PoRef> {
  const admin = await adminAccess();
  const made = await call<{ id: string; number: string; lines: { id: string; variantId: string; sku: string }[] }>("POST", "/api/v1/purchasing/orders", {
    token: admin,
    body: { branchId: hoId(), supplierId, expectedDate: null, notes: "e2e", lines },
    expect: 201,
  });
  if (stage === "Sent") await call("POST", `/api/v1/purchasing/orders/${made.json.id}/send`, { token: admin, expect: 200 });
  return { id: made.json.id, number: made.json.number, lines: made.json.lines };
}

export async function receive(po: PoRef, accepted: Record<string, number>, token?: string, expectStatus = 201) {
  return call<Json>("POST", `/api/v1/purchasing/orders/${po.id}/receive`, {
    token: token ?? (await adminAccess()),
    body: { note: "e2e", lines: po.lines.filter((l) => accepted[l.variantId] !== undefined).map((l) => ({ lineId: l.id, acceptedQuantity: accepted[l.variantId], rejectedQuantity: 0 })) },
    expect: expectStatus,
  });
}

export async function getPo(id: string) {
  const r = await call<{ status: string; paymentStatus: string; total: number | string; amountPayable: number | string; amountPaid: number | string; outstanding: number | string; receipts: { id: string; number: string }[]; lines: { id: string; variantId: string; sku: string; receivedQuantity: number | string; orderedQuantity: number | string }[] }>(
    "GET",
    `/api/v1/purchasing/orders/${id}`,
    { token: await adminAccess(), expect: 200 },
  );
  return r.json;
}

/** Units on hand of one variant in the head office branch. */
export async function onHand(productId: string, variantId: string): Promise<number> {
  const rows = await call<{ items: { branchId: string; variantId: string; onHand: number | string }[] }>("GET", `/api/v1/inventory/stock?productId=${productId}&PageSize=100`, { token: await adminAccess(), expect: 200 });
  const row = rows.json.items.find((r) => r.variantId === variantId && r.branchId === hoId());
  return row ? Number(row.onHand) : 0;
}

export async function costOf(productId: string): Promise<number | null> {
  const r = await call<{ costPrice: number | string | null }>("GET", `/api/v1/catalog/products/${productId}`, { token: await adminAccess(), expect: 200 });
  return r.json.costPrice === null ? null : Number(r.json.costPrice);
}

export async function makeAccount(name = unique("Drawer"), opening = 0, type = "Cash"): Promise<{ id: string; name: string }> {
  const made = await call<{ id: string }>("POST", "/api/v1/accounts", { token: await adminAccess(), body: { branchId: hoId(), name, type, openingBalance: opening }, expect: 201 });
  return { id: made.json.id, name };
}

export async function makeCategory(name = unique("Category")): Promise<{ id: string; name: string }> {
  const made = await call<{ id: string }>("POST", "/api/v1/expenses/categories", { token: await adminAccess(), body: { nameEn: name }, expect: 201 });
  return { id: made.json.id, name };
}

export const today = () => new Date(Date.now() + 6 * 3600_000).toISOString().slice(0, 10);

export async function makeExpense(token: string, categoryId: string, accountId: string, amount: number, reference?: string) {
  const made = await call<{ id: string; number: string }>("POST", "/api/v1/expenses", {
    token,
    body: { branchId: hoId(), categoryId, expenseDate: today(), amount, method: "Cash", accountId, reference: reference ?? null },
    expect: 201,
  });
  return made.json;
}

export async function balanceOf(accountId: string): Promise<number> {
  const list = await call<{ id: string; balance: number | string }[]>("GET", "/api/v1/accounts?includeInactive=true", { token: await adminAccess(), expect: 200 });
  return Number(list.json.find((a) => a.id === accountId)!.balance);
}

/** Money as the screens draw it, compared by digits only (separators and the taka sign differ by language). */
export const digits = (text: string | null) => Number((text ?? "").replace(/[^0-9.-]/g, ""));
