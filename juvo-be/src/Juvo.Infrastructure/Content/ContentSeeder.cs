using Juvo.Domain.Content;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Infrastructure.Content;

/// <summary>
/// Idempotent seed of the site content (module Q). Only facts from the SRS are written: the Facebook page (FR-Q03) and the shop email (FR-Q02). Page texts, the About story and the
/// badge wording are never invented: the six pages exist as empty drafts and the four badges as empty, switched-off rows until the owner writes them. Existing rows are never touched.
/// </summary>
public sealed class ContentSeeder(JuvoDbContext db)
{
    public const string FacebookUrl = "https://facebook.com/juvobd";
    public const string ShopEmail = "juvobd@gmail.com";

    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        var added = 0;
        var pages = await db.ContentPages.Select(p => p.Key).ToListAsync(cancellationToken);
        foreach (var key in ContentPageKeys.All.Where(k => !pages.Contains(k)))
        {
            db.ContentPages.Add(new ContentPage(key));
            added++;
        }

        var badges = await db.TrustBadges.Select(b => b.Key).ToListAsync(cancellationToken);
        var order = 0;
        foreach (var key in TrustBadgeKeys.All)
        {
            order++;
            if (!badges.Contains(key))
            {
                db.TrustBadges.Add(new TrustBadge(key, order));
                added++;
            }
        }

        if (!await db.BrandProfiles.AnyAsync(cancellationToken))
        {
            db.BrandProfiles.Add(new BrandProfile("Juvo BD", ShopEmail));
            db.SocialLinks.Add(new SocialLink(SocialPlatform.Facebook, FacebookUrl, true, 1)); // first run only, so a link the owner removes stays removed
            added += 2;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return added;
    }
}
