using Juvo.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Application.Loyalty;

public static class LoyaltyRegistration
{
    /// <summary>Rewards: points, store credit, gift cards, referrals (module AB).</summary>
    public static IServiceCollection AddLoyalty(this IServiceCollection services)
    {
        services.AddScoped<LoyaltySettingsService>();
        services.AddScoped<LoyaltyLedger>();
        services.AddScoped<GiftCardService>();
        services.AddScoped<TenderService>();
        services.AddScoped<ReferralService>();
        services.AddScoped<RewardsEngine>();
        services.AddScoped<RewardsService>();
        services.AddScoped<LoyaltyAdminService>();
        services.AddScoped<IOutboxMessageHandler, LoyaltyOrderDeliveredHandler>();
        services.AddScoped<IOutboxMessageHandler, LoyaltyReturnRefundedHandler>();
        return services;
    }
}
