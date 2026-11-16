# Step 11A backend: module Y, support tickets (builder BE-1)

Contract: `docs/support-api.md`. This file holds the decisions (with how to change them), the traceability rows for the lead to merge into `docs/understanding.md`, what is partial or deferred, and the gate numbers. CLAUDE.md, PROGRESS.md, `docs/assumptions.md` and `docs/understanding.md` were not edited.

## 1. What was built

- **Persistence**: DbSets `Tickets`, `TicketMessages`, `TicketAttachments`, `TicketStatusHistories`, `CannedReplies` on `JuvoDbContext` and `IApplicationDbContext` (`GlobalSequences` was already mapped); migration `SupportTickets` (own commit). One model change on the domain: `Ticket.CustomerEmail` (and `CustomerEmail` on the three events), so a guest's e-mail reaches the update messages.
- **Services** in `Juvo.Application/Support/`: `TicketCore` (numbering, due times, rate limit, state machine with reopen window), `TicketsService` (staff), `MyTicketsService` (customer and the contact form), `CannedRepliesService`, `SupportReportService`, `TicketAutoCloseService`, three outbox handlers (`TicketCreatedHandler`, `TicketRepliedHandler`, `TicketResolvedHandler`), pure logic (`CannedReplyTemplate`, `TicketStats`, `TicketLimits`).
- **Endpoints** `Juvo.Api/Endpoints/SupportEndpoints.cs`: 13 staff ticket endpoints, 5 canned reply endpoints, `GET /support/report`, 7 customer endpoints under `/me/tickets` and `POST /store/contact` (details in the contract).
- **Worker** `TicketAutoCloseWorker` in `OperationsWorkers.cs`, registered like `RecurringExpenseWorker`.
- **Notifications**: templates `ticket.received`, `ticket.replied`, `ticket.resolved` (SMS and email, English and Bengali) in `NotificationCatalog`.

## 2. Decisions (candidates for `docs/assumptions.md`)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| Y1 | A ticket without an order is **global** (`BranchId` null), not assigned to the default branch. A ticket with an order takes the order's branch. | The domain entity already says so; a "default branch" would make one branch own every general question and hide it from the others. Global tickets are open to everyone holding the permission in any assignment, and appear in every consolidated report. | Set a branch in `OpenTicketInput` (`TicketCore.OpenAsync`) when there is no order, and drop the global clause in `TicketsService.Visible` and `SupportReportService`. |
| Y2 | `Ticket` does **not** implement `IBranchScoped` (its `BranchId` is nullable); the branch scope is applied in the services (`Visible`, `CheckAsync`). | `IBranchScoped` needs a non-null branch and installs a global filter plus a save guard. | Make `BranchId` non-null and implement the interface when Y1 changes. |
| Y3 | Ticket numbers `TK-<yyMM>-<6 digits>` from the existing global counter, month in Bangladesh time. | Gap-free, no branch needed; the domain's `GlobalSequence` was written for it. | `NumberAllocator.NextGlobalAsync("TK", ...)`. |
| Y4 | Default `Support.AutoCloseDays` changed from 3 to **7** (code default only). | The brief asked for 7; no stored setting existed. | Set the setting `Support.AutoCloseDays` or change the default in `OpsSettingsService`. |
| Y5 | A first public staff reply moves Open to InProgress; Waiting is set explicitly (`setStatus` on the reply or the status endpoint); a customer reply moves Waiting to InProgress and reopens Resolved. | The state machine has no automatic rule; this keeps "who is waiting for whom" visible. | `TicketsService.ReplyAsync`, `MyTicketsService.ReplyAsync`. |
| Y6 | Reopening is limited for staff as well as customers (`Support.ReopenDays`, default 7, from the resolution or the closing). | The brief says `REOPEN_WINDOW_EXPIRED` after the window; staff can open a new ticket instead. | `TicketCore.MoveAsync`. |
| Y7 | Closing, and any reopening, need `support.ticket.close`; resolving and the other moves need `support.ticket.reply`; assign and priority need `support.ticket.assign`; the bulk endpoint requires `view` and checks each ticket for its action. | The permission catalogue has no resolve or reopen codes. | `TicketsService.StatusPermission`. |
| Y8 | A customer sees staff as "Juvo BD Support", never an internal note, never an author id; a guest ticket is never linked to an account by a typed number. | Privacy of staff; a stranger must not plant a ticket in someone's panel. | `MyTicketsService.ViewAsync`; link by `CustomerId` in `ContactAsync`. |
| Y9 | The contact form is protected by the `checkout` rate-limit policy per address, `Support.MaxTicketsPerMobilePerHour` (default 5) per mobile, and size limits; no honeypot, no CAPTCHA, guests cannot attach files. | Brief: keep it simple and safe. | Add a CAPTCHA provider behind an interface in Step 14 if abuse appears. |
| Y10 | An order that a customer or guest cannot prove answers `TICKET.ORDER_MISMATCH` for unknown and foreign orders alike. | No order-number enumeration. | `MyTicketsService.OrderAsync`. |
| Y11 | The reply text is sent in the **email** of `ticket.replied` and never in the SMS (the SMS only says there is an answer). A guest has no panel, so the e-mail is how they read it. | SMS length and privacy; guests have no other place to read answers. | Edit the templates in the notification screen. |
| Y12 | Report definitions: backlog figures are "as of now"; new, category, assignee and first response are by **creation** in the period; resolved and resolution time are by **resolution** in the period; Bangladesh days; at most 366 days. | One clear rule per figure that can be added up by hand. | `SupportReportService.BuildAsync`. |
| Y13 | Staff list answers a plain page; the tab counts are a separate `GET /support/tickets/summary`. | The list contract of the project is `{items,total,page,pageSize}`. | none needed |
| Y14 | Attachments use the Step 11A `AttachmentStore` (8 MB, JPEG, PNG, WebP, PDF by content) with per-caller folders (`tickets/staff`, `tickets/c-<customerId>`) so a path from another caller is refused. Files are served from `/files` like receipts. | Reuse; isolation. | `TicketsService.Folder`, `MyTicketsService.FolderOf`. |
| Y15 | No seed of canned replies. | They are shop wording; the owner writes them. | Add a seeder when the owner supplies texts. |

## 3. Traceability rows (for `docs/understanding.md`)

| ID | Status | Implemented in | Notes |
| --- | --- | --- | --- |
| FR-Y01 Create tickets (panel, contact form, staff for a customer, order link, channels) | Done | `MyTicketsService`, `TicketsService.CreateAsync`, `POST /store/contact`, `/me/tickets`, `/support/tickets` | Return link (`ReturnId`) is not exposed yet; see Partial. |
| FR-Y02 Work tickets (assign, priority, status, internal notes, history, bulk, reopen window, auto-close) | Done | `TicketsService`, `TicketAutoCloseService`, `TicketAutoCloseWorker` | |
| FR-Y03 Canned replies (en, bn, placeholders, preview) | Done | `CannedRepliesService`, `CannedReplyTemplate` | No seeded texts (Y15). |
| FR-Y04 Customer updates (received, replied, resolved; language and channel rules) | Done | `TicketEventHandlers`, `NotificationCatalog` | Bengali texts are a first pass for Step 12. |
| FR-Y05 Support report | Done | `SupportReportService`, `GET /support/report` | |

## 4. Partial, deferred, contract gaps

- **Return link**: the `Ticket` has `ReturnId` and `ReturnNumber` columns but no endpoint sets them (tickets link to an order only). Deferred.
- **Branch filter chosen by the active branch header** is not used; like the expense list, the caller passes `branchId` explicitly.
- **No SLA reminders or escalation** (only the `overdue` flag and filter); no assignment rules or round robin.
- **Customer panel does not show ticket history rows** (only the conversation); staff do.
- **Guests cannot read their ticket** online; they get the number on screen and the answers by SMS and e-mail.
- **FE contract note**: enums travel as strings; ticket ids are GUIDs; the staff list has no totals per status (use `/support/tickets/summary`). New OpenAPI types need `npm run gen:api` in the front end (not done here by instruction).
- No new error codes were needed.

## 5. Gate

See the final report of the builder for the numbers that were actually run (build, three test projects, migration check, vulnerable packages, leftover test databases).
