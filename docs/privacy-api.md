# Privacy API: consent history, data export, anonymisation (FR-AF06, FR-E08, FR-E07, NFR-010)

Base `/api/v1/customers/{id}`. Staff only (JWT), global (customers are shared by all branches, so nothing here is branch-scoped; export and anonymisation read every branch's rows). Errors are RFC 9457 problem details with a stable `code`.

| Permission | Allows | Default roles |
| --- | --- | --- |
| `customers.customer.view` | consent history | as before (Super Admin, Manager, Order Manager, Support, Accountant) |
| `customers.privacy.export` | download a customer's data | Super Admin only |
| `customers.privacy.anonymise` | irreversible anonymisation | Super Admin only |

Manager is explicitly denied `customers.privacy.*` in `DefaultRoles` (its `customers.*` pattern would otherwise include it). Existing databases: the seeder adds the two codes to the catalogue and Super Admin holds every code, so nothing else is granted; an owner can still add them to a custom role in the role screen.

| Method and path | Permission | Success | Errors |
| --- | --- | --- | --- |
| `GET /consents/history` | customers.customer.view | 200 `ConsentHistory` | 401, 403, 404 `CUSTOMER.NOT_FOUND` |
| `POST /privacy/export` | customers.privacy.export | 200 `application/json` file download | 401, 403, 404, 429 `PRIVACY.EXPORT_LIMIT` |
| `POST /privacy/anonymise` | customers.privacy.anonymise | 200 `AnonymiseResult` | 400 `PRIVACY.REASON_REQUIRED`, 400 `PRIVACY.CONFIRM_MISMATCH`, 401, 403, 404, 409 `PRIVACY.STAFF_ACCOUNT`, 409 `PRIVACY.OPEN_ORDERS` |

## Consent history

The consent log (`ConsentLogs`, append-only, written by the customer's `PUT /me/consent` with source `customer-profile` and by the unsubscribe link with source `unsubscribe-link` or `unsubscribe:<campaign>`) already records every change, so no new table was added. The history is built from it.

```
ConsentHistory = { customerId, piiMasked, items: ConsentHistoryEntry[] }   // newest first
ConsentHistoryEntry = {
  id, type: "MarketingSms" | "MarketingEmail" | "PrivacyPolicy",
  channel: "Sms" | "Email" | "Policy",
  appliesTo: ["Sms","WhatsApp"] | ["Email"] | [],      // SMS consent also covers WhatsApp (there is no separate WhatsApp consent)
  oldValue: bool | null,                               // null = first record of that type
  newValue: bool,
  source: string,                                      // as stored
  sourceKind: "Customer" | "Staff" | "UnsubscribeLink" | "Import" | "Other",
  ipAddress: string | null,                            // partly hidden (203.0.*.*) unless the caller holds customers.customer.view-pii
  at: datetime }
```

`piiMasked` is true without `view-pii`. With `view-pii`, showing unmasked IP addresses writes a `Viewed` audit row (entity `Customer`), like the customer detail screen.

## Export

`POST /privacy/export` (body ignored) returns a JSON file (`Content-Disposition: attachment; filename="customer-data-<id>-<yyyymmdd>.json"`). It is generated on the fly and never stored (no signed link, no file to expire or leak). Sections:

`export` (version, generatedAt, customerId, note), `profile`, `addresses` (also removed ones, flagged), `consents` (`current`, `history` with IP), `orders` (each: `order` snapshot with contact and address, `lines`, `customerNotes`, `payments`, `refunds`), `invoices`, `returns`, `tickets` (with `messages`: customer or staff text, no staff names), `contactMessages` (matched by the customer's mobile in any written form or by e-mail), `wishlist`, `loyalty` (`account`, `points`, `credit`), `notifications` (channel, recipient, template, category, status, time; no subject or body), `marketing` (SMS and e-mail state, suppression by unsubscribe link, back-in-stock requests, campaign recipient rows), `signIns`.

Left out on purpose: staff-only notes (order internal notes, ticket internal notes, contact internal note, fraud review notes), staff names, message bodies, card data (none exists), password or OTP hashes.

Audit: one row per export, entity `CustomerPrivacy`, `EntityId` = customer id, action `Viewed`, user = the staff member, `NewValue` = `{"event":"export","bytes":N,"format":"json"}`. The content is never written to the audit log.

Rate limit: 10 exports per staff member per rolling hour, counted from those audit rows (so it is correct across servers); the 11th answers 429 `PRIVACY.EXPORT_LIMIT` (`ErrorType.TooManyRequests`, no Retry-After header).

## Anonymise

Request `{ "reason": "5 to 300 characters", "confirm": "<the customer id again>" }`. `confirm` may be in any letter case, with or without dashes. Both are required: 400 `PRIVACY.REASON_REQUIRED` / `PRIVACY.CONFIRM_MISMATCH` (field `reason` / `confirm`).

Refusals (nothing is changed):

- 409 `PRIVACY.STAFF_ACCOUNT`: the id is a staff user.
- 409 `PRIVACY.OPEN_ORDERS` (the message names what is open): orders not Delivered, Cancelled or Returned; orders with cash on delivery collected by the courier but not settled (or delivered COD still Pending); payments Under review; returns in Requested, Approved, PickupScheduled, PickedUp, Received or Inspected; refunds Pending (payment refunds and return refunds).
- 404 for an unknown id.

Result `AnonymiseResult = { customerId, alreadyAnonymised, anonymisedAt, scrubbed: counts, kept: string[], forfeitedPoints, forfeitedCredit }`. Calling it again for an anonymised customer answers 200 with `alreadyAnonymised: true`, zero counts and writes nothing (after the reason, confirm and staff checks). The whole action runs in one database transaction (`IUnitOfWork.ExecuteInTransactionAsync`); a failure leaves everything as it was.

What changes is listed in `docs/step12i-privacy-notes.md`. In short: the customer row, orders, shipments, returns, tickets, contact messages, notification logs and similar rows get neutral placeholders (`Anonymised`, `[removed on request]`, `[removed]`, a placeholder mobile `ANON` + 16 hex characters); addresses, wishlist, carts, sessions, OTP codes and back-in-stock requests are deleted; invoices, payments, refunds, stock movements and ledgers stay. The customer is marked Anonymised (`Customers.AnonymisedAt`) and inactive, so they cannot sign in. The original mobile number can sign in again and starts a brand new customer (the unique index holds the placeholder, not the number).

Audit: entity `CustomerPrivacy`, action `Updated`, `NewValue` = `{"event":"anonymise","reason":...,"orders":n,"tickets":n,"contacts":n}`; user = the staff member. No personal data is written.

## Customer API changes

None. `GET /customers/{id}` and the list show an anonymised customer with the placeholder values (`fullName` "Anonymised", mobile `ANON...`, inactive). Activating one by `POST /customers/{id}/activate` is possible but the person is gone; it only re-enables an empty shell and is not blocked (see notes).

## Per-customer fraud signals (FR-E09)

Already exposed by the fraud module: `GET /api/v1/fraud/customers/{customerId}/stats` (permission `fraud.flag.view`): orders, delivered, cancelled, returned or failed, open, delivered value, first order date and a 0 to 100 score. No new fraud logic was added. Gaps: no "many orders from one device with different numbers" view per customer (the checkout rules flag it per order); no per-customer list of flags.
