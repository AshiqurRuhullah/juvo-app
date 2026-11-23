using System.Net;

namespace Juvo.Application.Integrations;

/// <summary>Protects a secret that must be read back (a webhook signing secret) with the platform's data-protection keys.</summary>
public interface ISecretProtector
{
    string Protect(string plain);

    /// <summary>The secret, or null when the stored text cannot be unprotected (keys lost or changed).</summary>
    string? Unprotect(string protectedValue);
}

/// <summary>What the guard decided about a host: the addresses a connection may use, or the reason it is refused.</summary>
public sealed record HostResolution(IReadOnlyList<IPAddress> Addresses, string? Refusal)
{
    public bool Allowed => Refusal is null && Addresses.Count > 0;
}

/// <summary>
/// SSRF protection for webhooks: checks a target address when it is saved and again, on the very address the socket connects to, at delivery time.
/// The production implementation allows https only and refuses private, loopback, link-local and metadata addresses after DNS resolution.
/// </summary>
public interface IWebhookTargetGuard
{
    /// <summary>Checks the whole address (scheme, no credentials, host resolves to allowed addresses). Null when it is acceptable, otherwise the reason.</summary>
    Task<string?> CheckUrlAsync(string url, CancellationToken cancellationToken);

    /// <summary>Resolves a host name or literal address and refuses it when any resulting address is not allowed.</summary>
    Task<HostResolution> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed record WebhookSendRequest(string Url, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>The outcome of one HTTP call. <see cref="StatusCode"/> is null when no response arrived; <see cref="Error"/> is a scrubbed one-line reason (never the URL or the response body).</summary>
public sealed record WebhookSendResult(int? StatusCode, string? Error)
{
    public bool Success => StatusCode is >= 200 and < 300 && Error is null;
}

/// <summary>Sends one POST: 5 second timeout, no redirects, response body not read, connects only to addresses the guard allows.</summary>
public interface IWebhookSender
{
    Task<WebhookSendResult> SendAsync(WebhookSendRequest request, CancellationToken cancellationToken);
}
