using Juvo.Application.Abstractions;
using SkiaSharp;

namespace Juvo.Infrastructure.Storage;

/// <summary>SkiaSharp (MIT) implementation. Decoding failures and absurd sizes come back as a problem, never as an exception.</summary>
internal sealed class SkiaImageProcessor : IImageProcessor
{
    private const int MaxMainSide = 1600;
    private const long MaxPixels = 60_000_000; // guards against decompression bombs
    private const int WebpQuality = 82;

    public (ProcessedImage? Image, ImageProblem Problem) Process(byte[] source, double focalX, double focalY)
    {
        SKCodec? codec = null;
        try
        {
            codec = SKCodec.Create(new SKMemoryStream(source));
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
            {
                return (null, ImageProblem.NotAnImage);
            }

            var info = codec.Info;
            if ((long)info.Width * info.Height > MaxPixels)
            {
                return (null, ImageProblem.TooLarge);
            }

            var origin = codec.EncodedOrigin;
            using var decoded = SKBitmap.Decode(codec);
            if (decoded is null)
            {
                return (null, ImageProblem.NotAnImage);
            }

            using var upright = ApplyOrigin(decoded, origin);
            if (Math.Max(upright.Width, upright.Height) < ImageRenditions.MinLongSide)
            {
                return (null, ImageProblem.TooSmall);
            }

            using var main = FitWithin(upright, MaxMainSide);
            var mainBytes = Encode(main);
            var (card, thumb) = Crops(main, focalX, focalY);
            using var blur = Resize(main, 16, Math.Max(1, (int)Math.Round(16.0 * main.Height / main.Width)));
            var blurUrl = "data:image/webp;base64," + Convert.ToBase64String(Encode(blur, 30));
            return (new ProcessedImage(mainBytes, card, thumb, main.Width, main.Height, blurUrl), ImageProblem.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, ImageProblem.NotAnImage);
        }
        finally
        {
            codec?.Dispose();
        }
    }

    public (byte[] Card, byte[] Thumb)? Recrop(byte[] main, double focalX, double focalY)
    {
        using var bitmap = SKBitmap.Decode(main);
        return bitmap is null ? null : Crops(bitmap, focalX, focalY);
    }

    private static (byte[] Card, byte[] Thumb) Crops(SKBitmap source, double focalX, double focalY)
    {
        // Largest 4:5 window that fits, centred on the focal point and kept inside the picture.
        var cropWidth = Math.Min(source.Width, (int)Math.Floor(source.Height * 4.0 / 5.0));
        var cropHeight = Math.Min(source.Height, (int)Math.Floor(cropWidth * 5.0 / 4.0));
        var left = Math.Clamp((int)Math.Round(focalX * source.Width - cropWidth / 2.0), 0, source.Width - cropWidth);
        var top = Math.Clamp((int)Math.Round(focalY * source.Height - cropHeight / 2.0), 0, source.Height - cropHeight);

        using var cropped = new SKBitmap(cropWidth, cropHeight);
        if (!source.ExtractSubset(cropped, new SKRectI(left, top, left + cropWidth, top + cropHeight)))
        {
            throw new InvalidOperationException("Could not crop the picture.");
        }

        using var card = Resize(cropped, 800, 1000);
        using var thumb = Resize(cropped, 160, 200);
        return (Encode(card), Encode(thumb));
    }

    private static SKBitmap ApplyOrigin(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return bitmap.Copy();
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = swap ? new SKBitmap(bitmap.Height, bitmap.Width) : new SKBitmap(bitmap.Width, bitmap.Height);
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(result.Width, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(result.Width, result.Height); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, result.Height); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Scale(-1, 1); canvas.RotateDegrees(-90); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(result.Width, result.Height); canvas.Scale(-1, 1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, result.Height); canvas.RotateDegrees(-90); break;
        }

        using var source = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        return result;
    }

    private static SKBitmap FitWithin(SKBitmap source, int longSide)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= longSide)
        {
            return source.Copy();
        }

        var scale = (double)longSide / longest;
        return Resize(source, Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
    }

    private static SKBitmap Resize(SKBitmap source, int width, int height) =>
        source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
        ?? throw new InvalidOperationException("Could not resize the picture.");

    private static byte[] Encode(SKBitmap bitmap, int quality = WebpQuality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality);
        return data.ToArray();
    }
}
