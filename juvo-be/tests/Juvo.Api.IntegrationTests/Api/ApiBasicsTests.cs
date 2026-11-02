using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;

namespace Juvo.Api.IntegrationTests.Api;

public class ApiBasicsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client() => factory.CreateClient();

    [Fact]
    public async Task Liveness_is_ok_without_touching_the_database()
    {
        var response = await Client().GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Healthy", json.GetProperty("status").GetString());
        Assert.Equal(0, json.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task Readiness_checks_the_database()
    {
        var response = await Client().GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Healthy", json.GetProperty("status").GetString());
        Assert.Equal("database", json.GetProperty("checks")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Ping_runs_the_application_pipeline()
    {
        var json = await Client().GetFromJsonAsync<JsonElement>("/api/v1/system/ping?name=Juvo", Ct);

        Assert.Equal("Hello, Juvo", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Validation_errors_are_problem_details_with_a_stable_code()
    {
        var response = await Client().GetAsync("/api/v1/system/ping?name=" + new string('x', 51), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("GENERAL.VALIDATION", json.GetProperty("code").GetString());
        Assert.Equal(400, json.GetProperty("status").GetInt32());
        Assert.True(json.GetProperty("errors").TryGetProperty("Name", out _));
        Assert.False(string.IsNullOrEmpty(json.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details_not_found()
    {
        var response = await Client().GetAsync("/api/v1/does-not-exist", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("GENERAL.NOT_FOUND", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Valid_correlation_id_is_echoed_and_invalid_one_is_replaced()
    {
        var client = Client();

        var echoed = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/health/live")
        {
            Headers = { { "X-Correlation-Id", "test-correlation-1" } },
        }, Ct);
        Assert.Equal("test-correlation-1", echoed.Headers.GetValues("X-Correlation-Id").Single());

        var replaced = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/health/live")
        {
            Headers = { { "X-Correlation-Id", "bad id with spaces!" } },
        }, Ct);
        var id = replaced.Headers.GetValues("X-Correlation-Id").Single();
        Assert.NotEqual("bad id with spaces!", id);
        Assert.Matches("^[a-f0-9]{32}$", id);
    }

    [Fact]
    public async Task Security_headers_are_present()
    {
        var response = await Client().GetAsync("/api/v1/system/info", Ct);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Openapi_document_lists_the_v1_endpoints()
    {
        var json = await Client().GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);

        var paths = json.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/system/ping", out _));
        Assert.True(paths.TryGetProperty("/api/v1/system/info", out _));
    }

    [Fact]
    public async Task Interactive_documentation_page_loads()
    {
        var response = await Client().GetAsync("/scalar/v1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cors_allows_the_front_end_origin_only()
    {
        var client = Client();

        var allowed = await Preflight(client, "http://localhost:3000");
        Assert.Equal("http://localhost:3000", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        var denied = await Preflight(client, "http://evil.example");
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Static_upload_route_serves_nothing_for_missing_files()
    {
        var response = await Client().GetAsync("/files/nope.png", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Preflight(HttpClient client, string origin) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/v1/system/ping")
        {
            Headers =
            {
                { "Origin", origin },
                { "Access-Control-Request-Method", "GET" },
                { "Access-Control-Request-Headers", "x-branch-id" },
            },
        }, Ct);
}
