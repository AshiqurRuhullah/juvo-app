using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Microsoft.AspNetCore.Mvc;
using Juvo.Application.Catalog;
using Juvo.Domain.Catalog;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>Admin side of the catalog (module B): categories, brands, size charts, products and variants. Shared by every branch.</summary>
internal static class CatalogEndpoints
{
    public static WebApplication MapCatalogEndpoints(this WebApplication app)
    {
        var catalog = app.MapApiV1().MapGroup("/catalog").WithTags("Catalog");

        MapCategories(catalog.MapGroup("/categories"));
        MapBrands(catalog.MapGroup("/brands"));
        MapSizeCharts(catalog.MapGroup("/size-charts"));
        MapProducts(catalog.MapGroup("/products"));
        return app;
    }

    private static void MapCategories(RouteGroupBuilder g)
    {
        g.MapGet("", async (bool? includeInactive, CatalogLookupsService s, CancellationToken ct) => (await s.CategoryTreeAsync(includeInactive ?? true, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.CategoryView).Produces<IReadOnlyList<CategoryNodeDto>>().WithSummary("The category tree with product counts.");

        g.MapGet("/{id:guid}", async (Guid id, CatalogLookupsService s, CancellationToken ct) => (await s.GetCategoryAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.CategoryView).Produces<CategoryDto>().WithSummary("One category.");

        g.MapPost("", async (SaveCategoryRequest r, CatalogLookupsService s, CancellationToken ct) =>
                (await s.CreateCategoryAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/catalog/categories/{v.Id}", v)))
            .RequirePermission(Permissions.Catalog.CategoryCreate).Produces<CategoryDto>(StatusCodes.Status201Created).WithSummary("Add a category.");

        g.MapPut("/{id:guid}", async (Guid id, SaveCategoryRequest r, CatalogLookupsService s, CancellationToken ct) => (await s.UpdateCategoryAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.CategoryEdit).Produces<CategoryDto>().WithSummary("Edit or move a category.");

        g.MapPut("/order", async (ReorderRequest r, CatalogLookupsService s, CancellationToken ct) => (await s.ReorderCategoriesAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.CategoryEdit).Produces(StatusCodes.Status204NoContent).WithSummary("Set the order of categories by the order of the ids.");

        g.MapDelete("/{id:guid}", async (Guid id, CatalogLookupsService s, CancellationToken ct) => (await s.DeleteCategoryAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.CategoryDelete).Produces(StatusCodes.Status204NoContent).WithSummary("Delete an empty category.");
    }

    private static void MapBrands(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, CatalogLookupsService s, CancellationToken ct) => (await s.ListBrandsAsync(list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.BrandView).Produces<PagedResult<BrandDto>>().WithSummary("List brands.");

        g.MapPost("", async (SaveBrandRequest r, CatalogLookupsService s, CancellationToken ct) =>
                (await s.CreateBrandAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/catalog/brands/{v.Id}", v)))
            .RequirePermission(Permissions.Catalog.BrandCreate).Produces<BrandDto>(StatusCodes.Status201Created).WithSummary("Add a brand.");

        g.MapPut("/{id:guid}", async (Guid id, SaveBrandRequest r, CatalogLookupsService s, CancellationToken ct) => (await s.UpdateBrandAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.BrandEdit).Produces<BrandDto>().WithSummary("Edit a brand.");

        g.MapDelete("/{id:guid}", async (Guid id, CatalogLookupsService s, CancellationToken ct) => (await s.DeleteBrandAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.BrandDelete).Produces(StatusCodes.Status204NoContent).WithSummary("Delete a brand that no product uses.");
    }

    private static void MapSizeCharts(RouteGroupBuilder g)
    {
        g.MapGet("", async (CatalogLookupsService s, CancellationToken ct) => (await s.ListSizeChartsAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.SizeChartView).Produces<IReadOnlyList<SizeChartDto>>().WithSummary("List size charts.");

        g.MapPost("", async (SaveSizeChartRequest r, CatalogLookupsService s, CancellationToken ct) =>
                (await s.CreateSizeChartAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/catalog/size-charts/{v.Id}", v)))
            .RequirePermission(Permissions.Catalog.SizeChartEdit).Produces<SizeChartDto>(StatusCodes.Status201Created).WithSummary("Add a size chart.");

        g.MapPut("/{id:guid}", async (Guid id, SaveSizeChartRequest r, CatalogLookupsService s, CancellationToken ct) => (await s.UpdateSizeChartAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.SizeChartEdit).Produces<SizeChartDto>().WithSummary("Edit a size chart.");

        g.MapDelete("/{id:guid}", async (Guid id, CatalogLookupsService s, CancellationToken ct) => (await s.DeleteSizeChartAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.SizeChartEdit).Produces(StatusCodes.Status204NoContent).WithSummary("Delete a size chart that no product uses.");
    }

    private static void MapProducts(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, ProductStatus? status, Guid? categoryId, Guid? brandId, bool? featured, ProductsService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new ProductListQuery(status, categoryId, brandId, featured, null), ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductView).Produces<PagedResult<ProductListItemDto>>().WithSummary("List products (search by name, slug, SKU or barcode).");

        g.MapGet("/{id:guid}", async (Guid id, ProductsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductView).Produces<ProductDetailDto>().WithSummary("A product with variants, images and what still blocks publishing. Cost is empty without catalog.product.view-cost.");

        g.MapPost("", async (SaveProductRequest r, ProductsService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/catalog/products/{v.Id}", v)))
            .RequirePermission(Permissions.Catalog.ProductCreate).Produces<ProductDetailDto>(StatusCodes.Status201Created).WithSummary("Create a draft product.");

        g.MapPut("/{id:guid}", async (Guid id, SaveProductRequest r, ProductsService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductEdit).Produces<ProductDetailDto>().WithSummary("Edit a product.");

        g.MapDelete("/{id:guid}", async (Guid id, ProductsService s, CancellationToken ct) => (await s.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductArchive).Produces(StatusCodes.Status204NoContent).WithSummary("Delete a draft or archived product (history is kept).");

        g.MapPost("/{id:guid}/publish", async (Guid id, ProductsService s, CancellationToken ct) => (await s.PublishAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductPublish).Produces<ProductDetailDto>().WithSummary("Publish when name, category, price, an image and an active variant exist (BR-B1).");

        g.MapPost("/{id:guid}/unpublish", async (Guid id, ProductsService s, CancellationToken ct) => (await s.UnpublishAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductPublish).Produces<ProductDetailDto>().WithSummary("Take a product off the shop (back to draft).");

        g.MapPost("/{id:guid}/archive", async (Guid id, ProductsService s, CancellationToken ct) => (await s.ArchiveAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductArchive).Produces<ProductDetailDto>().WithSummary("Archive a product.");

        g.MapPost("/{id:guid}/duplicate", async (Guid id, DuplicateProductRequest r, ProductsService s, CancellationToken ct) =>
                (await s.DuplicateAsync(id, r, ct)).ToHttpResult(v => Results.Created($"/api/v1/catalog/products/{v.Id}", v)))
            .RequirePermission(Permissions.Catalog.ProductDuplicate).Produces<ProductDetailDto>(StatusCodes.Status201Created).WithSummary("Copy a product as a draft with new SKUs.");

        g.MapPost("/{id:guid}/variants", async (Guid id, SaveVariantRequest r, ProductsService s, CancellationToken ct) =>
                (await s.AddVariantAsync(id, r, ct)).ToHttpResult(v => Results.Created($"/api/v1/catalog/products/{id}/variants/{v.Id}", v)))
            .RequirePermission(Permissions.Catalog.ProductEdit).Produces<VariantDto>(StatusCodes.Status201Created).WithSummary("Add a colour/size variant with a unique SKU.");

        g.MapPut("/{id:guid}/variants/{variantId:guid}", async (Guid id, Guid variantId, SaveVariantRequest r, ProductsService s, CancellationToken ct) =>
                (await s.UpdateVariantAsync(id, variantId, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductEdit).Produces<VariantDto>().WithSummary("Edit a variant.");

        g.MapDelete("/{id:guid}/variants/{variantId:guid}", async (Guid id, Guid variantId, ProductsService s, CancellationToken ct) =>
                (await s.RemoveVariantAsync(id, variantId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductEdit).Produces(StatusCodes.Status204NoContent).WithSummary("Remove a variant (frees the SKU).");

        // Pictures. The upload is a multipart form; the API is token based (no cookies), so antiforgery does not apply.
        g.MapPost("/{id:guid}/images", async (Guid id, IFormFile file, [FromForm] string? colorName, [FromForm] string? altText,
                [FromForm] double? focalX, [FromForm] double? focalY, ProductMediaService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(id, stream, colorName, altText, focalX, focalY, ct))
                    .ToHttpResult(v => Results.Created($"/api/v1/catalog/products/{id}/images/{v.Id}", v));
            })
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(12 * 1024 * 1024))
            .RequirePermission(Permissions.Catalog.MediaUpload).Produces<ProductImageDto>(StatusCodes.Status201Created)
            .WithSummary("Upload a JPEG, PNG or WebP picture (at least 1000 px on the long side, at most 10 MB). WebP renditions, a 4:5 crop and a blur placeholder are made.");

        g.MapPut("/{id:guid}/images/{imageId:guid}", async (Guid id, Guid imageId, UpdateImageRequest r, ProductMediaService s, CancellationToken ct) =>
                (await s.UpdateAsync(id, imageId, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.MediaUpload).Produces<ProductImageDto>().WithSummary("Set the colour, alt text or focal point of a picture.");

        g.MapPut("/{id:guid}/images/order", async (Guid id, ReorderRequest r, ProductMediaService s, CancellationToken ct) => (await s.ReorderAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.MediaUpload).Produces(StatusCodes.Status204NoContent).WithSummary("Set the picture order by the order of the ids (the first is the card image).");

        g.MapDelete("/{id:guid}/images/{imageId:guid}", async (Guid id, Guid imageId, ProductMediaService s, CancellationToken ct) => (await s.DeleteAsync(id, imageId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.MediaDelete).Produces(StatusCodes.Status204NoContent).WithSummary("Delete a picture and its files.");
    }
}
