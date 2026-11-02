using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Domain.Identity;
using Juvo.Shared.Pagination;

namespace Juvo.Api.Endpoints;

/// <summary>The shared list query: <c>?page=1&amp;pageSize=20&amp;sort=name,-createdAt&amp;search=text&amp;filter=field:op:value</c>.</summary>
public sealed record ListParams(int? Page, int? PageSize, string? Sort, string? Search, string? Filter)
{
    public PageRequest ToRequest() => new()
    {
        Page = Page ?? 1,
        PageSize = PageSize ?? PageRequest.DefaultPageSize,
        Sort = Sort,
        Search = Search,
        Filter = Filter,
    };
}

internal static class AuthEndpoints
{
    public static ClientInfo ClientOf(HttpContext context, string? deviceName = null)
    {
        var agent = context.Request.Headers.UserAgent.ToString();
        return new ClientInfo(
            context.Connection.RemoteIpAddress?.ToString(),
            agent.Length > 0 ? agent : null,
            deviceName ?? (context.Request.Headers["X-Device-Name"].ToString() is { Length: > 0 } header ? header : null));
    }

    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapApiV1().MapGroup("/auth").WithTags("Authentication");

        // ---- staff sign-in ----
        auth.MapPost("/login", async (LoginRequest request, AuthService service, HttpContext http, CancellationToken ct) =>
                (await service.LoginAsync(request, ClientOf(http, request.DeviceName), ct)).ToHttpResult())
            .AllowAnonymous().RequireRateLimiting(RateLimitSettings.AuthPolicy)
            .WithSummary("Staff sign-in with email or user name and password (plus a 2FA code when enabled).");

        auth.MapPost("/refresh", async (RefreshRequest request, AuthService service, HttpContext http, CancellationToken ct) =>
                (await service.RefreshAsync(request, ClientOf(http, request.DeviceName), ct)).ToHttpResult())
            .AllowAnonymous()
            .WithSummary("Exchange a refresh token for a new access token and a new refresh token (rotation with reuse detection).");

        auth.MapPost("/logout", async (LogoutRequest request, AuthService service, CancellationToken ct) =>
                (await service.LogoutAsync(request, ct)).ToHttpResult())
            .AllowAnonymous()
            .WithSummary("End this session. Works with the refresh token even when the access token has expired.");

        auth.MapPost("/logout-all", async (AuthService service, CancellationToken ct) => (await service.LogoutAllAsync(ct)).ToHttpResult())
            .RequireAuthorization(Policies.Authenticated)
            .WithSummary("End every session of the signed-in user.");

        auth.MapGet("/me", async (AuthService service, CancellationToken ct) => (await service.GetMeAsync(ct)).ToHttpResult())
            .RequireAuthorization(Policies.Authenticated)
            .WithSummary("Profile, branches, roles per branch and effective permissions of the signed-in user.");

        // ---- password ----
        auth.MapPost("/change-password", async (ChangePasswordRequest request, AuthService service, HttpContext http, CancellationToken ct) =>
                (await service.ChangePasswordAsync(request, ClientOf(http), ct)).ToHttpResult())
            .RequireAuthorization(Policies.StaffLimited)
            .WithSummary("Change the password. All other sessions end; a new session is returned.");

        auth.MapPost("/forgot-password", async (ForgotPasswordRequest request, AuthService service, CancellationToken ct) =>
                (await service.ForgotPasswordAsync(request, ct)).ToHttpResult(() => Results.Accepted()))
            .AllowAnonymous().RequireRateLimiting(RateLimitSettings.AuthPolicy)
            .WithSummary("Email a password-reset link. Always answers 202, so it cannot reveal which emails have accounts.");

        auth.MapPost("/reset-password", async (ResetPasswordRequest request, AuthService service, CancellationToken ct) =>
                (await service.ResetPasswordAsync(request, ct)).ToHttpResult())
            .AllowAnonymous().RequireRateLimiting(RateLimitSettings.AuthPolicy)
            .WithSummary("Set a new password with the emailed token. All sessions end.");

        // ---- two-step verification ----
        var twoFactor = auth.MapGroup("/2fa").RequireAuthorization(Policies.StaffLimited);
        twoFactor.MapPost("/setup", async (AuthService service, CancellationToken ct) => (await service.BeginTwoFactorSetupAsync(ct)).ToHttpResult())
            .WithSummary("Get the authenticator key and otpauth URI to scan.");
        twoFactor.MapPost("/enable", async (EnableTwoFactorRequest request, AuthService service, CancellationToken ct) =>
                (await service.EnableTwoFactorAsync(request, ct)).ToHttpResult())
            .WithSummary("Turn two-step verification on with a code from the app. Returns one-time recovery codes.");
        twoFactor.MapPost("/disable", async (DisableTwoFactorRequest request, AuthService service, CancellationToken ct) =>
                (await service.DisableTwoFactorAsync(request, ct)).ToHttpResult())
            .WithSummary("Turn two-step verification off (not allowed for Super Admin).");
        twoFactor.MapPost("/recovery-codes", async (AuthService service, CancellationToken ct) =>
                (await service.RegenerateRecoveryCodesAsync(ct)).ToHttpResult())
            .WithSummary("Replace the recovery codes.");

        // ---- own sessions ----
        auth.MapGet("/sessions", async (AuthService service, ICurrentUser user, CancellationToken ct) =>
            {
                if (user.UserId is not { } id)
                {
                    return Results.Unauthorized();
                }

                var type = user.SubjectType == "customer" ? SubjectType.Customer : SubjectType.Staff;
                return Results.Ok(await service.ListSessionsAsync(type, id, user.SessionId, ct));
            })
            .RequireAuthorization(Policies.Authenticated)
            .WithSummary("Devices signed in as me.");

        auth.MapDelete("/sessions/{sessionId:guid}", async (Guid sessionId, AuthService service, ICurrentUser user, CancellationToken ct) =>
            {
                if (user.UserId is not { } id)
                {
                    return Results.Unauthorized();
                }

                var type = user.SubjectType == "customer" ? SubjectType.Customer : SubjectType.Staff;
                return (await service.RevokeSessionAsync(type, id, sessionId, ct)).ToHttpResult();
            })
            .RequireAuthorization(Policies.Authenticated)
            .WithSummary("Sign one of my devices out.");

        // ---- customers: mobile number + one-time password ----
        auth.MapPost("/otp/request", async (RequestOtpRequest request, CustomerAuthService service, HttpContext http, CancellationToken ct) =>
                (await service.RequestOtpAsync(request, ClientOf(http), ct)).ToHttpResult(value => Results.Accepted(value: value)))
            .AllowAnonymous().RequireRateLimiting(RateLimitSettings.OtpPolicy)
            .WithSummary("Send a one-time password by SMS to a mobile number.");

        auth.MapPost("/otp/verify", async (VerifyOtpRequest request, CustomerAuthService service, HttpContext http, CancellationToken ct) =>
                (await service.VerifyOtpAsync(request, ClientOf(http, request.DeviceName), ct)).ToHttpResult())
            .AllowAnonymous().RequireRateLimiting(RateLimitSettings.OtpPolicy)
            .WithSummary("Verify the code; creates the quick profile on first use and signs the customer in.");

        return app;
    }
}
