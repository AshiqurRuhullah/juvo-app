using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Juvo.Application.Integrations;
using Microsoft.AspNetCore.DataProtection;

namespace Juvo.Infrastructure.Integrations;

/// <summary>Data-protection backed secret store. The keys live in <c>App_Data/keys</c> (see CLAUDE.md known issue 2: persist them in production).</summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Juvo.Integrations.WebhookSecret.v1");

    public string Protect(string plain) => _protector.Protect(plain);

    public string? Unprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// The production SSRF guard: https only, no credentials in the address, and every address the host resolves to must be a public unicast address
/// (<see cref="SsrfAddressClassifier"/>). It is consulted when an endpoint is saved, before a delivery, and again on the exact address the socket connects to (so a
/// name that is switched to a private address after the check, DNS rebinding, is still refused). There is deliberately no configuration switch to relax it;
/// tests replace the whole service with a subclass.
/// </summary>
internal class WebhookTargetGuard : IWebhookTargetGuard
{
    /// <summary>Test seam only. Production never overrides it.</summary>
    protected virtual bool AllowLoopback => false;

    /// <summary>Test seam only. Production never overrides it.</summary>
    protected virtual bool AllowInsecureHttp => false;

    public async Task<string?> CheckUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "This is not a valid web address.";
        }

        var schemeOk = uri.Scheme == Uri.UriSchemeHttps || AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp;
        if (!schemeOk)
        {
            return "Only https addresses are allowed.";
        }

        if (uri.UserInfo.Length > 0)
        {
            return "The address must not contain a user name or password.";
        }

        var resolution = await ResolveAsync(uri.IdnHost, cancellationToken);
        return resolution.Allowed ? null : resolution.Refusal ?? "The address cannot be reached.";
    }

    public async Task<HostResolution> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        host = host.Trim().Trim('[', ']');
        if (host.Length == 0)
        {
            return new HostResolution([], "The address has no host name.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                return new HostResolution([], "The host name does not resolve.");
            }

            if (addresses.Length == 0)
            {
                return new HostResolution([], "The host name does not resolve.");
            }
        }

        return addresses.Any(a => SsrfAddressClassifier.IsBlocked(a, AllowLoopback))
            ? new HostResolution([], "The address leads to a private, loopback, link-local or otherwise reserved network, which is not allowed.")
            : new HostResolution(addresses, null);
    }
}

internal sealed class WebhookTargetRefusedException(string reason) : Exception(reason);

/// <summary>
/// Sends the POST. Redirects are not followed, system proxies are not used (a proxy would bypass the address check), the whole call is limited to 5 seconds, the response body
/// is never read or kept, and the socket is opened only to an address the guard has just approved.
/// </summary>
internal sealed class WebhookHttpSender : IWebhookSender, IDisposable
{
    private readonly IWebhookTargetGuard _guard;
    private readonly HttpClient _client;

    public WebhookHttpSender(IWebhookTargetGuard guard)
    {
        _guard = guard;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = WebhookDelivering.Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<WebhookSendResult> SendAsync(WebhookSendRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url) { Content = new StringContent(request.Body, Encoding.UTF8, "application/json") };
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("Juvo-Webhooks", "1"));
        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WebhookDelivering.Timeout);
        try
        {
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            return code switch
            {
                >= 200 and < 300 => new WebhookSendResult(code, null),
                >= 300 and < 400 => new WebhookSendResult(code, $"HTTP {code}: redirects are not followed."),
                _ => new WebhookSendResult(code, $"HTTP {code}"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebhookSendResult(null, "No answer within 5 seconds.");
        }
        catch (HttpRequestException ex)
        {
            // Never the exception text: it can contain the address.
            for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
            {
                if (inner is WebhookTargetRefusedException)
                {
                    return new WebhookSendResult(null, "Refused: the address leads to a network that is not allowed.");
                }

                if (inner is System.Security.Authentication.AuthenticationException)
                {
                    return new WebhookSendResult(null, "The secure connection (TLS) could not be set up.");
                }
            }

            return new WebhookSendResult(null, "The connection failed.");
        }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var resolution = await _guard.ResolveAsync(context.DnsEndPoint.Host, cancellationToken);
        if (!resolution.Allowed)
        {
            throw new WebhookTargetRefusedException(resolution.Refusal ?? "refused");
        }

        Exception? last = null;
        foreach (var address in resolution.Addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    public void Dispose() => _client.Dispose();
}
