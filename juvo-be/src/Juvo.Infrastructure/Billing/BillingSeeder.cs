using Juvo.Domain.Billing;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Infrastructure.Billing;

/// <summary>
/// Idempotent seed of the sample tax rule: 15% VAT, inactive and marked unverified (CLAUDE.md section 7). It is only a starting point; the owner's accountant must confirm the rate and
/// what it applies to before it is switched on. Never touches a rule that already exists.
/// </summary>
public sealed class BillingSeeder(JuvoDbContext db)
{
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await db.TaxRules.AnyAsync(r => r.Code == "VAT15", cancellationToken))
        {
            return 0;
        }

        db.TaxRules.Add(new TaxRule("VAT15", "Value added tax (sample)", 15m, true, false,
            "Sample rule from the specification. Not verified: confirm the rate and what it applies to with the accountant before turning it on."));
        await db.SaveChangesAsync(cancellationToken);
        return 1;
    }
}
