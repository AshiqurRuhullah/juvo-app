# Step 12i slice 1: customer data privacy tools, backend notes

Contract: `docs/privacy-api.md`. Code: `Juvo.Domain/Customers/PrivacyPlaceholders.cs`, `Customer.Anonymise`, `Juvo.Application/Privacy/*`, three routes in `StoreAdminEndpoints`. Migration `PrivacyTools` (adds `Customers.AnonymisedAt`, nothing else). Branch `feature/12i-privacy-backend`.

## What anonymisation scrubs and what it keeps

| Data | Treatment | Why |
| --- | --- | --- |
| Customer row | mobile = `ANON` + 16 hex, name = `Anonymised`, e-mail and language null, level Guest, not verified, no last login, inactive, `AnonymisedAt` set | The mobile column is unique and 20 characters wide; a placeholder frees the real number to register again (BR-006). Real numbers start with `+`, so no clash. |
| Orders | contact name, mobile, e-mail, ship name, phone, area, street, landmark, city, postal code, map pin, device id, client IP, duplicate fingerprint, tracking token hash, cancel reason scrubbed. Totals, taxes, status, history, lines, division, district, upazila and country stay | Books and delivery statistics need them (BRD 2.6, "order records are kept in anonymised form for legal and accounting needs"). Emptying the tracking hash stops guest tracking by token. |
| Order notes, fraud review notes | text replaced (author and time stay) | Free text about the person. Money events are in payments and history. |
| Invoices, credit notes | **kept as issued** (buyer name, mobile, e-mail, billing address) | Tax invoices are gap-free, append-only legal records. See owner decision O1. |
| Payments, refunds | amounts, method, status, dates and references stay; the payer name is removed | Books. |
| Shipments | delivery name, phone, address, note replaced | Snapshots of the person; courier cost and status stay. |
| Returns | mobile, name, reason text, pickup note, return notes replaced | Free text and contact data. |
| Tickets and their messages | contact fields and subject replaced, every message text replaced, customer author name replaced (staff names stay), attachments deleted (rows and files), status-history reasons replaced | "Free text on their tickets". Attachment pictures may show the person. |
| Contact messages | matched by mobile (any written form) or e-mail: name, mobile, e-mail, subject, text, internal note, ip hash replaced | Same person, guest channel. |
| Notification log | queued or failed sends stopped (status Skipped), recipient, subject, body, provider answer replaced | Bodies quote names and OTP codes. |
| Marketing | campaign recipient contact cleared, back-in-stock requests deleted | Contact data. |
| Loyalty | ledger reasons, gift card entry notes, referral notes, gift card recipient contact cleared; amounts and balances stay | Ledgers are money records. See O2 for the balance. |
| Addresses, wishlist, carts, refresh tokens (sessions), OTP codes | deleted | Convenience data only. |
| Sign-in history | identifier, IP, user agent replaced (rows stay) | Security log without the person. |
| Consent log | IP removed; type, value, source and time stay | Proof that a withdrawn consent was honoured. |
| Audit trail | for the entities Customer, Address, Order, Payment, Ticket, ReturnRequest, ContactMessage of this person, the stored old and new values are overwritten with `{"anonymised":true}`; who, what and when stay; `Viewed` rows untouched | The trail held names, numbers and addresses (Order is audited without ignoring them). Invoice audit rows are kept because invoices are kept. |
| Outbox | processed messages that mention the customer id get payload `{}` | Event payloads carried the mobile. Pending messages are left (handlers read fresh data). |
| Stock movements, COD settlements, courier events | untouched | No personal data, or books. |

Mechanics: the scrub uses set-based `ExecuteUpdate` / `ExecuteDelete` inside one transaction, which deliberately bypasses the append-only guard, the audit interceptor and domain events: these are the only code paths that edit append-only rows, and each only writes placeholders. The customer row itself is changed through the domain method inside the same transaction; the audit row that change creates is blanked afterwards, then one clean audit entry is written. Files of ticket attachments are deleted after the commit (best effort).

## Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| P1 | No `ConsentEvents` table; the existing `ConsentLogs` is the consent log and the history API derives old value, channel and source kind from it. | The log is already append-only and written by both consent paths; a second table would duplicate it. | Add columns for actor id if staff-made changes ever exist. |
| P2 | There is no staff or import path that changes consent today, so `Staff` and `Import` source kinds are recognised but never occur. | Not asked to add one. | A staff consent change would write a `ConsentLog` with a source starting `staff`. |
| P3 | WhatsApp has no consent of its own; the history shows `appliesTo: [Sms, WhatsApp]` for the SMS consent. | `NotificationService` sends WhatsApp only to customers who allowed SMS. | A new `ConsentType` plus its use in `NotificationService`. |
| P4 | Export is a direct JSON download, not a stored file with a signed link. | Simpler and safer: nothing persists, nothing to expire or leak. Size is small (one customer). | Add a zip or stored file later if exports grow large. |
| P5 | Export limit: 10 per staff member per hour from audit rows, constant `PrivacyService.ExportsPerHour`. | The global rate limiter runs before authentication and keys by IP, so a per-user rule cannot live there. | Change the constant or move it to a setting. |
| P6 | Permissions `customers.privacy.export` and `anonymise` exist for Super Admin only; Manager is denied the group. | Owner brief: anonymise Super Admin only; export kept to the same people (conservative). | Give `export` to a role in the role screen; edit `DefaultRoles` for new installs. |
| P7 | Open work refuses with 409 `PRIVACY.OPEN_ORDERS`: open orders, unsettled COD, payments under review, open returns, pending refunds. Open tickets do not block (they are scrubbed). | Owner brief. | `PrivacyService.OpenWorkAsync`. |
| P8 | Staff ids give 409 `PRIVACY.STAFF_ACCOUNT`. | Staff are a different table and have their own deactivate rule. | n/a |
| P9 | Idempotent: a second call answers 200 `alreadyAnonymised` and writes nothing. | Safe retries. | n/a |
| P10 | `reason` 5 to 300 characters and `confirm` = the customer id are both mandatory. | Prevents slips; reason is audited. | `PrivacyRules`. |
| P11 | The anonymised customer stays visible in the staff list (not soft-deleted) with placeholder values and inactive. | The orders still point at it; staff must see why. | n/a |
| P12 | `POST /customers/{id}/activate` is not blocked for an anonymised customer. | Harmless (no way to sign in: the mobile is a placeholder, sessions are gone). | Add a guard in `CustomersAdminService.SetActiveAsync`. |

## Owner decisions needed (conservative choice already implemented)

- **O1 Invoice retention.** Invoices keep the buyer name, mobile, e-mail and billing address, and are never erased. Reason: the invoice and credit-note numbering is gap-free and append-only, and Bangladesh VAT and income-tax record-keeping rules (retention period, whether buyer details are needed on an invoice below a value) were not verified; `docs/assumptions.md` marks the VAT rules as unverified. Needed from the owner or accountant: the retention period (commonly several years) and whether personal details on invoices must be blanked after it. Until then the personal data on invoices is the one place data survives anonymisation, and it is reachable only with the invoice permissions.
- **O2 Store credit, points and gift cards.** A customer with a balance can be anonymised; the balance stays on the closed account and cannot be used (the response reports `forfeitedPoints` and `forfeitedCredit`). Decide whether to refuse while store credit is above zero, or to refund first. Today staff must refund by hand before anonymising if the owner wants that.
- **O3 Audit trail blanking.** The trail is append-only by policy; anonymisation blanks old and new values of personal rows. Confirm that the owner accepts changing audit values for erasure requests (who, when and what action stay).
- **O4 Legal basis and wording.** No Bangladesh data-protection review was done (NFR-010 stays Partial): who may request erasure, the response time, identity check before acting, and whether orders must be kept for a stated period before the name may be removed.
- **O5 Export permission.** Whether support staff may export (now Super Admin only).

## Gaps

- FR-E08 customer-facing delete: the customer cannot ask for deletion in the shop yet (`/me` has no delete endpoint); this slice is the staff tool. A customer request flow (button, confirmation by OTP, queue for staff) is not built.
- No front-end screen (back end only).
- Contact messages and tickets of a guest are matched by mobile and e-mail only; a person who wrote with another number is not found.
- Order status history reasons, risk flag messages, shipment events and return photos are not scrubbed (they hold staff or system text; photos of goods).
- Pending outbox messages are not touched; the customer cannot have open orders, so only marketing or ticket messages could still be waiting and they read fresh (scrubbed) data.
- Loyalty: referral codes owned by the customer stay and the account row is kept (frozen); points and credit balances are not zeroed.
- Backups and logs outside the database (application logs, copies) are out of reach; the owner's backup retention decides when the data finally disappears.
- Retention rules (automatic anonymisation after N years of inactivity) are not built.
- FR-E09: no new fraud logic. Per-customer signals exist as `GET /fraud/customers/{id}/stats` (fraud.flag.view). Missing: per-customer device and number overlap view, flagged-order list per customer.
- No staff or import path changes consent, so the log has only customer and unsubscribe sources.

## Tests

- Domain (`tests/Juvo.Domain.Tests/Privacy`): `Customer.Anonymise` replaces fields, is idempotent; placeholder mobile length, uniqueness, never equals a real number.
- Application (`tests/Juvo.Application.Tests/Privacy`): consent history old values and order, channel and WhatsApp coverage, source kinds, IP masking, mobile variants, reason and confirm rules.
- Integration (`tests/Juvo.Api.IntegrationTests/Storefront/PrivacyTests.cs`, real SQL Server): history from the customer and unsubscribe paths with masking; 401/403 for anonymous, customer, wrong permission; default roles hold no privacy permission; export sections, no other customer's data, no message bodies, audit without content; export limit 429; refusals (reason, confirm, open order, staff, unknown); happy path (customer, order totals kept, invoice kept, ticket, contact message, notification log, addresses, wishlist, sessions, audit trail blanked, other customer untouched, old session refused, same mobile registers as a new customer, export of the anonymised customer); idempotency.

## Traceability suggestions

- FR-AF06: Implemented (backend): consent log history, export, anonymisation. Notes: no screen; see owner decisions O1 to O5.
- FR-E08: Partial: staff-side anonymisation keeping orders and invoices; customer self-service deletion not built.
- FR-E07: consent history now exposed per customer to staff (already Implemented for recording).
- FR-E09: stays Partial: per-customer stats exist (`/fraud/customers/{id}/stats`), no new logic.
- FR-N04 (internal notes and tags): not affected; customer notes and tags still do not exist.
- NFR-010: stays Partial: consent recorded, export and erasure exist, access logged; missing legal review, retention rules, customer self-service.
- NFR-012: privacy export and anonymise are audited without personal data.
- Assumptions to add: P1 to P12 and O1 to O5 above (section name suggestion: "AK. Step 12i slice 1 (privacy tools backend)").
