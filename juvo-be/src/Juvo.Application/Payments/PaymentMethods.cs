using System.Globalization;
using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Application.Ums;
using Juvo.Shared.Results;
using Juvo.Shared.Security;

namespace Juvo.Application.Payments;

public enum PaymentMethodKind
{
    Cash = 1,
    Gateway = 2,
    Manual = 3,
}

/// <summary>A payment method the shop knows. <see cref="Code"/> is stored on the order.</summary>
public sealed record PaymentMethodInfo(string Code, string Name, string NameBn, PaymentMethodKind Kind, bool DefaultEnabled);

public static class PaymentMethodCatalog
{
    public const string Cod = "COD";
    public const string Sandbox = "SANDBOX";
    public const string Manual = "MANUAL";

    public static readonly IReadOnlyList<PaymentMethodInfo> All =
    [
        new(Cod, "Cash on delivery", "ক্যাশ অন ডেলিভারি", PaymentMethodKind.Cash, true),
        new(Sandbox, "Online payment (sandbox)", "অনলাইন পেমেন্ট (স্যান্ডবক্স)", PaymentMethodKind.Gateway, true),
        new(Manual, "Bank or mobile transfer", "ব্যাংক বা মোবাইল ট্রান্সফার", PaymentMethodKind.Manual, false),
        new("BKASH", "bKash", "বিকাশ", PaymentMethodKind.Gateway, false),
        new("NAGAD", "Nagad", "নগদ", PaymentMethodKind.Gateway, false),
        new("SSLCOMMERZ", "SSLCommerz", "এসএসএলকমার্জ", PaymentMethodKind.Gateway, false),
        new("AAMARPAY", "aamarPay", "আমারপে", PaymentMethodKind.Gateway, false),
        new("STRIPE", "Card (Stripe)", "কার্ড (স্ট্রাইপ)", PaymentMethodKind.Gateway, false),
        new("PAYPAL", "PayPal", "পেপাল", PaymentMethodKind.Gateway, false),
    ];

    public static PaymentMethodInfo? Find(string? code) => All.FirstOrDefault(m => string.Equals(m.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Keys of the payment settings (table <c>PlatformSettings</c>).</summary>
public static class PaymentSettingKeys
{
    public const string CodMaxAmount = "Payments.Cod.MaxAmount";
    public const string ManualInstructions = "Payments.Manual.Instructions";
    public const string AutoConfirmPaid = "Payments.AutoConfirmPaidOrders";

    public static string Enabled(string code) => $"Payments.{code}.Enabled";
}

public sealed record PaymentConfig(IReadOnlyDictionary<string, bool> Enabled, decimal CodMaxAmount, string? ManualInstructions, bool AutoConfirmPaid);

public sealed record PaymentMethodView(string Code, string Name, string NameBn, PaymentMethodKind Kind, bool Enabled, bool Available, string? Reason, string? Instructions);

public sealed record PaymentMethodSetting(string Code, bool Enabled);

public sealed record UpdatePaymentSettingsRequest(IReadOnlyList<PaymentMethodSetting>? Methods, decimal? CodMaxAmount, string? ManualInstructions, bool? AutoConfirmPaid);

public sealed record PaymentSettingsDto(IReadOnlyList<PaymentMethodView> Methods, decimal CodMaxAmount, string? ManualInstructions, bool AutoConfirmPaid);

/// <summary>
/// Which payment methods the shop offers and when a method is usable (module I): switched on in the settings, cash on delivery only inside Bangladesh and under the limit,
/// a gateway only when its adapter is configured, a manual transfer only when the owner wrote the account details.
/// </summary>
public sealed class PaymentSettingsService(IApplicationDbContext db, SettingsService settings, IPaymentGatewayRegistry gateways, ActorContext actors)
{
    public async Task<PaymentConfig> ReadAsync(CancellationToken cancellationToken)
    {
        var keys = PaymentMethodCatalog.All.Select(m => PaymentSettingKeys.Enabled(m.Code)).Concat([PaymentSettingKeys.CodMaxAmount, PaymentSettingKeys.ManualInstructions, PaymentSettingKeys.AutoConfirmPaid]).ToList();
        var values = await settings.ReadAsync(keys, cancellationToken);
        var enabled = PaymentMethodCatalog.All.ToDictionary(m => m.Code, m => values.TryGetValue(PaymentSettingKeys.Enabled(m.Code), out var raw) && bool.TryParse(raw, out var on) ? on : m.DefaultEnabled);
        var max = values.TryGetValue(PaymentSettingKeys.CodMaxAmount, out var maxRaw) && decimal.TryParse(maxRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : 0m;
        var instructions = values.GetValueOrDefault(PaymentSettingKeys.ManualInstructions);
        var auto = !values.TryGetValue(PaymentSettingKeys.AutoConfirmPaid, out var autoRaw) || !bool.TryParse(autoRaw, out var autoOn) || autoOn;
        return new PaymentConfig(enabled, max, string.IsNullOrWhiteSpace(instructions) ? null : instructions, auto);
    }

    /// <summary>The methods a customer can see at checkout: the ones switched on, each marked available or not (with the reason).</summary>
    public async Task<List<PaymentMethodView>> MethodsAsync(string country, decimal amount, bool includeDisabled, CancellationToken cancellationToken)
    {
        var config = await ReadAsync(cancellationToken);
        var list = new List<PaymentMethodView>();
        foreach (var m in PaymentMethodCatalog.All)
        {
            var on = config.Enabled.GetValueOrDefault(m.Code);
            if (!on && !includeDisabled)
            {
                continue;
            }

            var reason = on ? Problem(m, config, country, amount) : "This payment method is switched off.";
            list.Add(new PaymentMethodView(m.Code, m.Name, m.NameBn, m.Kind, on, reason is null, reason, m.Kind == PaymentMethodKind.Manual && on ? config.ManualInstructions : null));
        }

        return list;
    }

    /// <summary>Null when the method can be used for this country and amount; otherwise why not.</summary>
    public async Task<string?> ProblemAsync(string code, string country, decimal amount, CancellationToken cancellationToken)
    {
        var method = PaymentMethodCatalog.Find(code);
        if (method is null)
        {
            return "This payment method is not available.";
        }

        var config = await ReadAsync(cancellationToken);
        return config.Enabled.GetValueOrDefault(method.Code) ? Problem(method, config, country, amount) : "This payment method is switched off.";
    }

    private string? Problem(PaymentMethodInfo m, PaymentConfig config, string country, decimal amount) => m.Kind switch
    {
        PaymentMethodKind.Cash when !string.Equals(country, "BD", StringComparison.OrdinalIgnoreCase) => "Cash on delivery is only available inside Bangladesh.",
        PaymentMethodKind.Cash when config.CodMaxAmount > 0 && amount > config.CodMaxAmount => $"Cash on delivery is limited to BDT {config.CodMaxAmount:0.##} per order.",
        PaymentMethodKind.Gateway when gateways.Find(m.Code) is not { IsConfigured: true } => "This payment method is not set up yet.",
        PaymentMethodKind.Manual when config.ManualInstructions is null => "The account details for transfers are not set up yet.",
        _ => null,
    };

    // ---- Staff settings ----------------------------------------------------------------------------------

    public async Task<Result<PaymentSettingsDto>> GetAsync(CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        if (!actor.Access.HasAnywhere(Permissions.Payments.GatewayView))
        {
            return Error.Forbidden();
        }

        var config = await ReadAsync(cancellationToken);
        return new PaymentSettingsDto(await MethodsAsync("BD", 0m, true, cancellationToken), config.CodMaxAmount, config.ManualInstructions, config.AutoConfirmPaid);
    }

    public async Task<Result<PaymentSettingsDto>> UpdateAsync(UpdatePaymentSettingsRequest request, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        if (!actor.Access.HasAnywhere(Permissions.Payments.GatewayEdit))
        {
            return Error.Forbidden();
        }

        foreach (var m in request.Methods ?? [])
        {
            if (PaymentMethodCatalog.Find(m.Code) is not { } known)
            {
                return Error.Validation(new Dictionary<string, string[]> { ["methods"] = [$"Unknown payment method '{m.Code}'."] });
            }

            await settings.SetAsync(PaymentSettingKeys.Enabled(known.Code), m.Enabled ? "true" : "false", cancellationToken);
        }

        if (request.CodMaxAmount is { } max)
        {
            if (max < 0 || max > 10_000_000m)
            {
                return Error.Validation(new Dictionary<string, string[]> { ["codMaxAmount"] = ["Use 0 for no limit, or an amount up to 10,000,000."] });
            }

            await settings.SetAsync(PaymentSettingKeys.CodMaxAmount, max.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }

        if (request.ManualInstructions is { } text)
        {
            if (text.Length > 1000)
            {
                return Error.Validation(new Dictionary<string, string[]> { ["manualInstructions"] = ["Up to 1000 characters."] });
            }

            await settings.SetAsync(PaymentSettingKeys.ManualInstructions, text.Trim(), cancellationToken);
        }

        if (request.AutoConfirmPaid is { } auto)
        {
            await settings.SetAsync(PaymentSettingKeys.AutoConfirmPaid, auto ? "true" : "false", cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(cancellationToken);
    }
}
