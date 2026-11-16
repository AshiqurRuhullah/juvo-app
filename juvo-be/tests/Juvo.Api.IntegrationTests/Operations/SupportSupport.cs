using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Infrastructure.Outbox;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Operations;

/// <summary>Drives the real support endpoints (staff, customer and the public contact form) and reads what they leave in the database.</summary>
public sealed class SupportHelper(OpsFactory factory)
{
    public const string TicketsUrl = "/api/v1/support/tickets";

    public static readonly string[] AllSupport =
    [
        "support.ticket.view", "support.ticket.create", "support.ticket.assign", "support.ticket.reply", "support.ticket.close", "support.canned-reply.view", "support.canned-reply.edit", "support.report.view",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public OpsHelper O { get; } = new(new OrderHelper(new StoreHelper(factory)));

    public StoreHelper H => O.H;

    public static string Normalised(string mobile) => "+88" + mobile;

    public Task<string> StaffAsync(string admin, Guid? branch, params string[] permissions) => O.StaffAsync(admin, branch, permissions);

    // ---- opening --------------------------------------------------------------------------------------------

    public Task<HttpResponseMessage> TryContactAsync(string? mobile = null, string name = "Nadia Rahman", string? email = null, string subject = "Where is my parcel", string message = "It was due yesterday.", string? category = null, string? orderNumber = null) =>
        H.SendAsync(HttpMethod.Post, "/api/v1/store/contact", new { name, mobile = mobile ?? StoreHelper.NewMobile(), email, subject, message, category, orderNumber });

    public async Task<string> ContactAsync(string? mobile = null, string? email = null, string subject = "Where is my parcel", string message = "It was due yesterday.", string? category = null)
    {
        var json = await TestApi.ExpectAsync(await TryContactAsync(mobile, email: email, subject: subject, message: message, category: category), HttpStatusCode.Created);
        return json.GetProperty("number").GetString()!;
    }

    public Task<JsonElement> MyCreateAsync(string customer, string subject = "Wrong size", string message = "The shirt is too small.", string? category = "order", string? orderNumber = null, string[]? paths = null) =>
        H.JsonAsync(HttpMethod.Post, "/api/v1/me/tickets", HttpStatusCode.Created, new { subject, message, category, orderNumber, attachmentPaths = paths }, customer);

    public Task<HttpResponseMessage> TryMyCreateAsync(string customer, string? orderNumber = null, string subject = "Help", string message = "Please help.", string[]? paths = null) =>
        H.SendAsync(HttpMethod.Post, "/api/v1/me/tickets", new { subject, message, category = "order", orderNumber, attachmentPaths = paths }, customer);

    public Task<JsonElement> StaffCreateAsync(string staff, string mobile, string? orderNumber = null, string subject = "Phone call", string message = "Customer phoned.", string? category = "other", string? priority = null, Guid? assignee = null, string channel = "Phone") =>
        H.JsonAsync(HttpMethod.Post, TicketsUrl, HttpStatusCode.Created, new { mobile, customerName = "Walk In", subject, message, category, priority, channel, orderNumber, assigneeId = assignee }, staff);

    public Task<HttpResponseMessage> TryStaffCreateAsync(string staff, object body) => H.SendAsync(HttpMethod.Post, TicketsUrl, body, staff);

    /// <summary>A guest ticket by contact form, read back by staff (so the test has its id).</summary>
    public async Task<JsonElement> GuestTicketAsync(string staff, string? mobile = null, string? email = null, string subject = "Where is my parcel", string? category = null)
    {
        var number = await ContactAsync(mobile, email, subject, category: category);
        return await FindAsync(staff, number);
    }

    public async Task<JsonElement> FindAsync(string staff, string number)
    {
        var list = await H.JsonAsync(HttpMethod.Get, $"{TicketsUrl}?search={number}", HttpStatusCode.OK, bearer: staff);
        var id = Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        return await GetAsync(staff, id);
    }

    public Task<JsonElement> GetAsync(string staff, Guid id) => H.JsonAsync(HttpMethod.Get, $"{TicketsUrl}/{id}", HttpStatusCode.OK, bearer: staff);

    // ---- acting ---------------------------------------------------------------------------------------------

    public Task<JsonElement> ActAsync(string staff, Guid id, string action, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        H.JsonAsync(HttpMethod.Post, $"{TicketsUrl}/{id}/{action}", expected, body ?? new { }, staff);

    public Task<HttpResponseMessage> TryActAsync(string bearer, Guid id, string action, object? body = null) =>
        H.SendAsync(HttpMethod.Post, $"{TicketsUrl}/{id}/{action}", body ?? new { }, bearer);

    public Task<JsonElement> ReplyAsync(string staff, Guid id, string text, bool isInternal = false, string? setStatus = null) =>
        ActAsync(staff, id, "reply", new { text, @internal = isInternal, setStatus });

    public Task<JsonElement> MyActAsync(string customer, Guid id, string action, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        H.JsonAsync(HttpMethod.Post, $"/api/v1/me/tickets/{id}/{action}", expected, body ?? new { }, customer);

    public Task<HttpResponseMessage> TryMyActAsync(string customer, Guid id, string action, object? body = null) =>
        H.SendAsync(HttpMethod.Post, $"/api/v1/me/tickets/{id}/{action}", body ?? new { }, customer);

    public Task<JsonElement> MyGetAsync(string customer, Guid id) => H.JsonAsync(HttpMethod.Get, $"/api/v1/me/tickets/{id}", HttpStatusCode.OK, bearer: customer);

    // ---- files ----------------------------------------------------------------------------------------------

    /// <summary>Just enough of a PNG for the upload check, which looks at the first bytes.</summary>
    public static byte[] PngBytes() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    public async Task<HttpResponseMessage> UploadAsync(string url, string bearer, byte[] bytes, string fileName = "proof.png", string contentType = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await H.Api.Client.SendAsync(request, Ct);
    }

    public async Task<string> UploadedPathAsync(string url, string bearer)
    {
        var json = await TestApi.ExpectAsync(await UploadAsync(url, bearer, PngBytes()), HttpStatusCode.Created);
        return json.GetProperty("path").GetString()!;
    }

    // ---- the database, the outbox and the clock -----------------------------------------------------------------

    public Task<T> DbAsync<T>(Func<JuvoDbContext, Task<T>> action) => O.WithDbAsync(action);

    public async Task DrainOutboxAsync()
    {
        var processor = factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    /// <summary>Moves the resolution time of a ticket into the past (the domain never lets a caller choose it).</summary>
    public Task SetResolvedAtAsync(Guid id, DateTimeOffset at) =>
        DbAsync(db => db.Tickets.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ResolvedAt, (DateTimeOffset?)at), Ct));

    public Task SetClosedAtAsync(Guid id, DateTimeOffset at) =>
        DbAsync(db => db.Tickets.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClosedAt, (DateTimeOffset?)at), Ct));

    public async Task SetPlatformSettingAsync(string key, string? value)
    {
        if (value is null)
        {
            await O.ClearSettingAsync(key);
        }
        else
        {
            await O.SetSettingAsync(key, value);
        }
    }

    /// <summary>A placed order (one unit, Head Office) of a fresh customer: the order number and the mobile.</summary>
    public async Task<(string Number, string Mobile, Guid OrderId)> OrderAsync(string admin)
    {
        var category = await H.CategoryAsync(admin);
        var product = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Support Tee"), 900m, [("Blue", "M", 5)]);
        var mobile = StoreHelper.NewMobile();
        var placed = await O.O.PlaceAsync(product.Variants[0].Id, 1, mobile);
        return (placed.GetProperty("number").GetString()!, mobile, placed.GetProperty("orderId").GetGuid());
    }
}
