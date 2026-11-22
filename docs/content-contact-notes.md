# Step 12d slice 3: contact form and admin inbox, decisions and notes

Contract: `docs/content-api.md` section 7. Branch `feature/12d-contact-backend`, migration `ContactInbox`. For the owner to confirm and for the lead to copy into `docs/assumptions.md` and `docs/understanding.md` (not edited here).

## Decisions (decision, reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| 1 | A contact message is a separate inbox row in the Content module, not a support Ticket | The existing `POST /store/contact` (11A) already makes a guest ticket for order questions, needs a mobile number, sends SMS and runs SLA clocks. A contact page also gets pre-sales and email-only messages that need none of that. Two paths cost nothing extra and the shop can pick one | Add a "convert to ticket" action later using `TicketCore.OpenAsync` (needs a mobile), or point the contact page at `/store/contact` |
| 2 | New route `POST /store/content/contact`, 202 with `{ reference }` | Brief; 202 because staff handle it later | n/a |
| 3 | Mobile or email required (at least one); mobile normalised with `PhoneNumber.Normalize` | Brief; same normaliser as the rest | `ContactRules.Validate` |
| 4 | Plain text only: markup in name, subject, message, note and source page is rejected (400), not stripped | Same rule as page bodies; stored text equals typed text | Relax the `LooksLikeMarkup` checks |
| 5 | Honeypot field `website`; when filled, a fake 202 receipt and nothing stored | A bot learns nothing | `ContactPublicService.SubmitAsync` |
| 6 | Limits: policy `contact` per address (3 per minute, `RateLimiting:ContactPerMinute`), plus per mobile or email 3 per hour and per address hash 10 per hour counted from stored rows | Strict, survives restarts, no extra store (no Redis) | constants in `ContactRules` |
| 7 | Never leaks whether a mobile or email belongs to a customer: no lookup is made and the answer shape is fixed | Brief | n/a |
| 8 | Client address kept only as SHA-256 of `juvo-contact` + `\|` + address (no secret salt) | Brief says hash, not raw. An IPv4 hash can be brute-forced, so it is an abuse key and a privacy aid, not anonymisation | Add a configured pepper (HMAC) if the owner wants stronger protection |
| 9 | Status New, Read, Replied, Archived; any status may be set (an archive can be undone); Read/Replied/Archived stamp handled by and time, New clears them | Simple inbox, mistakes are cheap to fix; history is in the audit log | Add a transition table in `ContactMessage.SetStatus` |
| 10 | Internal note is one text column; notes are appended as `[date UTC, author] text`, 4000 characters in all | Brief: "internal note"; avoids a notes table | Move to a notes table if threads are needed |
| 11 | Reuse `customers.customer.view-pii` for masking (no new PII code); masked callers cannot search by mobile or email; unmasked views are audited as `Viewed` | Brief: copy the Customers pattern | n/a |
| 12 | Permissions `content.message.view` and `content.message.handle`; Manager and Content Editor via `content.*`, Support via `content.message.*`; Order Manager, Inventory Manager and Accountant do not get them | Brief. Existing roles receive new codes through the seeder's "new codes only" rule | `DefaultRoles` |
| 13 | Messages are never deleted (archive only); personal fields and the note are `[AuditIgnore]` | Soft-delete rule; the audit log should not copy personal data | n/a |
| 14 | Reference `CM-` plus the last 8 hex digits of the row id, not a gap-free sequence | A receipt for the sender, not a business number | Use `NumberAllocator` if a sequence is wanted |
| 15 | Not branch scoped (content is global) | Same as the other content endpoints | n/a |

## Not done (gaps)

- Staff notification of a new message (FR-Q06 only says messages arrive in an admin inbox, which is met): the notification service sends to customers by template and has no staff recipient list, so nothing was built. Gap: a staff alert (email or in-app) and an unread-count endpoint.
- Reply from the inbox (staff answer by phone or email themselves, then set Replied), convert to ticket or manual order (the FR-D11 link of FR-Q05), attachments, spam scoring, retention or purge of old messages.
- FR-Q05 "order via Messenger/WhatsApp" is data only: the brand profile `whatsApp` and the WhatsApp social link already exist; no endpoint was added.
- No front-end screen, no OpenAPI client regeneration (`npm run gen:api` when the front end starts).
- Only filtered test namespaces were run, not the whole integration suite.

## Traceability suggestions

| ID | Suggested status | Implemented in |
| --- | --- | --- |
| FR-Q06 | Done (back end; form and inbox; map point, phone and email come from the brand profile) | `ContactMessage`, `ContactPublicService`, `ContactInboxService`, `POST /store/content/contact`, `/content/messages` |
| FR-Q05 | Partial (data only: WhatsApp number and link exist; no button, no enquiry-to-order link) | `BrandProfile.WhatsApp`, `SocialLink` |
