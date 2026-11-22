# Step 12d slice 4: contact page and staff inbox (front end), notes

Branch `feature/12d-contact-ui`. Contract: `docs/content-api.md` section 7, decisions of the backend in `docs/content-contact-notes.md`. FR-Q06.

## Built

Shop:
- `/contact`: general contact page. Form (name, mobile, e-mail, optional subject, message), honeypot `website`, checks before sending that mirror the API (mobile or e-mail needed, message 5 to 4000 characters, markup refused with a friendly text, Bengali digits accepted and sent as ASCII), the reference after sending, plain-word messages for 400, per-minute 429 and hourly 429 (`CONTENT.RATE_LIMITED`). Brand block beside the form from the public brand API (address, phone, WhatsApp link, e-mail, hours); every empty line is hidden and the whole block when all are empty. `generateMetadata` with title and description.
- `/contact/order-help`: the older guest form of Step 11A (opens a support ticket), moved here unchanged. Linked from the contact page and from the footer ("Help with an order"). The footer link "Contact us and help" now leads to the new `/contact`.

Staff (menu group Content, item "Contact messages", permission `content.message.view`, `ready: true`):
- `/admin/content/messages`: status filter (Any, New, Read, Replied, Archived) with the New count on the New button, search, shared pager (10 per page), cards on phones, a note when contact data is masked.
- `/admin/content/messages/[id]`: message, sender details exactly as the API sends them (masked or not, with a plain note when masked), handled time, status buttons, internal notes (append), archive and restore. Read-only note and no buttons without `content.message.handle`.

Code: `src/modules/contact` (`logic.ts`, `api.ts`, `contact-message-form.tsx`, `brand-panel.tsx`, `common.tsx`, `inbox-list.tsx`, `message-detail.tsx`), messages `contact.en|bn.json`, e2e helper `e2e/support/contact.ts`.

## Decisions (reason, how to change)
1. **`/contact` was already taken** by the 11A support-ticket form. The brief asks for the new page at `/contact`, so the ticket form moved to `/contact/order-help` and its three specs now visit that path (`support.spec.ts`, `support-ui-checks.spec.ts`). Reason: a general message needs no mobile and no SMS, while order questions benefit from a ticket. Change: swap the two pages' routes, or fold the order form into the new one.
2. **Opening a message does not change its status** (the contract has no automatic change), so staff use a "Mark as read" button. Change: call the status endpoint from the detail screen on first open.
3. Status buttons: every status except the current one and Archived, plus Archive; for an archived message "Restore from archive" sets New (any change is allowed by the API). Logic in `statusChoices`.
4. The New count comes from the list endpoint (`status=New`, one row, `total`), because there is no count endpoint. It is re-read after every change.
5. The notes text is shown as the API stores it (`[date UTC, author] text`, one column); no splitting into entries.
6. Honeypot: an input named `website` inside an `aria-hidden`, `inert`, off-screen wrapper with `tabindex=-1`, so keyboard users, screen readers and the 44 px check never reach it; bots that fill every input fall in. Its label text is a hard-coded English word because it is never shown.
7. Map: no read-only map component exists (the Leaflet component in `myaccount` is an address picker), so no map is embedded. When the brand profile has a map point the block offers a link to OpenStreetMap. Change: add a small read-only Leaflet component and use it in `brand-panel.tsx`.
8. WhatsApp link: digits of the brand number, a leading 0 becomes 88 (`whatsAppHref`).
9. Test address budget: the API keeps 10 messages per client-address hash per hour from stored rows, and every browser and API call of a test run comes from one local address. The specs reset the stored address key (`IpHash`) of the rows in the throw-away test database after each message (`freeAddress`). `RateLimiting__Enabled=false` already disables the per-minute policy in the e2e config. Production values are untouched.

## Contract gaps
- OpenAPI names two different DTOs `ContactReceiptDto` (support ticket receipt `{ number }` and contact receipt `{ reference }`); the generated type for `POST /store/content/contact` is wrong. Local type `ContactReceipt` with a cast in `api.ts` (same kind of gap as the brand DTO). Fix: rename the DTO on the backend (e.g. `ContactMessageReceiptDto`).
- `handledBy` is only an id; the screen shows the handled time, not the name (no endpoint resolves a staff id for this role).
- No unread count endpoint (decision 4) and no staff notification of new messages (already listed in the backend notes).

## Not built
Embedded map, reply from the inbox, convert to ticket or order, attachments, new-message alert, Bengali native review of the new texts. A Messenger or WhatsApp order button (FR-Q05) is not part of this slice.

## Tests
- vitest `tests/contact.test.ts` (15 tests); full suite 515 passed (29 files).
- Playwright desktop project: `contact.spec.ts` 10 flow tests + setup and cleanup, all passed (visitor: checks, markup, Bengali digits, reference, stored as New with normalised number; honeypot and nothing stored; hourly limit; brand block hide/show and footer links; Bengali page; staff: find, filter, opening keeps New, read, replied, notes, archive, restore, e-mail-only message, search; masked data; view-only; no-permission redirect and menu; unknown id). `contact-ui-checks.spec.ts` 23 tests (360/768/1280: form, errors, sent, inbox list and filter, message with notes, refused note, not found, masked read-only; dark theme; Bengali; axe, overflow, 44 px, console) all passed. `support.spec.ts` guest tests and the support public-form UI check re-run after the path move: passed. Pixel 7 and iPhone 14 not run.
- Key screenshots in `juvo-front/docs/ui-checks/contact/`.

## Traceability suggestions
FR-Q06 Done (contact page with brand block and honeypot, staff inbox with statuses, notes, archive, masking; map shown as an OpenStreetMap link only). FR-Q05 unchanged (Partial).
