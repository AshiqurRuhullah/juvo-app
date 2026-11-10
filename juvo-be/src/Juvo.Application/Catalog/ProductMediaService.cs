using Juvo.Application.Abstractions;
using Juvo.Domain.Catalog;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Catalog;

/// <summary>
/// Product pictures (module B, media): upload with validation, WebP renditions with a 4:5 focal-point crop and a blur placeholder,
/// per-colour sets, reorder and delete. Files live behind <see cref="IFileStorage"/>.
/// </summary>
public sealed class ProductMediaService(IApplicationDbContext db, IFileStorage storage, IImageProcessor processor, TimeProvider clock)
{
    public const int MaxImagesPerProduct = 12;

    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error ImageNotFound() => Error.NotFound(ErrorCodes.Catalog.ImageNotFound, "Image not found.");

    public async Task<Result<ProductImageDto>> UploadAsync(
        Guid productId, Stream content, string? colorName, string? altText, double? focalX, double? focalY, CancellationToken cancellationToken)
    {
        var product = await db.Products.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
        {
            return Error.NotFound(ErrorCodes.Catalog.ProductNotFound, "Product not found.");
        }

        if (product.Status == ProductStatus.Archived)
        {
            return Error.Conflict(ErrorCodes.Catalog.ProductArchived, "An archived product cannot be changed.");
        }

        if (product.Images.Count >= MaxImagesPerProduct)
        {
            return Invalid("file", $"A product can have at most {MaxImagesPerProduct} images.");
        }

        var fx = focalX ?? 0.5;
        var fy = focalY ?? 0.5;
        if (fx is < 0 or > 1 || fy is < 0 or > 1)
        {
            return Invalid("focal", "The focal point must be between 0 and 1.");
        }

        var read = await ReadBoundedAsync(content, cancellationToken);
        if (read is null)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["file"] = ["The image must be 10 MB or smaller."] });
        }

        var (image, problem) = processor.Process(read, fx, fy);
        if (image is null)
        {
            var message = problem switch
            {
                ImageProblem.TooSmall => $"The image must be at least {ImageRenditions.MinLongSide} px on its longer side.",
                ImageProblem.TooLarge => "The image has too many pixels. Resize it and try again.",
                _ => "The file is not a JPEG, PNG or WebP image.",
            };
            return Error.Validation(new Dictionary<string, string[]> { ["file"] = [message] }, message);
        }

        var path = NewPath(productId);
        var entity = new ProductImage(productId, path, colorName, product.Images.Count == 0 ? 0 : product.Images.Max(i => i.SortOrder) + 1);
        entity.SetDetails(colorName, altText, fx, fy);
        entity.SetMedia(image.Width, image.Height, image.BlurDataUrl);

        await SaveFilesAsync(path, image.Main, image.Card, image.Thumb, cancellationToken);
        try
        {
            db.ProductImages.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await DeleteFilesAsync(path, CancellationToken.None);
            throw;
        }

        return ToDto(entity);
    }

    public async Task<Result<ProductImageDto>> UpdateAsync(Guid productId, Guid imageId, UpdateImageRequest request, CancellationToken cancellationToken)
    {
        var image = await db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == productId, cancellationToken);
        if (image is null)
        {
            return ImageNotFound();
        }

        var fx = request.FocalX ?? image.FocalX;
        var fy = request.FocalY ?? image.FocalY;
        if (fx is < 0 or > 1 || fy is < 0 or > 1)
        {
            return Invalid("focal", "The focal point must be between 0 and 1.");
        }

        var focalChanged = Math.Abs(fx - image.FocalX) > 1e-9 || Math.Abs(fy - image.FocalY) > 1e-9;
        var oldPath = image.Path;
        string? newPath = null;
        if (focalChanged)
        {
            await using var stream = await storage.OpenReadAsync(oldPath, cancellationToken);
            if (stream is null)
            {
                return ImageNotFound();
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            var crops = processor.Recrop(buffer.ToArray(), fx, fy);
            if (crops is null)
            {
                return Invalid("file", "The stored image could not be read.");
            }

            // Copy on write: a duplicated product shares files, so a shared picture is re-saved under a new name.
            var shared = await db.ProductImages.AnyAsync(i => i.Path == oldPath && i.Id != image.Id, cancellationToken);
            var target = shared ? NewPath(productId) : oldPath;
            if (shared)
            {
                buffer.Position = 0;
                await storage.SaveAsync(target, buffer, "image/webp", cancellationToken);
                newPath = target;
            }

            using (var card = new MemoryStream(crops.Value.Card))
            {
                await storage.SaveAsync(ImageRenditions.Card(target), card, "image/webp", cancellationToken);
            }

            using (var thumb = new MemoryStream(crops.Value.Thumb))
            {
                await storage.SaveAsync(ImageRenditions.Thumb(target), thumb, "image/webp", cancellationToken);
            }
        }

        image.SetDetails(request.ColorName ?? image.ColorName, request.AltText ?? image.AltText, fx, fy);
        if (newPath is not null)
        {
            image.SetPath(newPath);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(image);
    }

    public async Task<Result> ReorderAsync(Guid productId, ReorderRequest request, CancellationToken cancellationToken)
    {
        var images = await db.ProductImages.Where(i => i.ProductId == productId).ToListAsync(cancellationToken);
        if (!await db.Products.AnyAsync(p => p.Id == productId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Catalog.ProductNotFound, "Product not found.");
        }

        if (request.Ids.Count != images.Count || request.Ids.Distinct().Count() != images.Count || images.Any(i => !request.Ids.Contains(i.Id)))
        {
            return Invalid("ids", "List every image of the product exactly once.");
        }

        for (var order = 0; order < request.Ids.Count; order++)
        {
            images.First(i => i.Id == request.Ids[order]).SetOrder(order);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(Guid productId, Guid imageId, CancellationToken cancellationToken)
    {
        var image = await db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == productId, cancellationToken);
        if (image is null)
        {
            return ImageNotFound();
        }

        var path = image.Path;
        db.ProductImages.Remove(image);
        await db.SaveChangesAsync(cancellationToken);

        // A duplicated product may still use the same files.
        if (!await db.ProductImages.AnyAsync(i => i.Path == path, cancellationToken))
        {
            await DeleteFilesAsync(path, cancellationToken);
        }

        return Result.Success();
    }

    public ProductImageDto ToDto(ProductImage i) => ToDto(i, storage);

    public static ProductImageDto ToDto(ProductImage i, IFileStorage storage) => new(
        i.Id, i.Path, storage.GetPublicUrl(i.Path), storage.GetPublicUrl(ImageRenditions.Card(i.Path)), storage.GetPublicUrl(ImageRenditions.Thumb(i.Path)),
        i.ColorName, i.SortOrder, i.AltText, i.FocalX, i.FocalY, i.Width, i.Height, i.BlurDataUrl);

    private string NewPath(Guid productId) =>
        $"products/{clock.GetUtcNow():yyMM}/{productId:N}/{Guid.NewGuid():N}.webp";

    private async Task SaveFilesAsync(string path, byte[] main, byte[] card, byte[] thumb, CancellationToken ct)
    {
        try
        {
            foreach (var (name, bytes) in new[] { (path, main), (ImageRenditions.Card(path), card), (ImageRenditions.Thumb(path), thumb) })
            {
                using var stream = new MemoryStream(bytes);
                await storage.SaveAsync(name, stream, "image/webp", ct);
            }
        }
        catch
        {
            await DeleteFilesAsync(path, CancellationToken.None);
            throw;
        }
    }

    private async Task DeleteFilesAsync(string path, CancellationToken ct)
    {
        await storage.DeleteAsync(path, ct);
        await storage.DeleteAsync(ImageRenditions.Card(path), ct);
        await storage.DeleteAsync(ImageRenditions.Thumb(path), ct);
    }

    /// <summary>Reads at most the size limit; null when the stream is larger (the declared length is never trusted).</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > ImageRenditions.MaxBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }
}
