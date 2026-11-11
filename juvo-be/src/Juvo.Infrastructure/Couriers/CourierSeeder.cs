using Juvo.Domain.Couriers;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Infrastructure.Couriers;

/// <summary>
/// Idempotent seed of the courier list (module H): the sandbox courier, switched on, and the six real companies as switched-off slots (Pathao, Steadfast, RedX, Paperfly, eCourier, DHL)
/// that become usable in Step 14 when the owner gives account keys. Runs only while no courier exists, so the owner's later edits (rates, priority, archiving) are never undone.
/// The slot rates are zero on purpose: only the courier's own price list may fill them.
/// </summary>
public sealed class CourierSeeder(JuvoDbContext db)
{
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await db.Couriers.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return 0;
        }

        CourierDetails Slot(string name, string provider, int priority, bool international) =>
            new(name, provider, false, priority, true, international, null, international ? 30000 : 20000, [], null, 0m, 0m, 1000, null, "Not set up yet: needs the shop's own account keys (Step 14).", new Dictionary<string, string>());

        db.Couriers.Add(new Courier("SANDBOX", new CourierDetails("Sandbox courier", SandboxCourierProvider.ProviderCode, true, 100, true, true, null, null, [], null, 60m, 20m, 1000, null,
            "Practice courier with no account. Parcels move when staff press Advance, or when a signed webhook arrives. Switch it off when a real courier is set up.", new Dictionary<string, string>())));
        db.Couriers.Add(new Courier("PATHAO", Slot("Pathao", "PATHAO", 10, false)));
        db.Couriers.Add(new Courier("STEADFAST", Slot("Steadfast", "STEADFAST", 20, false)));
        db.Couriers.Add(new Courier("REDX", Slot("RedX", "REDX", 30, false)));
        db.Couriers.Add(new Courier("PAPERFLY", Slot("Paperfly", "PAPERFLY", 40, false)));
        db.Couriers.Add(new Courier("ECOURIER", Slot("eCourier", "ECOURIER", 50, false)));
        db.Couriers.Add(new Courier("DHL", Slot("DHL Express", "DHL", 60, true)));
        await db.SaveChangesAsync(cancellationToken);
        return 7;
    }
}
