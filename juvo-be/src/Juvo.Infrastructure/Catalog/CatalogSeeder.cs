using Juvo.Application.Abstractions;
using Juvo.Application.Catalog;
using Juvo.Domain.Catalog;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juvo.Infrastructure.Catalog;

/// <summary>
/// Idempotent seed of catalog reference data (assumptions, section C2): the four top-level categories with sub-categories and two
/// size charts. It only adds what is missing by slug and never touches what an administrator edited or deleted.
/// </summary>
public sealed class CatalogSeeder(JuvoDbContext db, IUnitOfWork unitOfWork, ILogger<CatalogSeeder> logger)
{
    private static readonly (string En, string Bn, string[] Children)[] Tree =
    [
        ("Clothing", "পোশাক", ["T-shirts", "Shirts", "Pants", "Jackets"]),
        ("Shoes", "জুতা", ["Sneakers", "Sandals", "Formal shoes"]),
        ("Ladies' Dresses", "মহিলাদের পোশাক", ["Casual dresses", "Party dresses", "Kurtis"]),
        ("Purses", "পার্স", ["Handbags", "Clutches", "Wallets"]),
    ];

    private const string ClothingSizes =
        """{"columns":["Size","Chest (in)","Length (in)"],"rows":[["XS","34","26"],["S","36","27"],["M","38","28"],["L","40","29"],["XL","42","30"],["XXL","44","31"]]}""";

    private const string ShoeSizes =
        """{"columns":["EU","UK","Foot length (cm)"],"rows":[["38","5","24.0"],["39","6","24.6"],["40","6.5","25.3"],["41","7.5","26.0"],["42","8","26.6"],["43","9","27.3"]]}""";

    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        var added = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var count = 0;
            var slugs = (await db.Categories.IgnoreQueryFilters().Select(c => c.Slug).ToListAsync(ct)).ToHashSet();
            var order = 0;
            foreach (var (en, bn, children) in Tree)
            {
                var parentSlug = SlugRules.Slugify(en);
                var parent = await db.Categories.FirstOrDefaultAsync(c => c.Slug == parentSlug, ct);
                if (parent is null && !slugs.Contains(parentSlug))
                {
                    parent = new Category(en, bn, parentSlug, null);
                    parent.Update(en, bn, parentSlug, null, order, true);
                    db.Categories.Add(parent);
                    slugs.Add(parentSlug);
                    count++;
                }

                order++;
                if (parent is null)
                {
                    continue; // deleted by an administrator: leave it
                }

                var childOrder = 0;
                foreach (var child in children)
                {
                    var childSlug = SlugRules.Slugify(child);
                    if (slugs.Add(childSlug))
                    {
                        var node = new Category(child, null, childSlug, parent.Id);
                        node.Update(child, null, childSlug, parent.Id, childOrder, true);
                        db.Categories.Add(node);
                        count++;
                    }

                    childOrder++;
                }
            }

            if (!await db.SizeCharts.IgnoreQueryFilters().AnyAsync(ct))
            {
                db.SizeCharts.Add(new SizeChart("Clothing sizes", "পোশাকের মাপ", ClothingSizes, null));
                db.SizeCharts.Add(new SizeChart("Shoe sizes", "জুতার মাপ", ShoeSizes, null));
                count += 2;
            }

            await db.SaveChangesAsync(ct);
            return count;
        }, cancellationToken);

        logger.LogInformation("Catalog seed done: {Count} row(s) added.", added);
        return added;
    }
}
