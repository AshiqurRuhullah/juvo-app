using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Integrations;
using Juvo.Domain.Integrations;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Staff management of the public API (FR-AF05): API keys (<c>integrations.apikey.*</c>) and outgoing webhooks (<c>integrations.webhook.*</c>). Both are platform-wide, not branch scoped,
/// and Super Admin only by default. Secrets are returned once, when they are created or rotated, and never again.
/// </summary>
internal static class IntegrationsEndpoints
{
    public static WebApplication MapIntegrationsEndpoints(this WebApplication app)
    {
        var integrations = app.MapApiV1().MapGroup("/integrations");

        var keys = integrations.MapGroup("/api-keys").WithTags("API keys");
        keys.MapGet("", async ([AsParameters] ListParams list, bool? enabled, ApiKeyService s, CancellationToken ct) => (await s.ListAsync(list.ToRequest(), enabled, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.ApiKeyView).Produces<PagedResult<ApiKeyDto>>()
            .WithSummary("API keys, newest first: name, visible prefix, scopes, branch limit, state, expiry, last use. The secret is never listed. Filter: enabled. Search: name or prefix.");

        keys.MapGet("/scopes", () => Results.Ok(ApiKeyService.Scopes()))
            .RequirePermission(Permissions.Integrations.ApiKeyView).Produces<IReadOnlyList<ScopeInfo>>().WithSummary("The fixed list of scopes a key can hold. pii:read is the one that shows unmasked customer details.");

        keys.MapGet("/{id:guid}", async (Guid id, ApiKeyService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.ApiKeyView).Produces<ApiKeyDto>().WithSummary("One API key.");

        keys.MapPost("", async (ApiKeyRequest r, ApiKeyService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttpResult(created => Results.Created($"/api/v1/integrations/api-keys/{created.Key.Id}", created)))
            .RequirePermission(Permissions.Integrations.ApiKeyManage).Produces<ApiKeyCreatedDto>(StatusCodes.Status201Created)
            .WithSummary("Create a key. The secret is in this response only (in the field secret); copy it now, it cannot be shown again. Audited without the secret.");

        keys.MapPut("/{id:guid}", async (Guid id, ApiKeyRequest r, ApiKeyService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.ApiKeyManage).Produces<ApiKeyDto>().WithSummary("Change name, scopes, branch limit, expiry or rate limit of a key. The secret does not change.");

        keys.MapPost("/{id:guid}/rotate", async (Guid id, ApiKeyService s, CancellationToken ct) => (await s.RotateAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.ApiKeyManage).Produces<ApiKeyCreatedDto>()
            .WithSummary("Replace the secret. The old secret stops working at once; the new one is in this response only.");

        keys.MapPost("/{id:guid}/disable", async (Guid id, ApiKeyService s, CancellationToken ct) => (await s.SetEnabledAsync(id, false, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.ApiKeyManage).Produces<ApiKeyDto>().WithSummary("Disable a key. Calls with it answer 401 PUBLICAPI.KEY_DISABLED.");

        keys.MapPost("/{id:guid}/enable", async (Guid id, ApiKeyService s, CancellationToken ct) => (await s.SetEnabledAsync(id, true, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.ApiKeyManage).Produces<ApiKeyDto>().WithSummary("Enable a key again.");

        keys.MapDelete("/{id:guid}", async (Guid id, ApiKeyService s, CancellationToken ct) => (await s.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.ApiKeyManage).WithSummary("Delete a key for good. Calls with it answer 401 PUBLICAPI.INVALID_KEY.");

        var hooks = integrations.MapGroup("/webhooks").WithTags("Webhooks");
        hooks.MapGet("", async ([AsParameters] ListParams list, WebhookService s, CancellationToken ct) => (await s.ListAsync(list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookView).Produces<PagedResult<WebhookEndpointDto>>()
            .WithSummary("Webhook endpoints with their events, state and the number of failed deliveries. The secret is never listed. Search: name or address.");

        hooks.MapGet("/events", () => Results.Ok(WebhookService.EventTypes()))
            .RequirePermission(Permissions.Integrations.WebhookView).Produces<IReadOnlyList<string>>().WithSummary("The event types a webhook can subscribe to.");

        hooks.MapGet("/{id:guid}", async (Guid id, WebhookService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookView).Produces<WebhookEndpointDto>().WithSummary("One webhook endpoint.");

        hooks.MapPost("", async (WebhookEndpointRequest r, WebhookService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttpResult(created => Results.Created($"/api/v1/integrations/webhooks/{created.Endpoint.Id}", created)))
            .RequirePermission(Permissions.Integrations.WebhookManage).Produces<WebhookEndpointCreatedDto>(StatusCodes.Status201Created)
            .WithSummary("Add an endpoint. https only; addresses that lead to private, loopback, link-local or metadata networks are refused (422/400 INTEGRATIONS.URL_REFUSED). The signing secret is in this response only.");

        hooks.MapPut("/{id:guid}", async (Guid id, WebhookEndpointRequest r, WebhookService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookManage).Produces<WebhookEndpointDto>().WithSummary("Change name, address, events or state. The address is checked again.");

        hooks.MapPost("/{id:guid}/enable", async (Guid id, WebhookService s, CancellationToken ct) => (await s.SetEnabledAsync(id, true, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookManage).Produces<WebhookEndpointDto>().WithSummary("Enable an endpoint.");

        hooks.MapPost("/{id:guid}/disable", async (Guid id, WebhookService s, CancellationToken ct) => (await s.SetEnabledAsync(id, false, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookManage).Produces<WebhookEndpointDto>().WithSummary("Disable an endpoint. No new deliveries are written for it.");

        hooks.MapPost("/{id:guid}/rotate-secret", async (Guid id, WebhookService s, CancellationToken ct) => (await s.RotateSecretAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookManage).Produces<WebhookEndpointCreatedDto>().WithSummary("Replace the signing secret; the new one is in this response only.");

        hooks.MapPost("/{id:guid}/test", async (Guid id, WebhookService s, CancellationToken ct) => (await s.PingAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookManage).Produces<WebhookDeliveryDto>()
            .WithSummary("Send a signed ping event now (one try, no retry) and return the delivery with its outcome. Works for a disabled endpoint too.");

        hooks.MapDelete("/{id:guid}", async (Guid id, WebhookService s, CancellationToken ct) => (await s.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookManage).WithSummary("Remove an endpoint and its delivery log.");

        hooks.MapGet("/deliveries", async ([AsParameters] ListParams list, Guid? endpointId, WebhookDeliveryStatus? status, string? eventType, DateTimeOffset? from, DateTimeOffset? to, WebhookService s, CancellationToken ct) =>
                (await s.DeliveriesAsync(list.ToRequest(), new WebhookDeliveryQuery(endpointId, status, eventType, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookView).Produces<PagedResult<WebhookDeliveryDto>>()
            .WithSummary("Delivery log, newest first. Filters: endpointId, status (Pending, Succeeded, Failed), eventType, from, to, search (an event or delivery id).");

        hooks.MapGet("/deliveries/{id:guid}", async (Guid id, WebhookService s, CancellationToken ct) => (await s.DeliveryAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookView).Produces<WebhookDeliveryDetailDto>().WithSummary("One delivery with the body that is sent (ids and public fields, no personal data).");

        hooks.MapPost("/deliveries/{id:guid}/redeliver", async (Guid id, WebhookService s, CancellationToken ct) => (await s.RedeliverAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Integrations.WebhookManage).Produces<WebhookDeliveryDto>()
            .WithSummary("Send the same event again (same event id and body); the attempts start from zero. A pending delivery answers 409 INTEGRATIONS.NOT_REDELIVERABLE. Audited.");

        return app;
    }
}
