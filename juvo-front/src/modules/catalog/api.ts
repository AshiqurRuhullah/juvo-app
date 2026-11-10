"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, unwrap } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";
import { toast } from "@/components/ui/toaster";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";

/** Typed access to the catalog endpoints (Steps 6-a, 6-b, 6-d). Every call goes through the BFF, which adds the token and the active branch. */
export type CategoryNode = Schemas["CategoryNodeDto"];
export type Category = Schemas["CategoryDto"];
export type SaveCategory = Schemas["SaveCategoryRequest"];
export type Brand = Schemas["BrandDto"];
export type SaveBrand = Schemas["SaveBrandRequest"];
export type SizeChart = Schemas["SizeChartDto"];
export type SaveSizeChart = Schemas["SaveSizeChartRequest"];
export type ProductListItem = Schemas["ProductListItemDto"];
export type ProductDetail = Schemas["ProductDetailDto"];
export type SaveProduct = Schemas["SaveProductRequest"];
export type ProductStatus = Schemas["ProductStatus"];
export type TargetGroup = Schemas["TargetGroup"];
export type Variant = Schemas["VariantDto"];
export type SaveVariant = Schemas["SaveVariantRequest"];
export type ProductImage = Schemas["ProductImageDto"];
export type ImportReport = Schemas["ImportReportDto"];
export type ImportIssue = Schemas["ImportIssueDto"];

export const PRODUCT_STATUSES: ProductStatus[] = ["Draft", "Published", "Archived"];
export const TARGET_GROUPS: TargetGroup[] = ["Unisex", "Women", "Men", "Kids"];

export type ListParams = { page: number; pageSize: number; sort?: string; search?: string };

const clean = <T extends Record<string, unknown>>(value: T) =>
  Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null)) as Partial<T>;

/* ---- Queries -------------------------------------------------------------------------------------------------------------- */

export function useCategoryTree(includeInactive = true) {
  return useQuery({
    queryKey: ["catalog", "categories", "tree", includeInactive],
    queryFn: async () => unwrap(await api.GET("/api/v1/catalog/categories", { params: { query: { includeInactive } } })),
  });
}

export function useBrands(params: ListParams) {
  return useQuery({
    queryKey: ["catalog", "brands", params],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/catalog/brands", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search }) } })),
  });
}

/** Every brand, for pickers (the API allows 100 per page). */
export function useAllBrands() {
  return useQuery({
    queryKey: ["catalog", "brands", "all"],
    queryFn: async () => unwrap(await api.GET("/api/v1/catalog/brands", { params: { query: { Page: 1, PageSize: 100, Sort: "name" } } })),
  });
}

export function useSizeCharts() {
  return useQuery({ queryKey: ["catalog", "size-charts"], queryFn: async () => unwrap(await api.GET("/api/v1/catalog/size-charts")) });
}

export type ProductFilters = { status?: ProductStatus; categoryId?: string; brandId?: string; featured?: boolean };

export function useProducts(params: ListParams & ProductFilters) {
  return useQuery({
    queryKey: ["catalog", "products", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/catalog/products", {
          params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, status: params.status, categoryId: params.categoryId, brandId: params.brandId, featured: params.featured }) },
        }),
      ),
  });
}

export function useProduct(id: string) {
  return useQuery({ queryKey: ["catalog", "product", id], enabled: !!id, queryFn: async () => unwrap(await api.GET("/api/v1/catalog/products/{id}", { params: { path: { id } } })) });
}

/* ---- Mutations ------------------------------------------------------------------------------------------------------------ */

/** Anything that changes the catalog refreshes every catalog list and detail (stock lists show product names too). */
function useCatalogMutation<TVars, TData>(run: (vars: TVars) => Promise<TData>) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: ["catalog"] }), queryClient.invalidateQueries({ queryKey: ["inventory"] })]),
  });
}

export const useCreateCategory = () => useCatalogMutation(async (body: SaveCategory) => unwrap(await api.POST("/api/v1/catalog/categories", { body })));
export const useUpdateCategory = () =>
  useCatalogMutation(async (vars: { id: string; body: SaveCategory }) => unwrap(await api.PUT("/api/v1/catalog/categories/{id}", { params: { path: { id: vars.id } }, body: vars.body })));
export const useDeleteCategory = () => useCatalogMutation(async (id: string) => unwrap(await api.DELETE("/api/v1/catalog/categories/{id}", { params: { path: { id } } })));
export const useReorderCategories = () => useCatalogMutation(async (ids: string[]) => unwrap(await api.PUT("/api/v1/catalog/categories/order", { body: { ids } })));

export const useCreateBrand = () => useCatalogMutation(async (body: SaveBrand) => unwrap(await api.POST("/api/v1/catalog/brands", { body })));
export const useUpdateBrand = () =>
  useCatalogMutation(async (vars: { id: string; body: SaveBrand }) => unwrap(await api.PUT("/api/v1/catalog/brands/{id}", { params: { path: { id: vars.id } }, body: vars.body })));
export const useDeleteBrand = () => useCatalogMutation(async (id: string) => unwrap(await api.DELETE("/api/v1/catalog/brands/{id}", { params: { path: { id } } })));

export const useCreateSizeChart = () => useCatalogMutation(async (body: SaveSizeChart) => unwrap(await api.POST("/api/v1/catalog/size-charts", { body })));
export const useUpdateSizeChart = () =>
  useCatalogMutation(async (vars: { id: string; body: SaveSizeChart }) => unwrap(await api.PUT("/api/v1/catalog/size-charts/{id}", { params: { path: { id: vars.id } }, body: vars.body })));

export const useCreateProduct = () => useCatalogMutation(async (body: SaveProduct) => unwrap(await api.POST("/api/v1/catalog/products", { body })));
export const useUpdateProduct = (id: string) =>
  useCatalogMutation(async (body: SaveProduct) => unwrap(await api.PUT("/api/v1/catalog/products/{id}", { params: { path: { id } }, body })));
export const useDeleteProduct = () => useCatalogMutation(async (id: string) => unwrap(await api.DELETE("/api/v1/catalog/products/{id}", { params: { path: { id } } })));
export const useDuplicateProduct = () =>
  useCatalogMutation(async (vars: { id: string; nameEn?: string | null }) =>
    unwrap(await api.POST("/api/v1/catalog/products/{id}/duplicate", { params: { path: { id: vars.id } }, body: { nameEn: vars.nameEn ?? null } })),
  );

export type ProductAction = "publish" | "unpublish" | "archive";
export const useProductAction = (id: string) =>
  useCatalogMutation(async (action: ProductAction) => {
    const params = { params: { path: { id } } };
    switch (action) {
      case "publish":
        return unwrap(await api.POST("/api/v1/catalog/products/{id}/publish", params));
      case "unpublish":
        return unwrap(await api.POST("/api/v1/catalog/products/{id}/unpublish", params));
      case "archive":
        return unwrap(await api.POST("/api/v1/catalog/products/{id}/archive", params));
    }
  });

export const useAddVariant = (productId: string) =>
  useCatalogMutation(async (body: SaveVariant) => unwrap(await api.POST("/api/v1/catalog/products/{id}/variants", { params: { path: { id: productId } }, body })));
export const useUpdateVariant = (productId: string) =>
  useCatalogMutation(async (vars: { variantId: string; body: SaveVariant }) =>
    unwrap(await api.PUT("/api/v1/catalog/products/{id}/variants/{variantId}", { params: { path: { id: productId, variantId: vars.variantId } }, body: vars.body })),
  );
export const useRemoveVariant = (productId: string) =>
  useCatalogMutation(async (variantId: string) => unwrap(await api.DELETE("/api/v1/catalog/products/{id}/variants/{variantId}", { params: { path: { id: productId, variantId } } })));

/* ---- Pictures ------------------------------------------------------------------------------------------------------------- */

export type UploadImage = { file: File; colorName?: string | null; altText?: string | null; focalX?: number; focalY?: number };

/** Multipart upload through the BFF (the typed client is JSON only). Throws an ApiError with the API's problem. */
export async function uploadProductImage(productId: string, input: UploadImage): Promise<ProductImage> {
  const form = new FormData();
  form.append("file", input.file);
  if (input.colorName) form.append("colorName", input.colorName);
  if (input.altText) form.append("altText", input.altText);
  if (input.focalX !== undefined) form.append("focalX", String(input.focalX));
  if (input.focalY !== undefined) form.append("focalY", String(input.focalY));
  const response = await fetch(`/api/bff/api/v1/catalog/products/${productId}/images`, { method: "POST", body: form, credentials: "same-origin" });
  const text = await response.text();
  const parsed = text ? (JSON.parse(text) as unknown) : undefined;
  if (!response.ok) throw new ApiError(toProblem(parsed, response.status));
  return parsed as ProductImage;
}

export const useUploadImage = (productId: string) => useCatalogMutation((input: UploadImage) => uploadProductImage(productId, input));
export const useUpdateImage = (productId: string) =>
  useCatalogMutation(async (vars: { imageId: string; body: Schemas["UpdateImageRequest"] }) =>
    unwrap(await api.PUT("/api/v1/catalog/products/{id}/images/{imageId}", { params: { path: { id: productId, imageId: vars.imageId } }, body: vars.body })),
  );
export const useReorderImages = (productId: string) =>
  useCatalogMutation(async (ids: string[]) => unwrap(await api.PUT("/api/v1/catalog/products/{id}/images/order", { params: { path: { id: productId } }, body: { ids } })));
export const useDeleteImage = (productId: string) =>
  useCatalogMutation(async (imageId: string) => unwrap(await api.DELETE("/api/v1/catalog/products/{id}/images/{imageId}", { params: { path: { id: productId, imageId } } })));

/* ---- Bulk import and export ----------------------------------------------------------------------------------------------- */

export type ImportOptions = { file: File; mode: "create" | "upsert"; dryRun: boolean };

/** Uploads a CSV or Excel file. A file with errors still answers 200 with the report; only an unusable file is an ApiError. */
export async function importProducts(options: ImportOptions): Promise<ImportReport> {
  const form = new FormData();
  form.append("file", options.file);
  const query = new URLSearchParams({ mode: options.mode, dryRun: String(options.dryRun) });
  const response = await fetch(`/api/bff/api/v1/catalog/products/import?${query}`, { method: "POST", body: form, credentials: "same-origin" });
  const text = await response.text();
  const parsed = text ? (JSON.parse(text) as unknown) : undefined;
  if (!response.ok) throw new ApiError(toProblem(parsed, response.status));
  return parsed as ImportReport;
}

export const useImportProducts = () => useCatalogMutation(importProducts);

/** Address of the export (a normal download link: the browser sends the session cookie, the BFF adds the token). */
export function exportUrl(format: "csv" | "xlsx", filters: { status?: ProductStatus; categoryId?: string; branchId?: string } = {}): string {
  const query = new URLSearchParams({ format, ...(clean(filters) as Record<string, string>) });
  return `/api/bff/api/v1/catalog/products/export?${query}`;
}

export const templateUrl = (format: "csv" | "xlsx") => `/api/bff/api/v1/catalog/products/import-template?format=${format}`;

/* ---- Errors --------------------------------------------------------------------------------------------------------------- */

/** Shows an API error as a toast with friendly translated text. Returns the field errors (if any) so a form can mark the fields. */
export function useReportError() {
  const message = useProblemMessage();
  return (error: unknown): Record<string, string> => {
    const problem = error instanceof ApiError ? error.problem : null;
    toast.error(message(problem));
    return problem ? fieldErrors(problem) : {};
  };
}

/* ---- Stock of one product (read-only grid, and the quick "set quantity" on the product screen) ----------------------------- */

export type ProductStock = Schemas["ProductStockDto"];
export type StockReason = Schemas["StockReason"];

/** Every variant against every branch the user may view. Needs inventory.stock.view, so callers pass `enabled`. */
export function useProductStock(productId: string, enabled: boolean) {
  return useQuery({
    queryKey: ["inventory", "product-stock", productId],
    enabled: enabled && !!productId,
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/stock/products/{productId}", { params: { path: { productId } } })),
  });
}

export const useAdjustStock = () =>
  useCatalogMutation(async (body: Schemas["AdjustStockRequest"]) => unwrap(await api.POST("/api/v1/inventory/adjustments", { body })));
