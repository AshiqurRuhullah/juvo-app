# Customers admin (Step 12a): notes

## Built
- `/admin/customers`: list with name search (name, mobile, email with `view-pii`; name only without), status filter (active, deactivated), profile-level filter (guest, quick, full), shared 10-row pager, cards on phones. Menu item `customers` is `ready: true`.
- `/admin/customers/[id]`: profile facts, mobile verified flag, sign-in count and last sign-in, wishlist count, addresses, the three consents (given, withdrawn, not asked), order history numbers, account access card.
- Deactivate and activate with a confirm dialog (states: cannot sign in, sessions end, history kept); button only with `customers.customer.block`, read-only note otherwise.
- Masked data (no `customers.customer.view-pii`): values shown exactly as received; a notice says plainly they are hidden; the search placeholder says name only. Nothing is unmasked on the client.
- Code: `juvo-front/src/modules/customers/{logic,api,common,customers-list,customer-detail}`, messages `src/messages/customers.{en,bn}.json`, error text `CUSTOMER_NOT_FOUND` in `en.json`/`bn.json`, `load.ts` fragment, `playwright.config.ts` testMatch.

## Decisions
- Order history on the detail uses `GET /fraud/customers/{id}/stats` (permission `fraud.flag.view`): orders, spent, delivered, open, cancelled, returned/failed, first order. Without that permission the card says so. Change: drop `useCustomerOrderStats` if the API adds the figures to the customer DTO.
- Columns do not include orders count or total spent in the list (the list DTO lacks them). No sorting (API always sorts newest first).
- No link to a customer-filtered order list: `/admin/orders` accepts only `?status=`. The card tells staff to search orders by number.

## Contract gaps (API change needed)
1. Lifetime orders and spend on `CustomerListItemDto` / `CustomerDetailDto` (add `orderCount`, `totalSpent`, `lastOrderAt`), and a `customerId` filter on `GET /orders` (so the detail can link to the customer's orders). Without both, the owner cannot sort by best customers.
2. FR-N04 internal notes and tags on customers: needs endpoints such as `GET/POST /customers/{id}/notes`, `PUT /customers/{id}/tags` plus a `tag` list filter and permission `customers.customer.note`.
3. FR-E08 delete or anonymise an account: needs `POST /customers/{id}/anonymise` (keeps orders in anonymised form), permission and audit; also a customer self-service `DELETE /me`.
4. FR-E09 fraud signals per customer (many devices or numbers, repeated fake or returned COD): needs a per-customer signal list on the detail DTO (or `GET /fraud/customers/{id}/signals`).
5. Admin-created customer: needs `POST /customers` (mobile, name, email) with the same normalisation as OTP sign-in.
6. Sorting and a `Sort` whitelist on `GET /customers`; export (FR-N01).
7. Address district and upazila come as ids only; names need a geography lookup or names in `AddressDto`.

## Not built
Everything in the gaps above. None is shown or faked in the UI.

## Tests
- vitest `tests/customers.test.ts`: 13 pass; full suite 415 pass (25 files). `tsc --noEmit` and eslint on the module: clean.
- Browser specs `e2e/customers.spec.ts` and `e2e/customers-ui-checks.spec.ts` are written and type-checked but NOT run (the production build was stopped by the system for low memory); the `next build` of this branch also did not complete.

## Traceability suggestions
- FR-N01 customers list: Partial (search, filters, pager; no sort or export).
- FR-N04: Not started (contract gap 2). FR-E08: Not started (gap 3). FR-E09: Not started (gap 4).
- FR-E07 staff view of consents: Done (read-only view). Module N customers screen: Partial.
