namespace Juvo.Domain.Returns;

/// <summary>
/// The allowed transitions of a return, exactly as in the specification (CLAUDE.md section 7): Requested to Approved or Rejected; Approved to PickupScheduled,
/// then PickedUp, Received, Inspected, and then Refunded, Exchanged or RefundRejected; a customer can cancel while Requested or Approved. Anything else is refused.
/// </summary>
public static class ReturnStateMachine
{
    private static readonly Dictionary<ReturnStatus, ReturnStatus[]> Moves = new()
    {
        [ReturnStatus.Requested] = [ReturnStatus.Approved, ReturnStatus.Rejected, ReturnStatus.Cancelled],
        [ReturnStatus.Approved] = [ReturnStatus.PickupScheduled, ReturnStatus.Cancelled],
        [ReturnStatus.PickupScheduled] = [ReturnStatus.PickedUp],
        [ReturnStatus.PickedUp] = [ReturnStatus.Received],
        [ReturnStatus.Received] = [ReturnStatus.Inspected],
        [ReturnStatus.Inspected] = [ReturnStatus.Refunded, ReturnStatus.Exchanged, ReturnStatus.RefundRejected],
        [ReturnStatus.Rejected] = [],
        [ReturnStatus.Refunded] = [],
        [ReturnStatus.Exchanged] = [],
        [ReturnStatus.RefundRejected] = [],
        [ReturnStatus.Cancelled] = [],
    };

    public static IReadOnlyList<ReturnStatus> NextFor(ReturnStatus from) => Moves[from];

    public static bool CanMove(ReturnStatus from, ReturnStatus to) => Moves[from].Contains(to);

    /// <summary>Closed for good: nothing more happens to the return.</summary>
    public static bool IsFinal(ReturnStatus status) => Moves[status].Length == 0;

    /// <summary>The return still holds a claim on the items (they count against the quantity that can be returned).</summary>
    public static bool HoldsItems(ReturnStatus status) =>
        status is not (ReturnStatus.Rejected or ReturnStatus.Cancelled or ReturnStatus.RefundRejected);

    /// <summary>The goods are, or will be, with the shop: the order is held in ReturnInProgress.</summary>
    public static bool IsOpen(ReturnStatus status) => !IsFinal(status) && status != ReturnStatus.Requested;

    /// <summary>The return is completed and the items count as returned (Refunded or Exchanged).</summary>
    public static bool IsCompleted(ReturnStatus status) => status is ReturnStatus.Refunded or ReturnStatus.Exchanged;
}
