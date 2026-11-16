# Marketing API (module AC)

Segments, consent-based SMS and email campaigns, automations and their results. Base path `/api/v1`. Lists answer `{items,total,page,pageSize}` and take `page`, `pageSize` (max 100), `sort`, `search`. Errors are RFC 9457 ProblemDetails with a stable `code` (section 9). Enums travel as strings. Marketing is a global module: the permissions are not branch scoped.

## 1. The law of this module: consent

* A marketing message (campaign, abandoned cart, review request, win-back) is sent only when the **latest** `ConsentLog` row of the customer for that channel (`MarketingSms` for SMS, `MarketingEmail` for email) says granted. No row, or a latest row "withdrawn", means no message, ever, also when a segment contains the person.
* The check lives in the shared send path `NotificationService.QueueCustomAsync` (same rules as the template path: channel switched on, dedupe key, consent, outbox with retry, `NotificationLog` category `Marketing`). Campaign and automation code cannot skip it.
* Consent is checked twice for campaigns: when the recipient rows are written (consent missing is recorded as `NoConsent`) and again at the moment of sending (consent withdrawn in between gives a `Skipped` log row, never a queued message).
* Transactional messages (orders, payments, OTP) never need consent and are not touched by this module.
* **Back in stock** is a message the customer asked for: category `Transactional`, no consent needed, but a global opt-out is honoured (the latest decision for the channel is "no", from the profile or an unsubscribe link, gives `OptedOut`). A guest who gave only a number and never decided is messaged.
* Every email carries an unsubscribe link; every SMS carries the short opt-out text (setting `Marketing.SmsOptOutText`, default `Stop: {unsubscribeLink}`).

## 2. Permissions

| Code | Allows |
| --- | --- |
| `marketing.segment.view` | list/read segments, counts, members |
| `marketing.segment.edit` | create, change, copy, delete segments |
| `marketing.campaign.view` | list/read campaigns, preview, recipients, results, read settings |
| `marketing.campaign.create` | create, change, delete a draft; send a test to yourself |
| `marketing.campaign.send` | schedule, send now, cancel, change settings |
| `marketing.automation.view` | list/read automations, dry run, results |
| `marketing.automation.edit` | switch on/off, change delay, texts, caps |

No new permission codes were added. The two public endpoints (section 8) are anonymous and rate limited.

## 3. Segments (FR-AC01)

A segment is a saved, typed definition: rules joined by `and` (every rule) or `or` (any rule), 1 to 20 rules. It is validated, then translated into one set-based SQL query over **active, not archived customers** (sub-queries on orders, addresses, consent, wishlist, carts). Nothing is loaded into memory except one page of members. Nothing a user types is executed as text.

```json
{ "name": "Dhaka big spenders", "description": "optional",
  "definition": { "combinator": "and", "rules": [
    { "type": "highSpenders", "minSpend": 10000, "periodDays": 90 },
    { "type": "location", "level": "district", "ids": ["<district id>"] } ] } }
```

| `type` | Fields | Matches |
| --- | --- | --- |
| `newCustomers` | `days` | joined in the last N days |
| `repeatBuyers` | `minOrders` (2 to 1000), optional `periodDays` | at least N counted orders (in the period) |
| `highSpenders` | `minSpend` (> 0, BDT), optional `periodDays` (lifetime when missing) | sum of counted orders at least the amount |
| `inactive` | `days` | joined more than N days ago, no order placed and no sign-in in the last N days |
| `location` | `level` `division`/`district`, `ids` | the **default** address is in one of the divisions or districts |
| `consent` | `channel` `sms`/`email`, optional `granted` (default true) | latest consent decision for the channel is yes (or no) |
| `lastOrderStatus` | `status` (an order status) | status of the newest order (any status) |
| `wishlist` | optional `productIds` | has a wishlist item (of those products) |
| `cart` | optional `idleHours` | has a cart with items (untouched for at least N hours) |

"Counted orders" are all orders except `Cancelled` and `Returned`. A field that does not belong to the rule type is refused. Errors name the rule, for example `rules[1].minSpend`.

Built-in segments (seeded on first use, cannot be changed or deleted, can be copied): `new-customers` (30 days), `repeat-buyers` (2 orders), `high-spenders` (BDT 10,000 lifetime), `inactive-60`.

| Method and path | Permission | Notes |
| --- | --- | --- |
| `GET /marketing/segments` | segment.view | built-in first; `search` by name |
| `GET /marketing/segments/{id}` | segment.view | with `definition` |
| `POST /marketing/segments` | segment.edit | `201`; name must be unique |
| `PUT /marketing/segments/{id}` | segment.edit | built-in: `MARKETING.SEGMENT_BUILT_IN` |
| `DELETE /marketing/segments/{id}` | segment.edit | in use: `MARKETING.SEGMENT_IN_USE` |
| `POST /marketing/segments/{id}/copy` | segment.edit | `201`, name `"X (copy)"` |
| `GET /marketing/segments/{id}/count` | segment.view | live count |
| `POST /marketing/segments/preview-count` | segment.view | body `{definition}` for unsaved rules |
| `GET /marketing/segments/{id}/members` | segment.view | `sort`: `name` (default), `spend`, `-spend`, `lastOrder`, `-lastOrder`, `-createdAt`; `search` |

```json
{ "total": 120, "reachable": { "sms": 80, "email": 35 } }
```

`reachable` = consented on the channel **and** contactable (SMS: mobile; email: address). Member rows: `{customerId,name,mobile,email,lastOrderAt,orderCount,totalSpend,smsReachable,emailReachable}`. Mobile and email are masked unless the staff member also holds `customers.customer.view-pii`; without it, `search` matches the name only.

## 4. Campaigns (FR-AC02, FR-AC04)

A campaign targets one segment on one channel (`Sms` or `Email`). Texts: `bodyEn` (required), `bodyBn` (optional, used for customers whose language is Bengali), `subjectEn` (required for email), `subjectBn`. Placeholders: `{customerName}` (first name), `{shopName}`, `{link}`, `{unsubscribeLink}`; others are refused (`MARKETING.CAMPAIGN_INVALID`). Statuses: `Draft`, `Scheduled`, `Sending`, `Sent`, `Cancelled`.

| Method and path | Permission | Notes |
| --- | --- | --- |
| `GET /marketing/campaigns` | campaign.view | the campaign report; filters `status`, `channel`, `segmentId`; `search` by name |
| `GET /marketing/campaigns/{id}` | campaign.view | includes `counts`, `deferredByQuietHours` |
| `POST /marketing/campaigns` | campaign.create | `201` draft |
| `PUT /marketing/campaigns/{id}` | campaign.create | draft or scheduled only (`MARKETING.CAMPAIGN_NOT_EDITABLE`) |
| `DELETE /marketing/campaigns/{id}` | campaign.create | only a campaign that never started |
| `POST /marketing/campaigns/{id}/preview` | campaign.view | real counts and sample messages |
| `POST /marketing/campaigns/{id}/schedule` | campaign.send | body `{scheduledAt}` with offset, e.g. `2026-12-01T10:00:00+06:00` |
| `POST /marketing/campaigns/{id}/send` | campaign.send | send now |
| `POST /marketing/campaigns/{id}/cancel` | campaign.send | draft, scheduled or sending |
| `POST /marketing/campaigns/{id}/test` | campaign.create | body `{language}`; to your own staff email or phone |
| `GET /marketing/campaigns/{id}/recipients` | campaign.view | filters `status`, `reason`; contacts masked |
| `GET /marketing/campaigns/{id}/results` | campaign.view | section 6 |

```json
POST /marketing/campaigns
{ "name": "Winter sale", "segmentId": "…", "channel": "Email", "subjectEn": "Winter sale",
  "bodyEn": "Hi {customerName}, new at {shopName}: {link}", "bodyBn": "হ্যালো {customerName}, {shopName}-এ নতুন: {link}" }
```

Preview answer (the numbers are what sending would do now):

```json
{ "segmentMembers": 5, "recipients": 2, "skippedNoConsent": 2, "skippedNoContact": 1, "skippedFrequencyCap": 0,
  "quietHoursNow": false, "smsDeferred": false,
  "samples": [ { "language": "en", "subject": "Winter sale", "body": "Hi Nadia, new at Juvo BD: https://…\n\n--\nUnsubscribe: https://…/api/v1/store/unsubscribe?token=…", "characters": 120 } ],
  "unknownPlaceholders": [] }
```

Sending flow:

1. **Fix the recipients.** Every active segment member gets exactly one `CampaignRecipient` row (unique per campaign and customer, so a customer reachable in two ways is one recipient). A member with no contact on the channel is `Skipped/NoContact`, one without consent `Skipped/NoConsent`, the rest are `Pending`. Written in batches of 500 before anything is sent; a crash resumes with the missing rows only.
2. **Send in batches** (200 at most) of `Pending` recipients, each batch one transaction under an application lock `campaign-send:<id>`. Per recipient: current contact, frequency cap (`FrequencyCap`), language (customer preference, else shop default), text, then `QueueCustomAsync` with consent required and dedupe key `campaign:<campaignId>:<customerId>`.
3. **Complete** when no recipient is `Pending`.

`POST /send` does step 1 and the first batch at once; the `CampaignSendWorker` carries on with the rest (and starts due scheduled campaigns). Crash safety: the log row, outbox message and recipient mark are one save; a message whose recipient mark was lost is adopted through the dedupe key (unique index on `DedupeKey` + channel), never sent twice.

**Throttle.** At most `Marketing.ThrottlePerMinute` (default 600) messages per campaign per minute (counted from recipients queued in the last 60 seconds).

**Quiet hours.** SMS (campaigns and automations) is held back between `Marketing.QuietHours.Start` and `.End` (default 21:00 to 09:00, Asia/Dhaka, UTC+06:00). The campaign stays `Sending`, recipients stay `Pending` and `deferredByQuietHours` is true; the worker sends after the window. Email is not affected.

**Frequency cap.** At most `Marketing.FrequencyCap.MaxMessages` (default 3; 0 = off) campaign messages per customer per `Marketing.FrequencyCap.Days` (default 7). Counts campaign messages (template `marketing.campaign`) that are `Pending`, `Sent` or `Failed`. Automations have their own cap.

**Unsubscribe.** Every message of a campaign carries a signed token (customer, channel, campaign; HMAC-SHA256 with a key derived from the platform signing key; it never expires). Link: `{Marketing:UnsubscribeUrl}?token=…`, default `http://localhost:5080/api/v1/store/unsubscribe`. A web page may use its own address and call the endpoint (section 8). The consent row written has source `unsubscribe:<campaignId>` (or `unsubscribe-link`).

## 5. Automations (FR-AC03)

Four fixed automations, **off by default** (`isEnabled=false`), each with a channel (`Sms` default or `Email`), `delayMinutes`, `windowDays`, `capDays`, texts in English and Bengali.

| Kind | Trigger | `delayMinutes` means | Default delay / window / cap | Category, consent | Placeholders |
| --- | --- | --- | --- | --- | --- |
| `AbandonedCart` | signed-in customer, cart with items, not expired, no order placed since the last cart change | idle time of the cart | 180 min / 7 d / 7 d | Marketing, consent required | `customerName shopName link itemCount unsubscribeLink` |
| `BackInStock` | open `notify-me` request and the variant has available stock | minimum wait after the request | 0 / 365 d / 0 | Transactional, opt-out honoured | `customerName shopName link productName variantName` |
| `ReviewRequest` | order `Delivered`, no return request (other than rejected/cancelled) | time after `DeliveredAt` | 10080 min (7 d) / 14 d / 0 | Marketing, consent required | `customerName shopName link orderNumber unsubscribeLink` |
| `WinBack` | has a counted order, but no order and no sign-in for the delay | inactivity | 86400 min (60 d) / 30 d / 90 d | Marketing, consent required | `customerName shopName link unsubscribeLink` |

`windowDays` stops a freshly switched-on automation from messaging events that are too old (abandoned cart: carts idle longer than delay + window; review request: deliveries older than delay + window). It is not used by win-back and back-in-stock. `capDays` is the per-customer cap: no second message of the same automation within N days (0 = none). Idempotency keys: `automation:abandoned-cart:<cartId>:<last change>`, `automation:review-request:<orderId>`, `automation:win-back:<customerId>:<last order date>`, `automation:back-in-stock:<subscriptionId>`; a second run finds nothing new. `{link}` is the cart page (`<shop>/cart`), the product page (`<shop>/products/<slug>`), the order page (`<shop>/account/orders/<number>`) or the shop; `<shop>` is `Marketing.LinkUrl` or `Auth:FrontendBaseUrl`.

| Method and path | Permission |
| --- | --- |
| `GET /marketing/automations` | automation.view |
| `GET /marketing/automations/{kind}` | automation.view |
| `PUT /marketing/automations/{kind}` | automation.edit (only the fields you send) |
| `POST /marketing/automations/dry-run` | automation.view |
| `GET /marketing/automations/{kind}/results` | automation.view |

Dry run (nothing is written; it uses the same code as a run):

```json
{ "at": "…", "quietHoursNow": false, "automations": [
  { "kind": "AbandonedCart", "name": "Abandoned cart", "isEnabled": true, "candidates": 2,
    "wouldSend": 1, "wouldSendIfEnabled": 1, "deferredByQuietHours": 0,
    "skipped": [ { "reason": "NoConsent", "count": 1 } ] } ] }
```

`wouldSend` is 0 while the automation is off; `wouldSendIfEnabled` says what switching it on would do. Skip reasons: `NoConsent`, `OptedOut`, `NoContact`, `FrequencyCap`, `AlreadySent`, `ChannelOff`.

Results: `{kind,name,isEnabled,queued,sent,failed,skipped,attribution,lastRunAt}` from the notification log of the automation's template (`automation.abandoned-cart`, `automation.back-in-stock`, `automation.review-request`, `automation.win-back`).

The `AutomationWorker` runs all switched-on automations every `MarketingJobs:AutomationIntervalSeconds` (default 120). Each run is one transaction per automation under the application lock `automation:<kind>`.

## 6. Results and attribution (FR-AC04)

```json
GET /marketing/campaigns/{id}/results
{ "campaignId": "…", "name": "…", "status": "Sent", "channel": "Sms",
  "recipients": 4, "pending": 0, "inFlight": 0, "sent": 3, "delivered": 3, "failed": 0, "skipped": 1,
  "skippedByReason": [ { "reason": "NoConsent", "count": 1 } ],
  "unsubscribed": 0,
  "attribution": { "orders": 2, "revenue": 8000.00, "windowDays": 7 } }
```

* `sent` = messages whose log row is `Sent`; `failed` = `Failed` or `DeadLetter`; `inFlight` = queued but not yet delivered. The sandbox senders report no delivery receipts, so `delivered` equals `sent` (a real provider adapter can separate them).
* Skip reasons: `NoConsent`, `NoContact`, `FrequencyCap`, `ChannelOff`, `Cancelled`, `Inactive`.
* `unsubscribed` = customers who unsubscribed through a link of this campaign.
* **Attribution** (last touch, no change to the order table): an order is credited to a `Sent` message when it was placed by that customer at or after the send and within `Marketing.AttributionWindowDays` (default 7), is not `Cancelled` or `Returned`, and no later `Sent` marketing message reached the customer between the message and the order (the newest touch wins, across campaigns and automations). Revenue is the order grand total.

## 7. Settings

`GET /marketing/settings` (campaign.view), `PUT /marketing/settings` (campaign.send; only the fields sent).

| Field | Key in `PlatformSettings` | Default | Range |
| --- | --- | --- | --- |
| `quietHoursEnabled` | `Marketing.QuietHours.Enabled` | true | |
| `quietHoursStart`, `quietHoursEnd` | `Marketing.QuietHours.Start`, `.End` | `21:00`, `09:00` | `HH:mm`, Dhaka time |
| `frequencyCapMessages` | `Marketing.FrequencyCap.MaxMessages` | 3 | 0 to 100 (0 = off) |
| `frequencyCapDays` | `Marketing.FrequencyCap.Days` | 7 | 1 to 365 |
| `throttlePerMinute` | `Marketing.ThrottlePerMinute` | 600 | 1 to 100000 |
| `attributionWindowDays` | `Marketing.AttributionWindowDays` | 7 | 1 to 90 |
| `shopName` | `Marketing.ShopName` | Juvo BD | up to 60 characters |
| `linkUrl` | `Marketing.LinkUrl` | `Auth:FrontendBaseUrl` | absolute http(s) address |
| `maxNotifyMePerMobile` | `Marketing.NotifyMe.MaxPerMobile` | 10 | 1 to 100 |
| `smsOptOutText` | `Marketing.SmsOptOutText` | `Stop: {unsubscribeLink}` | up to 80, must hold `{unsubscribeLink}` |

Configuration: `Marketing:UnsubscribeUrl`; workers `MarketingJobs:CampaignSendEnabled` / `CampaignSendIntervalSeconds` (default 30) and `MarketingJobs:AutomationsEnabled` / `AutomationIntervalSeconds` (default 120). The channel switches stay in the notification settings (`Notifications.SmsEnabled`, `Notifications.EmailEnabled`): a campaign for a switched-off channel is refused (`MARKETING.CHANNEL_OFF`).

## 8. Public endpoints (anonymous, rate limited with the `checkout` policy)

**`GET` or `POST /store/unsubscribe?token=…`** (POST also accepts a form field `token`, for one-click mail clients). Turns the marketing consent of that channel off with a timestamp. Idempotent: the second call answers `alreadyUnsubscribed:true` and writes nothing.

```json
{ "channel": "email", "unsubscribed": true, "alreadyUnsubscribed": false }
```

Bad, tampered or empty token: `400 MARKETING.UNSUBSCRIBE_TOKEN_INVALID`.

**`POST /store/products/{slug}/notify-me`** body `{ "variantId": "…", "mobile": "01712345678", "email": "optional" }`. A signed-in customer sends only `variantId`; a product with one variant needs no `variantId`. Answers `{subscriptionId, variantId, alreadySubscribed}`. Refused when the variant has stock (`409 MARKETING.VARIANT_IN_STOCK`), the number is invalid (`400 MARKETING.CONTACT_REQUIRED`), the product or variant is unknown (`404 MARKETING.VARIANT_NOT_FOUND`) or the number already waits for the maximum of items (`429 MARKETING.SUBSCRIPTION_LIMIT`). One open request per variant and number.

Note: anyone can enter a number, so a person may receive one back-in-stock text for a number they did not enter. The limits (per client address and per number, one message per request, opt-out honoured) bound this; see the step document.

## 9. Error codes

| Code | HTTP | Meaning |
| --- | --- | --- |
| `MARKETING.SEGMENT_NOT_FOUND` | 404 | The segment does not exist. |
| `MARKETING.SEGMENT_INVALID` | 400 | The segment name or rules are not valid (field errors name the rule). |
| `MARKETING.SEGMENT_IN_USE` | 409 | A campaign uses the segment, so it cannot be deleted. |
| `MARKETING.SEGMENT_BUILT_IN` | 409 | A built-in segment cannot be changed or deleted; copy it. |
| `MARKETING.CAMPAIGN_NOT_FOUND` | 404 | The campaign does not exist. |
| `MARKETING.CAMPAIGN_INVALID` | 400 | The campaign data is not valid (name, channel, text, unknown placeholder, subject). |
| `MARKETING.CAMPAIGN_NOT_EDITABLE` | 409 | Only a draft or scheduled campaign can be changed. |
| `MARKETING.CAMPAIGN_INVALID_STATE` | 409 | The action does not fit the campaign status (send twice, cancel a sent one, delete a started one). |
| `MARKETING.CHANNEL_OFF` | 409 | The campaign channel is switched off in the notification settings. |
| `MARKETING.SCHEDULE_INVALID` | 400 | The send time is missing, in the past or more than a year ahead. |
| `MARKETING.TEST_NO_CONTACT` | 409 | The staff profile has no email or phone to send the test to. |
| `MARKETING.UNSUBSCRIBE_TOKEN_INVALID` | 400 | The unsubscribe link is damaged, tampered with or empty. |
| `MARKETING.AUTOMATION_NOT_FOUND` | 404 | No such automation. |
| `MARKETING.AUTOMATION_INVALID` | 400 | The automation settings are not valid (delay, window, cap, channel, text, placeholder). |
| `MARKETING.SETTINGS_INVALID` | 400 | Reserved; settings errors currently use `GENERAL.VALIDATION` with field errors. |
| `MARKETING.VARIANT_NOT_FOUND` | 404 | The product or variant for a back-in-stock request does not exist (or the variant was not chosen). |
| `MARKETING.VARIANT_IN_STOCK` | 409 | The variant is in stock, so no request is needed. |
| `MARKETING.CONTACT_REQUIRED` | 400 | A valid mobile number (or a valid email address) is needed. |
| `MARKETING.SUBSCRIPTION_LIMIT` | 429 | The number already waits for the maximum number of items. |

Other codes used: `GENERAL.VALIDATION` (settings), `GENERAL.TOO_MANY_REQUESTS` (rate limit), `AUTH.*` (permissions).

## 10. Workers and tables

* `CampaignSendWorker` (job `marketing.campaign-send`): starts scheduled campaigns that are due, sends batches of campaigns in `Sending`; switchable; writes a `JobLog` when it did something.
* `AutomationWorker` (job `marketing.automations`): runs the switched-on automations.
* Tables: `CustomerSegments`, `Campaigns`, `CampaignRecipients`, `Automations`, `BackInStockSubscriptions`. Messages and their states live in `NotificationLogs` (templates `marketing.campaign`, `marketing.campaign-test`, `automation.*`).
