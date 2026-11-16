# Support tickets API (module Y, Step 11A)

Base path `/api/v1`. JSON, camelCase, enums as strings, errors as RFC 9457 ProblemDetails with `code` and `correlationId`. Lists use `page`, `pageSize` (max 100), `sort`, `search` and answer `{ items, total, page, pageSize }`. Days in the report are Bangladesh days (UTC+6).

Three kinds of caller:

| Caller | Endpoints | Guard |
| --- | --- | --- |
| Staff | `/support/tickets...`, `/support/canned-replies...`, `/support/report` | `RequirePermission` (any assignment) on the route, then the service proves the permission again in the ticket's branch |
| Signed-in customer | `/me/tickets...` | customer token only; staff tokens are refused |
| Guest | `POST /store/contact` | anonymous, rate limit policy `checkout` per client address and `Support.MaxTicketsPerMobilePerHour` per mobile number |

## 1. Rules

- **Ticket number**: `TK-<yyMM>-<6 digits>` (for example `TK-2611-000007`), gap-free per month, issued inside the transaction that creates the ticket (`NumberAllocator.NextGlobalAsync`, table `GlobalSequences`). A rolled-back ticket gives its number back.
- **Branch**: a ticket tied to an order belongs to the order's branch (`branchId`). A ticket without an order is **global** (`branchId` null): every staff member who holds the permission in any assignment can see and work on it. Another branch's ticket looks like `404 TICKET.NOT_FOUND` (and the refusal is audit-logged); a person assigned to the branch but missing the permission gets `403 AUTH.BRANCH_FORBIDDEN`.
- **Status machine** (`TicketStateMachine`): Open to InProgress, Waiting, Resolved or Closed; InProgress to Waiting, Resolved, Closed or Open; Waiting to InProgress, Resolved, Closed or Open; Resolved to Closed or Open; Closed to Open. Any other move (including to the same status) answers `409 TICKET.INVALID_TRANSITION`. Every move writes a history row (from, to, reason, who, when).
- **Reopening** (Resolved or Closed back to Open) is allowed for `Support.ReopenDays` (default 7) counted from the resolution (Resolved) or the closing (Closed). After that: `409 TICKET.REOPEN_WINDOW_EXPIRED`. It applies to staff and customers alike.
- **Staff reply** (`internal=false`): moves an Open ticket to InProgress and is the first response of the ticket; on a Closed ticket it answers `409 TICKET.CLOSED`; on a Resolved ticket it stays Resolved. `setStatus` moves the ticket after the message. An **internal note** (`internal=true`) is staff only, changes nothing and notifies nobody; it is allowed on a Closed ticket.
- **Customer reply**: Waiting goes back to InProgress; Resolved reopens to Open (window applies); Closed answers `409 TICKET.CLOSED` (reopen first).
- **Due times** by priority (settings `Support.Sla.ResponseHours.<Priority>` and `Support.Sla.ResolutionHours.<Priority>`, defaults 48/168 Low, 24/72 Normal, 8/24 High, 2/8 Urgent). A ticket is overdue while Open or InProgress and the first reply (or the resolution) is past due; a ticket waiting for the customer is never overdue. Changing the priority recalculates both from the creation time.
- **Rate limit**: at most `Support.MaxTicketsPerMobilePerHour` (default 5) new tickets per normalised mobile number per hour from the contact form and the customer panel: `429 TICKET.RATE_LIMITED`. Staff creation is not limited.
- **Order link proof**: a signed-in customer may link only an order of their own account; a guest only with the order number and the mobile number that placed it. Anything else (including an unknown number) answers `422 TICKET.ORDER_MISMATCH` with no hint whether the order exists. Staff link an order they can see (`404 TICKET.ORDER_NOT_FOUND` otherwise; `422 TICKET.ORDER_MISMATCH` if the typed mobile differs from the order's).
- **Guest tickets** are never linked to an account by a typed mobile number (`customerId` null), so a stranger cannot make a ticket appear in someone's panel. Staff-created tickets are linked to the customer found by mobile.
- **Customer view**: never an internal note; staff appear as `Juvo BD Support`; no staff ids.
- **Attachments**: JPEG, PNG, WebP or PDF up to 8 MB, judged by the first bytes; at most 5 files per message; paths must come from an upload of the same caller (customers: their own folder, staff: the staff folder) else `422 TICKET.ATTACHMENT_INVALID`. A bad upload answers `400` validation. Guests cannot attach files.
- **Canned reply placeholders**: `{customerName}`, `{orderNumber}`, `{ticketNumber}`; any other `{word}` is refused when saving; titles are unique per language (`en`, `bn`).
- **Bulk**: at most 50 tickets (`422 TICKET.BULK_LIMIT`); each ticket is checked on its own and the answer lists every outcome.

## 2. Staff endpoints

Permissions: `support.ticket.view|create|assign|reply|close`, `support.canned-reply.view|edit`, `support.report.view`.

### GET `/support/tickets` (view)
Query: `status`, `priority`, `assigneeId`, `unassigned`, `branchId`, `orderNumber`, `channel` (Web, Phone, WhatsApp, Facebook, Email, OrderPage), `category` (order, delivery, payment, product, return, account, other), `overdue`, `active` (not Resolved or Closed), `from`/`to` (creation date, `yyyy-MM-dd`, Bangladesh days), `search` (number, subject, customer name or mobile), `sort` (`created`, `activity`, `priority`, `status`, `number`, `due`; prefix `-` or default descending), `page`, `pageSize`.
```json
{ "items": [ { "id": "…", "number": "TK-2611-000007", "subject": "Parcel late", "status": "Open", "priority": "Normal", "channel": "Web", "category": "delivery",
    "customerMobile": "+8801712345678", "customerName": "Nadia", "customerId": null, "orderId": null, "orderNumber": null, "branchId": null, "branchCode": null,
    "assigneeId": null, "assigneeName": null, "createdAt": "…", "lastActivityAt": "…", "responseDueAt": "…", "resolveDueAt": "…", "firstResponseAt": null, "overdue": false, "messageCount": 1 } ],
  "total": 1, "page": 1, "pageSize": 20 }
```

### GET `/support/tickets/summary` (view)
```json
{ "byStatus": { "Open": 3, "InProgress": 1, "Waiting": 0, "Resolved": 2, "Closed": 9 }, "active": 4, "unassigned": 2, "overdue": 1, "mine": 1 }
```

### GET `/support/tickets/{id}` (view)
```json
{ "id": "…", "number": "TK-2611-000007", "subject": "Parcel late", "status": "InProgress", "priority": "Normal", "channel": "Web", "category": "delivery",
  "customerMobile": "+8801712345678", "customerName": "Nadia", "customerEmail": "nadia@example.com", "customerId": null, "orderId": "…", "orderNumber": "JV-DHK01-2611-000123",
  "branchId": "…", "branchCode": "DHK01", "assigneeId": "…", "assigneeName": "Rina", "createdAt": "…", "lastActivityAt": "…", "responseDueAt": "…", "resolveDueAt": "…",
  "firstResponseAt": "…", "resolvedAt": null, "closedAt": null, "reopenCount": 0, "overdue": false, "canReopen": false,
  "nextStatuses": ["Waiting", "Resolved", "Closed", "Open"],
  "messages": [ { "id": "…", "kind": "Customer", "text": "It was due yesterday.", "authorId": null, "authorName": "Nadia", "at": "…", "attachments": [] },
                { "id": "…", "kind": "Internal", "text": "Check the courier log", "authorId": "…", "authorName": "Rina", "at": "…", "attachments": [] },
                { "id": "…", "kind": "Staff", "text": "We are checking.", "authorId": "…", "authorName": "Rina", "at": "…",
                  "attachments": [ { "id": "…", "fileName": "label.png", "contentType": "image/png", "size": 1234, "url": "/files/tickets/staff/2611/…-label.png" } ] } ],
  "history": [ { "fromStatus": null, "toStatus": "Open", "reason": "Ticket created", "actorName": "Nadia", "at": "…" },
               { "fromStatus": "Open", "toStatus": "InProgress", "reason": "Staff replied", "actorName": "Rina", "at": "…" } ] }
```
Message kinds: `Customer`, `Staff`, `Internal`, `System`.

### POST `/support/tickets` (create)
```json
{ "mobile": "01712345678", "customerName": "Nadia", "email": "n@example.com", "subject": "Call about the size", "message": "Wants to exchange.",
  "channel": "Phone", "priority": "High", "category": "return", "orderNumber": "JV-DHK01-2611-000123", "assigneeId": null, "attachmentPaths": [] }
```
`201` with the detail above. Channel defaults to Phone, priority to Normal, category to `other`. With `orderNumber` the ticket takes the order's branch and customer; `assigneeId` also needs `support.ticket.assign`. Errors: `GENERAL.VALIDATION`, `TICKET.ORDER_NOT_FOUND`, `TICKET.ORDER_MISMATCH`, `TICKET.ASSIGNEE_NOT_FOUND`, `TICKET.ATTACHMENT_INVALID`.

### POST `/support/tickets/uploads` (reply)
Multipart `file`. `201 { "path", "fileName", "contentType", "size", "url" }`. Send `path` in `attachmentPaths`.

### POST `/support/tickets/{id}/reply` (reply)
```json
{ "text": "We are checking.", "internal": false, "setStatus": "Waiting", "attachmentPaths": [] }
```
Answers the detail. Errors: `TICKET.NOT_FOUND`, `TICKET.CLOSED` (public reply on a closed ticket), `TICKET.INVALID_TRANSITION`, `TICKET.ATTACHMENT_INVALID`, 403 when `setStatus` is Closed or a reopening and the caller lacks `support.ticket.close`.

### POST `/support/tickets/{id}/assign` (assign)
`{ "assigneeId": "…" }` or `{ "assigneeId": null }` to unassign. The assignee must be an active staff member who may view tickets in the ticket's branch (anywhere for a global ticket): else `404 TICKET.ASSIGNEE_NOT_FOUND`. An internal note records the change.

### PUT `/support/tickets/{id}/priority` (assign)
`{ "priority": "Urgent" }` (Low, Normal, High, Urgent). Recalculates due times, writes an internal note.

### POST `/support/tickets/{id}/status` (reply; close for Closed and for reopenings)
`{ "status": "Waiting", "reason": "Asked for a photo" }`.

### POST `/support/tickets/{id}/resolve` (reply), `/close` (close), `/reopen` (close)
Body `{ "reason": "…" }` (optional). Resolving tells the customer (`ticket.resolved`).

### POST `/support/tickets/bulk` (view; each item needs its own permission)
```json
{ "ids": ["…", "…"], "action": "assign", "assigneeId": "…", "reason": null }
```
`action` is `assign` or `close`. Answer:
```json
{ "total": 2, "succeeded": 1, "failed": 1, "items": [ { "id": "…", "ok": true, "code": null, "message": null }, { "id": "…", "ok": false, "code": "TICKET.INVALID_TRANSITION", "message": "…" } ] }
```
More than 50 ids: `422 TICKET.BULK_LIMIT`; none or an unknown action: `400 GENERAL.VALIDATION`.

### Canned replies
| Method and path | Permission | Notes |
| --- | --- | --- |
| GET `/support/canned-replies?language&category&includeInactive` | canned-reply.view | array of `{ id, title, body, language, category, isActive, placeholders }` |
| POST `/support/canned-replies` | canned-reply.edit | `{ title, body, language: "en"|"bn", category, isActive }`; `409 GENERAL.CONFLICT` for a duplicate title in the language; unknown placeholder is `400` |
| PUT `/support/canned-replies/{id}` | canned-reply.edit | same body; `404 TICKET.CANNED_REPLY_NOT_FOUND` |
| DELETE `/support/canned-replies/{id}` | canned-reply.edit | `204` |
| POST `/support/canned-replies/render` | canned-reply.view | `{ "cannedReplyId": "…" or "body": "…", "ticketId": "…" }` answers `{ "text", "unknownPlaceholders": [], "emptyPlaceholders": ["orderNumber"] }`. The ticket is read with the caller's view rights (another branch: `404 TICKET.NOT_FOUND`) |

### GET `/support/report?from&to&branchId` (report.view)
Default period: the last 30 days. At most 366 days (`400` otherwise). Without `branchId`: the consolidated view of every branch the caller may report on plus the global tickets; with it: that branch only (`404` for a branch the caller cannot see). Definitions: backlog figures (`openNow`, `openByStatus`, `openByPriority`, `overdueNow`, `oldestOpen`) are as of now; `newTickets`, `byCategory`, `byAssignee` and `firstResponse` cover tickets **created** in the period; `resolvedTickets` and `resolution` cover tickets **resolved** in the period (a reopened ticket counts again when it is resolved again); durations are minutes between creation and the first staff reply, or the resolution.
```json
{ "from": "2026-11-01", "to": "2026-11-15", "branchId": null, "newTickets": 6, "resolvedTickets": 2, "openNow": 4, "overdueNow": 1,
  "openByStatus": [ { "key": "Open", "count": 3 }, { "key": "InProgress", "count": 0 }, { "key": "Waiting", "count": 1 } ],
  "openByPriority": [ { "key": "Low", "count": 0 }, { "key": "Normal", "count": 3 }, { "key": "High", "count": 0 }, { "key": "Urgent", "count": 1 } ],
  "daily": [ { "date": "2026-11-01", "new": 1, "resolved": 0 } ],
  "firstResponse": { "count": 3, "averageMinutes": 60, "medianMinutes": 60 },
  "resolution": { "count": 2, "averageMinutes": 420, "medianMinutes": 420 },
  "byCategory": [ { "key": "delivery", "count": 2 } ],
  "byAssignee": [ { "assigneeId": null, "name": "Unassigned", "total": 3, "open": 3, "resolved": 0 } ],
  "oldestOpen": [ { "id": "…", "number": "TK-2611-000003", "subject": "T3", "status": "Open", "priority": "Normal", "createdAt": "…", "ageHours": 98.5 } ] }
```
Average and median are null when there is nothing to measure.

## 3. Customer endpoints (`/me/tickets`, customer token)

| Method and path | Notes |
| --- | --- |
| GET `/me/tickets?status&search&page&pageSize` | `{ items: [ { id, number, subject, status, category, orderNumber, createdAt, lastActivityAt } ], total, page, pageSize }`, newest activity first |
| POST `/me/tickets` | `{ "subject", "message", "category", "orderNumber", "attachmentPaths" }` answers `201` with the ticket; `TICKET.ORDER_MISMATCH`, `TICKET.RATE_LIMITED`, `TICKET.ATTACHMENT_INVALID` |
| GET `/me/tickets/{id}` | `{ id, number, subject, status, category, orderNumber, createdAt, lastActivityAt, canReply, canClose, canReopen, messages: [ { id, kind, text, authorId: null, authorName, at, attachments } ] }`; no internal notes; staff are `Juvo BD Support`; someone else's ticket (or a guest ticket) is `404 TICKET.NOT_FOUND` |
| POST `/me/tickets/uploads` | multipart `file`, `201 { path, fileName, contentType, size, url }` |
| POST `/me/tickets/{id}/reply` | `{ "text", "attachmentPaths" }`; `TICKET.CLOSED`, `TICKET.REOPEN_WINDOW_EXPIRED` |
| POST `/me/tickets/{id}/close` | any not-closed ticket |
| POST `/me/tickets/{id}/reopen` | only Resolved or Closed, inside the window; else `TICKET.INVALID_TRANSITION` or `TICKET.REOPEN_WINDOW_EXPIRED` |

## 4. Public contact form

`POST /store/contact` (anonymous)
```json
{ "name": "Nadia Rahman", "mobile": "01712345678", "email": "nadia@example.com", "subject": "Where is my parcel", "message": "It was due yesterday.", "category": "delivery", "orderNumber": null }
```
`201 { "number": "TK-2611-000007" }`. `name`, `mobile`, `subject`, `message` are required (`400 GENERAL.VALIDATION`); the mobile is normalised to `+880…` (Bengali digits accepted); `email` is optional and used for the update messages. There is no honeypot field: the protection is the per-address rate limit, the per-mobile hourly limit and the size limits (subject 200, message 4000 characters). `429 TICKET.RATE_LIMITED` beyond the limit, `422 TICKET.ORDER_MISMATCH` for an order the mobile did not place.

## 5. Error codes used

All `TICKET.*` codes were already defined; **no new code was added**.

| Code | HTTP | Meaning |
| --- | --- | --- |
| `TICKET.NOT_FOUND` | 404 | No such ticket, or it is not yours or not in your branch |
| `TICKET.INVALID_TRANSITION` | 409 | The state machine does not allow that move |
| `TICKET.REOPEN_WINDOW_EXPIRED` | 409 | Resolved or closed longer ago than the reopen window |
| `TICKET.CLOSED` | 409 | The ticket is closed; reopen it before writing |
| `TICKET.RATE_LIMITED` | 429 | Too many new tickets from this mobile number this hour |
| `TICKET.ORDER_NOT_FOUND` | 404 | Staff named an order that does not exist or is outside their branches |
| `TICKET.ORDER_MISMATCH` | 422 | The order is not the customer's, or the mobile is not the one on the order |
| `TICKET.ASSIGNEE_NOT_FOUND` | 404 | Not an active staff member who may work on the ticket's branch |
| `TICKET.CANNED_REPLY_NOT_FOUND` | 404 | No such canned reply |
| `TICKET.ATTACHMENT_INVALID` | 422 | A path that was not uploaded by you, a missing file, or more than 5 files |
| `TICKET.BULK_LIMIT` | 422 | More than 50 tickets in one bulk request |

Also seen: `GENERAL.VALIDATION` (400), `GENERAL.CONFLICT` (409, duplicate canned reply title), `AUTH.BRANCH_FORBIDDEN` / `AUTH.FORBIDDEN` (403).

## 6. Customer messages (FR-Y04)

Raised as domain events and sent through the outbox handlers (`TicketCreated`, `TicketReplied`, `TicketResolved`), with the notification service: the customer's language (`en` or `bn`, else the shop default), SMS to the ticket's mobile and email when there is an address (the guest's, or the customer's), a log row per channel deduplicated by outbox message id plus template code, so a redelivery sends nothing twice. All three are transactional (no marketing consent is needed). An internal note raises no event.

| Template | When | SMS | Email |
| --- | --- | --- | --- |
| `ticket.received` | ticket created (any channel) | "we received your request {{TicketNumber}}" | subject and body with `{{Name}}`, `{{TicketNumber}}`, `{{Subject}}` |
| `ticket.replied` | a public staff reply | says there is an answer (no text) | the reply text as `{{Reply}}` (cut at 1500 characters) |
| `ticket.resolved` | status becomes Resolved | "your request is resolved" | same |

Each exists in English and Bengali (Bengali is a first pass for the Step 12 review) and can be edited, versioned, in the notification templates screen (`/api/v1/notifications/templates`). Variables: `TicketNumber`, `Subject`, `Name`, `Reply`.

## 7. Worker and settings

`TicketAutoCloseWorker` (hosted service, `Operations:TicketAutoCloseEnabled`, `Operations:TicketAutoCloseIntervalMinutes`, default 30) closes tickets that stayed Resolved for `Support.AutoCloseDays` (default **7**): history row "Closed automatically after N days resolved" by actor `System`, a `System` message the customer can read, and a `JobLogs` row (`support.ticket-auto-close`) when it closed something. A ticket reopened in the meantime is no longer Resolved and is left alone; a second run closes nothing again. It is switched off in the test host.

Settings (table `PlatformSettings`, defaults in code): `Support.ReopenDays` 7, `Support.AutoCloseDays` 7, `Support.MaxTicketsPerMobilePerHour` 5, `Support.Sla.ResponseHours.<Priority>`, `Support.Sla.ResolutionHours.<Priority>`.
