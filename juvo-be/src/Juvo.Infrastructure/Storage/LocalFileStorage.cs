using Juvo.Application.Abstractions;
using Juvo.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Storage;

/// <summary>Stores files on local disk below a root folder. Rejects any path that could escape the root.</summary>
internal sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;
    private readonly string _publicBase;

    public LocalFileStorage(IOptions<StorageOptions> options, IHostEnvironment environment)
    {
        _root = StorageLocation.ResolveRoot(options.Value, environment);
        _publicBase = options.Value.PublicBaseUrl.TrimEnd('/');
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string path, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Write to a temp file first so a failed upload never leaves a half-written file at the final path.
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(target, cancellationToken);
            }

            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Resolve(path);
        Stream? stream = File.Exists(full)
            ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(Resolve(path)));

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Resolve(path);
        if (File.Exists(full))
        {
            File.Delete(full);
        }

        return Task.CompletedTask;
    }

    public string GetPublicUrl(string path)
    {
        _ = Resolve(path); // validates
        var clean = path.Replace('\\', '/').TrimStart('/');
        return $"{_publicBase}/{string.Join('/', clean.Split('/').Select(Uri.EscapeDataString))}";
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A storage path is required.", nameof(path));
        }

        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(segment => segment is ".." or "." or ""))
        {
            throw new ArgumentException($"Invalid storage path '{path}'.", nameof(path));
        }

        var full = Path.GetFullPath(Path.Combine(_root, normalized));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid storage path '{path}'.", nameof(path));
        }

        return full;
    }
}
