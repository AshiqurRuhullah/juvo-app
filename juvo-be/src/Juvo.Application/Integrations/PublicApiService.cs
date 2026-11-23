using Juvo.Application.Abstractions;
using Juvo.Application.Customers;
using Juvo.Domain.Catalog;
using Juvo.Domain.Orders;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Integrations;

/// <summary>
/// The read side of the public API (FR-AF05) and the single place where an order, a product or a stock row is turned into its public shape, so the webhooks carry
/// exactly the fields the API shows. Reads span all branches (the caller is a key, not a signed-in user) and apply the key's own branch limit by hand.
/// Never exposes cost prices, internal notes, fraud data or tracking secrets; customer details only for a caller with <c>pii:read</c>.
/// </summary>
public sealed class PublicApiService(IApplicationDbContext db, IFileStorage storage, TimeProvider clock)
{
    public const string Currency = "BDT";

    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static PageRequest Page(int? page, int? pageSize) => new PageRequest { Page = page ?? 1, PageSize = pageSize ?? 20 }.Normalized;

    // ---- orders ----------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<PublicOrderDto>>> OrdersAsync(ApiCaller caller, PublicOrderQuery query, int? page, int? pageSize, CancellationToken ct)
    {
        var request = Page(page, pageSize);
        var orders = db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().AsQueryable();
        if (caller.BranchId is { } branch)
        {
            orders = orders.Where(o => o.BranchId == branch);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<OrderStatus>(query.Status, ignoreCase: true, out var status) || !Enum.IsDefined(status))
            {
                return Invalid("status", $"Use one of: {string.Join(", ", Enum.GetNames<OrderStatus>())}.");
            }

            orders = orders.Where(o => o.Status == status);
        }

        if (query.PlacedFrom is { } from)
        {
            orders = orders.Where(o => o.PlacedAt >= from);
        }

        if (query.PlacedTo is { } to)
        {
            orders = orders.Where(o => o.PlacedAt <= to);
        }

        if (query.UpdatedSince is { } since)
        {
            orders = orders.Where(o => (o.UpdatedAt ?? o.PlacedAt) >= since);
        }

        var total = await orders.CountAsync(ct);
        var rows = await orders.Include(o => o.Items).OrderByDescending(o => o.PlacedAt).ThenBy(o => o.Id).Skip(request.Skip).Take(request.PageSize).ToListAsync(ct);
        var codes = await BranchCodesAsync(rows.Select(o => o.BranchId), ct);
        return PagedResult<PublicOrderDto>.Create([.. rows.Select(o => ToOrder(o, codes.GetValueOrDefault(o.BranchId, string.Empty), caller.Pii))], total, request);
    }

    public async Task<Result<PublicOrderDto>> OrderAsync(ApiCaller caller, string number, CancellationToken ct)
    {
        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Number == number, ct);
        if (order is null || caller.BranchId is { } branch && order.BranchId != branch)
        {
            return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
        }

        var codes = await BranchCodesAsync([order.BranchId], ct);
        return ToOrder(order, codes.GetValueOrDefault(order.BranchId, string.Empty), caller.Pii);
    }

    /// <summary>An order without personal data, for the webhooks. Null when it no longer exists.</summary>
    public async Task<PublicOrderDto?> OrderForWebhookAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return null;
        }

        var codes = await BranchCodesAsync([order.BranchId], ct);
        return ToOrder(order, codes.GetValueOrDefault(order.BranchId, string.Empty), pii: false);
    }

    private async Task<Dictionary<Guid, string>> BranchCodesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        return await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => distinct.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
    }

    private static PublicOrderDto ToOrder(Order o, string branchCode, bool pii) => new(
        o.Id, o.Number, o.Status.ToString(), o.PaymentStatus.ToString(), o.DeliveryStatus.ToString(), o.PaymentMethod, o.Source.ToString(), branchCode, o.Currency,
        o.Subtotal, o.CouponDiscount + o.PromotionDiscount + o.ManualDiscount, o.DeliveryCharge, o.TaxAmount, o.GrandTotal, o.AdvancePaid,
        o.PlacedAt, o.ConfirmedAt, o.PackedAt, o.DeliveredAt, o.CancelledAt, o.UpdatedAt ?? o.PlacedAt,
        new PublicOrderCustomer(o.CustomerId, pii ? o.CustomerMobile : CustomersAdminService.MaskMobile(o.CustomerMobile), pii ? o.CustomerName : null, pii ? o.CustomerEmail : null),
        new PublicOrderShipping(o.ShipCountryCode, o.ShipDivision, o.ShipDistrict, o.ShipUpazila, o.ShipCity,
            pii ? o.ShipName : null, pii ? o.ShipPhone : null, pii ? o.ShipAddressLine : null, pii ? o.ShipLandmark : null, pii ? o.ShipPostalCode : null),
        [.. o.Items.OrderBy(i => i.Sku, StringComparer.Ordinal).Select(i => new PublicOrderLine(i.VariantId, i.ProductId, i.Sku, i.Name, i.ColorName, i.Size, i.Quantity, i.UnitPrice, i.LineTotal))]);

    // ---- products --------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<PublicProductDto>>> ProductsAsync(DateTimeOffset? updatedSince, int? page, int? pageSize, CancellationToken ct)
    {
        var request = Page(page, pageSize);
        var products = db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Published);
        if (updatedSince is { } since)
        {
            products = products.Where(p => (p.UpdatedAt ?? p.CreatedAt) >= since);
        }

        var total = await products.CountAsync(ct);
        var rows = await products.OrderByDescending(p => p.PublishedAt).ThenBy(p => p.Id).Skip(request.Skip).Take(request.PageSize).ToListAsync(ct);
        return PagedResult<PublicProductDto>.Create(await ToProductsAsync(rows, ct), total, request);
    }

    public async Task<Result<PublicProductDto>> ProductAsync(string slug, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug && p.Status == ProductStatus.Published, ct);
        return product is null ? Error.NotFound(ErrorCodes.Catalog.ProductNotFound, "Product not found.") : (await ToProductsAsync([product], ct))[0];
    }

    /// <summary>A published product in its public shape for the webhooks; null when it is not published (or gone).</summary>
    public async Task<PublicProductDto?> ProductForWebhookAsync(Guid productId, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && p.Status == ProductStatus.Published, ct);
        return product is null ? null : (await ToProductsAsync([product], ct))[0];
    }

    /// <summary>Variants, pictures and brand names are read in separate queries (two collections in one query would multiply the rows).</summary>
    private async Task<IReadOnlyList<PublicProductDto>> ToProductsAsync(IReadOnlyList<Product> products, CancellationToken ct)
    {
        var ids = products.Select(p => p.Id).ToList();
        var brandIds = products.Where(p => p.BrandId is not null).Select(p => p.BrandId!.Value).Distinct().ToList();
        var brands = await db.Brands.IgnoreQueryFilters().AsNoTracking().Where(b => brandIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.NameEn, ct);
        var variants = (await db.ProductVariants.AsNoTracking().Where(v => ids.Contains(v.ProductId)).ToListAsync(ct)).ToLookup(v => v.ProductId);
        var images = (await db.ProductImages.AsNoTracking().Where(i => ids.Contains(i.ProductId)).ToListAsync(ct)).ToLookup(i => i.ProductId);
        var now = clock.GetUtcNow();
        return [.. products.Select(p => ToProduct(p, variants[p.Id], images[p.Id], brands, now))];
    }

    private PublicProductDto ToProduct(Product p, IEnumerable<ProductVariant> variants, IEnumerable<ProductImage> images, Dictionary<Guid, string> brands, DateTimeOffset now)
    {
        var price = p.EffectivePrice(now);
        return new PublicProductDto(
            p.Id, p.Slug, p.NameEn, p.NameBn, p.ShortDescription, p.Description, p.Material, p.TargetGroup.ToString(), p.CategoryId, p.BrandId,
            p.BrandId is { } b && brands.TryGetValue(b, out var brand) ? brand : null, Currency, p.RegularPrice, price, price < p.RegularPrice, p.IsNew, p.IsFeatured, p.Tags, p.WeightGrams,
            p.PublishedAt, p.UpdatedAt ?? p.CreatedAt,
            [.. images.OrderBy(i => i.SortOrder).Select(i => new PublicImage(storage.GetPublicUrl(i.Path), i.AltText))],
            [.. variants.OrderBy(v => v.SortOrder).ThenBy(v => v.Sku, StringComparer.Ordinal).Select(v => new PublicVariant(v.Id, v.Sku, v.ColorName, v.Size, v.PriceOverride ?? price, v.IsActive))]);
    }

    // ---- stock -----------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<PublicStockDto>>> StockAsync(ApiCaller caller, string? sku, int? page, int? pageSize, CancellationToken ct)
    {
        var request = Page(page, pageSize);
        var variants = db.ProductVariants.AsNoTracking().Where(v => db.Products.Any(p => p.Id == v.ProductId && p.Status == ProductStatus.Published));
        if (!string.IsNullOrWhiteSpace(sku))
        {
            var normalized = sku.Trim().ToUpperInvariant();
            variants = variants.Where(v => v.Sku == normalized);
        }

        var total = await variants.CountAsync(ct);
        var page1 = await variants.OrderBy(v => v.Sku).Skip(request.Skip).Take(request.PageSize).Select(v => new { v.Id, v.Sku, v.ProductId, v.TracksStock }).ToListAsync(ct);
        var variantIds = page1.Select(v => v.Id).ToList();
        var productIds = page1.Select(v => v.ProductId).Distinct().ToList();
        var slugs = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Slug, ct);

        var levels = db.StockLevels.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(s => variantIds.Contains(s.VariantId));
        if (caller.BranchId is { } branch)
        {
            levels = levels.Where(s => s.BranchId == branch);
        }

        var levelRows = await levels.ToListAsync(ct);
        var codes = await BranchCodesAsync(levelRows.Select(s => s.BranchId), ct);
        var items = page1.Select(v =>
        {
            var branches = levelRows.Where(s => s.VariantId == v.Id)
                .Select(s => new PublicStockBranch(codes.GetValueOrDefault(s.BranchId, string.Empty), s.OnHand, s.Reserved, s.Available, s.ReorderLevel))
                .OrderBy(b => b.BranchCode, StringComparer.Ordinal).ToList();
            return new PublicStockDto(v.Id, v.Sku, v.ProductId, slugs.GetValueOrDefault(v.ProductId, string.Empty), v.TracksStock, branches.Sum(b => b.OnHand), branches.Sum(b => b.Available), branches);
        }).ToList();
        return PagedResult<PublicStockDto>.Create(items, total, request);
    }

    // ---- customers -------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<PublicCustomerDto>>> CustomersAsync(ApiCaller caller, DateTimeOffset? updatedSince, int? page, int? pageSize, CancellationToken ct)
    {
        var request = Page(page, pageSize);
        var customers = db.Customers.AsNoTracking().AsQueryable();
        if (updatedSince is { } since)
        {
            customers = customers.Where(c => (c.UpdatedAt ?? c.CreatedAt) >= since);
        }

        var total = await customers.CountAsync(ct);
        var rows = await customers.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip(request.Skip).Take(request.PageSize).ToListAsync(ct);
        var ids = rows.Select(c => c.Id).ToList();
        var counts = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => ids.Contains(o.CustomerId)).GroupBy(o => o.CustomerId)
            .Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return PagedResult<PublicCustomerDto>.Create([.. rows.Select(c => ToCustomer(c, caller.Pii, counts.GetValueOrDefault(c.Id)))], total, request);
    }

    public async Task<Result<PublicCustomerDto>> CustomerAsync(ApiCaller caller, Guid id, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return Error.NotFound(ErrorCodes.Customer.NotFound, "Customer not found.");
        }

        var count = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().CountAsync(o => o.CustomerId == id, ct);
        return ToCustomer(customer, caller.Pii, count);
    }

    private static PublicCustomerDto ToCustomer(Domain.Customers.Customer c, bool pii, int orders) => new(
        c.Id, pii ? c.Mobile : CustomersAdminService.MaskMobile(c.Mobile), pii ? c.FullName : null, pii ? c.Email : CustomersAdminService.MaskEmail(c.Email),
        c.ProfileLevel.ToString(), c.CreatedAt, c.UpdatedAt ?? c.CreatedAt, orders);

    // ---- stock for the stock.low webhook ---------------------------------------------------------------------

    public async Task<StockLowData?> StockLowForWebhookAsync(Guid variantId, Guid branchId, CancellationToken ct)
    {
        var level = await db.StockLevels.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().FirstOrDefaultAsync(s => s.VariantId == variantId && s.BranchId == branchId, ct);
        var variant = await db.ProductVariants.AsNoTracking().Where(v => v.Id == variantId).Select(v => new { v.Sku, v.ProductId }).FirstOrDefaultAsync(ct);
        if (level is null || variant is null)
        {
            return null;
        }

        var slug = await db.Products.AsNoTracking().Where(p => p.Id == variant.ProductId).Select(p => p.Slug).FirstOrDefaultAsync(ct) ?? string.Empty;
        var codes = await BranchCodesAsync([branchId], ct);
        return new StockLowData(variantId, variant.Sku, slug, codes.GetValueOrDefault(branchId, string.Empty), level.OnHand, level.Available, level.ReorderLevel);
    }
}
