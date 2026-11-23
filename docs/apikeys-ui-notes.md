# Step 12j slice 2: API keys and webhooks screens (notes)

Branch `feature/12j-apikeys-ui`. Scope FR-AF05 (screens). Front end only; `juvo-be/` untouched. Contract: `docs/public-api.md`, `docs/step12j-api-notes.md`.

## Built

* Routes `/admin/integrations/api-keys` (permission `integrations.apikey.view`) and `/admin/integrations/webhooks` (`integrations.webhook.view`). Menu items "API keys" and "Webhooks" sit in the System group (`src/lib/nav.ts`, `ready: true`). Super Admin only by default, so a Manager sees neither item and gets the forbidden page.
* Module `src/modules/integrations`: `logic.ts` (pure rules, 34 vitest tests in `tests/integrations.test.ts`), `api.ts`, `common.tsx`, `secret-dialog.tsx`, `key-dialog.tsx`, `keys-list.tsx`, `how-to-use.tsx`, `webhook-dialog.tsx`, `webhooks-list.tsx`, `deliveries-panel.tsx`, `verify-help.tsx`. Texts: fragment `integrations` (en + bn) registered in `src/messages/load.ts`; menu labels `nav.items.apiKeys|webhooks`.
* API keys: paged list (name, key start, scope chips, branch limit, state, expiry, rate limit, last used, created), search and on/off filter, create and edit dialog (scopes in plain words, `pii:read` warning, branch limit, expiry as a Dhaka day = end of that day, per-minute limit 1 to 10,000), rotate and delete with confirm, turn on/off, read-only view, "How to use" block.
* One-time secret dialog (keys and webhook secrets): not dismissible by outside click or Escape, only by "I have saved it" (or the close icon); copy button with spoken and visible feedback; value only in component state; mutations use `gcTime: 0` and the answer is never put in the query cache; focus returns to the "New" button.
* Webhooks: list (URL, event chips, state, newest delivery per endpoint, failed count), create and edit dialog (client check: https only, 500 characters, no credentials; the API's refusal text for private addresses is shown beside the field), test event (result shown, a failed ping is a normal answer), new signing secret (confirm, then one-time dialog), turn on/off, delete, signature help with a Node sample using placeholders, delivery log (filters endpoint, status, event, search; redeliver one failed delivery or the selected failed ones after a confirm, with the API's answer shown).

## Decisions (reason, how to change)

| # | Decision | Reason | Change |
| --- | --- | --- | --- |
| U1 | Menu items in the System group, routes under `/admin/integrations/`. | Few items, same group as jobs and health. | `nav.ts`. |
| U2 | The how-to-use block shows `https://YOUR_API_HOST/api/public/v1` and `/openapi/public-v1.json`, not a real host. | The browser only talks to this site (BFF); no public API address setting exists. | Add a public API URL setting and use it in `how-to-use.tsx`. |
| U3 | "Last delivery" per endpoint is one small request per visible row (`endpointId`, page size 1). | The endpoint DTO has only `failedDeliveries`. | Add `lastDelivery` to `WebhookEndpointDto`, drop `LastDelivery`. |
| U4 | Redeliver is offered for Failed rows only (the API also accepts Succeeded). | Brief: failed or selected; resending a delivered event is rarely wanted. | `deliveries-panel.tsx`, `canRedeliver` in `logic.ts`. |
| U5 | Scope and event texts are ours (en and bn, keyed `orders_read`, `order_placed`); the API's English descriptions are only a fallback for an unknown future scope. | Bengali, and no dots or colons in message key paths. | `integrations.*.json`. |
| U6 | Test events and e2e: the e2e API runs the production address guard; endpoints for the log and toggles are inserted by SQL (`e2e/support/integrations.ts`) with a never-resolving host and a pending delivery scheduled a day ahead so the worker leaves it alone. | Loopback and http addresses cannot be created. | n/a |

## Tests run and seen

* `npx tsc --noEmit` clean; eslint clean on module, pages, specs; `npm test` 34 files, 625 tests passed.
* `npm run build` completed (first run stopped at the known advisory notice, second completed).
* `e2e/integrations.spec.ts` desktop: 15 passed (key lifecycle with public API proof: 200, 403 SCOPE_MISSING, 401 after rotate, 401 KEY_DISABLED, 401 after delete; secret not in page, URL, storage or after reload; webhook refusals; toggles; test event; delete; log filters; redeliver one and several; permission cases).
* `e2e/integrations-ui-checks.spec.ts` desktop: 30 passed (360/768/1280 for list, form, secret dialog, rotate confirm, webhooks, webhook form, log with confirm, view-only; dark; Bengali at 360) with axe, overflow and 44 px checks.
* Re-run for the menu change: `shell.spec`, `permissions.spec`, `settings.spec` desktop 28 passed, 1 skipped; `shell.spec` tablet, Pixel 7, iPhone 14: 21 passed, 8 skipped.
* Screenshots kept in `juvo-front/docs/ui-checks/integrations/` (11 files). The secret dialog is checked but never photographed.

## Contract gaps and not built

* No `lastDelivery` on the endpoint DTO (U3). No endpoint to read a delivery's body is used (the detail endpoint exists; not shown, to keep payloads out of the UI).
* No public API address setting (U2).
* Webhook secret rotation and creation cannot be proven end to end in e2e (the address guard refuses every reachable test address); only refusals and seeded rows are tested.

## Traceability suggestion

FR-AF05: screens Done (`src/modules/integrations`, routes above); the back end row stays as written in `step12j-api-notes.md`.
