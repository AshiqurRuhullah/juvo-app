# Step 11A front end B: support ticket screens (builder FE-B)

Contract: `docs/support-api.md`. Module `juvo-front/src/modules/support`. Not edited: CLAUDE.md, PROGRESS.md, assumptions, understanding, nav.ts, load.ts, playwright.config.ts, en.json, bn.json, anything under juvo-be.

## 1. Built

Routes: staff `/admin/support` (tabs Tickets, Canned replies, Report by permission), `/admin/support/[id]`; customer `/account/support`, `/account/support/new` (`?order=<id>` prefill), `/account/support/[id]`; public `/contact` (signed-in customers are redirected to `/account/support/new`). Small edits: footer link "Contact us and help", account overview tile, "Get help with this order" button on the customer order page.

Performance: page shells are server components; canned replies, report, charts (Recharts via next/dynamic), the create dialog and the saved-reply picker load on demand; list uses server paging with keepPreviousData, debounced search, memoised columns and messages; no polling; summary, list and staff lookups load in parallel; `/contact` ships only the form.

## 2. Decisions (candidates for assumptions.md)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| YF1 | Customer list has a status filter (All and the five statuses), not "open/closed" tabs. | `GET /me/tickets` takes one `status` only. | Add an `active` flag to the API, then tabs. |
| YF2 | Assignee choices: "Me" always, plus the staff list (`/ums/users`) only when the person holds `ums.user.view`. | The API has no assignable-staff list. | Add an endpoint, change `useAssigneeOptions`. |
| YF3 | No idempotency key for ticket creation (the API offers none): the form blocks double submit with a ref guard and a disabled button. | Contract. | Add `Idempotency-Key` to `/me/tickets` and the BFF allow-list. |
| YF4 | Closed tickets: public reply disabled, internal note allowed. Reply "set status" offers Waiting, In progress, Resolved, Closed (Closed only with `support.ticket.close`). | API rules Y5, Y7. | `logic.ts` `replyStatusChoices`. |
| YF5 | The report uses typed rows of its own (`KeyCount`) because the generated `CountDto` name collides with the stock-count DTO. | Schema name clash. | Rename the DTO in the API. |
| YF6 | Guest contact form has no file upload and a native select for the topic (keeps the page light). | Y9: guests cannot attach. | none |

## 3. Traceability rows

| ID | Status | Implemented in | Notes |
| --- | --- | --- | --- |
| FR-Y01 | Done | `contact-form.tsx`, `my-ticket-new.tsx`, `create-ticket-dialog.tsx`, order help link | Return link not exposed (backend gap). |
| FR-Y02 | Done | `tickets-list.tsx`, `ticket-detail.tsx`, `reply-composer.tsx`, `thread.tsx` | Assign, priority, status, notes, history, bulk, reopen window. |
| FR-Y03 | Done | `canned-replies.tsx`, `canned-picker.tsx` | Preview with sample values; server render on insert. |
| FR-Y04 | Done (backend) | customer thread and guest success text | Messages are sent by the backend; front end tells guests the answer comes by SMS or email. |
| FR-Y05 | Done | `report-view.tsx`, `report-chart.tsx` | Text summary and hidden data table with the chart. |

## 4. Partial, deferred, gaps

- No SLA timers beyond the API's overdue flag (shown as a badge); no ticket-to-return link (API gap).
- Contract gaps: no assignable-staff list; `/me/tickets` has no active/closed filter; no idempotency key; `CountDto` schema name clash.
- Bengali texts are a first pass (Step 12 review).
- Rate-limit test uses the per-mobile hourly setting (works with the global limiter off).
