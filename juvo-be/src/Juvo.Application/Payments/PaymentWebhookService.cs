using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Payments;

/// <summary>
/// Receives gateway webhooks (module I): signature and replay window are checked by the provider's adapter, an event id seen before is acknowledged and ignored (idempotent),
/// and even a signed "payment succeeded" is checked with the gateway server to server before the payment becomes Paid. Unknown payments are acknowledged without effect so the
/// provider stops retrying.
/// </summary>
public sealed class PaymentWebhookService(IApplicationDbContext db, IUnitOfWork uow, IPaymentGatewayRegistry gateways, PaymentOutcomes outcomes, TimeProvider clock)
{
    public async Task<Result<WebhookResultDto>> ProcessAsync(string provider, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var gateway = gateways.Find(provider);
        if (gateway is null)
        {
            return Error.NotFound(ErrorCodes.Payment.NotFound, "Unknown payment provider.");
        }

        if (!gateway.IsConfigured)
        {
            return Error.Conflict(ErrorCodes.Payment.NotConfigured, "This payment provider is not set up.");
        }

        var parsed = gateway.ParseWebhook(body, headers, clock.GetUtcNow());
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var evt = parsed.Value;
        var key = $"{gateway.Code}:{evt.EventId}";
        if (await db.PaymentEvents.AnyAsync(e => e.ProviderEventId == key, cancellationToken))
        {
            return new WebhookResultDto("duplicate", null);
        }

        // Ask the gateway before changing anything: a signed message is necessary, not sufficient.
        GatewayVerification? verification = null;
        if (evt.Kind == GatewayEventKind.PaymentSucceeded)
        {
            var asked = await gateway.VerifyAsync(evt.ProviderReference, cancellationToken);
            verification = asked.IsSuccess ? asked.Value : null;
        }

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<WebhookResultDto>>(async ct =>
            {
                var payment = await db.Payments.IgnoreQueryFilters(["BranchScope"]).Include(p => p.Events)
                    .FirstOrDefaultAsync(p => p.Provider == gateway.Code && p.ProviderReference == evt.ProviderReference, ct);
                if (payment is null)
                {
                    return new WebhookResultDto("ignored", null);
                }

                if (await db.PaymentEvents.AnyAsync(e => e.ProviderEventId == key, ct))
                {
                    return new WebhookResultDto("duplicate", payment.Status.ToString());
                }

                var order = await outcomes.LoadOrderAsync(payment.OrderId, ct) ?? throw new InvalidOperationException("The order of a payment is missing.");
                var now = clock.GetUtcNow();
                var who = new Who(null, $"{gateway.DisplayName} webhook");
                var status = "processed";
                switch (evt.Kind)
                {
                    case GatewayEventKind.PaymentSucceeded:
                        if (verification is not { Outcome: GatewayOutcome.Succeeded })
                        {
                            payment.Note("WebhookRejected", "The webhook said paid but the gateway does not confirm it. Not marked paid.", now, null, who.Name, key);
                            status = "not-confirmed";
                        }
                        else if (verification.Amount != payment.Amount || !string.Equals(verification.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase) ||
                                 (evt.Amount is { } claimed && claimed != payment.Amount))
                        {
                            payment.Note("AmountMismatch", $"The gateway reports {verification.Amount} {verification.Currency}; expected {payment.Amount} {payment.Currency}. Not marked paid.", now, null, who.Name, key);
                            status = "amount-mismatch";
                        }
                        else if (payment.Status == PaymentStatus.Pending || payment.Status == PaymentStatus.Failed)
                        {
                            var paid = await outcomes.PaidAsync(payment, order, verification.TransactionId, key, "Confirmed by the gateway", who, ct);
                            if (paid.IsFailure)
                            {
                                payment.Note("WebhookIgnored", paid.Error!.Message, now, null, who.Name, key);
                                status = "ignored";
                            }
                        }
                        else
                        {
                            payment.Note("Webhook", $"Payment succeeded event for a payment that is {payment.Status}.", now, null, who.Name, key);
                            status = "ignored";
                        }

                        break;
                    case GatewayEventKind.PaymentFailed:
                        if (outcomes.Failed(payment, order, "The gateway reported a failed payment", key, who).IsFailure)
                        {
                            payment.Note("Webhook", $"Failure event for a payment that is {payment.Status}.", now, null, who.Name, key);
                            status = "ignored";
                        }

                        break;
                    case GatewayEventKind.PaymentCancelled:
                        if (outcomes.Cancelled(payment, "The customer cancelled at the gateway", key, who).IsFailure)
                        {
                            payment.Note("Webhook", $"Cancel event for a payment that is {payment.Status}.", now, null, who.Name, key);
                            status = "ignored";
                        }

                        break;
                    default:
                        payment.Note("Webhook", $"Event {evt.Kind} received.", now, null, who.Name, key);
                        break;
                }

                await db.SaveChangesAsync(ct);
                return new WebhookResultDto(status, payment.Status.ToString());
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Payment.ConcurrentUpdate, "The payment changed at the same moment. The provider should retry.");
        }
        catch (DbUpdateException)
        {
            // A simultaneous replay won the unique index on the provider event id: this one is a duplicate.
            if (await db.PaymentEvents.AsNoTracking().AnyAsync(e => e.ProviderEventId == key, cancellationToken))
            {
                return new WebhookResultDto("duplicate", null);
            }

            throw;
        }
    }
}
