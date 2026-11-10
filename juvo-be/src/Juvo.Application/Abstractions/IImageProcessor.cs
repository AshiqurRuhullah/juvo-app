namespace Juvo.Application.Abstractions;

/// <summary>Result of processing one uploaded picture: WebP renditions plus the size of the main one.</summary>
public sealed record ProcessedImage(byte[] Main, byte[] Card, byte[] Thumb, int Width, int Height, string BlurDataUrl);

public enum ImageProblem
{
    None,
    NotAnImage,
    TooSmall,
    TooLarge,
}

/// <summary>Decodes, validates, resizes and re-encodes pictures. Implemented with a native library in infrastructure.</summary>
public interface IImageProcessor
{
    /// <summary>
    /// Reads the picture (JPEG, PNG or WebP). The main rendition keeps the aspect ratio (long side at most 1600 px), the card and
    /// thumbnail are 4:5 crops centred on the focal point (0..1). Returns the problem instead of throwing for bad input.
    /// </summary>
    (ProcessedImage? Image, ImageProblem Problem) Process(byte[] source, double focalX, double focalY);

    /// <summary>Rebuilds the 4:5 crops from an already processed main rendition after the focal point changed.</summary>
    (byte[] Card, byte[] Thumb)? Recrop(byte[] main, double focalX, double focalY);
}

/// <summary>Naming of the renditions that sit next to the main file.</summary>
public static class ImageRenditions
{
    public const int MinLongSide = 1000;
    public const long MaxBytes = 10 * 1024 * 1024;

    public static string Card(string mainPath) => Suffix(mainPath, "-card");

    public static string Thumb(string mainPath) => Suffix(mainPath, "-thumb");

    private static string Suffix(string path, string suffix)
    {
        var dot = path.LastIndexOf('.');
        return dot <= path.LastIndexOf('/') ? path + suffix : path[..dot] + suffix + path[dot..];
    }
}
