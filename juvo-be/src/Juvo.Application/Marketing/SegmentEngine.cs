using System.Linq.Expressions;
using Juvo.Application.Abstractions;
using Juvo.Domain.Customers;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Marketing;

/// <summary>The kinds of segment rule (the <c>type</c> of a rule).</summary>
public static class SegmentRuleTypes
{
    public const string NewCustomers = "newCustomers";
    public const string RepeatBuyers = "repeatBuyers";
    public const string HighSpenders = "highSpenders";
    public const string Inactive = "inactive";
    public const string Location = "location";
    public const string Consent = "consent";
    public const string LastOrderStatus = "lastOrderStatus";
    public const string Wishlist = "wishlist";
    public const string Cart = "cart";

    public static readonly IReadOnlyList<string> All = [NewCustomers, RepeatBuyers, HighSpenders, Inactive, Location, Consent, LastOrderStatus, Wishlist, Cart];
}

/// <summary>
/// Checks a segment definition before it is saved or run. The model is typed: a rule has a fixed type and only the fields that type uses, so nothing the staff
/// member types is ever executed as text. Returns field errors keyed like <c>rules[1].minSpend</c>, or null when the definition is valid.
/// </summary>
public static class SegmentValidator
{
    public const int MaxRules = 20;
    public const int MaxIds = 200;
    public const int MaxDays = 3650;

    public static Dictionary<string, string[]>? Validate(SegmentDefinition? definition)
    {
        var errors = new Dictionary<string, string[]>();
        if (definition is null)
        {
            return new Dictionary<string, string[]> { ["definition"] = ["Give the segment rules."] };
        }

        if (definition.Combinator is not ("and" or "or"))
        {
            errors["combinator"] = ["Use and (every rule) or or (any rule)."];
        }

        if (definition.Rules.Count is 0 or > MaxRules)
        {
            errors["rules"] = [$"Give between 1 and {MaxRules} rules."];
            return errors;
        }

        for (var i = 0; i < definition.Rules.Count; i++)
        {
            ValidateRule(definition.Rules[i], $"rules[{i}]", errors);
        }

        return errors.Count == 0 ? null : errors;
    }

    private static void ValidateRule(SegmentRule r, string at, Dictionary<string, string[]> errors)
    {
        void Bad(string field, string message) => errors[$"{at}.{field}"] = [message];

        bool Days(int? value, string field, bool required)
        {
            if (value is null)
            {
                if (required)
                {
                    Bad(field, "Give a number of days.");
                }

                return !required;
            }

            if (value is < 1 or > MaxDays)
            {
                Bad(field, $"Use 1 to {MaxDays} days.");
                return false;
            }

            return true;
        }

        // A field that does not belong to the rule type is refused, so a rule always means exactly what it says.
        void Only(params string[] allowed)
        {
            var used = new List<(string Name, bool Set)>
            {
                ("days", r.Days is not null), ("minOrders", r.MinOrders is not null), ("minSpend", r.MinSpend is not null), ("periodDays", r.PeriodDays is not null), ("level", r.Level is not null),
                ("ids", r.Ids is not null), ("channel", r.Channel is not null), ("granted", r.Granted is not null), ("status", r.Status is not null), ("productIds", r.ProductIds is not null),
                ("idleHours", r.IdleHours is not null),
            };
            foreach (var (name, set) in used)
            {
                if (set && !allowed.Contains(name))
                {
                    Bad(name, $"This field is not used by a {r.Type} rule.");
                }
            }
        }

        switch (r.Type)
        {
            case SegmentRuleTypes.NewCustomers:
                Only("days");
                Days(r.Days, "days", true);
                break;
            case SegmentRuleTypes.RepeatBuyers:
                Only("minOrders", "periodDays");
                if (r.MinOrders is null or < 2 or > 1000)
                {
                    Bad("minOrders", "Use 2 to 1000 orders.");
                }

                Days(r.PeriodDays, "periodDays", false);
                break;
            case SegmentRuleTypes.HighSpenders:
                Only("minSpend", "periodDays");
                if (r.MinSpend is null or <= 0 or > 100_000_000m)
                {
                    Bad("minSpend", "Give an amount above zero (BDT).");
                }

                Days(r.PeriodDays, "periodDays", false);
                break;
            case SegmentRuleTypes.Inactive:
                Only("days");
                Days(r.Days, "days", true);
                break;
            case SegmentRuleTypes.Location:
                Only("level", "ids");
                if (r.Level is not ("division" or "district"))
                {
                    Bad("level", "Use division or district.");
                }

                if (r.Ids is null or { Count: 0 } || r.Ids.Count > MaxIds)
                {
                    Bad("ids", $"Give 1 to {MaxIds} ids.");
                }

                break;
            case SegmentRuleTypes.Consent:
                Only("channel", "granted");
                if (r.Channel is not ("sms" or "email"))
                {
                    Bad("channel", "Use sms or email.");
                }

                break;
            case SegmentRuleTypes.LastOrderStatus:
                Only("status");
                if (r.Status is not { } status || !Enum.IsDefined(status))
                {
                    Bad("status", "Give an order status.");
                }

                break;
            case SegmentRuleTypes.Wishlist:
                Only("productIds");
                if (r.ProductIds is { Count: > MaxIds })
                {
                    Bad("productIds", $"At most {MaxIds} products.");
                }

                break;
            case SegmentRuleTypes.Cart:
                Only("idleHours");
                if (r.IdleHours is < 1 or > 24 * 365)
                {
                    Bad("idleHours", "Use 1 to 8760 hours.");
                }

                break;
            default:
                Bad("type", $"Unknown rule type. Use one of: {string.Join(", ", SegmentRuleTypes.All)}.");
                break;
        }
    }
}

/// <summary>
/// Translates a validated segment definition into one set-based query over active customers. Every rule becomes a predicate with sub-queries on orders, addresses, consent and carts,
/// joined with AND or OR, so the database does all the work and nothing is loaded into memory (FR-AC01). Orders count as a purchase unless Cancelled or Returned. Orders are read across all
/// branches (marketing is a global module).
/// </summary>
public sealed class SegmentQueryBuilder(IApplicationDbContext db, TimeProvider clock)
{
    private IQueryable<Order> Orders => db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking();

    /// <summary>Active customers that match the definition.</summary>
    public IQueryable<Customer> Members(SegmentDefinition definition)
    {
        var now = clock.GetUtcNow();
        var predicate = Combine(definition.Rules.Select(r => Predicate(r, now)).ToList(), definition.Combinator == "or");
        return db.Customers.AsNoTracking().Where(c => c.IsActive).Where(predicate);
    }

    /// <summary>The latest consent decision for the channel is "yes".</summary>
    public Expression<Func<Customer, bool>> Consented(ConsentType type) =>
        c => db.ConsentLogs.Where(l => l.CustomerId == c.Id && l.Type == type).OrderByDescending(l => l.At).ThenByDescending(l => l.Id).Select(l => (bool?)l.Granted).FirstOrDefault() == true;

    /// <summary>Consented on SMS and has a mobile number.</summary>
    public Expression<Func<Customer, bool>> SmsReachable() => Both(Consented(ConsentType.MarketingSms), c => c.Mobile != string.Empty);

    /// <summary>Consented on email and has an email address.</summary>
    public Expression<Func<Customer, bool>> EmailReachable() => Both(Consented(ConsentType.MarketingEmail), c => c.Email != null && c.Email != string.Empty);

    private static Expression<Func<Customer, bool>> Both(Expression<Func<Customer, bool>> a, Expression<Func<Customer, bool>> b) => Combine([a, b], false);

    private Expression<Func<Customer, bool>> Predicate(SegmentRule r, DateTimeOffset now)
    {
        var orders = Orders;
        switch (r.Type)
        {
            case SegmentRuleTypes.NewCustomers:
            {
                var from = now.AddDays(-r.Days!.Value);
                return c => c.CreatedAt >= from;
            }

            case SegmentRuleTypes.RepeatBuyers:
            {
                var min = r.MinOrders!.Value;
                var from = r.PeriodDays is { } p ? now.AddDays(-p) : DateTimeOffset.MinValue;
                return c => orders.Count(o => o.CustomerId == c.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.PlacedAt >= from) >= min;
            }

            case SegmentRuleTypes.HighSpenders:
            {
                var min = r.MinSpend!.Value;
                var from = r.PeriodDays is { } p ? now.AddDays(-p) : DateTimeOffset.MinValue;
                return c => orders.Where(o => o.CustomerId == c.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.PlacedAt >= from).Sum(o => (decimal?)o.GrandTotal) >= min;
            }

            case SegmentRuleTypes.Inactive:
            {
                var cutoff = now.AddDays(-r.Days!.Value);
                return c => c.CreatedAt < cutoff && (c.LastLoginAt == null || c.LastLoginAt < cutoff) && !orders.Any(o => o.CustomerId == c.Id && o.PlacedAt >= cutoff);
            }

            case SegmentRuleTypes.Location:
            {
                var ids = r.Ids!;
                var addresses = db.Addresses.AsNoTracking();
                return r.Level == "division"
                    ? c => addresses.Any(a => a.CustomerId == c.Id && a.IsDefault && a.DivisionId != null && ids.Contains(a.DivisionId.Value))
                    : c => addresses.Any(a => a.CustomerId == c.Id && a.IsDefault && a.DistrictId != null && ids.Contains(a.DistrictId.Value));
            }

            case SegmentRuleTypes.Consent:
            {
                var consented = Consented(r.Channel == "sms" ? ConsentType.MarketingSms : ConsentType.MarketingEmail);
                return r.Granted == false ? Not(consented) : consented;
            }

            case SegmentRuleTypes.LastOrderStatus:
            {
                var status = r.Status!.Value;
                return c => orders.Where(o => o.CustomerId == c.Id).OrderByDescending(o => o.PlacedAt).Select(o => (OrderStatus?)o.Status).FirstOrDefault() == status;
            }

            case SegmentRuleTypes.Wishlist:
            {
                var wishes = db.WishlistItems.AsNoTracking();
                if (r.ProductIds is { Count: > 0 } products)
                {
                    return c => wishes.Any(w => w.CustomerId == c.Id && products.Contains(w.ProductId));
                }

                return c => wishes.Any(w => w.CustomerId == c.Id);
            }

            case SegmentRuleTypes.Cart:
            {
                var carts = db.Carts.AsNoTracking();
                var idleBefore = r.IdleHours is { } h ? now.AddHours(-h) : DateTimeOffset.MaxValue;
                return c => carts.Any(k => k.CustomerId == c.Id && k.Items.Any() && (k.UpdatedAt ?? k.CreatedAt) <= idleBefore);
            }

            default:
                throw new InvalidOperationException($"Unknown rule type {r.Type}.");
        }
    }

    private static Expression<Func<Customer, bool>> Not(Expression<Func<Customer, bool>> e) => Expression.Lambda<Func<Customer, bool>>(Expression.Not(e.Body), e.Parameters);

    private static Expression<Func<Customer, bool>> Combine(IReadOnlyList<Expression<Func<Customer, bool>>> parts, bool any)
    {
        var parameter = Expression.Parameter(typeof(Customer), "c");
        Expression? body = null;
        foreach (var part in parts)
        {
            var next = new ReplaceParameter(part.Parameters[0], parameter).Visit(part.Body)!;
            body = body is null ? next : any ? Expression.OrElse(body, next) : Expression.AndAlso(body, next);
        }

        return Expression.Lambda<Func<Customer, bool>>(body ?? Expression.Constant(true), parameter);
    }

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
