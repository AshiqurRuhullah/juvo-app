/** Lines handed from the suggestions tab to the new purchase order screen (kept in the tab's session storage; read once). */
export const SEED_KEY = "juvo.po.seed";

export type SeedLine = { variantId: string; productId: string; productName: string; sku: string; colorName: string | null; size: string | null; quantity: string; unitCost: string };
export type PoSeed = { branchId: string; supplierId: string; lines: SeedLine[] };

export function takeSeed(): PoSeed | null {
  try {
    const raw = sessionStorage.getItem(SEED_KEY);
    if (!raw) return null;
    sessionStorage.removeItem(SEED_KEY);
    const parsed = JSON.parse(raw) as PoSeed;
    return parsed && Array.isArray(parsed.lines) ? parsed : null;
  } catch {
    return null;
  }
}
