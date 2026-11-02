namespace Juvo.Api.Http;

/// <summary>Adds conservative security headers to every response (OWASP secure headers baseline).</summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Resource-Policy"] = "same-site";

            // The API returns JSON only. The documentation UI needs scripts, so it is the one exception.
            var path = context.Request.Path;
            if (!path.StartsWithSegments("/scalar") && !path.StartsWithSegments("/openapi"))
            {
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
