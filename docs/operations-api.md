# Purchasing and accounting API (Step 11A backend, part 1)

Contract for module W (suppliers, purchase orders, goods receipts, supplier invoices and payments, reorder suggestions) and module X (expenses and approval, categories, recurring expenses, finance accounts, transfers, adjustments, balances, day book). Base path `/api/v1`. Errors are RFC 9457 ProblemDetails with `code` and `correlationId`. Enums travel as text. Money is BDT, `decimal(18,2)`; amounts are rounded to 2 places (away from zero). Decisions and reasons: `docs/assumptions.md` section U.

Support tickets and reports are part 2 and are not described here.

## 1. Conventions

- **Permissions.** Every endpoint declares its permission (table per area below). The service proves the permission again in the branch of the record: a record of a branch where you lack the permission looks like **not found** (`404`, the area's `*.NOT_FOUND` code) and the denial is audit logged. Lists only show the branches where you hold the list's view permission (global scope sees all).
- **Active branch.** Creating a purchase order, expense, recurring template or account uses `branchId` from the body, else the active branch (`X-Branch-Id`), and needs the create/edit permission in that branch.
- **Lists.** `page`, `pageSize` (max 100), `sort`, `filter`, `search` (the `ListParams` of every list) answer `{items,total,page,pageSize}`. The extra query filters of each list are given below. Unknown `sort` fields fall back to the default (newest first).
- **Validation** errors are `400` with `errors: {field: [message]}` (code `VALIDATION.*` family, see `ErrorCodes`). Conflicts are `409`, refusals `403`.
- **Document numbers** are gap-free per branch, kind and month: `<KIND>-<BRANCH PREFIX>-<YYMM>-<6 digits>` (branch time, the prefix is the branch order prefix, for example `HO` in the tests or `DHK01`). Kinds: `PO-` purchase order, `GRN-` goods receipt, `EXP-` expense. Example `PO-DHK01-2611-000001`.
- **Dates.** Date-only fields (`expenseDate`, `paidOn`, `entryDate`) are calendar dates in Bangladesh time (UTC+6); defaults are "today" in that zone.
- **Settings** (table `PlatformSettings`, read through `OpsSettingsService`, defaults are safe):

| Key | Values | Default | Effect |
| --- | --- | --- | --- |
| `Purchasing.CostMethod` | `LastCost`, `WeightedAverage` | `LastCost` | How a goods receipt updates the product cost price (section 3.4). |
| `Expenses.ApprovalThreshold` | number >= 0 (BDT) | `0` (off) | A submitted expense at or below this amount is approved at once (section 7.2). |

- **Receipts and supplier documents** are uploaded first (`POST .../uploads`), then their returned `path` is sent in `attachmentPaths` (max 10 per save). Allowed: JPEG, PNG, WebP, PDF; 1 byte to 8 MB (request limit 10 MB). Folders `purchasing` and `expenses`, served via the file URL in the answer. An unknown or foreign path is `PURCHASE.ATTACHMENT_INVALID` / `EXPENSE.ATTACHMENT_INVALID`.

## 2. Suppliers

| Method and path | Permission | Purpose |
| --- | --- | --- |
| `GET /suppliers` | `purchasing.supplier.view` | List by name. Filter `active=true\|false`; `search` name, contact, phone or email. |
| `GET /suppliers/{id}` | `purchasing.supplier.view` | One supplier with `openOrders` (orders still Draft/Sent/PartlyReceived). |
| `POST /suppliers` | `purchasing.supplier.create` | Add (201). |
| `PUT /suppliers/{id}` | `purchasing.supplier.edit` | Change, or archive with `isActive=false`. Suppliers are never erased. |

Request (`SaveSupplierRequest`, same body for create and update):

```json
{ "name": "Dhaka Textiles Ltd", "contactName": "Rahim", "phone": "+8801711000000", "email": "sales@dtl.example",
  "address": "Tejgaon, Dhaka", "paymentTermsDays": 30, "taxId": "TIN-123", "notes": null, "isActive": true }
```

Response (`SupplierDto`): `id, name, contactName, phone, email, address, paymentTermsDays, taxId, notes, isActive, openOrders, createdAt`.

Rules:
- Name required, unique (`PURCHASE.SUPPLIER_NAME_TAKEN`). Limits: name 160, contact 120, phone 40, email 200, address 400, tax id 60, notes 1000. `paymentTermsDays` 0 to 365 (default 0 on create; kept when omitted on update).
- An archived supplier cannot be put on a new order or expense template (`PURCHASE.SUPPLIER_INACTIVE` on orders).

## 3. Purchase orders

### 3.1 Endpoints

All under `/purchasing/orders`.

| Method and path | Permission | Purpose |
| --- | --- | --- |
| `GET /` | `purchasing.po.view` | List, newest first. Filters `status, paymentStatus, supplierId, branchId, overdue, payable, from, to` (created date); `search` number, supplier or supplier invoice number; `sort` `createdAt`, `number`, `status`, `expected`, `due`. |
| `GET /{id}` | `purchasing.po.view` | Detail: lines, history, receipts, payments, attachments, `nextStatuses`. |
| `GET /{id}/print` | `purchasing.po.view` | Print data (buyer, supplier, numbered lines, total) for a print view or PDF. |
| `POST /` | `purchasing.po.create` | Create a draft (201). |
| `PUT /{id}` | `purchasing.po.edit` | Change a draft only. |
| `POST /uploads` | `purchasing.po.edit` | Upload a supplier document (multipart `file`, 201). |
| `POST /{id}/attachments` | `purchasing.po.edit` | Attach uploaded files at any stage. Body `{attachmentPaths}`. |
| `POST /{id}/send` | `purchasing.po.send` | Draft to Sent. |
| `POST /{id}/receive` | `purchasing.po.receive` | Book a delivery (201). |
| `POST /{id}/close-short` | `purchasing.po.receive` | PartlyReceived to Received, with a reason. |
| `POST /{id}/cancel` | `purchasing.po.cancel` | Cancel a Draft or Sent order, with a reason. |
| `POST /{id}/invoice` | `purchasing.po.edit` | Record the supplier's invoice. |
| `POST /{id}/payments` | `purchasing.payable.pay` | Record a payment to the supplier. |
| `GET /suggestions` | `purchasing.po.create` | Reorder suggestions (section 3.6). `?branchId=`. |

`GET /purchasing/receipts`, `GET /purchasing/receipts/{id}` and `POST /purchasing/receipts/{id}/apply-cost` are in section 3.5.

### 3.2 State machine

`PurchaseStatus`: `Draft -> Sent -> PartlyReceived -> Received`; `Sent -> Received` (everything arrives at once); `Draft | Sent -> Cancelled`. `Received` and `Cancelled` are final. Every move writes an append-only history row (from, to, reason, actor, time). `nextStatuses` in the detail lists the allowed moves. `SupplierPaymentStatus`: `Unpaid -> PartlyPaid -> Paid`.

### 3.3 Create and update

```json
POST /api/v1/purchasing/orders
{ "branchId": null, "supplierId": "0192...", "expectedDate": "2026-12-01", "notes": "Eid stock",
  "lines": [ { "variantId": "0192...", "quantity": 40, "unitCost": 850.00 } ],
  "attachmentPaths": [] }
```

Rules: 1 to 200 lines; `quantity` 1 to 1,000,000; `unitCost` 0 to 100,000,000; supplier must exist and be active; every variant must exist (`PURCHASE.VARIANT_NOT_FOUND`). `PUT` replaces supplier, date, notes and lines and only works on a Draft (`PURCHASE.INVALID_TRANSITION` otherwise). Response `PurchaseDetailDto`:

```json
{ "id": "...", "number": "PO-DHK01-2611-000001", "branchId": "...", "branchCode": "DHK01", "supplierId": "...", "supplierName": "Dhaka Textiles Ltd",
  "status": "Draft", "paymentStatus": "Unpaid", "expectedDate": "2026-12-01", "notes": "Eid stock", "currency": "BDT",
  "total": 34000.00, "receivedValue": 0, "amountPayable": 34000.00, "amountPaid": 0, "outstanding": 34000.00,
  "supplierInvoiceNumber": null, "supplierInvoiceDate": null, "dueDate": null, "invoiceAmount": null, "isOverdue": false,
  "sentAt": null, "firstReceivedAt": null, "completedAt": null, "cancelledAt": null, "cancelReason": null, "createdAt": "...",
  "lines": [ { "id": "...", "variantId": "...", "productId": "...", "sku": "JV-TSH-BLA-M", "productName": "...", "colorName": "Black", "size": "M",
               "orderedQuantity": 40, "receivedQuantity": 0, "rejectedQuantity": 0, "outstandingQuantity": 40, "unitCost": 850.00, "lineTotal": 34000.00 } ],
  "history": [ { "from": null, "to": "Draft", "reason": "Created", "actorName": "...", "at": "..." } ],
  "receipts": [], "payments": [], "attachments": [], "nextStatuses": ["Sent", "Cancelled"] }
```

Money fields: `total` = sum of ordered x unit cost; `receivedValue` = sum of received x unit cost; `amountPayable` = the supplier invoice amount if one was recorded, else `receivedValue` once the order is `Received`, else `total`; `outstanding` = `max(0, amountPayable - amountPaid)`; `isOverdue` = due date passed, not cancelled, not fully paid. The list item (`PurchaseListItemDto`) carries the same money fields plus `lineCount, orderedQuantity, receivedQuantity`.

### 3.4 Receiving goods (stock and cost)

```json
POST /api/v1/purchasing/orders/{id}/receive
{ "note": "Truck 1", "lines": [ { "lineId": "...", "acceptedQuantity": 30, "rejectedQuantity": 2, "note": "2 torn" } ] }
```

Response 201 `ReceiveResultDto`: `{ "receipt": GoodsReceiptDto, "order": PurchaseDetailDto }`. Receipt: `id, number (GRN-...), purchaseOrderId, purchaseOrderNumber, branchId, receivedAt, receivedByName, note, lines[{id, purchaseLineId, variantId, sku, acceptedQuantity, rejectedQuantity, unitCost, costApplied, note}]`.

Rules (one transaction):
- Order must be `Sent` or `PartlyReceived`. The order becomes `PartlyReceived` until every line has been received in full, then `Received` (first receipt sets `firstReceivedAt`, completion sets `completedAt`).
- At least one line with accepted + rejected > 0 (`PURCHASE.NOTHING_RECEIVED`); every line at most once; quantities 0 to 1,000,000; notes 500 (receipt) and 300 (line); a line must belong to the order.
- `acceptedQuantity` may not exceed the still expected units of that line (`PURCHASE.OVER_RECEIPT`, 409). Rejected units (damaged or refused) are recorded but not added to stock and not counted as received.
- **Stock.** Accepted units are added to the order branch's stock level for variants that track stock, by an append-only `StockMovement` with reason `PurchaseReceipt`, reference type `PurchaseReceipt` and the receipt id, note `Receipt <number> for <PO number>`.
- **Cost price.** Only when the receiver holds `catalog.product.view-cost` (the permission that may change costs), the product cost price is updated by `Purchasing.CostMethod`: `LastCost` sets it to the unit cost of this receipt; `WeightedAverage` sets it to `(onHandBefore x oldCost + accepted x unitCost) / (onHandBefore + accepted)`, where `onHandBefore` is the product's total on hand across all variants and branches before the receipt (the cost falls back to the unit cost when there was no stock or no earlier cost). Each receipt line stores the cost and on-hand before, and `costApplied`.
- **Receivers without the cost permission** still book stock; their lines have `costApplied=false` (rejected-only lines count as applied). A person with the cost permission later calls `POST /purchasing/receipts/{id}/apply-cost` (permission `catalog.product.view-cost`, otherwise `403 PURCHASE.COST_NOT_ALLOWED`); it applies the method to the not-yet-applied lines, oldest first, and skips a product that has a newer receipt (so an old cost never overwrites a newer one). Answers the receipt.

`POST /{id}/close-short` body `{reason}` (required, max 300): only a `PartlyReceived` order; the order becomes `Received` and `amountPayable` then follows the received value.

`POST /{id}/cancel` body `{reason}` (required, max 300): only `Draft` or `Sent`, and not once payments exist (`PURCHASE.INVALID_TRANSITION`).

### 3.5 Receipts

| Method and path | Permission | Purpose |
| --- | --- | --- |
| `GET /purchasing/receipts` | `purchasing.receipt.view` | List, newest first. Filters `purchaseOrderId, branchId, from, to`; `search` receipt or order number. |
| `GET /purchasing/receipts/{id}` | `purchasing.receipt.view` | One receipt with lines (`404 PURCHASE.RECEIPT_NOT_FOUND`, also for another branch). |
| `POST /purchasing/receipts/{id}/apply-cost` | `catalog.product.view-cost` | See 3.4. |

### 3.6 Supplier invoice and payments

```json
POST /api/v1/purchasing/orders/{id}/invoice
{ "number": "INV-8841", "invoiceDate": "2026-11-20", "dueDate": null, "amount": 33500.00 }
```

- Not on a `Draft` or `Cancelled` order (`PURCHASE.INVOICE_NOT_ALLOWED`). Number required, max 80; `amount` 0 to 1,000,000,000 or null. `dueDate` defaults to `invoiceDate + supplier.paymentTermsDays`. A given `amount` replaces the order value as `amountPayable` (and `invoiceAmount`). Answers the order.

```json
POST /api/v1/purchasing/orders/{id}/payments
{ "amount": 10000.00, "method": "Bank", "reference": "TRX-1", "paidOn": "2026-11-21", "accountId": "...", "note": null }
```

- Needs `purchasing.payable.pay`. `method`: `Cash | Bank | MobileBanking | Cheque | Other`. Amount > 0 and <= 1,000,000,000; reference max 120, note max 300; `paidOn` default today.
- Not on `Draft` or `Cancelled` (`PURCHASE.PAYMENT_NOT_ALLOWED`); not more than `outstanding` (`PURCHASE.PAYMENT_EXCEEDS_DUE`, so an order cannot be overpaid). Payments are append-only. Status moves to `PartlyPaid`, then `Paid` when `amountPaid >= amountPayable`.
- **Cash book.** `accountId` is optional. When given, the account must exist (`EXPENSE.ACCOUNT_NOT_FOUND`), belong to the order's branch (`EXPENSE.ACCOUNT_BRANCH_MISMATCH`) and be active (`EXPENSE.ACCOUNT_INACTIVE`); then a negative ledger line of kind `SupplierPayment` (source type `PurchasePayment`, description `Payment for <PO number> (<reference>)`) is written in the same transaction. Without `accountId` the payment is recorded on the order but nothing is booked into the cash book.
- Payment DTO: `id, amount, method, reference, paidOn, accountId, accountName, note, actorName, createdAt`.

### 3.7 Reorder suggestions

`GET /purchasing/orders/suggestions?branchId=` answers `SuggestionsDto { branchId, branchCode, groups[{supplierId, supplierName, lines[...]}], totalLines }`. A line is a live, stock-tracked variant of a non-archived product whose `available (onHand - reserved) <= reorderLevel` (reorder level > 0). `suggestedQuantity = 2 x reorderLevel - available - onOrder`, where `onOrder` is the open quantity on Sent and PartlyReceived orders of that branch; lines with a result <= 0 are left out. Groups are by the supplier the variant was **last** ordered from (newest non-cancelled order, only while that supplier is active), suppliers by name, the group without a supplier (`supplierId` null) last. Line: `variantId, productId, sku, productName, colorName, size, onHand, reserved, available, reorderLevel, onOrder, suggestedQuantity, lastUnitCost`.

### 3.8 Print data

`GET /{id}/print` -> `PurchasePrintDto { number, status, currency, createdAt, expectedDate, notes, buyer{name,contact,phone,email,address}, supplier{...}, lines[{no, sku, description, quantity, unitCost, lineTotal}], total, supplierInvoiceNumber, dueDate }`.

### 3.9 Errors (purchasing)

| Code | Status | Meaning |
| --- | --- | --- |
| `PURCHASE.NOT_FOUND` | 404 | Order does not exist or is in a branch you may not see. |
| `PURCHASE.SUPPLIER_NOT_FOUND` | 404 | Supplier missing (also used by expenses). |
| `PURCHASE.SUPPLIER_INACTIVE` | 409 | The supplier is archived. |
| `PURCHASE.SUPPLIER_NAME_TAKEN` | 409 | Another supplier has this name. |
| `PURCHASE.INVALID_TRANSITION` | 409 | Status does not allow the action (edit non-draft, send, receive, close short, cancel with payments). |
| `PURCHASE.VARIANT_NOT_FOUND` | 404 | A product variant on the order was not found. |
| `PURCHASE.OVER_RECEIPT` | 409 | Accepted units exceed what is still expected on the line. |
| `PURCHASE.NOTHING_RECEIVED` | 400 | No line has an accepted or rejected quantity. |
| `PURCHASE.PAYMENT_NOT_ALLOWED` | 409 | Order is Draft or Cancelled. |
| `PURCHASE.PAYMENT_EXCEEDS_DUE` | 409 | Payment is more than the outstanding amount. |
| `PURCHASE.INVOICE_NOT_ALLOWED` | 409 | Order is Draft or Cancelled. |
| `PURCHASE.RECEIPT_NOT_FOUND` | 404 | Goods receipt missing or in another branch. |
| `PURCHASE.COST_NOT_ALLOWED` | 403 | apply-cost without the cost permission. |
| `PURCHASE.ATTACHMENT_INVALID` | 400 | Bad file type or size, or unknown path. |
| `EXPENSE.ACCOUNT_NOT_FOUND`, `EXPENSE.ACCOUNT_BRANCH_MISMATCH`, `EXPENSE.ACCOUNT_INACTIVE` | 404/409/409 | Payment account problems (section 3.6). |

## 4. Expenses

### 4.1 Endpoints

All under `/expenses`.

| Method and path | Permission | Purpose |
| --- | --- | --- |
| `GET /` | `accounting.expense.view` | List, newest first, with the total of the filtered rows. Filters `status, categoryId, branchId, accountId, supplierId, from, to` (expense date); `search` number, reference, notes; `sort` `date`, `amount`, `number`, `status`. |
| `GET /{id}` | `accounting.expense.view` | Detail with receipts and `nextStatuses`. |
| `POST /` | `accounting.expense.create` | Record a draft (201). |
| `PUT /{id}` | `accounting.expense.edit` | Change a draft. |
| `DELETE /{id}` | `accounting.expense.delete` | Remove a draft (archived, 204). |
| `POST /uploads` | `accounting.expense.create` | Upload a receipt or bill (multipart `file`, 201). |
| `POST /{id}/submit` | `accounting.expense.create` | Draft to Submitted (may auto-approve). |
| `POST /{id}/approve` | `accounting.expense.approve` | Submitted to Approved; books the cash book. Body `{note}` (max 500). |
| `POST /{id}/reject` | `accounting.expense.approve` | Submitted to Rejected. Body `{reason}` (required, max 500). |
| `POST /{id}/return-to-draft` | `accounting.expense.create` | Withdraw a Submitted or reopen a Rejected expense. |
| `GET /categories` | `accounting.expense.view` | Categories (`includeInactive`) with `expenseCount`. |
| `POST /categories` | `accounting.expense.edit` | Add (201). |
| `PUT /categories/{id}` | `accounting.expense.edit` | Rename, move, archive with `isActive=false`. |
| `GET /recurring` | `accounting.expense.view` | Monthly templates (`?branchId=`). |
| `POST /recurring` | `accounting.expense.edit` | Add a template (201). |
| `PUT /recurring/{id}` | `accounting.expense.edit` | Change or switch off with `isActive=false`. |
| `POST /recurring/run` | `accounting.expense.edit` | Run the generator now for your branches. |

### 4.2 State machine and approval

`ExpenseStatus`: `Draft -> Submitted`; `Submitted -> Approved | Rejected | Draft` (withdrawn); `Rejected -> Draft` (fix and send again). `Approved` is final.

- Only a Draft can be edited or removed (`EXPENSE.NOT_EDITABLE`). Any other move not in the table is `EXPENSE.INVALID_TRANSITION`.
- **Submit.** Records `submittedBy`. If `Expenses.ApprovalThreshold` is above 0 and `amount <= threshold`, the expense is approved at once by the system (note `Below the approval threshold`, `autoApproved=true`) and booked as below; otherwise it waits as `Submitted`. The account must still be active (`EXPENSE.ACCOUNT_INACTIVE`).
- **Approve.** Needs `accounting.expense.approve`. **You cannot approve an expense you submitted yourself** (`403 EXPENSE.SELF_APPROVAL`); the **Super Admin is excepted**. Approval writes one negative ledger line of kind `Expense` (source type `Expense`, date = `expenseDate`, description `Expense <number> (<reference>)`) in the expense's account, so an expense is booked exactly once.
- **Reject.** Reason required; nothing is booked. The author may `return-to-draft`, fix and submit again.

### 4.3 Request and response

```json
POST /api/v1/expenses
{ "branchId": null, "categoryId": "...", "expenseDate": "2026-11-15", "amount": 2500.00, "method": "Cash", "accountId": "...",
  "supplierId": null, "reference": "Bill 77", "notes": "Shop rent top-up", "attachmentPaths": ["expenses/2026/11/abc.pdf"] }
```

Rules: amount 0.01 to 100,000,000; `method` as in 3.6; reference max 120, notes max 1000; `expenseDate` not more than 1 day ahead of today and not older than 5 years; category exists and is active (`EXPENSE.CATEGORY_NOT_FOUND`, `EXPENSE.CATEGORY_INACTIVE`); account exists, active and in the expense's branch (`EXPENSE.ACCOUNT_*`); supplier, when given, exists (`PURCHASE.SUPPLIER_NOT_FOUND`).

`ExpenseDetailDto`: `id, number (EXP-...), branchId, branchCode, categoryId, categoryName, expenseDate, amount, method, accountId, accountName, supplierId, supplierName, reference, notes, status, autoApproved, recurringExpenseId, createdAt, submittedAt, decidedAt, decidedByName, decisionNote, attachments[{id,fileName,contentType,size,url}], nextStatuses`.

The list answers `ExpenseListDto { expenses: {items,total,page,pageSize}, totalAmount }`; an item has `id, number, branchId, branchCode, categoryId, categoryName, expenseDate, amount, method, accountId, accountName, supplierId, reference, status, autoApproved, isRecurring, attachmentCount, createdAt`.

### 4.4 Categories

`SaveExpenseCategoryRequest { nameEn, nameBn, parentId, isActive }`; `ExpenseCategoryDto { id, nameEn, nameBn, parentId, isActive, expenseCount }`. Name required, max 120 (both languages); names unique among siblings (`EXPENSE.CATEGORY_NAME_TAKEN`); a parent must exist (`EXPENSE.CATEGORY_NOT_FOUND`) and only one level of sub-categories is allowed (validation error on `parentId`). Categories are archived (`isActive=false`), never deleted.

### 4.5 Recurring expenses

```json
POST /api/v1/expenses/recurring
{ "branchId": null, "title": "Shop rent", "categoryId": "...", "amount": 60000.00, "method": "Bank", "accountId": "...", "supplierId": null,
  "notes": null, "dayOfMonth": 5, "startDate": "2026-12-01", "endDate": null, "isActive": true }
```

`RecurringExpenseDto`: `id, branchId, branchCode, title, categoryId, categoryName, amount, method, accountId, accountName, supplierId, notes, dayOfMonth, startDate, endDate, isActive, lastGeneratedPeriod`.

Rules: title required max 160; notes max 1000; amount 0.01 to 100,000,000; `dayOfMonth` 1 to 28; `endDate` not before `startDate`; category, account (same branch) and supplier must exist. Generation (background job and `POST /recurring/run`, answering `{templates, created, autoApproved}`): for each active template whose start date has passed, every due month (day reached, within start and end, not already generated, at most the last 12 months) gets **one Submitted expense** (reference = the title, `isRecurring=true`), which then follows the normal submit rule (auto-approved at or below the threshold). A month already generated is skipped (a unique index keeps it single), so running twice is safe. A template pointing at an inactive account or category is skipped until the owner fixes it.

## 5. Finance accounts and cash book

### 5.1 Endpoints

All under `/accounts`.

| Method and path | Permission | Purpose |
| --- | --- | --- |
| `GET /` | `accounting.cashbook.view` | Accounts (`?branchId=&includeInactive=`) with current balance. |
| `POST /` | `accounting.cashbook.edit` | Add an account with an opening balance (201). |
| `PUT /{id}` | `accounting.cashbook.edit` | Rename, change type, switch off. Opening balance is fixed. |
| `GET /{id}/entries` | `accounting.cashbook.view` | Append-only ledger lines of one account, newest first (paged). |
| `POST /transfers` | `accounting.cashbook.edit` | Move money between two accounts (201). |
| `POST /adjustments` | `accounting.cashbook.edit` | Signed correction with a reason (201). |
| `GET /balances` | `accounting.cashbook.view` | Balance of every account as of a date. |
| `GET /daybook` | `accounting.cashbook.view` | Merged day book for a date range. |

### 5.2 Accounts

`SaveAccountRequest { branchId, name, type, accountNumber, note, openingBalance, openingDate }`; `type`: `Cash | Bank | MobileBanking`. Name required max 120, unique within the branch (`409 GENERAL.CONFLICT`), account number max 60, note max 300; `openingDate` defaults to today (branch time). `UpdateAccountRequest { name, type, accountNumber, note, isActive }`. `AccountDto { id, branchId, branchCode, name, type, accountNumber, note, openingBalance, openingDate, isActive, balance }` where `balance = openingBalance + sum of the account's ledger lines`. Nothing else is stored, so every balance can be explained. Correct a wrong opening figure with an adjustment.

### 5.3 The cash book (ledger)

A ledger line (`LedgerEntryDto { id, accountId, accountName, entryDate, kind, amount, description, sourceType, sourceId, transferId, actorName }`) is **append-only** and signed (positive = money in, negative = out). Kinds: `Expense` (negative, from an approved expense), `SupplierPayment` (negative, from a supplier payment with an account), `TransferOut` (negative), `TransferIn` (positive), `Adjustment` (positive or negative).

**Transfer** `{ fromAccountId, toAccountId, amount, date, note }`: two different accounts, amount > 0, note max 200 (else `EXPENSE.TRANSFER_INVALID`, 400); both accounts must be visible and editable by you and active (`EXPENSE.ACCOUNT_INACTIVE`). Writes two lines sharing one `transferId` (source type `Transfer`) and answers `{ transferId, out, in }`. The accounts may belong to different branches only if you may edit both.

**Adjustment** `{ accountId, amount, date, reason }`: `amount` non-zero (signed), `reason` required, max 250 (validation error on `reason`); description `Adjustment: <reason>`. Answers the new line. Lines are never edited or removed.

### 5.4 Balances

```json
GET /api/v1/accounts/balances?asOf=2026-11-30&branchId=
{ "asOf": "2026-11-30",
  "accounts": [ { "id": "...", "name": "Cash drawer", "type": "Cash", "branchId": "...", "branchCode": "DHK01", "isActive": true,
                  "opening": 10000.00, "moneyIn": 5000.00, "moneyOut": 2500.00, "balance": 12500.00 } ],
  "total": 12500.00, "derivedReceipts": 48200.00 }
```

`asOf` defaults to today. Per account: `moneyIn` and `moneyOut` are sums of positive and negative ledger lines with entry date up to `asOf`; `balance = opening + moneyIn - moneyOut`; `total` sums the balances. **`derivedReceipts`** is the net (customer receipts + settled COD cash - completed refunds) read from payment data from the beginning up to `asOf`, in the branches you may see (or the `branchId` filter). It is money that belongs to no account, so it is **not** part of any balance or of `total`.

### 5.5 Day book

`GET /accounts/daybook?from=&to=&branchId=&accountId=&page=&pageSize=` (default `to` = today, `from` = `to - 30 days`; `from` after `to` is a validation error). Answers

```json
{ "entries": { "items": [ { "date": "2026-11-15", "at": "...", "kind": "Expense", "amount": -2500.00, "description": "Expense EXP-DHK01-2611-000004 (Bill 77)",
                            "branchId": "...", "branchCode": "DHK01", "accountId": "...", "accountName": "Cash drawer", "sourceType": "Expense", "sourceId": "...", "derived": false } ],
               "total": 12, "page": 1, "pageSize": 25 },
  "totalIn": 53200.00, "totalOut": 2500.00, "net": 50700.00, "from": "2026-10-16", "to": "2026-11-15" }
```

The day book **merges two sources**, newest first:
1. **Stored ledger lines** (`derived=false`): the lines above, `kind` = the ledger kind (`Expense`, `SupplierPayment`, `TransferOut`, `TransferIn`, `Adjustment`), filtered by entry date.
2. **Derived rows** (`derived=true`, no account, read live from payment data and **never copied into the ledger**): `CustomerReceipt` (non-COD payments with `paidAt` in range whose status is Paid, PartiallyRefunded or Refunded, amount positive, source `Payment`), `CodSettlement` (COD settlement batches with status Settled, `receivedTotal` positive, source `CodSettlement`) and `Refund` (completed refunds, amount negative, source `Refund`). Dates use Bangladesh days.

`totalIn`/`totalOut` add the positive and negative amounts of both sources over the whole range (not only the page); `net = totalIn - totalOut`. Setting `accountId` shows ledger lines of that account only (derived rows are left out because they belong to no account). Branch scoping applies to both sources. Paging counts stored and derived rows together.

### 5.6 Errors (accounting)

| Code | Status | Meaning |
| --- | --- | --- |
| `EXPENSE.NOT_FOUND` | 404 | Expense missing or in a branch you may not see. |
| `EXPENSE.INVALID_TRANSITION` | 409 | The status does not allow this move. |
| `EXPENSE.NOT_EDITABLE` | 409 | Only a draft can be edited or removed. |
| `EXPENSE.SELF_APPROVAL` | 403 | You submitted this expense yourself (Super Admin excepted). |
| `EXPENSE.CATEGORY_NOT_FOUND` | 404 | Category (or parent) missing. |
| `EXPENSE.CATEGORY_INACTIVE` | 409 | Category archived. |
| `EXPENSE.CATEGORY_NAME_TAKEN` | 409 | Same name among siblings. |
| `EXPENSE.ACCOUNT_NOT_FOUND` | 404 | Account missing or in a branch you may not see. |
| `EXPENSE.ACCOUNT_INACTIVE` | 409 | Account switched off (expense, submit, transfer, supplier payment). |
| `EXPENSE.ACCOUNT_BRANCH_MISMATCH` | 409 | Account belongs to another branch than the expense, template or order. |
| `EXPENSE.RECURRING_NOT_FOUND` | 404 | Template missing or in another branch. |
| `EXPENSE.ATTACHMENT_INVALID` | 400 | Bad file type or size, or unknown path. |
| `EXPENSE.TRANSFER_INVALID` | 400 | Same account twice, amount <= 0 or note too long. |
| `GENERAL.CONFLICT` | 409 | Account name already used in the branch. |

## 6. Permission summary

| Area | Permissions |
| --- | --- |
| Suppliers | `purchasing.supplier.view`, `.create`, `.edit` |
| Purchase orders | `purchasing.po.view`, `.create` (also suggestions), `.edit` (also invoice, uploads, attachments), `.send`, `.receive` (also close short), `.cancel` |
| Receipts | `purchasing.receipt.view`; apply-cost needs `catalog.product.view-cost` |
| Supplier payments | `purchasing.payable.pay` |
| Expenses | `accounting.expense.view`, `.create` (also submit, return to draft, uploads), `.edit` (also categories, recurring), `.delete`, `.approve` (approve and reject) |
| Accounts | `accounting.cashbook.view`, `accounting.cashbook.edit` |

## 7. Branch scoping

Purchase orders, goods receipts, supplier payments, expenses, recurring templates, accounts and ledger lines belong to one branch (`IBranchScoped`). Reads filter to the branches where you hold the view permission; single-record reads and every write prove the permission in the record's branch, and a record elsewhere answers `404` with the area's not-found code (never 403) and an `AccessDenied` audit entry. A purchase payment or expense must use an account of the same branch as the order or expense. Suppliers and expense categories are global.

## 8. Contract gaps noticed

- `purchasing.receipt.create` and `purchasing.payable.view` exist in the permission catalog but no endpoint uses them (receiving needs `purchasing.po.receive`; payables are read through the order list filters `payable` and `overdue` under `purchasing.po.view`). `accounting.pnl.view` and `accounting.export.run` are also unused by part 1.
- `EXPENSE.INSUFFICIENT_BALANCE` and `EXPENSE.CATEGORY_IN_USE` are defined but never returned: transfers and expenses may drive an account negative, and categories in use are simply archivable.
- The goods receipt number kind is `GRN-` (not `GR-`).
- A supplier payment without `accountId` is recorded on the order but not booked into any cash book account, so it never appears in the day book.
- Derived day-book rows have no account, so they are excluded when `accountId` is filtered, and `derivedReceipts` is outside `total`.
- Transfers are not blocked across branches when the caller may edit both accounts; the ledger lines then belong to each account's own branch.
- The account-name conflict uses the generic `GENERAL.CONFLICT` code rather than an `ACCOUNT.*` code.
- Ledger lines have no reversal for a mistaken approved expense (an approved expense is final); the only correction is an `Adjustment`.
