using Juvo.Application.Catalog;
using Juvo.Application.Inventory;
using Juvo.Domain.Catalog;
using Juvo.Domain.Inventory;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Juvo.Infrastructure.Catalog;

/// <summary>
/// Demo catalog so the admin and the storefront have something to show: 16 clearly marked sample products (tag <c>sample</c>) across the four
/// categories, with colour and size variants, generated placeholder pictures, opening stock in Head Office and Online Fulfilment, and some
/// sale prices and low-stock rows. Pictures go through the real upload pipeline (WebP renditions, 4:5 card, blur placeholder).
/// Switched on with <c>Seed:SampleCatalog</c> (Development only by default). It never runs once a product without the <c>sample</c> tag exists,
/// so the owner's own catalog is never mixed with it. Delete the samples from the admin (filter by tag) or by import when real data arrives.
/// </summary>
public sealed class SampleCatalogSeeder(JuvoDbContext db, ProductMediaService media, StockLedger ledger, TimeProvider clock, ILogger<SampleCatalogSeeder> logger)
{
    private sealed record Colour(string Name, string Hex);

    private sealed record Sample(
        string Name, string NameBn, string Category, string Code, decimal Price, decimal? Sale, decimal Cost, TargetGroup Target, string[] Colours, string[] Sizes,
        bool IsNew, bool IsFeatured, bool IsBestSeller, string Chart, string Summary, string Material, string Care);

    private static readonly Dictionary<string, Colour> Palette = new[]
    {
        new Colour("Black", "#1C1C1C"), new Colour("White", "#F4F1EC"), new Colour("Red", "#C62828"), new Colour("Cream", "#EDE3D0"),
        new Colour("Blue", "#2F5D9B"), new Colour("Khaki", "#B8A67A"), new Colour("Navy", "#1F2F55"), new Colour("Brown", "#6B4423"),
        new Colour("Rose", "#D98294"), new Colour("Teal", "#1E7F7A"), new Colour("Maroon", "#6E1423"), new Colour("Wine", "#5B1030"),
        new Colour("Emerald", "#0F7B54"), new Colour("Tan", "#B07B4A"), new Colour("Gold", "#C9A24B"), new Colour("Silver", "#B5B7BB"),
    }.ToDictionary(c => c.Name);

    private const string Clothing = "Clothing sizes";
    private const string Shoes = "Shoe sizes";
    private const string None = "";
    private static readonly string[] Letter = ["S", "M", "L", "XL"];
    private static readonly string[] Free = ["Free size"];

    private static readonly Sample[] Samples =
    [
        new("Classic Cotton T-Shirt", "ক্লাসিক কটন টি-শার্ট", "t-shirts", "TSH", 750, 650, 420, TargetGroup.Unisex, ["Black", "White", "Red"], Letter, true, false, true, Clothing,
            "Soft everyday tee with a relaxed fit.", "Cotton", "Machine wash cold"),
        new("Oversized Graphic Tee", "ওভারসাইজড গ্রাফিক টি", "t-shirts", "GTE", 890, null, 510, TargetGroup.Unisex, ["Black", "Cream"], ["M", "L", "XL"], true, false, false, Clothing,
            "Drop-shoulder tee for a loose street look.", "Cotton", "Machine wash cold, do not tumble dry"),
        new("Oxford Button-Down Shirt", "অক্সফোর্ড বাটন-ডাউন শার্ট", "shirts", "OXF", 1450, null, 880, TargetGroup.Men, ["Blue", "White"], ["M", "L", "XL"], false, true, false, Clothing,
            "Smart-casual shirt that works from office to weekend.", "Cotton blend", "Machine wash warm, iron medium"),
        new("Slim Fit Chino Pants", "স্লিম ফিট চিনো প্যান্ট", "pants", "CHN", 1850, 1590, 1100, TargetGroup.Men, ["Khaki", "Navy"], ["30", "32", "34", "36"], false, false, false, Clothing,
            "Stretch chinos with a clean tapered leg.", "Cotton twill with elastane", "Machine wash cold"),
        new("Denim Jacket", "ডেনিম জ্যাকেট", "jackets", "DNJ", 3200, null, 2000, TargetGroup.Unisex, ["Blue"], ["M", "L", "XL"], false, true, false, Clothing,
            "Classic washed denim jacket.", "Denim", "Wash inside out, cold"),
        new("Everyday Canvas Sneakers", "এভরিডে ক্যানভাস স্নিকার্স", "sneakers", "SNK", 2450, null, 1500, TargetGroup.Unisex, ["White", "Black"], ["39", "40", "41", "42", "43"], false, false, true, Shoes,
            "Lightweight canvas sneakers with a cushioned insole.", "Canvas upper, rubber sole", "Wipe clean"),
        new("Leather Slide Sandals", "লেদার স্লাইড স্যান্ডেল", "sandals", "SLD", 1650, null, 950, TargetGroup.Women, ["Brown", "Black"], ["37", "38", "39", "40"], false, false, false, Shoes,
            "Easy slip-on sandals with a padded footbed.", "Leather", "Wipe with a damp cloth"),
        new("Classic Derby Shoes", "ক্লাসিক ডার্বি জুতা", "formal-shoes", "DBY", 3900, null, 2400, TargetGroup.Men, ["Black", "Brown"], ["40", "41", "42", "43"], false, true, false, Shoes,
            "Polished lace-up shoes for formal days.", "Leather", "Polish regularly"),
        new("Floral Summer Dress", "ফ্লোরাল সামার ড্রেস", "casual-dresses", "FSD", 2650, 2290, 1500, TargetGroup.Women, ["Rose", "Teal"], ["S", "M", "L"], true, true, false, Clothing,
            "Light dress with a flowing skirt.", "Viscose", "Hand wash cold"),
        new("Embroidered Cotton Kurti", "এমব্রয়ডারি কটন কুর্তি", "kurtis", "KRT", 1950, null, 1150, TargetGroup.Women, ["Maroon", "Navy"], Letter, false, false, true, Clothing,
            "Everyday kurti with neckline embroidery.", "Cotton", "Machine wash cold"),
        new("Evening Satin Gown", "ইভনিং সাটিন গাউন", "party-dresses", "ESG", 5900, null, 3600, TargetGroup.Women, ["Wine", "Emerald"], ["S", "M", "L"], false, true, false, Clothing,
            "Floor-length satin gown for special evenings.", "Satin", "Dry clean only"),
        new("Structured Leather Handbag", "স্ট্রাকচার্ড লেদার হ্যান্ডব্যাগ", "handbags", "HBG", 4200, null, 2600, TargetGroup.Women, ["Tan", "Black"], Free, false, true, false, None,
            "Roomy structured bag with a zip top.", "Leather", "Wipe with a soft cloth"),
        new("Mini Crossbody Bag", "মিনি ক্রসবডি ব্যাগ", "handbags", "MCB", 2250, 1890, 1300, TargetGroup.Women, ["Rose", "Black"], Free, false, false, true, None,
            "Compact crossbody with an adjustable strap.", "Faux leather", "Wipe with a soft cloth"),
        new("Beaded Party Clutch", "বিডেড পার্টি ক্লাচ", "clutches", "BPC", 1750, null, 980, TargetGroup.Women, ["Gold", "Silver"], Free, true, false, false, None,
            "Hand-finished clutch for parties.", "Beads on fabric", "Spot clean only"),
        new("Slim Leather Wallet", "স্লিম লেদার ওয়ালেট", "wallets", "SLW", 1250, null, 700, TargetGroup.Men, ["Black", "Brown"], Free, false, false, false, None,
            "Slim bifold with card slots and a note pocket.", "Leather", "Keep dry"),
        new("Zip Card Wallet", "জিপ কার্ড ওয়ালেট", "wallets", "ZCW", 950, null, 520, TargetGroup.Unisex, ["Navy"], Free, false, false, false, None,
            "Small zip wallet for cards and coins.", "Faux leather", "Wipe clean"),
    ];

    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await db.Products.IgnoreQueryFilters().AnyAsync(p => p.Tags == null || !p.Tags.Contains("sample"), cancellationToken))
        {
            logger.LogInformation("Sample catalog skipped: the catalog already holds products that are not samples.");
            return 0;
        }

        var seeded = 0;
        try
        {
            var categories = await db.Categories.ToDictionaryAsync(c => c.Slug, c => c.Id, cancellationToken);
            var charts = await db.SizeCharts.ToDictionaryAsync(c => c.NameEn, c => c.Id, cancellationToken);
            var branches = await db.Branches.Where(b => b.Code == "HO" || b.Code == "ONL").ToDictionaryAsync(b => b.Code, b => b.Id, cancellationToken);
            var brandId = await EnsureBrandAsync(cancellationToken);

            for (var index = 0; index < Samples.Length; index++)
            {
                var sample = Samples[index];
                var slug = SlugRules.Slugify(sample.Name);
                if (await db.Products.IgnoreQueryFilters().AnyAsync(p => p.Slug == slug, cancellationToken) || !categories.TryGetValue(sample.Category, out var categoryId))
                {
                    continue;
                }

                await SeedOneAsync(sample, index, slug, categoryId, brandId, charts.TryGetValue(sample.Chart, out var chart) ? chart : (Guid?)null, branches, cancellationToken);
                seeded++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The demo data is a convenience: a failure here must not stop the application from starting.
            logger.LogError(ex, "Sample catalog seeding failed after {Count} product(s).", seeded);
            return seeded;
        }

        logger.LogInformation("Sample catalog seeded: {Count} product(s).", seeded);
        return seeded;
    }

    private async Task<Guid> EnsureBrandAsync(CancellationToken cancellationToken)
    {
        var existing = await db.Brands.FirstOrDefaultAsync(b => b.Slug == "juvo", cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var brand = new Brand("Juvo", null, "juvo");
        brand.Update("Juvo", null, "juvo", true);
        db.Brands.Add(brand);
        await db.SaveChangesAsync(cancellationToken);
        return brand.Id;
    }

    private async Task SeedOneAsync(
        Sample sample, int index, string slug, Guid categoryId, Guid brandId, Guid? chartId, Dictionary<string, Guid> branches, CancellationToken cancellationToken)
    {
        var details = new ProductDetails(
            sample.Name, sample.NameBn, slug, categoryId, brandId, sample.Summary, $"{sample.Summary} Sample product for demonstration: replace it with your own details.",
            sample.Material, sample.Care, "sample", sample.Target, 350, null, null, null, sample.Price, sample.Sale, null, null, sample.Cost, sample.IsNew,
            sample.IsFeatured, sample.IsBestSeller, chartId, null, null, null, null);
        var product = new Product(details);
        db.Products.Add(product);

        var variants = new List<ProductVariant>();
        var order = 0;
        foreach (var colourName in sample.Colours)
        {
            var colour = Palette[colourName];
            foreach (var size in sample.Sizes)
            {
                var sku = $"JV-{sample.Code}-{colourName[..3].ToUpperInvariant()}-{size.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant()}";
                var variant = new ProductVariant(product.Id, sku, colour.Name, colour.Hex, size);
                variant.Update(sku, colour.Name, colour.Hex, size, null, null, null, true, order++);
                db.ProductVariants.Add(variant);
                variants.Add(variant);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var colourName in sample.Colours)
        {
            var bytes = Picture(sample.Name, Palette[colourName]);
            using var stream = new MemoryStream(bytes);
            var uploaded = await media.UploadAsync(product.Id, stream, colourName, $"{sample.Name} in {colourName} (sample picture)", 0.5, 0.5, cancellationToken);
            if (uploaded.IsFailure)
            {
                throw new InvalidOperationException($"Sample picture for {sample.Name} was refused: {uploaded.Error!.Message}");
            }
        }

        for (var v = 0; v < variants.Count; v++)
        {
            // Spread of quantities that is stable between runs; every ninth variant is low on stock to show the alerts.
            var low = (index + v) % 9 == 0;
            if (branches.TryGetValue("HO", out var ho))
            {
                var level = await ledger.LevelAsync(ho, variants[v].Id, cancellationToken);
                ledger.TryChange(level, low ? 2 : 6 + (((v * 7) + (index * 3)) % 18), StockReason.OpeningStock, "Sample data", null, null);
                level.SetReorderLevel(4);
            }

            if (branches.TryGetValue("ONL", out var online))
            {
                var level = await ledger.LevelAsync(online, variants[v].Id, cancellationToken);
                ledger.TryChange(level, 2 + (((v * 5) + index) % 8), StockReason.OpeningStock, "Sample data", null, null);
            }
        }

        product.Publish(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A plain placeholder: a colour gradient, a soft highlight and the product name. 1200 x 1500 so it meets the upload rules.</summary>
    private static byte[] Picture(string name, Colour colour)
    {
        const int width = 1200;
        const int height = 1500;
        var baseColour = SKColor.Parse(colour.Hex);
        var dark = new SKColor((byte)(baseColour.Red * 0.62), (byte)(baseColour.Green * 0.62), (byte)(baseColour.Blue * 0.62));
        var light = new SKColor((byte)Math.Min(255, baseColour.Red + ((255 - baseColour.Red) * 0.3)), (byte)Math.Min(255, baseColour.Green + ((255 - baseColour.Green) * 0.3)),
            (byte)Math.Min(255, baseColour.Blue + ((255 - baseColour.Blue) * 0.3)));
        var luminance = (0.299 * baseColour.Red) + (0.587 * baseColour.Green) + (0.114 * baseColour.Blue);
        var text = luminance > 150 ? new SKColor(0x1C, 0x08, 0x07) : SKColors.White;

        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [light, dark], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(0, 0, width, height, paint);
        }

        using (var glow = SKShader.CreateRadialGradient(new SKPoint(width / 2f, height * 0.42f), 520, [text.WithAlpha(46), text.WithAlpha(0)], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = glow })
        {
            canvas.DrawRect(0, 0, width, height, paint);
        }

        using var monogram = new SKFont(SKTypeface.FromFamilyName("Georgia", SKFontStyle.Bold) ?? SKTypeface.Default, 520);
        using var monogramPaint = new SKPaint { Color = text.WithAlpha(70), IsAntialias = true };
        canvas.DrawText("J", width / 2f, height * 0.55f, SKTextAlign.Center, monogram, monogramPaint);

        using var title = new SKFont(SKTypeface.FromFamilyName("Georgia") ?? SKTypeface.Default, 64);
        using var titlePaint = new SKPaint { Color = text, IsAntialias = true };
        var y = height - 250f;
        foreach (var line in Wrap(name, title, width - 160))
        {
            canvas.DrawText(line, width / 2f, y, SKTextAlign.Center, title, titlePaint);
            y += 84;
        }

        using var small = new SKFont(SKTypeface.Default, 38);
        using var smallPaint = new SKPaint { Color = text.WithAlpha(210), IsAntialias = true };
        canvas.DrawText($"{colour.Name}  |  SAMPLE PICTURE", width / 2f, height - 70f, SKTextAlign.Center, small, smallPaint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && font.MeasureText(candidate) > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }
}
