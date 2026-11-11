using System.Globalization;
using Juvo.Application.Abstractions;
using Juvo.Domain.Orders;
using Juvo.Shared.Contacts;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

public sealed record FraudRuleDto(string Code, string Description, bool Enabled, decimal Threshold, string ThresholdMeaning, RuleAction Action, int Score);

public sealed record UpdateFraudRuleRequest(bool Enabled, decimal Threshold, RuleAction Action);

public sealed record BlacklistDto(Guid Id, BlacklistKind Kind, string Value, string? Note, DateTimeOffset CreatedAt, string? CreatedByName);

public sealed record AddBlacklistRequest(BlacklistKind Kind, string Value, string? Note);

public sealed record CustomerOrderStatsDto(int TotalOrders, int Delivered, int Cancelled, int ReturnedOrFailed, int Open, decimal TotalSpent, DateTimeOffset? FirstOrderAt, int Score);

public sealed record RiskFlagDto(string RuleCode, string Message, int Score, RuleAction Action, DateTimeOffset At);

public sealed record FraudLogDto(FraudAction Action, FraudState FromState, FraudState ToState, string? Note, string? ActorName, DateTimeOffset At);

public sealed record ReviewItemDto(
    Guid OrderId, string Number, Guid BranchId, FraudState State, int RiskScore, string CustomerMobile, string? CustomerName, decimal GrandTotal, string PaymentMethod, OrderStatus OrderStatus,
    DateTimeOffset PlacedAt, IReadOnlyList<RiskFlagDto> Flags, CustomerOrderStatsDto CustomerStats);

public sealed record FraudDecisionRequest(FraudAction Action, string? Note);

/// <summary>What the rules said about an order that is about to be placed.</summary>
public sealed record FraudVerdict(string? BlockedBecause, IReadOnlyList<(string Code, string Message, int Score, RuleAction Action)> Findings)
{
    public bool IsBlocked => BlockedBecause is not null;
}

/// <summary>What the rules look at.</summary>
public sealed record FraudContext(
    Guid CustomerId, string Mobile, string? Email, string? DeviceId, string? Ip, decimal Total, bool IsCod, string ShipPhone, string ShipAddressLine, bool IsStaffOrder);

/// <summary>
/// Fraud basics (module T): configurable rules, blacklist, customer history and the review queue. Rules, thresholds and actions live in the settings
/// table with safe defaults (docs/assumptions.md section M). A blacklist match, or a rule whose action is Reject, stops the checkout with a
/// neutral message (FR-T03); Hold and RequireAdvance hold the order from confirmation until a reviewer decides (FR-T04, FR-T05); every decision is
/// logged with the staff name and note (FR-T06).
/// </summary>
public sealed class FraudService(IApplicationDbContext db, SettingsService settings, OrderAccess access, OrderWorkflow workflow, TimeProvider clock)
{
    public const string BlacklistCode = "BLACKLIST";

    private sealed record RuleDefinition(string Code, string Description, bool Enabled, decimal Threshold, string ThresholdMeaning, RuleAction Action, int Score);

    private static readonly RuleDefinition[] Rules =
    [
        new("HIGH_COD_NEW_CUSTOMER", "Cash on delivery for a high amount from a customer with no delivered order yet.", true, 5000m, "Order total in BDT at or above which the rule applies.", RuleAction.Hold, 30),
        new("FIRST_TIME_COD", "First cash on delivery order of a customer.", true, 1m, "Not used (always the first order).", RuleAction.Flag, 10),
        new("HIGH_VALUE", "Very high order total, any payment method.", true, 20000m, "Order total in BDT at or above which the rule applies.", RuleAction.Flag, 15),
        new("DEVICE_VELOCITY", "Many orders from one device or network address with different mobile numbers within 24 hours.", true, 3m, "Number of different mobile numbers (including this order) that triggers the rule.", RuleAction.Hold, 40),
        new("PAST_FAILED_COD", "The customer has refused or returned earlier cash on delivery orders.", true, 2m, "Number of returned or failed orders that triggers the rule.", RuleAction.Hold, 40),
        new("SHORT_ADDRESS", "The delivery address is very short.", true, 10m, "Minimum number of characters of the address line.", RuleAction.Flag, 15),
        new("MISMATCHED_DETAILS", "The same address was used with other mobile numbers within 30 days.", true, 1m, "Number of other mobile numbers that triggers the rule.", RuleAction.Flag, 20),
    ];

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static string NormalizeText(string? text) =>
        string.Join(' ', (text ?? string.Empty).Trim().ToLowerInvariant().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    // ---- Rules configuration ----------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<FraudRuleDto>>> ListRulesAsync(CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is null)
        {
            return Error.Unauthorized();
        }

        var keys = Rules.SelectMany(r => new[] { SettingKeys.RuleEnabled(r.Code), SettingKeys.RuleThreshold(r.Code), SettingKeys.RuleAction(r.Code) }).ToList();
        var values = await settings.ReadAsync(keys, cancellationToken);
        return Result.Success<IReadOnlyList<FraudRuleDto>>([.. Rules.Select(r => Configured(r, values))]);
    }

    private static FraudRuleDto Configured(RuleDefinition r, Dictionary<string, string> values) =>
        new(r.Code, r.Description,
            !values.TryGetValue(SettingKeys.RuleEnabled(r.Code), out var enabled) || !bool.TryParse(enabled, out var on) ? r.Enabled : on,
            SettingsService.Number(values, SettingKeys.RuleThreshold(r.Code), r.Threshold, 0m), r.ThresholdMeaning,
            SettingsService.EnumValue(values, SettingKeys.RuleAction(r.Code), r.Action), r.Score);

    public async Task<Result<FraudRuleDto>> UpdateRuleAsync(string code, UpdateFraudRuleRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is null)
        {
            return Error.Unauthorized();
        }

        var rule = Rules.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            return Error.NotFound(ErrorCodes.Fraud.RuleNotFound, "Rule not found.");
        }

        if (request.Threshold < 0 || request.Threshold > 100_000_000 || !Enum.IsDefined(request.Action))
        {
            return Invalid("threshold", "The threshold must be 0 or more and the action one of Flag, Hold, RequireAdvance, Reject.");
        }

        await settings.SetAsync(SettingKeys.RuleEnabled(rule.Code), request.Enabled ? "true" : "false", cancellationToken);
        await settings.SetAsync(SettingKeys.RuleThreshold(rule.Code), request.Threshold.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await settings.SetAsync(SettingKeys.RuleAction(rule.Code), request.Action.ToString(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var values = await settings.ReadAsync([SettingKeys.RuleEnabled(rule.Code), SettingKeys.RuleThreshold(rule.Code), SettingKeys.RuleAction(rule.Code)], cancellationToken);
        return Configured(rule, values);
    }

    // ---- Blacklist --------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<BlacklistDto>>> ListBlacklistAsync(PageRequest request, BlacklistKind? kind, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is null)
        {
            return Error.Unauthorized();
        }

        var query = from b in db.BlacklistEntries.AsNoTracking()
                    join u in db.Users.AsNoTracking() on b.CreatedBy equals (Guid?)u.Id into users
                    from u in users.DefaultIfEmpty()
                    select new { Entry = b, Name = u == null ? null : u.FullName };
        if (kind is { } k)
        {
            query = query.Where(x => x.Entry.Kind == k);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = NormalizeText(request.Search);
            query = query.Where(x => x.Entry.Value.Contains(term) || (x.Entry.Note != null && x.Entry.Note.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.Entry.CreatedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<BlacklistDto>([.. rows.Select(x => new BlacklistDto(x.Entry.Id, x.Entry.Kind, x.Entry.Value, x.Entry.Note, x.Entry.CreatedAt, x.Name))], total, page.Page, page.PageSize);
    }

    public async Task<Result<BlacklistDto>> AddBlacklistAsync(AddBlacklistRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var value = request.Kind switch
        {
            BlacklistKind.Mobile => PhoneNumber.Normalize(request.Value),
            BlacklistKind.Email => NormalizeText(request.Value) is { Length: > 0 } e && e.Contains('@', StringComparison.Ordinal) ? e : null,
            BlacklistKind.Address => NormalizeText(request.Value) is { Length: >= 6 } a ? a : null,
            BlacklistKind.Device => request.Value?.Trim() is { Length: >= 6 } d ? d : null,
            _ => null,
        };
        if (!Enum.IsDefined(request.Kind) || value is null || value.Length > 300)
        {
            return Invalid("value", request.Kind switch
            {
                BlacklistKind.Mobile => "Enter a valid mobile number.",
                BlacklistKind.Email => "Enter a valid email address.",
                BlacklistKind.Address => "Enter at least 6 characters of the address text.",
                _ => "Enter the value to block.",
            });
        }

        if (await db.BlacklistEntries.AnyAsync(b => b.Kind == request.Kind && b.Value == value, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Fraud.BlacklistExists, "This entry is already on the blacklist.");
        }

        var entry = new BlacklistEntry(request.Kind, value, request.Note);
        db.BlacklistEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return new BlacklistDto(entry.Id, entry.Kind, entry.Value, entry.Note, entry.CreatedAt, access.WhoIs(actor).Name);
    }

    public async Task<Result> RemoveBlacklistAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is null)
        {
            return Error.Unauthorized();
        }

        var entry = await db.BlacklistEntries.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (entry is null)
        {
            return Error.NotFound(ErrorCodes.Fraud.BlacklistNotFound, "Blacklist entry not found.");
        }

        db.BlacklistEntries.Remove(entry); // soft delete: the audit interceptor keeps who removed it and when
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ---- Customer history -------------------------------------------------------------------------------

    public async Task<CustomerOrderStatsDto> StatsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var rows = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.CustomerId == customerId)
            .Select(o => new { o.Status, o.GrandTotal, o.PlacedAt }).ToListAsync(cancellationToken);
        return StatsOf(rows.Select(r => (r.Status, r.GrandTotal, r.PlacedAt)).ToList());
    }

    private static CustomerOrderStatsDto StatsOf(List<(OrderStatus Status, decimal Total, DateTimeOffset At)> rows)
    {
        var delivered = rows.Count(r => r.Status is OrderStatus.Delivered or OrderStatus.ReturnInProgress);
        var bad = rows.Count(r => r.Status is OrderStatus.Returned or OrderStatus.DeliveryFailed);
        var cancelled = rows.Count(r => r.Status == OrderStatus.Cancelled);
        var open = rows.Count - delivered - bad - cancelled;
        // Score 0-100: starts neutral at 50, rises with delivered orders, falls with returns, failures and cancellations.
        var score = Math.Clamp(50 + (delivered * 10) - (bad * 25) - (cancelled * 3), 0, 100);
        return new CustomerOrderStatsDto(rows.Count, delivered, cancelled, bad, open, rows.Where(r => r.Status == OrderStatus.Delivered).Sum(r => r.Total),
            rows.Count == 0 ? null : rows.Min(r => r.At), score);
    }

    // ---- Evaluation at checkout -------------------------------------------------------------------------

    public async Task<FraudVerdict> EvaluateAsync(FraudContext c, CancellationToken cancellationToken)
    {
        var blocked = await BlacklistMatchAsync(c, cancellationToken);
        if (blocked is not null)
        {
            return new FraudVerdict(blocked, []);
        }

        var keys = Rules.SelectMany(r => new[] { SettingKeys.RuleEnabled(r.Code), SettingKeys.RuleThreshold(r.Code), SettingKeys.RuleAction(r.Code) }).ToList();
        var values = await settings.ReadAsync(keys, cancellationToken);
        var configured = Rules.ToDictionary(r => r.Code, r => Configured(r, values));
        var now = clock.GetUtcNow();
        var stats = await StatsAsync(c.CustomerId, cancellationToken);
        var findings = new List<(string Code, string Message, int Score, RuleAction Action)>();

        void Hit(string code, string message)
        {
            var rule = configured[code];
            if (rule.Enabled)
            {
                findings.Add((code, message, rule.Score, rule.Action));
            }
        }

        if (c.IsCod && stats.Delivered == 0 && c.Total >= configured["HIGH_COD_NEW_CUSTOMER"].Threshold)
        {
            Hit("HIGH_COD_NEW_CUSTOMER", $"Cash on delivery of BDT {c.Total:0.##} from a customer with no delivered order.");
        }

        if (c.IsCod && stats.TotalOrders == 0)
        {
            Hit("FIRST_TIME_COD", "First cash on delivery order of this customer.");
        }

        if (c.Total >= configured["HIGH_VALUE"].Threshold)
        {
            Hit("HIGH_VALUE", $"High order total (BDT {c.Total:0.##}).");
        }

        if (!c.IsStaffOrder && (!string.IsNullOrEmpty(c.DeviceId) || !string.IsNullOrEmpty(c.Ip)))
        {
            var since = now.AddHours(-24);
            var device = c.DeviceId;
            var ip = c.Ip;
            var others = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking()
                .Where(o => o.PlacedAt >= since && o.CustomerMobile != c.Mobile && ((device != null && o.DeviceId == device) || (ip != null && o.ClientIp == ip)))
                .Select(o => o.CustomerMobile).Distinct().CountAsync(cancellationToken);
            if (others + 1 >= configured["DEVICE_VELOCITY"].Threshold)
            {
                Hit("DEVICE_VELOCITY", $"{others + 1} different mobile numbers ordered from this device or network within 24 hours.");
            }
        }

        if (c.IsCod && stats.ReturnedOrFailed >= Math.Max(1m, configured["PAST_FAILED_COD"].Threshold))
        {
            Hit("PAST_FAILED_COD", $"{stats.ReturnedOrFailed} earlier orders of this customer were returned or refused.");
        }

        if (NormalizeText(c.ShipAddressLine).Length < configured["SHORT_ADDRESS"].Threshold)
        {
            Hit("SHORT_ADDRESS", "The delivery address is very short.");
        }

        var address = NormalizeText(c.ShipAddressLine);
        var addressRaw = c.ShipAddressLine.Trim();
        var thirtyDays = now.AddDays(-30);
        var sameAddress = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking()
            .Where(o => o.PlacedAt >= thirtyDays && o.CustomerMobile != c.Mobile && o.ShipAddressLine == addressRaw)
            .Select(o => o.CustomerMobile).Distinct().CountAsync(cancellationToken);
        if (address.Length > 0 && sameAddress >= Math.Max(1m, configured["MISMATCHED_DETAILS"].Threshold))
        {
            Hit("MISMATCHED_DETAILS", $"This address was used with {sameAddress} other mobile number(s) in the last 30 days.");
        }

        var reject = findings.FirstOrDefault(f => f.Action == RuleAction.Reject);
        return reject.Code is not null
            ? new FraudVerdict($"Rule {reject.Code}: {reject.Message}", findings)
            : new FraudVerdict(null, findings);
    }

    private async Task<string?> BlacklistMatchAsync(FraudContext c, CancellationToken cancellationToken)
    {
        var entries = await db.BlacklistEntries.AsNoTracking().ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return null;
        }

        var phone = PhoneNumber.Normalize(c.ShipPhone);
        var address = NormalizeText(c.ShipAddressLine);
        var email = NormalizeText(c.Email);
        foreach (var e in entries)
        {
            var hit = e.Kind switch
            {
                BlacklistKind.Mobile => e.Value == c.Mobile || (phone is not null && e.Value == phone),
                BlacklistKind.Email => email.Length > 0 && e.Value == email,
                BlacklistKind.Address => address.Contains(e.Value, StringComparison.Ordinal),
                BlacklistKind.Device => c.DeviceId is not null && e.Value == c.DeviceId,
                _ => false,
            };
            if (hit)
            {
                return $"Blacklisted {e.Kind.ToString().ToLowerInvariant()}.";
            }
        }

        return null;
    }

    // ---- Review queue -----------------------------------------------------------------------------------

    public async Task<Result<PagedResult<ReviewItemDto>>> QueueAsync(PageRequest request, FraudState? state, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Fraud.FlagView);
        var query = db.Orders.AsNoTracking().Include(o => o.Flags).AsQueryable();
        if (visible is not null)
        {
            query = query.Where(o => visible.Contains(o.BranchId));
        }

        query = state is { } s
            ? query.Where(o => o.FraudState == s)
            : query.Where(o => o.FraudState == FraudState.Flagged || o.FraudState == FraudState.InReview || o.FraudState == FraudState.AdvanceRequested);

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var orders = await query.OrderByDescending(o => o.RiskScore).ThenBy(o => o.PlacedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var items = new List<ReviewItemDto>();
        foreach (var o in orders)
        {
            items.Add(new ReviewItemDto(o.Id, o.Number, o.BranchId, o.FraudState, o.RiskScore, o.CustomerMobile, o.CustomerName, o.GrandTotal, o.PaymentMethod, o.Status, o.PlacedAt,
                [.. o.Flags.OrderBy(f => f.At).Select(f => new RiskFlagDto(f.RuleCode, f.Message, f.Score, f.Action, f.At))], await StatsAsync(o.CustomerId, cancellationToken)));
        }

        return new PagedResult<ReviewItemDto>(items, total, page.Page, page.PageSize);
    }

    /// <summary>
    /// Records a decision. Approve releases the hold; RequestAdvance asks for the delivery charge up front (the order stays held until approved);
    /// Call marks the order as in review and keeps the note; Reject cancels the order and returns its stock.
    /// </summary>
    public async Task<Result<IReadOnlyList<FraudLogDto>>> DecideAsync(Guid orderId, FraudDecisionRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        if (!Enum.IsDefined(request.Action))
        {
            return Invalid("action", "Choose Approve, RequestAdvance, Call or Reject.");
        }

        if (request.Action == FraudAction.Reject && string.IsNullOrWhiteSpace(request.Note))
        {
            return Invalid("note", "Give the reason for rejecting the order.");
        }

        if (request.Note is { Length: > 500 })
        {
            return Invalid("note", "The note can have at most 500 characters.");
        }

        var order = await db.Orders.Include(o => o.Items).Include(o => o.Notes).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
        }

        var refusal = await access.CheckAsync(actor, order.BranchId, Permissions.Fraud.ReviewDecide, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!order.IsHeld)
        {
            return Error.Conflict(ErrorCodes.Fraud.NotWaiting, "This order is not waiting for a fraud decision.");
        }

        var who = access.WhoIs(actor);
        var now = clock.GetUtcNow();
        var log = order.Decide(request.Action, request.Note, now, who.Id, who.Name);
        db.FraudReviewLogs.Add(log);
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            order.AddNote(NoteKind.Internal, $"Fraud review ({request.Action}): {request.Note.Trim()}", who.Id, who.Name, now);
        }

        if (request.Action == FraudAction.RequestAdvance)
        {
            order.RequireAdvance(order.DeliveryCharge);
        }

        if (request.Action == FraudAction.Reject)
        {
            var cancelled = await workflow.CancelAsync(order, $"Rejected after fraud review: {request.Note!.Trim()}", who, cancellationToken);
            if (cancelled.IsFailure)
            {
                return cancelled.Error!;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(await LogAsync(orderId, cancellationToken));
    }

    public async Task<IReadOnlyList<FraudLogDto>> LogAsync(Guid orderId, CancellationToken cancellationToken) =>
        [.. (await db.FraudReviewLogs.AsNoTracking().Where(l => l.OrderId == orderId).OrderBy(l => l.At).ToListAsync(cancellationToken))
            .Select(l => new FraudLogDto(l.Action, l.FromState, l.ToState, l.Note, l.ActorName, l.At))];
}
