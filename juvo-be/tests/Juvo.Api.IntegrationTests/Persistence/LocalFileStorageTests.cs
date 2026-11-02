using System.Text;
using Juvo.Infrastructure.Options;
using Juvo.Infrastructure.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Juvo.Api.IntegrationTests.Persistence;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "juvo-storage-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        _storage = new LocalFileStorage(
            MsOptions.Create(new StorageOptions { RootPath = _root, PublicBaseUrl = "/files/" }),
            new FakeEnvironment(_root));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Save_read_exists_and_delete_round_trip()
    {
        await _storage.SaveAsync("products/2611/dress.webp", Bytes("image-bytes"), "image/webp", Ct);

        Assert.True(await _storage.ExistsAsync("products/2611/dress.webp", Ct));
        await using (var stream = await _storage.OpenReadAsync("products/2611/dress.webp", Ct))
        {
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);
            Assert.Equal("image-bytes", await reader.ReadToEndAsync(Ct));
        }

        await _storage.DeleteAsync("products/2611/dress.webp", Ct);
        Assert.False(await _storage.ExistsAsync("products/2611/dress.webp", Ct));
        Assert.Null(await _storage.OpenReadAsync("products/2611/dress.webp", Ct));
    }

    [Fact]
    public async Task Overwrite_replaces_the_file_and_leaves_no_temp_files()
    {
        await _storage.SaveAsync("a/b.txt", Bytes("one"), "text/plain", Ct);
        await _storage.SaveAsync("a/b.txt", Bytes("two"), "text/plain", Ct);

        await using var stream = await _storage.OpenReadAsync("a/b.txt", Ct);
        using var reader = new StreamReader(stream!);
        Assert.Equal("two", await reader.ReadToEndAsync(Ct));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("a//b.txt")]
    [InlineData("./a.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Paths_that_could_escape_the_root_are_rejected(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.SaveAsync(path, Bytes("x"), "text/plain", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.ExistsAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DeleteAsync(path, Ct));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt")));
    }

    [Fact]
    public void Public_url_uses_the_base_and_escapes_each_segment()
    {
        Assert.Equal("/files/products/red%20dress.webp", _storage.GetPublicUrl("products/red dress.webp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Juvo.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
