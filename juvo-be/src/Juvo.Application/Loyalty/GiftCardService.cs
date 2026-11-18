using System.Security.Cryptography;
using System.Text;
using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Application.Ums;
using Juvo.Domain.Loyalty;
using Juvo.Shared.Contacts;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Loyalty;

/// <summary>Gift card codes: 16 characters from an alphabet without look-alikes (about 80 bits), written <c>XXXX-XXXX-XXXX-XXXX</c>, stored only as a SHA-256 hash.</summary>
public static class GiftCardCodes
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string NewCode()
    {
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..]}";
    }

    /// <summary>Upper case with every character that is not a letter or digit removed, so spaces and dashes do not matter.</summary>
    public static string Normalize(string? code) => new((code ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Hash(string normalized) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();

    public static string Hint(string normalized) => normalized.Length <= 4 ? normalized : normalized[^4..];
}

/// <summary>
/// Gift cards (FR-AB03). Staff create cards and see them (balance, status, history) but never the code again. A customer moves a card into store credit or pays an order with it.
/// Wrong codes are counted per customer (5 wrong in 15 minutes lock code entry for 15 minutes) and the endpoints are rate limited per address.
/// </summary>
public sealed class GiftCardService(
    IApplicationDbContext db, LoyaltyLedger ledger, LoyaltySettingsService loyalty, NotificationService notifications, ActorContext actors, ICurrentUser currentUser, TimeProvider clock)
{
    public const int FailureLimit = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    private static Error InvalidCode() => Error.NotFound(ErrorCodes.Loyalty.GiftCardInvalid, "This gift card code is not valid.");

    private static string StatusOf(GiftCard c, DateTimeOffset now) => !c.IsActive ? "Deactivated" : c.IsExpired(now) ? "Expired" : c.Balance <= 0 ? "Used" : "Active";

    internal static GiftCardDto ToDto(GiftCard c, DateTimeOffset now) =>
        new(c.Id, c.CodeHint, c.InitialAmount, c.Balance, StatusOf(c, now), c.ExpiresAt, c.Note, c.RecipientMobile, c.CreatedByName, c.CreatedAt, c.RedeemedAt);

    // ---- Finding a card by its code (customer side) --------------------------------------------------------

    /// <summary>
    /// The usable card behind a code. An unknown code is the same neutral "not valid" answer for everybody and counts as a failure for the signed-in customer; a known code that cannot be used
    /// says why (expired, switched off, empty). Nothing changes for a card that is found.
    /// </summary>
    public async Task<Result<GiftCard>> FindUsableAsync(string? code, Guid? customerId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (customerId is { } id)
        {
            var locked = await db.LoyaltyAccounts.AsNoTracking().Where(a => a.CustomerId == id).Select(a => a.CodeLockedUntil).FirstOrDefaultAsync(cancellationToken);
            if (locked is { } until && until > now)
            {
                return Error.Conflict(ErrorCodes.Loyalty.CodeLocked, "Too many wrong codes. Try again in a few minutes.");
            }
        }

        var normalized = GiftCardCodes.Normalize(code);
        var card = normalized.Length < 8 ? null : await db.GiftCards.AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == GiftCardCodes.Hash(normalized), cancellationToken);
        if (card is null)
        {
            if (customerId is { } who)
            {
                await ledger.WithCustomerLockAsync(who, async ct =>
                {
                    var account = await ledger.AccountLockedAsync(who, ct);
                    account.RecordCodeFailure(now, FailureLimit, FailureWindow, LockFor);
                    await db.SaveChangesAsync(ct);
                    return true;
                }, cancellationToken);
            }

            return InvalidCode();
        }

        if (!card.IsActive)
        {
            return Error.Conflict(ErrorCodes.Loyalty.GiftCardInactive, "This gift card has been switched off.");
        }

        if (card.IsExpired(now))
        {
            return Error.Conflict(ErrorCodes.Loyalty.GiftCardExpired, "This gift card has expired.");
        }

        if (card.Balance <= 0)
        {
            return Error.Conflict(ErrorCodes.Loyalty.GiftCardEmpty, "This gift card has no balance left.");
        }

        return card;
    }

    /// <summary>The customer moves the whole balance of a card into store credit. The card and the ledger change in one transaction; a second call with the same code finds an empty card.</summary>
    public async Task<Result<RedeemGiftCardResultDto>> RedeemToCreditAsync(Guid customerId, string? code, CancellationToken cancellationToken)
    {
        if ((await loyalty.RequireEnabledAsync(cancellationToken)).IsFailure)
        {
            return LoyaltySettingsService.FeatureOff();
        }

        var found = await FindUsableAsync(code, customerId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var card = found.Value;
        return await ledger.WithCustomerLockAsync<Result<RedeemGiftCardResultDto>>(customerId, async ct =>
        {
            var amount = card.Balance;
            var key = $"redeem:{card.Id:N}:{customerId:N}";
            var spent = await ledger.SpendGiftCardAsync(card.Id, amount, GiftCardEntryKind.RedeemToCredit, key, customerId, null, null, null, "Moved into store credit", ct);
            if (spent.IsFailure)
            {
                return spent.Error!;
            }

            var credited = await ledger.PostCreditAsync(new CreditPosting(customerId, CreditEntryKind.GiftCardRedeem, amount, $"Gift card …{card.CodeHint}", $"giftcard:{card.Id:N}", GiftCardId: card.Id), ct);
            if (credited.IsFailure)
            {
                throw new InvalidOperationException(credited.Error!.Message); // undo the card spend with the transaction
            }

            var tracked = await db.GiftCards.FirstAsync(c => c.Id == card.Id, ct);
            tracked.MarkRedeemed(customerId, clock.GetUtcNow());
            var account = await ledger.AccountLockedAsync(customerId, ct);
            account.ClearCodeFailures();
            await db.SaveChangesAsync(ct);
            return new RedeemGiftCardResultDto(amount, credited.Value.Balance);
        }, cancellationToken);
    }

    // ---- Staff ---------------------------------------------------------------------------------------------

    private async Task<(Guid? Id, string? Name)> ActorAsync(CancellationToken cancellationToken)
    {
        var actor = await actors.GetAsync(cancellationToken);
        return (actor?.Id, currentUser.UserName ?? "Staff");
    }

    public async Task<Result<GiftCardCreatedDto>> CreateAsync(CreateGiftCardRequest r, CancellationToken cancellationToken)
    {
        var s = await loyalty.ReadAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>();
        if (r.Amount < s.GiftCardMinAmount || r.Amount > s.GiftCardMaxAmount || decimal.Round(r.Amount, 2) != r.Amount)
        {
            errors["amount"] = [$"Use an amount from BDT {s.GiftCardMinAmount:0.##} to BDT {s.GiftCardMaxAmount:0.##} with at most 2 decimals."];
        }

        var now = clock.GetUtcNow();
        var expiry = r.ExpiresAt ?? (s.GiftCardDefaultValidityMonths > 0 ? now.AddMonths(s.GiftCardDefaultValidityMonths) : (DateTimeOffset?)null);
        if (expiry is { } e && e <= now)
        {
            errors["expiresAt"] = ["The expiry date must be in the future."];
        }

        if (r.Note is { Length: > 300 })
        {
            errors["note"] = ["The note can have at most 300 characters."];
        }

        string? mobile = null;
        if (!string.IsNullOrWhiteSpace(r.RecipientMobile))
        {
            mobile = PhoneNumber.Normalize(r.RecipientMobile);
            if (mobile is null)
            {
                errors["recipientMobile"] = ["Enter a valid mobile number."];
            }
        }

        var email = string.IsNullOrWhiteSpace(r.RecipientEmail) ? null : r.RecipientEmail.Trim();
        if (email is not null && !(email.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(email, out var parsed) && parsed.Address == email))
        {
            errors["recipientEmail"] = ["Enter a valid email address."];
        }

        if (r.SendToRecipient && mobile is null && email is null)
        {
            errors["recipientMobile"] = ["Give a mobile number or an email to send the card to."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation(errors);
        }

        var (_, actorName) = await ActorAsync(cancellationToken);
        var code = GiftCardCodes.NewCode();
        var normalized = GiftCardCodes.Normalize(code);
        var card = new GiftCard(GiftCardCodes.Hash(normalized), GiftCardCodes.Hint(normalized), r.Amount, expiry, r.Note, mobile, email, actorName);
        db.GiftCards.Add(card);
        db.GiftCardEntries.Add(new GiftCardEntry(card.Id, GiftCardEntryKind.Issue, r.Amount, r.Amount, $"issue:{card.Id:N}", null, null, null, actorName, "Gift card created", now));
        await db.SaveChangesAsync(cancellationToken);

        if (r.SendToRecipient)
        {
            // The one message that carries the code to its recipient (see docs/loyalty-api.md: the code is in this message and its log row only).
            await notifications.NotifyAsync(new NotifyRequest("loyalty.gift-card-received", mobile ?? string.Empty, email, null,
                NotifyVariables.Of(("Code", code), ("Amount", NotifyVariables.Money(r.Amount)), ("Expiry", expiry?.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "no expiry date")),
                $"giftcard-sent:{card.Id:N}"), cancellationToken);
        }

        return new GiftCardCreatedDto(ToDto(card, now), code);
    }

    public async Task<Result<PagedResult<GiftCardDto>>> ListAsync(PageRequest request, GiftCardListQuery filter, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var query = db.GiftCards.AsNoTracking().AsQueryable();
        switch (filter.Status?.Trim().ToLowerInvariant())
        {
            case "active":
                query = query.Where(c => c.IsActive && c.Balance > 0 && (c.ExpiresAt == null || c.ExpiresAt > now));
                break;
            case "used":
                query = query.Where(c => c.IsActive && c.Balance <= 0);
                break;
            case "expired":
                query = query.Where(c => c.IsActive && c.ExpiresAt != null && c.ExpiresAt <= now);
                break;
            case "deactivated":
                query = query.Where(c => !c.IsActive);
                break;
            default:
                break;
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.CreatedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<GiftCardDto>([.. rows.Select(c => ToDto(c, now))], total, page.Page, page.PageSize);
    }

    public async Task<Result<GiftCardDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var card = await db.GiftCards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (card is null)
        {
            return Error.NotFound(ErrorCodes.Loyalty.GiftCardNotFound, "Gift card not found.");
        }

        var entries = await db.GiftCardEntries.AsNoTracking().Where(e => e.GiftCardId == id).OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        return new GiftCardDetailDto(ToDto(card, clock.GetUtcNow()), [.. entries.Select(e => new GiftCardEntryDto(e.Id, e.Kind, e.Amount, e.BalanceAfter, e.OrderNumber, e.ActorName, e.Note, e.At))]);
    }

    /// <summary>Switches a card off: its remaining balance can no longer be used. The history stays. Idempotent.</summary>
    public async Task<Result<GiftCardDto>> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var (_, actorName) = await ActorAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return await ledger.WithCardLockAsync<Result<GiftCardDto>>(id, async ct =>
        {
            var card = await db.GiftCards.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (card is null)
            {
                return Error.NotFound(ErrorCodes.Loyalty.GiftCardNotFound, "Gift card not found.");
            }

            if (card.IsActive)
            {
                card.Deactivate();
                db.GiftCardEntries.Add(new GiftCardEntry(card.Id, GiftCardEntryKind.Deactivate, 0m, card.Balance, $"deactivate:{card.Id:N}", null, null, null, actorName, "Switched off by staff", now));
                await db.SaveChangesAsync(ct);
            }

            return ToDto(card, now);
        }, cancellationToken);
    }
}
