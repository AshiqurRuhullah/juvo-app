using Juvo.Application.Couriers;
using Juvo.Domain.Couriers;
using Juvo.Shared.Results;

namespace Juvo.Application.Tests.Couriers;

public class CourierProviderTests
{
    private static readonly IReadOnlyDictionary<string, string> NoSettings = new Dictionary<string, string>();
    private static readonly CourierParty Party = new("Nadia", "+8801712345678", "House 1, Dhaka", "Dhaka", "BD");

    [Fact]
    public async Task A_provider_slot_without_keys_answers_NOT_CONFIGURED_to_every_call()
    {
        var slot = new NotConfiguredCourierProvider("PATHAO", "Pathao");
        Assert.False(slot.IsConfigured);

        var quote = await slot.QuoteAsync(new CourierQuoteRequest("BD", null, "Dhaka", 500, 0m, NoSettings), CancellationToken.None);
        var book = await slot.BookAsync(new CourierBookingRequest("JV-1", false, Party, Party, 500, null, null, null, 0m, null, NoSettings), CancellationToken.None);
        var cancel = await slot.CancelAsync("X", CancellationToken.None);
        var status = await slot.FetchStatusAsync("X", CancellationToken.None);
        var label = await slot.LabelAsync("X", CancellationToken.None);
        var webhook = slot.ParseWebhook("{}", new Dictionary<string, string>(), DateTimeOffset.UtcNow);

        foreach (var error in new[] { quote.Error, book.Error, cancel.Error, status.Error, label.Error, webhook.Error })
        {
            Assert.NotNull(error);
            Assert.Equal(ErrorCodes.Courier.NotConfigured, error.Code);
            Assert.Equal(ErrorType.Conflict, error.Type);
        }
    }

    [Fact]
    public void The_registry_finds_providers_by_code_ignoring_case_and_the_last_registration_wins()
    {
        var first = new NotConfiguredCourierProvider("REDX", "RedX one");
        var second = new NotConfiguredCourierProvider("redx", "RedX two");
        var registry = new CourierProviderRegistry([first, second, new NotConfiguredCourierProvider("DHL", "DHL")]);
        Assert.Equal(2, registry.All.Count);
        Assert.Same(second, registry.Find("RedX"));
        Assert.Null(registry.Find("nope"));
        Assert.Null(registry.Find(null));
    }

    [Fact]
    public void Suggestions_are_ordered_by_priority_then_cost_then_name_and_ineligible_ones_are_left_out()
    {
        Courier Make(string code, int priority) => new(code, new CourierDetails(code, code, true, priority, true, false, null, null, [], null, 0m, 0m, 1000, null, null, new Dictionary<string, string>()));
        CourierCheck Ok(Courier c, decimal? cost) => new(c, true, null, null, cost, 1, 3);
        var a = Make("A", 20);
        var b = Make("B", 10);
        var c2 = Make("C", 10);
        var d = Make("D", 5);
        var ranked = CourierSelector.Rank([Ok(a, 10m), Ok(b, 80m), Ok(c2, 60m), new CourierCheck(d, false, ErrorCodes.Courier.NotServiceable, "no", null, null, null)]);
        Assert.Equal(["C", "B", "A"], ranked.Select(x => x.Courier.Code).ToArray());
    }
}
