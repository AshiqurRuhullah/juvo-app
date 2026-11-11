using Juvo.Application.Abstractions;
using Juvo.Application.Ums;
using Juvo.Domain.Billing;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Billing;

/// <summary>
/// Tax rules (module U). Prices are tax-inclusive, so a rule only reports the tax inside the price: <see cref="ComputeAsync"/> never changes a total, and with no active rule
/// it reports zero. At most one rule is active; activating an unverified rule needs an explicit acknowledgement.
/// </summary>
public sealed class TaxService(IApplicationDbContext db, ActorContext actors)
{
    public const string SampleCode = "VAT15";

    public static TaxRuleDto ToDto(TaxRule r) => new(r.Id, r.Code, r.Name, r.RatePercent, r.IsInclusive, r.AppliesToDelivery, r.IsActive, r.IsVerified, r.Note);

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>The tax inside an order's totals under the active rule (none: zero). Does not change any amount.</summary>
    public async Task<(TaxRule? Rule, TaxBreakdown Breakdown)> ComputeAsync(IReadOnlyList<decimal> lineTotals, decimal discount, decimal deliveryCharge, CancellationToken cancellationToken)
    {
        var rule = await db.TaxRules.AsNoTracking().FirstOrDefaultAsync(r => r.IsActive, cancellationToken);
        return (rule, TaxMath.ForOrder(lineTotals, discount, deliveryCharge, rule?.RatePercent ?? 0m, rule?.AppliesToDelivery ?? false));
    }

    // ---- Staff -------------------------------------------------------------------------------------------------

    private async Task<(Actor? Actor, Error? Refusal)> RequireAsync(string permission, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return (null, Error.Unauthorized());
        }

        return actor.Access.HasAnywhere(permission) ? (actor, null) : (null, Error.Forbidden());
    }

    public async Task<Result<IReadOnlyList<TaxRuleDto>>> ListAsync(CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Tax.RuleView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var rules = await db.TaxRules.AsNoTracking().OrderBy(r => r.Code).ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<TaxRuleDto>>([.. rules.Select(ToDto)]);
    }

    private static Error? Validate(SaveTaxRuleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
        {
            return Invalid("name", "Give the rule a name (up to 120 characters).");
        }

        if (request.RatePercent is < 0 or > 100)
        {
            return Invalid("ratePercent", "The rate is between 0 and 100 percent.");
        }

        if (request.Note is { Length: > 500 })
        {
            return Invalid("note", "Up to 500 characters.");
        }

        return request.IsInclusive
            ? null
            : new Error(ErrorCodes.Invoice.ExclusiveNotSupported, "Only tax-inclusive rules are supported: the prices in the shop already contain the tax.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["isInclusive"] = ["Only tax-inclusive rules are supported."] });
    }

    public async Task<Result<TaxRuleDto>> CreateAsync(SaveTaxRuleRequest request, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Tax.RuleEdit, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var code = request.Code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || code.Length > 30 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return Invalid("code", "Use up to 30 letters, digits, dashes or underscores.");
        }

        if (await db.TaxRules.AnyAsync(r => r.Code == code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Invoice.TaxRuleCodeTaken, "A tax rule with this code exists.");
        }

        var rule = new TaxRule(code, request.Name!, request.RatePercent, request.AppliesToDelivery, request.IsVerified, request.Note);
        db.TaxRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }

    public async Task<Result<TaxRuleDto>> UpdateAsync(Guid id, SaveTaxRuleRequest request, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Tax.RuleEdit, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var rule = await db.TaxRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            return Error.NotFound(ErrorCodes.Invoice.TaxRuleNotFound, "Tax rule not found.");
        }

        rule.Update(request.Name!, request.RatePercent, request.AppliesToDelivery, request.IsVerified, request.Note);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }

    public async Task<Result<TaxRuleDto>> SetActiveAsync(Guid id, bool active, bool acknowledgeUnverified, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Tax.RuleEdit, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var rule = await db.TaxRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            return Error.NotFound(ErrorCodes.Invoice.TaxRuleNotFound, "Tax rule not found.");
        }

        if (active)
        {
            if (!rule.IsVerified && !acknowledgeUnverified)
            {
                return Error.Conflict(ErrorCodes.Invoice.TaxRuleUnverified, "This rule is not verified with an accountant yet. Confirm that you want to use it anyway (acknowledgeUnverified).");
            }

            foreach (var other in await db.TaxRules.Where(r => r.IsActive && r.Id != id).ToListAsync(cancellationToken))
            {
                other.SetActive(false);
            }
        }

        rule.SetActive(active);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }
}
