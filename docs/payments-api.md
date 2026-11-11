# Payments, invoices and notifications API (Step 9-a backend)

Contract for modules I (payments), U (tax and invoices) and K (notifications). Base path `/api/v1`. Errors are RFC 9457 ProblemDetails with `code` and `correlationId`. Lists use `page, pageSize (max 100), sort, search` and answer `{items,total,page,pageSize}`. Money is BDT, `decimal(18,2)`. Decisions and reasons: `docs/assumptions.md` section Q.

## 1. What the checkout page must do (sandbox flow)

1. Show the methods from `GET /store/payment-methods?country=BD&amount=<grand total>`. Each has `code`, `name`, `nameBn`, `kind` (`Cash`, `Gateway`, `Manual`), `available`, `reason` (why not), `instructions` (the account details of a manual transfer). Disabled methods are not listed. The quote and checkout (`/store/checkout/quote`, `/store/checkout`) use the same list.
2. Place the order with `paymentMethod` = the code. Keep the **tracking token** from the checkout answer: it proves the order for every payment call (a signed-in customer needs no token).
3. For an online method (`SANDBOX` now; `BKASH`, `NAGAD`, `SSLCOMMERZ`, `AAMARPAY`, `STRIPE`, `PAYPAL` once configured in Step 14): `POST /store/payments/initiate`
   ```json
   { "orderNumber": "JV-HO-2611-000123", "trackingToken": "…", "method": null, "returnUrl": "/checkout/return" }
   ```
   `method` is optional (default: the order's method; a different online method switches the order, used for a retry). `returnUrl` is a path on the site (starts with `/`) or an address on the configured front-end origin, anything else is `PAYMENT.RETURN_URL_INVALID`. The answer is a **payment session**:
   ```json
   { "paymentId": "…", "orderNumber": "…", "kind": "Online", "method": "SANDBOX", "amount": 1060.00, "currency": "BDT", "status": "Pending",
     "redirectUrl": "…", "expiresAt": "…", "returnUrl": "/checkout/return", "failureReason": null,
     "orderStatus": "Placed", "orderPaymentStatus": "Pending", "canRetry": false, "sandboxSessionId": "sbx_…" }
   ```
   Asking again while the page is still valid returns the same payment (idempotent).
4. Send the browser to `redirectUrl`. For the sandbox it is either the API's own page `GET /store/payments/sandbox/pay/{sessionId}` (three buttons, plain HTML) or, when `Payments:Sandbox:PayPageUrl` is set, the front end's page. A front-end page uses the JSON form:
   - `GET /store/payments/sandbox/sessions/{sessionId}` returns `{sessionId, orderNumber, amount, currency, status (Pending|Succeeded|Failed|Cancelled|Expired), expiresAt, returnUrl}`.
   - `POST /store/payments/sandbox/sessions/{sessionId}/complete` with `{"outcome":"success"|"fail"|"cancel","sendWebhook":true}` records what the customer chose, makes the sandbox send the shop a **signed webhook** (unless `sendWebhook` is false), and returns `{sessionId, outcome, returnUrl, paymentStatus}`. A page can be used once (`PAYMENT.INVALID_TRANSITION` afterwards).
5. When the browser comes back to `returnUrl`, call `POST /store/payments/{paymentId}/confirm?token=<tracking token>` (or header `X-Tracking-Token`). The shop asks the gateway server to server; only a confirmed success makes the payment `Paid`. Nothing in the return URL is trusted. A payment the gateway still calls pending stays `Pending`.
6. Poll `GET /store/payments/{paymentId}/status?token=…` (reads the shop's own records, cheap) until `status` is no longer `Pending`. Outcomes:
   - `status = Paid` (and normally `orderStatus = Confirmed`, because paid, unheld orders confirm themselves; setting `Payments.AutoConfirmPaidOrders`).
   - `status = Failed`: `canRetry` is true; call `initiate` again (a NEW payment is made; the failed one stays in the history). The 30 minute hold (BR-D2) keeps running: after it the order is cancelled and `initiate` answers `PAYMENT.ORDER_NOT_PAYABLE`.
   - `status = Cancelled` (the customer backed out): the order stays open, `canRetry` is true.
7. **Manual bank or mobile transfer** (`MANUAL`, off until the owner writes the account details): show `instructions`, then `POST /store/payments/manual` with `{orderNumber|orderId, trackingToken, reference, payerName}`; the payment and the order's payment track become `UnderReview`. Optionally `POST /store/payments/{paymentId}/proof` (multipart field `file`: JPEG, PNG, WebP or PDF, up to 5 MB, query `token`). Staff then approve or reject.
8. **Cash on delivery**: nothing to pay online. Above the advance threshold the order carries `advanceRequired`; staff record it (section 3).

Webhooks are never called by the browser. A real provider calls `POST /store/payments/webhooks/{provider}` (section 4).

## 2. Customer and public endpoints

| Method and path | Auth | Purpose |
| --- | --- | --- |
| `GET /store/payment-methods` | public | Methods for `country` and `amount`. |
| `POST /store/payments/initiate` | token or customer | Start or resume an online payment. Rate limited (checkout policy). |
| `GET /store/payments/{id}/status` | token or customer | Payment session (status, order status, `canRetry`). |
| `POST /store/payments/{id}/confirm` | token or customer | Browser return: server-side check with the gateway. |
| `POST /store/payments/manual` | token or customer | Report a manual transfer (UnderReview). |
| `POST /store/payments/{id}/proof` | token or customer | Upload the proof file. |
| `POST /store/payments/webhooks/{provider}` | provider signature | Gateway webhook. |
| `GET /store/payments/sandbox/pay/{sessionId}` | public | Sandbox pay page (HTML). |
| `GET /store/payments/sandbox/sessions/{sessionId}` | public | Sandbox session (JSON). |
| `POST /store/payments/sandbox/sessions/{sessionId}/complete` | public | Sandbox decision (success, fail, cancel). |
| `GET /me/orders/{id}/invoice` | customer | Invoice JSON of my order (issued when the order is confirmed). |
| `GET /me/orders/{id}/invoice/pdf?lang=en\|bn` | customer | Invoice PDF. |
| `GET /store/orders/invoice?number=&token=` | tracking token | Invoice JSON for a guest. |
| `GET /store/orders/invoice/pdf?number=&token=&lang=` | tracking token | Invoice PDF for a guest. |
| `PUT /me/notification-language` | customer | `{ "language": "en"\|"bn" }` language of my messages (204). |

"token or customer": the order is proven by the tracking token (query `token`, body `trackingToken`, or header `X-Tracking-Token`) or by being signed in as the order's customer. A wrong, missing or foreign token answers 404 (`PAYMENT.NOT_FOUND` or `INVOICE.NOT_FOUND`), never a hint that the order exists.

## 3. Staff endpoints

Every endpoint names one permission; the service proves it again in the payment's own branch (another branch's payment is 404 and audit-logged; a missing permission in the own branch is 403).

| Method and path | Permission | Purpose |
| --- | --- | --- |
| `GET /payments` | `payments.payment.view` | List. Filters `status` (UnderReview = transfer queue), `method`, `kind`, `branchId`, `from`, `to`; search order number, transfer reference, gateway transaction id. |
| `GET /payments/{id}` | `payments.payment.view` | Payment with events, refunds, `nextActions` (approve, reject, refund, settle). |
| `GET /payments/orders/{orderId}` | `payments.payment.view` | Payments of one order. |
| `POST /payments/{id}/approve` | `payments.payment.verify` | UnderReview to Paid. Body `{note}`. |
| `POST /payments/{id}/reject` | `payments.payment.verify` | UnderReview to Rejected; the order is cancelled and its stock released. Body `{reason}`. |
| `POST /payments/orders/{orderId}/advance` | `payments.payment.record` | Record an advance: `{amount, method (CASH, BKASH…), reference}`. Counts toward `advanceRequired`; once covered, confirm works without `advanceReceived`. |
| `POST /payments/orders/{orderId}/cod-collect` | `payments.payment.record` | Cash collected by the courier: `{amount?, reference}` (amount must equal total minus advance). |
| `POST /payments/orders/{orderId}/cod-settle` | `couriers.cod.settle` | Cash handed to the shop: `{reference}`; payment and order track become Settled. |
| `GET /payments/ledger` | `payments.ledger.view` | Money in and out (`from`, `to`, `branchId`), per method, COD collected, settled and outstanding, under review, pending refunds. |
| `GET /payments/refunds` | `payments.refund.view` | Refunds. Filters `status`, `branchId`, `orderId`, dates. |
| `POST /payments/{id}/refunds` | `payments.refund.create` | `{amount, mode: Gateway\|Manual, reason}`. Gateway: sent at once. Manual: Pending. |
| `POST /payments/refunds/{refundId}/complete` | `payments.refund.approve` | Finish a Pending refund: `{reference}`. Issues the credit note. |
| `POST /payments/refunds/{refundId}/fail` | `payments.refund.approve` | A Pending refund did not happen: `{reason}`; the money is refundable again. |
| `GET /payments/settings` | `payments.gateway.view` | Methods (enabled, available, reason), `codMaxAmount`, `manualInstructions`, `autoConfirmPaid`. |
| `PUT /payments/settings` | `payments.gateway.edit` | `{methods:[{code,enabled}], codMaxAmount (0 = none), manualInstructions, autoConfirmPaid}`; every field optional. |
| `GET /billing/tax-rules` | `tax.rule.view` | Tax rules. |
| `POST /billing/tax-rules` | `tax.rule.edit` | Create `{code,name,ratePercent,isInclusive:true,appliesToDelivery,isVerified,note}`. |
| `PUT /billing/tax-rules/{id}` | `tax.rule.edit` | Change. |
| `POST /billing/tax-rules/{id}/activate` | `tax.rule.edit` | `{acknowledgeUnverified}`; activating an unverified rule needs `true`. Any other rule becomes inactive. |
| `POST /billing/tax-rules/{id}/deactivate` | `tax.rule.edit` | Switch off. |
| `GET /billing/invoices`, `/{id}` | `tax.invoice.view` | List (search number, order, mobile, name) and detail with lines, tax, credit notes. |
| `GET /billing/invoices/{id}/pdf?lang=` | `tax.invoice.download` | PDF. |
| `POST /billing/orders/{orderId}/invoice` | `tax.invoice.issue` | Issue (or return) the invoice of a confirmed order. |
| `GET /billing/credit-notes`, `/{id}`, `/{id}/pdf` | `tax.credit-note.view` | Credit notes. |
| `GET /notifications/templates?code=` | `notifications.template.view` | All templates (built-in and edited). |
| `PUT /notifications/templates/{code}/{channel}/{language}` | `notifications.template.edit` | `{subject, body, isActive}`; new version. Channel `Sms`, `Email`, `WhatsApp`; language `en`, `bn`. |
| `GET /notifications/templates/{code}/{channel}/{language}/history` | `notifications.template.view` | Versions, newest first (0 = built-in). |
| `POST /notifications/templates/preview` | `notifications.template.view` | `{code, channel, language, subject?, body?, variables?}` rendered with sample values. |
| `GET /notifications/logs` | `notifications.log.view` | Delivery log; filters `status`, `channel`, `template`, `recipient`, dates. |
| `GET /notifications/logs/{id}` | `notifications.log.view` | One message. |
| `POST /notifications/logs/{id}/resend` | `notifications.log.retry` | Resend a Failed or DeadLetter message. |
| `GET/PUT /notifications/channels` | `notifications.template.view` / `notifications.channel.edit` | `{smsEnabled, emailEnabled, whatsAppEnabled, defaultLanguage}`. |

Default roles: Super Admin all; Manager `payments.payment.*`, `payments.refund.*`, `tax.*`, `notifications.*`; Accountant `payments.payment.view|verify`, `payments.refund.view`, `payments.ledger.*`, `tax.*` (the SRS matrix: no refund create or approve); Order Manager `payments.payment.view|record`, `tax.invoice.view|download|issue`, `notifications.log.view`; Support `payments.payment.view`, `tax.invoice.view|download`, `notifications.log.view`. Existing databases keep their stored role permissions: add the new ones in the role screen.

## 4. Webhook

`POST /store/payments/webhooks/{provider}` (`SANDBOX`; real providers in Step 14). The body is read raw. Header `Juvo-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "t.body" with the provider secret>`. The sandbox event body:
```json
{ "id": "evt_…", "type": "payment.succeeded" | "payment.failed" | "payment.cancelled" | "refund.completed", "sessionId": "sbx_…", "amount": 1060.00, "currency": "BDT", "created": 1789000000 }
```
Answers: `200 {status, paymentStatus}` with `status` = `processed`, `duplicate` (event id seen before: nothing changed), `ignored` (unknown session, or the payment cannot take this event), `not-confirmed` (signed "paid" that the gateway does not confirm: nothing changed), `amount-mismatch` (the amount differs: nothing changed, an event is written); `401 PAYMENT.INVALID_SIGNATURE`; `400 PAYMENT.WEBHOOK_EXPIRED` (timestamp more than 5 minutes off) or `PAYMENT.WEBHOOK_MALFORMED`; `404` unknown provider; `409 PAYMENT.NOT_CONFIGURED` for a slot without credentials; `409 PAYMENT.CONCURRENT_UPDATE` (the provider should retry). Configure the sandbox secret with `Payments:Sandbox:WebhookSecret` (empty: random per process; the sandbox signs its own webhooks).

## 5. Status tables

Payment status (same table as the order's payment track; `Payment` rows and `PaymentEvents` are append-only history):

| From | To |
| --- | --- |
| Pending | Paid, Failed, Cancelled, UnderReview, CollectedByCourier |
| UnderReview | Paid, Rejected |
| Paid | PartiallyRefunded, Refunded |
| PartiallyRefunded | Refunded |
| CollectedByCourier | Settled, PartiallyRefunded, Refunded |
| Settled | PartiallyRefunded, Refunded |
| Failed | Pending (order track retry), Cancelled |
| Cancelled, Rejected, Refunded | none |

A retry after Failed is a new payment row. A payment becomes PartiallyRefunded or Refunded only when a refund COMPLETES; money reserved for a Pending refund counts against `refundable` at once (`refundPendingAmount`). Kinds: `Online`, `Manual`, `Cod`, `Advance`. Refund: `Pending` then `Completed` or `Failed` (final). Modes: `Gateway`, `Manual`. Notification log: `Pending`, `Sent`, `Failed` (retrying), `DeadLetter`, `Skipped`.

## 6. Error codes

| Code | Status | Meaning |
| --- | --- | --- |
| `PAYMENT.NOT_FOUND` | 404 | No such payment, or the token does not prove the order. |
| `PAYMENT.ORDER_NOT_PAYABLE` | 409 | The order is not an open online-payment order (cash on delivery, cancelled, timed out). |
| `PAYMENT.ALREADY_PAID` | 409 | Already paid or being checked. |
| `PAYMENT.METHOD_UNAVAILABLE` | 409 | Not an online method, switched off, or not available for this country or amount. |
| `PAYMENT.NOT_CONFIGURED` | 409 | A provider slot without credentials. |
| `PAYMENT.GATEWAY_ERROR` | 409 | The gateway refused or could not be reached. |
| `PAYMENT.AMOUNT_MISMATCH` | 409 | Gateway amount differs, wrong cash or advance amount. |
| `PAYMENT.INVALID_SIGNATURE`, `PAYMENT.WEBHOOK_EXPIRED`, `PAYMENT.WEBHOOK_MALFORMED` | 401, 400 | Webhook refused. |
| `PAYMENT.RETURN_URL_INVALID` | 400 | The return address is not on the site. |
| `PAYMENT.REFERENCE_REQUIRED`, `PAYMENT.REFERENCE_REUSED` | 400, 409 | Manual transfer reference missing, or already used for another payment. |
| `PAYMENT.PROOF_INVALID` | 400 | Proof file type or size. |
| `PAYMENT.NOT_UNDER_REVIEW` | 409 | Approve, reject or proof on a payment that is not UnderReview. |
| `PAYMENT.NOT_COD`, `PAYMENT.NOTHING_TO_COLLECT` | 409 | Cash or advance action on the wrong order or at the wrong time. |
| `PAYMENT.INVALID_TRANSITION` | 409 | The state machine refuses the move (also: sandbox page already used, settle before collect). |
| `PAYMENT.NOTHING_PAID` | 409 | Refund of a payment that holds no money. |
| `PAYMENT.REFUND_EXCEEDS_PAID` | 409 | Refund above what is left to refund (including pending refunds). |
| `PAYMENT.REFUND_NOT_PENDING`, `PAYMENT.REFUND_NOT_FOUND` | 409, 404 | Complete or fail a refund that is finished, or unknown. |
| `PAYMENT.REFUND_FAILED` | 409 | The gateway refused the refund. A refund of a return that fails leaves the return unrefunded. |
| `PAYMENT.CONCURRENT_UPDATE` | 409 | Changed at the same moment; reload and try again. |
| `INVOICE.NOT_FOUND`, `INVOICE.CREDIT_NOTE_NOT_FOUND` | 404 | |
| `INVOICE.ORDER_NOT_INVOICEABLE` | 409 | The order is not confirmed yet. |
| `INVOICE.TAX_RULE_NOT_FOUND`, `INVOICE.TAX_RULE_CODE_TAKEN`, `INVOICE.TAX_RULE_UNVERIFIED`, `INVOICE.EXCLUSIVE_NOT_SUPPORTED` | 404, 409, 409, 400 | Tax rule rules. |
| `INVOICE.PDF_FAILED` | 422 | The PDF could not be drawn. |
| `NOTIFY.TEMPLATE_NOT_FOUND`, `NOTIFY.TEMPLATE_INVALID`, `NOTIFY.UNKNOWN_PLACEHOLDER`, `NOTIFY.LANGUAGE_INVALID` | 404, 400 | Template edit refused. |
| `NOTIFY.LOG_NOT_FOUND`, `NOTIFY.NOT_RESENDABLE` | 404, 409 | Resend refused. |

Also `ORDER.PAYMENT_METHOD_UNAVAILABLE` and `ORDER.COD_NOT_AVAILABLE` at checkout (method off, not configured, or over the cash-on-delivery limit).

## 7. Invoices, credit notes, tax

- One invoice per order, issued when the order is **Confirmed** (an outbox handler issues it and sends `invoice.ready`; the customer's first download and `POST /billing/orders/{id}/invoice` issue it too). Number `INV-<branch prefix>-<yyMM>-<6 digits>`, gap-free per branch and month (counter locked in the same transaction as the invoice). Invoices and their lines are append-only: any update or delete is refused by the persistence layer.
- A credit note `CN-<prefix>-<yyMM>-<6 digits>` is issued for every COMPLETED refund (gateway, manual, or the refund of a return), linked to the invoice (issued first if the order had none). Its tax is the refund's share of the invoice tax.
- Tax: prices are tax-inclusive and no total ever changes. With no active rule the tax is zero. An active rule (`VAT15` is seeded inactive and unverified) makes the order store `taxRuleCode`, `taxRatePercent` and the tax inside the total; the invoice shows tax per line and in total. Orders placed before a rule was switched on stay without tax.
- PDF: `GET .../pdf?lang=en|bn`, A4, brand colours, seller details from the settings `Business.Name`, `Business.Address`, `Business.Phone`, `Business.Email`, `Business.TaxId` (fall back to the branch). Bengali text is shaped correctly (conjuncts, vowel signs). Files are about 0.6 to 0.8 MB because the full fonts are embedded.

## 8. Notifications

Template codes (all with English and Bengali SMS; many with email; a few with WhatsApp): `auth.otp`, `order.placed`, `order.confirmed`, `order.packed`, `order.handed-to-courier`, `order.out-for-delivery`, `order.delivered`, `order.cancelled`, `payment.received`, `payment.failed`, `payment.under-review`, `payment.rejected`, `payment.refunded`, `invoice.ready`, `return.requested`, `return.approved`, `return.rejected`, `return.pickup-scheduled`, `return.received`, `return.refunded`, `return.exchanged`, `return.refund-rejected`, `marketing.promotion` (marketing: needs consent). Placeholders are written `{{OrderNumber}}`; each code declares what it may use (`GET /notifications/templates` returns `variables`).

Order and return events go to the outbox; the handlers queue one log row per channel (`Pending`) and a `notification.deliver` outbox message; delivery retries with exponential backoff (outbox settings) up to `Notifications:Delivery:MaxAttempts` (5), then the row is `DeadLetter` and staff resend it. Language: the customer's choice, else `defaultLanguage`. Email goes out when the customer has an address; WhatsApp only when switched on. Marketing needs the latest consent of the channel (SMS consent for SMS and WhatsApp), otherwise the row is `Skipped` with the reason.

## 9. Settings in `PlatformSettings`

`Payments.<CODE>.Enabled` (COD and SANDBOX default true, others false), `Payments.Cod.MaxAmount` (0 = no limit), `Payments.Manual.Instructions`, `Payments.AutoConfirmPaidOrders` (true), `Notifications.SmsEnabled` (true), `Notifications.EmailEnabled` (true), `Notifications.WhatsAppEnabled` (false), `Notifications.DefaultLanguage` (en), `Business.*`. Configuration: `Payments:Sandbox:Enabled|WebhookSecret|PayPageUrl|PublicApiBaseUrl|RefundOutcome`, `Notifications:Delivery:MaxAttempts`.
