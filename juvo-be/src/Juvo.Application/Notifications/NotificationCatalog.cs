using System.Text.RegularExpressions;
using Juvo.Domain.Notifications;

namespace Juvo.Application.Notifications;

/// <summary>A template code the system sends: what it is for, its category and the placeholders its texts may use.</summary>
public sealed record TemplateDefinition(string Code, string Description, NotificationCategory Category, IReadOnlyList<string> Variables);

/// <summary>The built-in text of a template for one channel and language. Staff edits are stored as database rows that replace it.</summary>
public sealed record TemplateDefault(string Code, NotificationChannel Channel, string Language, string? Subject, string Body);

/// <summary>
/// The templates the platform ships with (module K): every message the order, payment, return and sign-in flows send, in English and Bengali. Bengali texts are a first pass that
/// needs native review in Step 12. A template that has no text for a channel is simply not sent on that channel.
/// </summary>
public static partial class NotificationCatalog
{
    public static readonly IReadOnlyList<string> Languages = ["en", "bn"];

    public const string DefaultLanguage = "en";

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}")]
    public static partial Regex Placeholder();

    private static readonly string[] OrderVars = ["OrderNumber", "Total", "Name"];
    private static readonly string[] PaymentVars = ["OrderNumber", "Amount", "Name", "Reason"];
    private static readonly string[] TicketVars = ["TicketNumber", "Subject", "Name", "Reply"];
    private static readonly string[] ReturnVars = ["ReturnNumber", "OrderNumber", "Kind", "KindBn", "Amount", "Name"];

    public static readonly IReadOnlyList<TemplateDefinition> Definitions =
    [
        new("auth.otp", "One-time code for signing in or confirming an order", NotificationCategory.Transactional, ["Code", "Minutes"]),
        new("order.placed", "Order received", NotificationCategory.Transactional, OrderVars),
        new("order.confirmed", "Order confirmed", NotificationCategory.Transactional, OrderVars),
        new("order.packed", "Order packed", NotificationCategory.Transactional, OrderVars),
        new("order.handed-to-courier", "Order handed to the courier", NotificationCategory.Transactional, OrderVars),
        new("order.out-for-delivery", "Order out for delivery", NotificationCategory.Transactional, OrderVars),
        new("order.delivered", "Order delivered", NotificationCategory.Transactional, OrderVars),
        new("order.delivery-failed", "Delivery attempt failed", NotificationCategory.Transactional, OrderVars),
        new("order.returned-to-sender", "Parcel returned to the shop", NotificationCategory.Transactional, OrderVars),
        new("order.cancelled", "Order cancelled", NotificationCategory.Transactional, OrderVars),
        new("order.preorder-ready", "Pre-order stock arrived and was reserved for the order", NotificationCategory.Transactional, OrderVars),
        new("payment.received", "Payment received", NotificationCategory.Transactional, PaymentVars),
        new("payment.failed", "Payment failed", NotificationCategory.Transactional, PaymentVars),
        new("payment.under-review", "Transfer reference received, being checked", NotificationCategory.Transactional, PaymentVars),
        new("payment.rejected", "Payment could not be verified", NotificationCategory.Transactional, PaymentVars),
        new("payment.refunded", "Refund sent", NotificationCategory.Transactional, PaymentVars),
        new("invoice.ready", "Invoice is ready", NotificationCategory.Transactional, ["OrderNumber", "InvoiceNumber", "Name"]),
        new("return.requested", "Return or exchange request received", NotificationCategory.Transactional, ReturnVars),
        new("return.approved", "Return or exchange approved", NotificationCategory.Transactional, ReturnVars),
        new("return.rejected", "Return or exchange not approved", NotificationCategory.Transactional, ReturnVars),
        new("return.pickup-scheduled", "Return pickup scheduled", NotificationCategory.Transactional, ReturnVars),
        new("return.received", "Return parcel received", NotificationCategory.Transactional, ReturnVars),
        new("return.refunded", "Return refund sent", NotificationCategory.Transactional, ReturnVars),
        new("return.exchanged", "Replacement on its way", NotificationCategory.Transactional, ReturnVars),
        new("return.refund-rejected", "Return could not be completed", NotificationCategory.Transactional, ReturnVars),
        new("ticket.received", "Support request received", NotificationCategory.Transactional, TicketVars),
        new("ticket.replied", "Support replied to the request", NotificationCategory.Transactional, TicketVars),
        new("ticket.resolved", "Support request resolved", NotificationCategory.Transactional, TicketVars),
        new("marketing.promotion", "Promotion message (needs the customer's consent)", NotificationCategory.Marketing, ["Name", "Message"]),
        new("loyalty.points-earned", "Points earned on a delivered order", NotificationCategory.Transactional, ["OrderNumber", "Points", "Balance", "Name"]),
        new("loyalty.referral-reward", "Referral reward for the customer who referred a friend", NotificationCategory.Transactional, ["Amount", "Friend", "Name"]),
        new("loyalty.referral-welcome", "Welcome reward for a customer who joined with a referral code", NotificationCategory.Transactional, ["Amount", "Name"]),
        new("loyalty.gift-card-received", "A gift card sent to its recipient", NotificationCategory.Transactional, ["Code", "Amount", "Expiry", "Name"]),
        new("loyalty.points-expiring", "Points that expire soon", NotificationCategory.Transactional, ["Points", "Date", "Name"]),
    ];

    private static TemplateDefault Sms(string code, string language, string body) => new(code, NotificationChannel.Sms, language, null, body);

    private static TemplateDefault Email(string code, string language, string subject, string body) => new(code, NotificationChannel.Email, language, subject, body);

    private static TemplateDefault Wa(string code, string language, string body) => new(code, NotificationChannel.WhatsApp, language, null, body);

    public static readonly IReadOnlyList<TemplateDefault> Defaults =
    [
        Sms("auth.otp", "en", "Your Juvo BD verification code is {{Code}}. It is valid for {{Minutes}} minutes. Do not share it."),
        Sms("auth.otp", "bn", "আপনার জুভো বিডি ভেরিফিকেশন কোড {{Code}}। এটি {{Minutes}} মিনিট বৈধ থাকবে। কাউকে কোডটি জানাবেন না।"),
        Email("auth.otp", "en", "Your Juvo BD verification code", "Your Juvo BD verification code is {{Code}}. It is valid for {{Minutes}} minutes. Do not share it."),
        Email("auth.otp", "bn", "আপনার জুভো বিডি ভেরিফিকেশন কোড", "আপনার জুভো বিডি ভেরিফিকেশন কোড {{Code}}। এটি {{Minutes}} মিনিট বৈধ থাকবে। কাউকে কোডটি জানাবেন না।"),

        Sms("order.placed", "en", "Juvo BD: we received your order {{OrderNumber}}. Total BDT {{Total}}. Thank you!"),
        Sms("order.placed", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} পেয়েছি। মোট ৳{{Total}}। ধন্যবাদ!"),
        Email("order.placed", "en", "We received your order {{OrderNumber}}", "Hello {{Name}},\n\nWe received your order {{OrderNumber}}. The total is BDT {{Total}}. We will tell you when it is confirmed.\n\nJuvo BD"),
        Email("order.placed", "bn", "আপনার অর্ডার {{OrderNumber}} পেয়েছি", "হ্যালো {{Name}},\n\nআপনার অর্ডার {{OrderNumber}} পেয়েছি। মোট ৳{{Total}}। কনফার্ম হলে আপনাকে জানাব।\n\nজুভো বিডি"),
        Wa("order.placed", "en", "Juvo BD: we received your order {{OrderNumber}}. Total BDT {{Total}}."),
        Wa("order.placed", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} পেয়েছি। মোট ৳{{Total}}।"),

        Sms("order.confirmed", "en", "Juvo BD: your order {{OrderNumber}} is confirmed."),
        Sms("order.confirmed", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} কনফার্ম হয়েছে।"),
        Email("order.confirmed", "en", "Your order {{OrderNumber}} is confirmed", "Hello {{Name}},\n\nYour order {{OrderNumber}} is confirmed. We are getting it ready.\n\nJuvo BD"),
        Email("order.confirmed", "bn", "আপনার অর্ডার {{OrderNumber}} কনফার্ম হয়েছে", "হ্যালো {{Name}},\n\nআপনার অর্ডার {{OrderNumber}} কনফার্ম হয়েছে। আমরা এটি প্রস্তুত করছি।\n\nজুভো বিডি"),

        Sms("order.packed", "en", "Juvo BD: your order {{OrderNumber}} is packed and will be handed to the courier soon."),
        Sms("order.packed", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} প্যাক হয়েছে, শীঘ্রই কুরিয়ারে দেওয়া হবে।"),
        Sms("order.handed-to-courier", "en", "Juvo BD: your order {{OrderNumber}} was handed to the courier."),
        Sms("order.handed-to-courier", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} কুরিয়ারে দেওয়া হয়েছে।"),
        Sms("order.out-for-delivery", "en", "Juvo BD: your order {{OrderNumber}} is out for delivery."),
        Sms("order.out-for-delivery", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} ডেলিভারির জন্য বের হয়েছে।"),

        Sms("order.delivered", "en", "Juvo BD: your order {{OrderNumber}} was delivered."),
        Sms("order.delivered", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} ডেলিভারি হয়েছে।"),
        Email("order.delivered", "en", "Your order {{OrderNumber}} was delivered", "Hello {{Name}},\n\nYour order {{OrderNumber}} was delivered. Thank you for shopping with us.\n\nJuvo BD"),
        Email("order.delivered", "bn", "আপনার অর্ডার {{OrderNumber}} ডেলিভারি হয়েছে", "হ্যালো {{Name}},\n\nআপনার অর্ডার {{OrderNumber}} ডেলিভারি হয়েছে। আমাদের সঙ্গে কেনাকাটার জন্য ধন্যবাদ।\n\nজুভো বিডি"),
        Wa("order.delivered", "en", "Juvo BD: your order {{OrderNumber}} was delivered."),
        Wa("order.delivered", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} ডেলিভারি হয়েছে।"),

        Sms("order.delivery-failed", "en", "Juvo BD: we could not deliver your order {{OrderNumber}}. Please keep your phone on; the courier will try again."),
        Sms("order.delivery-failed", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} ডেলিভারি করা যায়নি। অনুগ্রহ করে ফোন চালু রাখুন, কুরিয়ার আবার চেষ্টা করবে।"),
        Sms("order.returned-to-sender", "en", "Juvo BD: your order {{OrderNumber}} could not be delivered and came back to us. We will contact you."),
        Sms("order.returned-to-sender", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} ডেলিভারি করা যায়নি এবং আমাদের কাছে ফিরে এসেছে। আমরা আপনার সাথে যোগাযোগ করব।"),

        Sms("order.preorder-ready", "en", "Juvo BD: good news! The item(s) of your pre-order {{OrderNumber}} are now in stock and reserved for you. We will confirm and ship your order soon."),
        Sms("order.preorder-ready", "bn", "জুভো বিডি: সুখবর! আপনার প্রি-অর্ডার {{OrderNumber}}-এর পণ্য এখন স্টকে এসেছে এবং আপনার জন্য রাখা হয়েছে। শীঘ্রই অর্ডার কনফার্ম করে পাঠানো হবে।"),
        Email("order.preorder-ready", "en", "Your pre-order {{OrderNumber}} is in stock", "Hello {{Name}},\n\nGood news: the item(s) of your pre-order {{OrderNumber}} have arrived and are reserved for you. We will confirm your order and ship it soon.\n\nJuvo BD"),
        Email("order.preorder-ready", "bn", "আপনার প্রি-অর্ডার {{OrderNumber}} স্টকে এসেছে", "হ্যালো {{Name}},\n\nসুখবর: আপনার প্রি-অর্ডার {{OrderNumber}}-এর পণ্য এসে গেছে এবং আপনার জন্য রাখা হয়েছে। শীঘ্রই অর্ডার কনফার্ম করে পাঠানো হবে।\n\nজুভো বিডি"),

        Sms("order.cancelled", "en", "Juvo BD: your order {{OrderNumber}} was cancelled."),
        Sms("order.cancelled", "bn", "জুভো বিডি: আপনার অর্ডার {{OrderNumber}} বাতিল করা হয়েছে।"),
        Email("order.cancelled", "en", "Your order {{OrderNumber}} was cancelled", "Hello {{Name}},\n\nYour order {{OrderNumber}} was cancelled. If you paid, the money will be refunded.\n\nJuvo BD"),
        Email("order.cancelled", "bn", "আপনার অর্ডার {{OrderNumber}} বাতিল হয়েছে", "হ্যালো {{Name}},\n\nআপনার অর্ডার {{OrderNumber}} বাতিল করা হয়েছে। টাকা পরিশোধ করে থাকলে তা ফেরত দেওয়া হবে।\n\nজুভো বিডি"),

        Sms("payment.received", "en", "Juvo BD: we received your payment of BDT {{Amount}} for order {{OrderNumber}}. Thank you!"),
        Sms("payment.received", "bn", "জুভো বিডি: অর্ডার {{OrderNumber}}-এর জন্য ৳{{Amount}} পেমেন্ট পেয়েছি। ধন্যবাদ!"),
        Email("payment.received", "en", "Payment received for order {{OrderNumber}}", "Hello {{Name}},\n\nWe received your payment of BDT {{Amount}} for order {{OrderNumber}}.\n\nJuvo BD"),
        Email("payment.received", "bn", "অর্ডার {{OrderNumber}}-এর পেমেন্ট পেয়েছি", "হ্যালো {{Name}},\n\nঅর্ডার {{OrderNumber}}-এর জন্য ৳{{Amount}} পেমেন্ট পেয়েছি।\n\nজুভো বিডি"),
        Sms("payment.failed", "en", "Juvo BD: your payment for order {{OrderNumber}} did not go through. You can try again from your order page."),
        Sms("payment.failed", "bn", "জুভো বিডি: অর্ডার {{OrderNumber}}-এর পেমেন্ট সম্পন্ন হয়নি। আপনার অর্ডার পেজ থেকে আবার চেষ্টা করতে পারেন।"),
        Sms("payment.under-review", "en", "Juvo BD: we received your transfer reference for order {{OrderNumber}} and are checking it."),
        Sms("payment.under-review", "bn", "জুভো বিডি: অর্ডার {{OrderNumber}}-এর ট্রান্সফার রেফারেন্স পেয়েছি, যাচাই করছি।"),
        Sms("payment.rejected", "en", "Juvo BD: we could not verify your payment for order {{OrderNumber}}. Please call us."),
        Sms("payment.rejected", "bn", "জুভো বিডি: অর্ডার {{OrderNumber}}-এর পেমেন্ট যাচাই করা যায়নি। অনুগ্রহ করে আমাদের কল করুন।"),
        Sms("payment.refunded", "en", "Juvo BD: BDT {{Amount}} was refunded for order {{OrderNumber}}."),
        Sms("payment.refunded", "bn", "জুভো বিডি: অর্ডার {{OrderNumber}}-এর ৳{{Amount}} ফেরত দেওয়া হয়েছে।"),
        Email("payment.refunded", "en", "Refund for order {{OrderNumber}}", "Hello {{Name}},\n\nBDT {{Amount}} was refunded for order {{OrderNumber}}.\n\nJuvo BD"),
        Email("payment.refunded", "bn", "অর্ডার {{OrderNumber}}-এর রিফান্ড", "হ্যালো {{Name}},\n\nঅর্ডার {{OrderNumber}}-এর ৳{{Amount}} ফেরত দেওয়া হয়েছে।\n\nজুভো বিডি"),

        Sms("invoice.ready", "en", "Juvo BD: the invoice {{InvoiceNumber}} for order {{OrderNumber}} is ready in your order page."),
        Sms("invoice.ready", "bn", "জুভো বিডি: অর্ডার {{OrderNumber}}-এর ইনভয়েস {{InvoiceNumber}} আপনার অর্ডার পেজে তৈরি।"),
        Email("invoice.ready", "en", "Invoice {{InvoiceNumber}} for order {{OrderNumber}}", "Hello {{Name}},\n\nThe invoice {{InvoiceNumber}} for your order {{OrderNumber}} is ready. You can download it from your order page.\n\nJuvo BD"),
        Email("invoice.ready", "bn", "অর্ডার {{OrderNumber}}-এর ইনভয়েস {{InvoiceNumber}}", "হ্যালো {{Name}},\n\nআপনার অর্ডার {{OrderNumber}}-এর ইনভয়েস {{InvoiceNumber}} তৈরি হয়েছে। অর্ডার পেজ থেকে ডাউনলোড করতে পারবেন।\n\nজুভো বিডি"),

        Sms("return.requested", "en", "Juvo BD: we received your {{Kind}} request {{ReturnNumber}}. We will review it soon."),
        Sms("return.requested", "bn", "জুভো বিডি: আপনার {{KindBn}} অনুরোধ {{ReturnNumber}} পেয়েছি। শীঘ্রই দেখব।"),
        Sms("return.approved", "en", "Juvo BD: your {{Kind}} request {{ReturnNumber}} is approved. We will arrange the pickup."),
        Sms("return.approved", "bn", "জুভো বিডি: আপনার {{KindBn}} অনুরোধ {{ReturnNumber}} অনুমোদিত হয়েছে। আমরা পিকআপের ব্যবস্থা করব।"),
        Sms("return.rejected", "en", "Juvo BD: your {{Kind}} request {{ReturnNumber}} could not be approved. Please call us for details."),
        Sms("return.rejected", "bn", "জুভো বিডি: আপনার {{KindBn}} অনুরোধ {{ReturnNumber}} অনুমোদন করা যায়নি। বিস্তারিত জানতে কল করুন।"),
        Sms("return.pickup-scheduled", "en", "Juvo BD: the pickup for your {{Kind}} {{ReturnNumber}} is scheduled."),
        Sms("return.pickup-scheduled", "bn", "জুভো বিডি: আপনার {{KindBn}} {{ReturnNumber}}-এর পিকআপ নির্ধারিত হয়েছে।"),
        Sms("return.received", "en", "Juvo BD: we received the parcel of your {{Kind}} {{ReturnNumber}} and will check it."),
        Sms("return.received", "bn", "জুভো বিডি: আপনার {{KindBn}} {{ReturnNumber}}-এর পার্সেল পেয়েছি, যাচাই করব।"),
        Sms("return.refunded", "en", "Juvo BD: your refund for {{Kind}} {{ReturnNumber}} was sent."),
        Sms("return.refunded", "bn", "জুভো বিডি: আপনার {{KindBn}} {{ReturnNumber}}-এর রিফান্ড পাঠানো হয়েছে।"),
        Sms("return.exchanged", "en", "Juvo BD: the replacement for your {{Kind}} {{ReturnNumber}} is on its way."),
        Sms("return.exchanged", "bn", "জুভো বিডি: আপনার {{KindBn}} {{ReturnNumber}}-এর বদলি পণ্য পাঠানো হচ্ছে।"),
        Sms("return.refund-rejected", "en", "Juvo BD: we could not complete your {{Kind}} {{ReturnNumber}} after checking the parcel. Please call us."),
        Sms("return.refund-rejected", "bn", "জুভো বিডি: পার্সেল যাচাইয়ের পর আপনার {{KindBn}} {{ReturnNumber}} সম্পন্ন করা যায়নি। অনুগ্রহ করে কল করুন।"),

        Sms("ticket.received", "en", "Juvo BD: we received your request {{TicketNumber}}. Our team will reply soon."),
        Sms("ticket.received", "bn", "জুভো বিডি: আপনার অনুরোধ {{TicketNumber}} পেয়েছি। আমাদের টিম শীঘ্রই উত্তর দেবে।"),
        Email("ticket.received", "en", "We received your request {{TicketNumber}}", "Hello {{Name}},\n\nWe received your request {{TicketNumber}} ({{Subject}}). Our team will reply soon.\n\nJuvo BD"),
        Email("ticket.received", "bn", "আপনার অনুরোধ {{TicketNumber}} পেয়েছি", "হ্যালো {{Name}},\n\nআপনার অনুরোধ {{TicketNumber}} ({{Subject}}) পেয়েছি। আমাদের টিম শীঘ্রই উত্তর দেবে।\n\nজুভো বিডি"),
        Sms("ticket.replied", "en", "Juvo BD: we replied to your request {{TicketNumber}}. Check your email or your account, or call us."),
        Sms("ticket.replied", "bn", "জুভো বিডি: আপনার অনুরোধ {{TicketNumber}}-এর উত্তর দিয়েছি। ইমেইল বা আপনার অ্যাকাউন্ট দেখুন, অথবা আমাদের কল করুন।"),
        Email("ticket.replied", "en", "Reply to your request {{TicketNumber}}", "Hello {{Name}},\n\n{{Reply}}\n\nRequest {{TicketNumber}}: {{Subject}}\nJuvo BD"),
        Email("ticket.replied", "bn", "আপনার অনুরোধ {{TicketNumber}}-এর উত্তর", "হ্যালো {{Name}},\n\n{{Reply}}\n\nঅনুরোধ {{TicketNumber}}: {{Subject}}\nজুভো বিডি"),
        Sms("ticket.resolved", "en", "Juvo BD: your request {{TicketNumber}} is resolved. If you still need help, reply from your account or call us."),
        Sms("ticket.resolved", "bn", "জুভো বিডি: আপনার অনুরোধ {{TicketNumber}} সমাধান হয়েছে। আরও সাহায্য লাগলে অ্যাকাউন্ট থেকে উত্তর দিন বা আমাদের কল করুন।"),
        Email("ticket.resolved", "en", "Your request {{TicketNumber}} is resolved", "Hello {{Name}},\n\nYour request {{TicketNumber}} ({{Subject}}) is resolved. If you still need help, reply from your account or call us.\n\nJuvo BD"),
        Email("ticket.resolved", "bn", "আপনার অনুরোধ {{TicketNumber}} সমাধান হয়েছে", "হ্যালো {{Name}},\n\nআপনার অনুরোধ {{TicketNumber}} ({{Subject}}) সমাধান হয়েছে। আরও সাহায্য লাগলে অ্যাকাউন্ট থেকে উত্তর দিন বা আমাদের কল করুন।\n\nজুভো বিডি"),

        Sms("marketing.promotion", "en", "Juvo BD: {{Message}}"),
        Sms("marketing.promotion", "bn", "জুভো বিডি: {{Message}}"),

        // Rewards (module AB): points, referral, gift cards. All transactional (they tell the customer about their own balance), so no marketing consent is needed.
        Sms("loyalty.points-earned", "en", "Juvo BD: you earned {{Points}} points on order {{OrderNumber}}. Your balance is {{Balance}} points."),
        Sms("loyalty.points-earned", "bn", "জুভো বিডি: অর্ডার {{OrderNumber}}-এ আপনি {{Points}} পয়েন্ট পেয়েছেন। আপনার মোট পয়েন্ট {{Balance}}।"),
        Email("loyalty.points-earned", "en", "You earned {{Points}} points", "Hello {{Name}},\n\nYou earned {{Points}} points on order {{OrderNumber}}. Your balance is {{Balance}} points. You can use them at checkout.\n\nJuvo BD"),
        Email("loyalty.points-earned", "bn", "আপনি {{Points}} পয়েন্ট পেয়েছেন", "হ্যালো {{Name}},\n\nঅর্ডার {{OrderNumber}}-এ আপনি {{Points}} পয়েন্ট পেয়েছেন। আপনার মোট পয়েন্ট {{Balance}}। চেকআউটে এগুলো ব্যবহার করতে পারবেন।\n\nজুভো বিডি"),
        Sms("loyalty.referral-reward", "en", "Juvo BD: your friend {{Friend}} received the first order, so BDT {{Amount}} was added to your store credit."),
        Sms("loyalty.referral-reward", "bn", "জুভো বিডি: আপনার বন্ধু {{Friend}} প্রথম অর্ডার পেয়েছেন, তাই আপনার স্টোর ক্রেডিটে ৳{{Amount}} যোগ হয়েছে।"),
        Email("loyalty.referral-reward", "en", "Your referral reward", "Hello {{Name}},\n\nYour friend {{Friend}} received the first order, so BDT {{Amount}} was added to your store credit. Thank you for spreading the word.\n\nJuvo BD"),
        Email("loyalty.referral-reward", "bn", "আপনার রেফারেল পুরস্কার", "হ্যালো {{Name}},\n\nআপনার বন্ধু {{Friend}} প্রথম অর্ডার পেয়েছেন, তাই আপনার স্টোর ক্রেডিটে ৳{{Amount}} যোগ হয়েছে। ধন্যবাদ।\n\nজুভো বিডি"),
        Sms("loyalty.referral-welcome", "en", "Juvo BD: welcome! BDT {{Amount}} was added to your store credit for joining with a referral code."),
        Sms("loyalty.referral-welcome", "bn", "জুভো বিডি: স্বাগতম! রেফারেল কোডে যোগ দেওয়ায় আপনার স্টোর ক্রেডিটে ৳{{Amount}} যোগ হয়েছে।"),
        Email("loyalty.referral-welcome", "en", "Welcome reward", "Hello {{Name}},\n\nWelcome to Juvo BD! BDT {{Amount}} was added to your store credit for joining with a referral code. You can use it at checkout.\n\nJuvo BD"),
        Email("loyalty.referral-welcome", "bn", "স্বাগতম পুরস্কার", "হ্যালো {{Name}},\n\nজুভো বিডিতে স্বাগতম! রেফারেল কোডে যোগ দেওয়ায় আপনার স্টোর ক্রেডিটে ৳{{Amount}} যোগ হয়েছে। চেকআউটে ব্যবহার করতে পারবেন।\n\nজুভো বিডি"),
        Sms("loyalty.gift-card-received", "en", "Juvo BD: you received a gift card of BDT {{Amount}}. Code: {{Code}}. Valid until {{Expiry}}. Redeem it in your account or at checkout."),
        Sms("loyalty.gift-card-received", "bn", "জুভো বিডি: আপনি ৳{{Amount}} মূল্যের একটি গিফট কার্ড পেয়েছেন। কোড: {{Code}}। মেয়াদ: {{Expiry}}। আপনার অ্যাকাউন্টে বা চেকআউটে ব্যবহার করুন।"),
        Email("loyalty.gift-card-received", "en", "You received a Juvo BD gift card", "Hello {{Name}},\n\nYou received a gift card of BDT {{Amount}}.\nCode: {{Code}}\nValid until: {{Expiry}}\n\nRedeem it in your account (Rewards) or enter the code at checkout.\n\nJuvo BD"),
        Email("loyalty.gift-card-received", "bn", "আপনি একটি জুভো বিডি গিফট কার্ড পেয়েছেন", "হ্যালো {{Name}},\n\nআপনি ৳{{Amount}} মূল্যের একটি গিফট কার্ড পেয়েছেন।\nকোড: {{Code}}\nমেয়াদ: {{Expiry}}\n\nআপনার অ্যাকাউন্টে (রিওয়ার্ডস) ব্যবহার করুন বা চেকআউটে কোডটি দিন।\n\nজুভো বিডি"),
        Sms("loyalty.points-expiring", "en", "Juvo BD: {{Points}} of your points expire on {{Date}}. Use them at checkout before then."),
        Sms("loyalty.points-expiring", "bn", "জুভো বিডি: আপনার {{Points}} পয়েন্টের মেয়াদ {{Date}} তারিখে শেষ হবে। তার আগে চেকআউটে ব্যবহার করুন।"),
        Email("loyalty.points-expiring", "en", "Your points expire soon", "Hello {{Name}},\n\n{{Points}} of your points expire on {{Date}}. Use them at checkout before then.\n\nJuvo BD"),
        Email("loyalty.points-expiring", "bn", "আপনার পয়েন্টের মেয়াদ শেষ হচ্ছে", "হ্যালো {{Name}},\n\nআপনার {{Points}} পয়েন্টের মেয়াদ {{Date}} তারিখে শেষ হবে। তার আগে চেকআউটে ব্যবহার করুন।\n\nজুভো বিডি"),
    ];

    public static TemplateDefinition? Find(string? code) => Definitions.FirstOrDefault(d => string.Equals(d.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static TemplateDefault? DefaultFor(string code, NotificationChannel channel, string language) =>
        Defaults.FirstOrDefault(d => d.Code == code && d.Channel == channel && d.Language == language);

    /// <summary>Replaces <c>{{Name}}</c> placeholders with the values (a missing value becomes empty).</summary>
    public static string Render(string text, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : string.Empty);

    /// <summary>The placeholders a text uses.</summary>
    public static IReadOnlyList<string> PlaceholdersIn(string? text) =>
        string.IsNullOrEmpty(text) ? [] : [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
}
