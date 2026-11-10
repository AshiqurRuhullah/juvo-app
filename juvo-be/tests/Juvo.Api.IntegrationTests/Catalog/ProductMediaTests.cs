using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Shared.Security;
using SkiaSharp;

namespace Juvo.Api.IntegrationTests.Catalog;

public class ProductMediaTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly TestApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A picture whose left half is red and right half blue, so a crop shows which side the focal point picked.</summary>
    private static byte[] Picture(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        using var red = new SKPaint { Color = SKColors.Red };
        using var blue = new SKPaint { Color = SKColors.Blue };
        canvas.DrawRect(0, 0, width / 2f, height, red);
        canvas.DrawRect(width / 2f, 0, width / 2f, height, blue);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    private async Task<(string Token, Guid ProductId)> ProductAsync()
    {
        var admin = await _api.LoginAdminAsync();
        var category = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/categories",
            new { nameEn = $"Media-{Guid.NewGuid():N}"[..18], sortOrder = 0 }, admin.Access), HttpStatusCode.Created);
        var product = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/products", new
        {
            nameEn = $"Tee-{Guid.NewGuid():N}"[..16], categoryId = category.GetProperty("id").GetGuid(), regularPrice = 1500,
            targetGroup = "Unisex", isNew = false, isFeatured = false, isBestSeller = false,
        }, admin.Access), HttpStatusCode.Created);
        return (admin.Access, product.GetProperty("id").GetGuid());
    }

    private async Task<HttpResponseMessage> UploadAsync(string token, Guid productId, byte[] bytes, string? color = null, string? alt = null,
        double? focalX = null, double? focalY = null, string fileName = "photo.jpg")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", fileName);
        if (color is not null) { form.Add(new StringContent(color), "colorName"); }
        if (alt is not null) { form.Add(new StringContent(alt), "altText"); }
        if (focalX is not null) { form.Add(new StringContent(focalX.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "focalX"); }
        if (focalY is not null) { form.Add(new StringContent(focalY.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "focalY"); }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/catalog/products/{productId}/images") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _api.Client.SendAsync(request, Ct);
    }

    private async Task<SKBitmap> DownloadAsync(string url)
    {
        var response = await _api.Client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.NotNull(bitmap);
        return bitmap;
    }

    [Fact]
    public async Task Upload_makes_webp_renditions_a_4x5_card_and_a_blur_placeholder()
    {
        var (token, id) = await ProductAsync();

        var json = await TestApi.ExpectAsync(await UploadAsync(token, id, Picture(2400, 1600), "Red", "A red and blue tee"), HttpStatusCode.Created);

        Assert.Equal(1600, json.GetProperty("width").GetInt32()); // long side limited to 1600
        Assert.Equal(1067, json.GetProperty("height").GetInt32());
        Assert.Equal("Red", json.GetProperty("colorName").GetString());
        Assert.Equal("A red and blue tee", json.GetProperty("altText").GetString());
        Assert.StartsWith("data:image/webp;base64,", json.GetProperty("blurDataUrl").GetString());

        using var main = await DownloadAsync(json.GetProperty("url").GetString()!);
        using var card = await DownloadAsync(json.GetProperty("cardUrl").GetString()!);
        using var thumb = await DownloadAsync(json.GetProperty("thumbUrl").GetString()!);
        Assert.Equal(1600, main.Width);
        Assert.Equal((800, 1000), (card.Width, card.Height));
        Assert.Equal((160, 200), (thumb.Width, thumb.Height));

        var product = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products/{id}", token), HttpStatusCode.OK);
        Assert.Single(product.GetProperty("images").EnumerateArray());
    }

    [Fact]
    public async Task The_focal_point_decides_which_part_of_the_picture_the_card_shows_and_can_be_changed_later()
    {
        var (token, id) = await ProductAsync();
        var left = await TestApi.ExpectAsync(await UploadAsync(token, id, Picture(2400, 1600), focalX: 0.0, focalY: 0.5), HttpStatusCode.Created);

        using (var card = await DownloadAsync(left.GetProperty("cardUrl").GetString()!))
        {
            var pixel = card.GetPixel(card.Width / 2, card.Height / 2);
            Assert.True(pixel.Red > 200 && pixel.Blue < 60, "left focal point should show the red half");
        }

        var imageId = left.GetProperty("id").GetGuid();
        var moved = await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/catalog/products/{id}/images/{imageId}",
            new { focalX = 1.0, focalY = 0.5 }, token), HttpStatusCode.OK);

        Assert.Equal(1.0, moved.GetProperty("focalX").GetDouble());
        using var after = await DownloadAsync(moved.GetProperty("cardUrl").GetString()!);
        var recropped = after.GetPixel(after.Width / 2, after.Height / 2);
        Assert.True(recropped.Blue > 200 && recropped.Red < 60, "right focal point should show the blue half");
    }

    [Fact]
    public async Task Pictures_smaller_than_1000px_not_images_or_too_big_are_refused()
    {
        var (token, id) = await ProductAsync();

        var small = await UploadAsync(token, id, Picture(900, 700));
        Assert.Equal(HttpStatusCode.BadRequest, small.StatusCode);
        Assert.Contains("1000", await small.Content.ReadAsStringAsync(Ct));

        var text = await UploadAsync(token, id, "not a picture"u8.ToArray(), fileName: "evil.jpg");
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);

        var tooBig = await UploadAsync(token, id, new byte[10 * 1024 * 1024 + 1]);
        Assert.True(tooBig.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);

        var badFocal = await UploadAsync(token, id, Picture(1200, 1200), focalX: 2);
        Assert.Equal(HttpStatusCode.BadRequest, badFocal.StatusCode);

        var product = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products/{id}", token), HttpStatusCode.OK);
        Assert.Empty(product.GetProperty("images").EnumerateArray());
    }

    [Fact]
    public async Task PNG_uploads_work_and_a_product_can_hold_at_most_twelve_pictures()
    {
        var (token, id) = await ProductAsync();
        var png = Picture(1200, 1200, SKEncodedImageFormat.Png);

        for (var i = 0; i < 12; i++)
        {
            await TestApi.ExpectAsync(await UploadAsync(token, id, png), HttpStatusCode.Created);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(token, id, png)).StatusCode);
    }

    [Fact]
    public async Task Pictures_can_be_reordered_and_deleted_and_the_files_go_with_them()
    {
        var (token, id) = await ProductAsync();
        var a = await TestApi.ExpectAsync(await UploadAsync(token, id, Picture(1200, 1200), "Red"), HttpStatusCode.Created);
        var b = await TestApi.ExpectAsync(await UploadAsync(token, id, Picture(1200, 1200), "Blue"), HttpStatusCode.Created);
        var idA = a.GetProperty("id").GetGuid();
        var idB = b.GetProperty("id").GetGuid();

        var partial = await _api.PutAsync($"/api/v1/catalog/products/{id}/images/order", new { ids = new[] { idA } }, token);
        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PutAsync($"/api/v1/catalog/products/{id}/images/order", new { ids = new[] { idB, idA } }, token)).StatusCode);
        var product = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products/{id}", token), HttpStatusCode.OK);
        var order = product.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([idB, idA], order);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/catalog/products/{id}/images/{idA}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.Client.GetAsync(a.GetProperty("url").GetString(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.Client.GetAsync(a.GetProperty("cardUrl").GetString(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.Client.GetAsync(b.GetProperty("url").GetString(), Ct)).StatusCode);
        Assert.Equal("CATALOG.IMAGE_NOT_FOUND", await TestApi.ErrorCodeAsync(await _api.DeleteAsync($"/api/v1/catalog/products/{id}/images/{idA}", token)));
    }

    [Fact]
    public async Task A_duplicated_product_shares_files_until_one_side_changes_them()
    {
        var (token, id) = await ProductAsync();
        var image = await TestApi.ExpectAsync(await UploadAsync(token, id, Picture(2400, 1600), focalX: 0, focalY: 0.5), HttpStatusCode.Created);
        var copy = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/duplicate", new { }, token), HttpStatusCode.Created);
        var copyId = copy.GetProperty("id").GetGuid();
        var copied = copy.GetProperty("images")[0];
        Assert.Equal(image.GetProperty("path").GetString(), copied.GetProperty("path").GetString());
        Assert.Equal(image.GetProperty("width").GetInt32(), copied.GetProperty("width").GetInt32());

        // Moving the focal point on the copy must not change the original's card.
        var moved = await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/catalog/products/{copyId}/images/{copied.GetProperty("id").GetGuid()}",
            new { focalX = 1.0 }, token), HttpStatusCode.OK);
        Assert.NotEqual(image.GetProperty("path").GetString(), moved.GetProperty("path").GetString());
        using var original = await DownloadAsync(image.GetProperty("cardUrl").GetString()!);
        Assert.True(original.GetPixel(original.Width / 2, original.Height / 2).Red > 200);

        // Deleting the copy's picture keeps the files the original still uses.
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/catalog/products/{copyId}/images/{moved.GetProperty("id").GetGuid()}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.Client.GetAsync(image.GetProperty("url").GetString(), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_real_upload_satisfies_the_publish_rule()
    {
        var (token, id) = await ProductAsync();
        await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/variants", new { sku = $"MED-{Guid.NewGuid():N}"[..14], colorName = "Red", size = "M" }, token), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PostAsync($"/api/v1/catalog/products/{id}/publish", null, token)).StatusCode);

        await TestApi.ExpectAsync(await UploadAsync(token, id, Picture(1200, 1500)), HttpStatusCode.Created);

        await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/publish", null, token), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Media_endpoints_need_the_media_permissions()
    {
        var admin = await _api.LoginAdminAsync();
        var (_, id) = await ProductAsync();
        var image = await TestApi.ExpectAsync(await UploadAsync(admin.Access, id, Picture(1200, 1200)), HttpStatusCode.Created);
        var role = await _api.CreateRoleAsync(admin.Access, $"Viewer-{Guid.NewGuid():N}"[..16], Permissions.Catalog.ProductView);
        var user = await _api.CreateUserAsync(admin.Access, $"v{Guid.NewGuid():N}@juvo.test", "Viewer", global: true, (null, role));
        var viewer = await _api.LoginAsync(user.Email, user.Password);

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(viewer.Access, id, Picture(1200, 1200))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.DeleteAsync($"/api/v1/catalog/products/{id}/images/{image.GetProperty("id").GetGuid()}", viewer.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync("bad", id, Picture(1200, 1200))).StatusCode);
    }
}
