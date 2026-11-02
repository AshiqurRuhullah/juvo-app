using Juvo.Infrastructure.Options;
using Microsoft.Extensions.Hosting;

namespace Juvo.Infrastructure.Storage;

public static class StorageLocation
{
    /// <summary>Absolute folder for uploaded files: <c>Storage:RootPath</c>, relative to the content root when not rooted.</summary>
    public static string ResolveRoot(StorageOptions options, IHostEnvironment environment) =>
        Path.GetFullPath(Path.IsPathRooted(options.RootPath)
            ? options.RootPath
            : Path.Combine(environment.ContentRootPath, options.RootPath));
}
