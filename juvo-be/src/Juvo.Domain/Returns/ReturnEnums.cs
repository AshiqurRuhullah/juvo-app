namespace Juvo.Domain.Returns;

/// <summary>The return workflow (module L, FR-L04). Stored as an integer; append new values at the end.</summary>
public enum ReturnStatus
{
    Requested = 1,
    Approved = 2,
    Rejected = 3,
    PickupScheduled = 4,
    PickedUp = 5,
    Received = 6,
    Inspected = 7,
    Refunded = 8,
    Exchanged = 9,
    RefundRejected = 10,
    Cancelled = 11,
}

/// <summary>A return gives the money back; an exchange gives another size or colour with no extra charge (FR-L05).</summary>
public enum ReturnKind
{
    Return = 1,
    Exchange = 2,
}

/// <summary>Who is to blame for the return. It decides whether the delivery charge is refunded or a return charge is kept (FR-L07).</summary>
public enum ReturnFault
{
    Customer = 1,
    Seller = 2,
}

/// <summary>What staff found when they opened the parcel.</summary>
public enum ItemCondition
{
    Unopened = 1,
    Good = 2,
    Damaged = 3,
}

public enum RefundMethod
{
    OriginalPayment = 1,
    Cash = 2,
    BankTransfer = 3,
    Wallet = 4,
}

/// <summary>Completed: the money went back (a staff member did it and noted the reference). Pending is kept for gateway refunds that finish later (Step 9).</summary>
public enum RefundStatus
{
    Pending = 1,
    Completed = 2,
}

/// <summary>The reasons a customer can pick, with the party they point to by default (staff may change the fault at inspection).</summary>
public sealed record ReturnReason(string Code, string LabelEn, string LabelBn, ReturnFault DefaultFault)
{
    public static readonly IReadOnlyList<ReturnReason> All =
    [
        new("DAMAGED_ITEM", "The item arrived damaged", "পণ্যটি ক্ষতিগ্রস্ত অবস্থায় পৌঁছেছে", ReturnFault.Seller),
        new("WRONG_ITEM", "I received the wrong item", "ভুল পণ্য পেয়েছি", ReturnFault.Seller),
        new("NOT_AS_DESCRIBED", "Not as described or shown", "বর্ণনা বা ছবির সাথে মিল নেই", ReturnFault.Seller),
        new("QUALITY_ISSUE", "Quality problem", "মান নিয়ে সমস্যা", ReturnFault.Seller),
        new("SIZE_FIT", "Size or fit is not right", "সাইজ বা ফিট ঠিক নয়", ReturnFault.Customer),
        new("CHANGED_MIND", "I changed my mind", "মত বদলেছে", ReturnFault.Customer),
        new("OTHER", "Another reason", "অন্য কারণ", ReturnFault.Customer),
    ];

    public static ReturnReason? Find(string? code) =>
        All.FirstOrDefault(r => string.Equals(r.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));
}
