namespace Juvo.Application.Abstractions;

/// <summary>
/// Where uploaded files live. The default implementation writes to local disk; an S3-compatible
/// implementation can be switched on by configuration. Paths are logical, slash separated and relative
/// (for example <c>products/2611/abc.webp</c>); implementations must reject traversal.
/// </summary>
public interface IFileStorage
{
    Task SaveAsync(string path, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the file does not exist.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Public URL for a stored file.</summary>
    string GetPublicUrl(string path);
}
