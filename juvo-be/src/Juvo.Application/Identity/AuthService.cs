using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Juvo.Application.Abstractions;
using Juvo.Domain.Branches;
using Juvo.Domain.Customers;
using Juvo.Domain.Identity;
using Juvo.Shared.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Identity;

public sealed record LoginRequest(string Identifier, string Password, string? TotpCode, string? RecoveryCode, string? DeviceName);

public sealed record RefreshRequest(string RefreshToken, string? DeviceName);

public sealed record LogoutRequest(string? RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record EnableTwoFactorRequest(string Code);

public sealed record DisableTwoFactorRequest(string Password, string Code);

public sealed record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

public sealed record SessionDto(Guid SessionId, string? DeviceName, string? UserAgent, string? IpAddress,
    DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, bool IsCurrent);

public sealed record BranchSummaryDto(Guid Id, string Code, string NameEn, string? NameBn, bool IsDefault, bool IsActive);

public sealed record RoleAssignmentDto(Guid AssignmentId, Guid? BranchId, string? BranchCode, Guid RoleId, string RoleName);

public sealed record StaffProfileDto(
    Guid Id, string Email, string UserName, string FullName, string? Phone, bool IsGlobalScope, bool IsSuperAdmin,
    bool TwoFactorEnabled, bool MustChangePassword, bool RequiresTwoFactorSetup, DateTimeOffset? LastLoginAt);

public sealed record CustomerProfileDto(Guid Id, string Mobile, string? Email, string? FullName, string ProfileLevel, bool MobileVerified);

/// <summary>The signed-in person: profile, branches, roles per branch and effective permissions per branch.</summary>
public sealed record MeResponse(
    string SubjectType,
    StaffProfileDto? Staff,
    CustomerProfileDto? Customer,
    IReadOnlyList<BranchSummaryDto> Branches,
    IReadOnlyList<RoleAssignmentDto> Assignments,
    bool HasAllPermissions,
    IReadOnlyList<string> GlobalPermissions,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> BranchPermissions,
    Guid? ActiveBranchId,
    bool IsAllBranches,
    string PermissionCatalogVersion);

/// <summary>Staff sign-in, token refresh, sessions, password and two-factor flows.</summary>
public sealed class AuthService(
    IApplicationDbContext db,
    UserManager<StaffUser> users,
    SessionIssuer sessions,
    AccessService access,
    IUserStateCache states,
    IBranchContext branchContext,
    ICurrentUser currentUser,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    IOptions<AuthOptions> options)
{
    private readonly AuthOptions _options = options.Value;

    private static Error InvalidCredentials() =>
        new(ErrorCodes.Auth.InvalidCredentials, "The email, user name or password is incorrect.", ErrorType.Unauthorized);

    public async Task<Result<AuthTokens>> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Identifier) || string.IsNullOrEmpty(request.Password))
        {
            return Error.Validation(new Dictionary<string, string[]>
            {
                ["identifier"] = string.IsNullOrWhiteSpace(request.Identifier) ? ["Email or user name is required."] : [],
                ["password"] = string.IsNullOrEmpty(request.Password) ? ["Password is required."] : [],
            }.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value));
        }

        var identifier = request.Identifier.Trim();
        var user = await users.FindByEmailAsync(identifier) ?? await users.FindByNameAsync(identifier);
        if (user is null)
        {
            await RecordSignInAsync(null, identifier, false, "unknown-user", client, cancellationToken);
            return InvalidCredentials();
        }

        if (await users.IsLockedOutAsync(user))
        {
            await RecordSignInAsync(user.Id, identifier, false, "locked", client, cancellationToken);
            return LockedError(user);
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            await RecordSignInAsync(user.Id, identifier, false, "bad-password", client, cancellationToken);
            return await users.IsLockedOutAsync(user) ? LockedError(user) : InvalidCredentials();
        }

        if (!user.IsActive)
        {
            await RecordSignInAsync(user.Id, identifier, false, "inactive", client, cancellationToken);
            return new Error(ErrorCodes.Auth.AccountDisabled, "This account is disabled. Contact the administrator.", ErrorType.Forbidden);
        }

        var secondFactor = await CheckSecondFactorAsync(user, request, identifier, client, cancellationToken);
        if (secondFactor is not null)
        {
            return secondFactor;
        }

        await users.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = timeProvider.GetUtcNow();
        await users.UpdateAsync(user);

        var flags = await FlagsAsync(user, cancellationToken);
        var tokens = await sessions.StartAsync(SubjectType.Staff, user.Id, user.FullName, user.PermissionVersion,
            flags.Limited, flags.MustChangePassword, flags.RequiresTwoFactorSetup, client, cancellationToken);
        await RecordSignInAsync(user.Id, identifier, true, null, client, cancellationToken);
        return tokens;
    }

    public async Task<Result<AuthTokens>> RefreshAsync(RefreshRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Error.Unauthorized();
        }

        var hash = RefreshTokenValue.Hash(request.RefreshToken.Trim());
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is null)
        {
            return Error.Unauthorized();
        }

        var now = timeProvider.GetUtcNow();
        if (token.IsRevoked || token.IsUsed)
        {
            // An old token came back: somebody copied it. Cut the whole session.
            await sessions.RevokeFamilyAsync(token.FamilyId, "reuse-detected", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new Error(ErrorCodes.Auth.RefreshReused, "This session is no longer valid. Please sign in again.", ErrorType.Unauthorized);
        }

        if (token.IsExpired(now))
        {
            return new Error(ErrorCodes.Auth.TokenExpired, "The session expired. Please sign in again.", ErrorType.Unauthorized);
        }

        var next = client with { DeviceName = request.DeviceName ?? client.DeviceName ?? token.DeviceName };
        AuthTokens tokens;
        if (token.SubjectType == SubjectType.Staff)
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == token.SubjectId, cancellationToken);
            if (user is null || !user.IsActive)
            {
                await sessions.RevokeFamilyAsync(token.FamilyId, "account-disabled", cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return new Error(ErrorCodes.Auth.AccountDisabled, "This account is disabled.", ErrorType.Unauthorized);
            }

            var flags = await FlagsAsync(user, cancellationToken);
            tokens = sessions.Rotate(token, user.FullName, user.PermissionVersion, flags.Limited, flags.MustChangePassword,
                flags.RequiresTwoFactorSetup, next);
        }
        else
        {
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == token.SubjectId, cancellationToken);
            if (customer is null)
            {
                await sessions.RevokeFamilyAsync(token.FamilyId, "account-missing", cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return Error.Unauthorized();
            }

            tokens = sessions.Rotate(token, customer.FullName ?? customer.Mobile, 0, false, false, false, next);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two requests used the same refresh token at once. Only one may win; treat the other as reuse.
            db.RefreshTokens.Local.ToList().ForEach(t => db.RefreshTokens.Entry(t).State = EntityState.Detached);
            await sessions.RevokeFamilyAsync(token.FamilyId, "reuse-detected", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new Error(ErrorCodes.Auth.RefreshReused, "This session is no longer valid. Please sign in again.", ErrorType.Unauthorized);
        }

        return tokens;
    }

    public async Task<Result> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        Guid? family = currentUser.SessionId;
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            var hash = RefreshTokenValue.Hash(request.RefreshToken.Trim());
            family = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(cancellationToken)
                     ?? family;
        }

        if (family is { } id)
        {
            await sessions.RevokeFamilyAsync(id, "logout", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result> LogoutAllAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized();
        }

        var subject = currentUser.SubjectType == "customer" ? SubjectType.Customer : SubjectType.Staff;
        await sessions.RevokeAllAsync(subject, userId, "logout-all", cancellationToken);
        if (subject == SubjectType.Staff && await users.FindByIdAsync(userId.ToString()) is { } staff)
        {
            staff.BumpPermissionVersion(); // access tokens already issued stop working at once, not only the refresh tokens
            await users.UpdateAsync(staff);
            states.Invalidate(userId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<MeResponse>> GetMeAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } id)
        {
            return Error.Unauthorized();
        }

        if (currentUser.SubjectType == "customer")
        {
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (customer is null)
            {
                return Error.Unauthorized();
            }

            return new MeResponse("customer", null,
                new CustomerProfileDto(customer.Id, customer.Mobile, customer.Email, customer.FullName,
                    customer.ProfileLevel.ToString(), customer.MobileVerified),
                [], [], false, [], new Dictionary<Guid, IReadOnlyList<string>>(), null, false, Shared.Security.PermissionCatalog.Version);
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return Error.Unauthorized();
        }

        var userAccess = await access.GetAsync(user.Id, user.PermissionVersion, cancellationToken);
        var flags = await FlagsAsync(user, cancellationToken);

        var assignments = await (
                from ubr in db.UserBranchRoles.AsNoTracking()
                where ubr.UserId == id
                join role in db.Roles.AsNoTracking() on ubr.RoleId equals role.Id
                join branch in db.Branches.AsNoTracking() on ubr.BranchId equals branch.Id into branches
                from branch in branches.DefaultIfEmpty()
                select new RoleAssignmentDto(ubr.Id, ubr.BranchId, branch == null ? null : branch.Code, role.Id, role.Name))
            .ToListAsync(cancellationToken);

        var branchQuery = db.Branches.AsNoTracking().Where(b => b.IsActive);
        if (!user.IsGlobalScope)
        {
            var assigned = userAccess.AssignedBranchIds.ToList();
            branchQuery = branchQuery.Where(b => assigned.Contains(b.Id));
        }

        var visibleBranches = await branchQuery.OrderBy(b => b.Code)
            .Select(b => new BranchSummaryDto(b.Id, b.Code, b.NameEn, b.NameBn, b.IsDefault, b.IsActive))
            .ToListAsync(cancellationToken);

        var profile = new StaffProfileDto(user.Id, user.Email ?? string.Empty, user.UserName ?? string.Empty, user.FullName,
            user.PhoneNumber, user.IsGlobalScope, userAccess.HasAllPermissions || await IsSuperAdminAsync(user.Id, cancellationToken),
            user.TwoFactorEnabled, user.MustChangePassword, flags.RequiresTwoFactorSetup, user.LastLoginAt);

        return new MeResponse("staff", profile, null, visibleBranches, assignments, userAccess.HasAllPermissions,
            userAccess.GlobalPermissions.Order(StringComparer.Ordinal).ToList(),
            userAccess.BranchPermissions.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Order(StringComparer.Ordinal).ToList()),
            branchContext.ActiveBranchId, branchContext.IsAllBranches, Shared.Security.PermissionCatalog.Version);
    }

    public async Task<Result<AuthTokens>> ChangePasswordAsync(ChangePasswordRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } id || currentUser.SubjectType != "staff")
        {
            return Error.Unauthorized();
        }

        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Error.Unauthorized();
        }

        if (string.IsNullOrEmpty(request.NewPassword))
        {
            return Error.Validation(new Dictionary<string, string[]> { ["newPassword"] = ["New password is required."] });
        }

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword ?? string.Empty, request.NewPassword);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch))
                ? InvalidCredentials()
                : WeakPassword(result);
        }

        return await FinishPasswordChangeAsync(user, client, cancellationToken);
    }

    public async Task<Result> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        // Always succeeds, so the endpoint cannot be used to find out which emails have accounts.
        var email = request.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            return Result.Success();
        }

        var user = await users.FindByEmailAsync(email);
        if (user is { IsActive: true })
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var link = $"{_options.FrontendBaseUrl.TrimEnd('/')}/reset-password?email={UrlEncoder.Default.Encode(email)}&token={UrlEncoder.Default.Encode(token)}";
            await emailSender.SendAsync(user.Email!, "auth.password-reset", "Reset your Juvo BD password",
                $"Hello {user.FullName},\n\nUse this link to choose a new password (valid for {_options.PasswordResetTokenMinutes} minutes):\n{link}\n\nIf you did not ask for this, ignore this message.",
                cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var user = string.IsNullOrWhiteSpace(request.Email) ? null : await users.FindByEmailAsync(request.Email.Trim());
        var invalid = new Error(ErrorCodes.Auth.ResetTokenInvalid, "The reset link is invalid or has expired.", ErrorType.Validation);
        if (user is null || string.IsNullOrWhiteSpace(request.Token))
        {
            return invalid;
        }

        var result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword ?? string.Empty);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)) ? invalid : WeakPassword(result);
        }

        user.MustChangePassword = false;
        user.BumpPermissionVersion();
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await users.UpdateAsync(user);
        await sessions.RevokeAllAsync(SubjectType.Staff, user.Id, "password-reset", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        states.Invalidate(user.Id);
        return Result.Success();
    }

    public async Task<Result<TwoFactorSetupResponse>> BeginTwoFactorSetupAsync(CancellationToken cancellationToken)
    {
        var user = await CurrentStaffAsync();
        if (user is null)
        {
            return Error.Unauthorized();
        }

        if (user.TwoFactorEnabled)
        {
            return Error.Conflict(ErrorCodes.General.Conflict, "Two-step verification is already on.");
        }

        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }

        var issuer = UrlEncoder.Default.Encode("Juvo BD");
        var account = UrlEncoder.Default.Encode(user.Email ?? user.UserName ?? user.Id.ToString());
        var uri = string.Create(CultureInfo.InvariantCulture,
            $"otpauth://totp/{issuer}:{account}?secret={key}&issuer={issuer}&digits=6");
        return new TwoFactorSetupResponse(FormatKey(key!), uri);
    }

    public async Task<Result<RecoveryCodesResponse>> EnableTwoFactorAsync(EnableTwoFactorRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentStaffAsync();
        if (user is null)
        {
            return Error.Unauthorized();
        }

        var code = (request.Code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            return Error.Validation(new Dictionary<string, string[]> { ["code"] = ["The code is not correct. Check the time on your phone and try again."] });
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        user.BumpPermissionVersion(); // the "setup required" flag in tokens changes
        await users.UpdateAsync(user);
        states.Invalidate(user.Id);
        return new RecoveryCodesResponse(codes?.ToList() ?? []);
    }

    public async Task<Result> DisableTwoFactorAsync(DisableTwoFactorRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentStaffAsync();
        if (user is null)
        {
            return Error.Unauthorized();
        }

        if (_options.RequireTwoFactorForSuperAdmin && await IsSuperAdminAsync(user.Id, cancellationToken))
        {
            return Error.Forbidden(ErrorCodes.Auth.TwoFactorSetupRequired, "Two-step verification is mandatory for Super Admin.");
        }

        if (!await users.CheckPasswordAsync(user, request.Password ?? string.Empty))
        {
            return InvalidCredentials();
        }

        var code = (request.Code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            return Error.Validation(new Dictionary<string, string[]> { ["code"] = ["The code is not correct."] });
        }

        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        return Result.Success();
    }

    public async Task<Result<RecoveryCodesResponse>> RegenerateRecoveryCodesAsync(CancellationToken cancellationToken)
    {
        var user = await CurrentStaffAsync();
        if (user is null || !user.TwoFactorEnabled)
        {
            return Error.Conflict(ErrorCodes.General.Conflict, "Turn on two-step verification first.");
        }

        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        return new RecoveryCodesResponse(codes?.ToList() ?? []);
    }

    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(SubjectType subject, Guid subjectId, Guid? currentSession, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var rows = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.SubjectType == subject && t.SubjectId == subjectId && t.RevokedAt == null && t.UsedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.IssuedAt)
            .ToListAsync(cancellationToken);

        // The first token of each family tells when the session started.
        var familyIds = rows.Select(r => r.FamilyId).ToList();
        var starts = await db.RefreshTokens.AsNoTracking().Where(t => familyIds.Contains(t.FamilyId))
            .GroupBy(t => t.FamilyId).Select(g => new { Family = g.Key, Start = g.Min(t => t.IssuedAt) })
            .ToDictionaryAsync(x => x.Family, x => x.Start, cancellationToken);

        return rows.Select(t => new SessionDto(t.FamilyId, t.DeviceName, t.UserAgent, t.CreatedByIp,
            starts.GetValueOrDefault(t.FamilyId, t.IssuedAt), t.ExpiresAt, t.FamilyId == currentSession)).ToList();
    }

    public async Task<Result> RevokeSessionAsync(SubjectType subject, Guid subjectId, Guid sessionId, CancellationToken cancellationToken)
    {
        var owns = await db.RefreshTokens.AnyAsync(t => t.FamilyId == sessionId && t.SubjectType == subject && t.SubjectId == subjectId, cancellationToken);
        if (!owns)
        {
            return Error.NotFound(ErrorCodes.Ums.SessionNotFound, "Session not found.");
        }

        await sessions.RevokeFamilyAsync(sessionId, "revoked", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ---- shared helpers -----------------------------------------------------------------------------

    private async Task<Result<AuthTokens>> FinishPasswordChangeAsync(StaffUser user, ClientInfo client, CancellationToken cancellationToken)
    {
        user.MustChangePassword = false;
        user.BumpPermissionVersion(); // old access tokens (including a limited one) stop working
        await users.UpdateAsync(user);
        await sessions.RevokeAllAsync(SubjectType.Staff, user.Id, "password-changed", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        states.Invalidate(user.Id);

        var flags = await FlagsAsync(user, cancellationToken);
        return await sessions.StartAsync(SubjectType.Staff, user.Id, user.FullName, user.PermissionVersion,
            flags.Limited, flags.MustChangePassword, flags.RequiresTwoFactorSetup, client, cancellationToken);
    }

    private async Task<StaffUser?> CurrentStaffAsync() =>
        currentUser.UserId is { } id && currentUser.SubjectType == "staff" ? await users.FindByIdAsync(id.ToString()) : null;

    private async Task<Error?> CheckSecondFactorAsync(StaffUser user, LoginRequest request, string identifier, ClientInfo client, CancellationToken cancellationToken)
    {
        if (!user.TwoFactorEnabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.TotpCode) && string.IsNullOrWhiteSpace(request.RecoveryCode))
        {
            return new Error(ErrorCodes.Auth.TwoFactorRequired, "Enter the code from your authenticator app.", ErrorType.Unauthorized);
        }

        var ok = false;
        if (!string.IsNullOrWhiteSpace(request.TotpCode))
        {
            var code = request.TotpCode.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
            ok = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
        }
        else if (!string.IsNullOrWhiteSpace(request.RecoveryCode))
        {
            ok = (await users.RedeemTwoFactorRecoveryCodeAsync(user, request.RecoveryCode.Trim())).Succeeded;
        }

        if (ok)
        {
            return null;
        }

        await users.AccessFailedAsync(user);
        await RecordSignInAsync(user.Id, identifier, false, "bad-second-factor", client, cancellationToken);
        return await users.IsLockedOutAsync(user) ? LockedError(user) : InvalidCredentials();
    }

    private async Task<(bool Limited, bool MustChangePassword, bool RequiresTwoFactorSetup)> FlagsAsync(StaffUser user, CancellationToken cancellationToken)
    {
        var requiresSetup = _options.RequireTwoFactorForSuperAdmin && !user.TwoFactorEnabled
                            && await IsSuperAdminAsync(user.Id, cancellationToken);
        return (user.MustChangePassword || requiresSetup, user.MustChangePassword, requiresSetup);
    }

    private Task<bool> IsSuperAdminAsync(Guid userId, CancellationToken cancellationToken) =>
        (from ubr in db.UserBranchRoles.AsNoTracking()
         where ubr.UserId == userId
         join role in db.Roles.AsNoTracking() on ubr.RoleId equals role.Id
         where role.IsSuperAdmin
         select ubr.Id).AnyAsync(cancellationToken);

    private async Task RecordSignInAsync(Guid? userId, string identifier, bool ok, string? reason, ClientInfo client, CancellationToken cancellationToken)
    {
        db.SignInHistory.Add(new SignInHistory(SubjectType.Staff, userId, identifier, ok, reason, client.IpAddress, client.UserAgent,
            timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
    }

    private Error LockedError(StaffUser user)
    {
        var until = user.LockoutEnd;
        var minutes = until is null ? _options.LockoutMinutes : Math.Max(1, (int)Math.Ceiling((until.Value - timeProvider.GetUtcNow()).TotalMinutes));
        return new Error(ErrorCodes.Auth.Locked,
            string.Create(CultureInfo.InvariantCulture, $"Too many failed attempts. Try again in {minutes} minute(s)."), ErrorType.Locked);
    }

    private static Error WeakPassword(IdentityResult result) =>
        new(ErrorCodes.Auth.WeakPassword, "The password does not meet the requirements.", ErrorType.Validation,
            new Dictionary<string, string[]> { ["password"] = [.. result.Errors.Select(e => e.Description)] });

    private static string FormatKey(string key)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            builder.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }

        return builder.ToString().Trim().ToLowerInvariant();
    }
}
