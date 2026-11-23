# Step 12g slice 2: settings centre screen (notes)

Branch `feature/12g-settings-ui`. Front end only, on the merged backend (`docs/settings-api.md`, `docs/step12g-settings-notes.md`). Scope: FR-AF01 (settings centre, switches without code change) and FR-AF08 in part (what the API lists for language and currency).

## 1. What was built

* Route `/admin/settings` (menu item `settings`, permission `settings.system.view`, `ready: true` in `src/lib/nav.ts`; it was the last unbuilt menu item).
* Module `juvo-front/src/modules/settings`:
  * `logic.ts` (pure, `tests/settings.test.ts`, 25 tests): draft <-> typed value per type, validation mirroring the backend (whole, number up to 4 decimals, range, text length, allowed choices without case), dirty detection, "who may do what" (read-only config, fixed value, no permission), dangerous-switch map, search over key and both descriptions, grouping, history kind.
  * `api.ts`: `GET /settings`, `GET /settings/history?limit=30`, `PUT /settings/{key}` (with `version`), `DELETE /settings/{key}` via the generated client and the BFF.
  * `settings-centre.tsx`: search box, group chips (a wrapped row of 44 px buttons with the count, one group at a time; with a search all groups with matches are stacked and the chips give way to a result count), loading skeleton, load error with retry, empty and no-match states, then the history panel.
  * `setting-row.tsx`: one setting. Title = the description in the current language (the API has no separate label), key in small monospace, source badge (Changed / Default / Server configuration), control by type (switch, number input with min and max, select for a choice, text input or textarea above 200 characters), default and range line, restart note when `requiresRestart`, plain reason when disabled, Save (enabled only when changed), Undo, Reset to default (only when a row is stored and the caller may edit).
  * `history-panel.tsx`: "Recent changes" from `GET /settings/history` (setting name and key, first value / changed from-to / reset, who, when).
* Messages `src/messages/settings.en.json` and `.bn.json` (namespace `settings`), registered in `src/messages/load.ts`. The admin scope loads every namespace, so `scopes.ts` needed no change.
* Generated client regenerated first (separate commit): `openapi/v1.json` and `src/lib/api/schema.d.ts` (391 paths, adds `/api/v1/settings*`). Run against a throw-away database `JuvoDb_Gen` on port 5098, then API stopped, database dropped, `dotnet build-server shutdown`.

## 2. Behaviour worth knowing

* Everything shown comes from `GET /settings`; nothing is invented. Groups, order, texts, defaults, limits, `canEdit`, `readOnly`, `source` are the API's.
* Concurrency: Save sends the row `version` the screen read. On `GENERAL.CONCURRENCY_CONFLICT` the list is reloaded, the row shows the newer value and a warning "Someone else changed this setting while you were editing..."; the edit is dropped on purpose (the person must look at the new value first). A setting with no stored row has `version: null`, so the first save cannot conflict (the API only checks a version when one is sent).
* Rows are keyed by key + version + value, so after a reload or save the draft starts from the stored value. Switching group or searching discards unsaved edits.
* Validation: the screen checks type, range and length first with its own plain texts; whatever the API answers is shown plainly: for `SETTINGS.INVALID_VALUE` the API's own field sentence (`errors.value`) is shown (English from the server in both languages), other codes use `settings.errors.*`.
* Dangerous switches ask first and state the consequence: `Loyalty.Enabled` (on and off), each `Payments.<CODE>.Enabled` (on and off, names the method), `Payments.AutoConfirmPaidOrders`, `Checkout.OtpMode`. Confirm button is the danger colour with a warning line (icon plus text). Reset has its own confirm that names the default and the stored value.
* Read-only (`readOnly`): shown disabled with a reason, no Save or Reset. `source: appsettings` -> "set in the server configuration, needs the file edited and a restart"; fixed values such as `Currency.Store` -> "fixed by the shop's rules".
* No edit permission (`canEdit: false`): control disabled, note names `editPermission`. Verified for view-only staff and for a staff member with only `settings.feature.edit` (switch editable, system settings not).
* Sensitive settings (none registered today) would arrive with null value and default; the screen would show an empty field and "(empty)" as the default. Not exercised.

## 3. Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| U1 | The visible name is the localized description; the key is shown under it. | The API gives no label field. | Add a label to the API and use it in `titleOf`. |
| U2 | Group chips (wrapping buttons with `aria-current`) instead of the shared `Tabs`. | 13 groups do not fit the shared tab bar on a phone or at 1280 px. | Use an accordion if preferred. |
| U3 | A stale answer drops the edit and shows the newer value with a warning. | Safest; the person decides again with the current value in view. | Keep the draft and offer "Overwrite". |
| U4 | Numbers use `<input type="number">` with min, max and step. | Native keyboard on phones; the screen still validates the same rules. | Switch to text with `inputMode`. |
| U5 | History shows the latest 30 changes of all settings; no per-setting filter or paging. | The API allows a key filter and up to 200 but a panel of 30 answers "what changed lately". | Add a "show more" and a key filter (the endpoint already supports both). |
| U6 | Existing module screens for the same settings (loyalty, payments, marketing, notifications) are unchanged. | They write the same rows, so both views agree. | None needed. |

## 4. Contract gaps and not built

* The API gives no `format` for text settings (time `HH:mm`, e-mail, web address), so those formats are validated by the server only; its sentence is shown. Add a `format` field to `SettingDto` to mirror them on the screen.
* No label field (see U1); descriptions are one sentence each.
* Display currency list and conversion, storefront default language (FR-AF08), module on/off switches beyond rewards and channels, OTP length and expiry (server configuration) are not settings in the registry; the screen can only show what the API lists (OTP and lockout entries appear read-only). See `docs/step12g-settings-notes.md` section 4.
* Fraud rules, delivery charge rules, tax rules and courier slots stay on their own screens.
* History `oldValue`/`newValue` are the stored text, so the screen shows them as stored (booleans are translated to On/Off).
* Live updates (SignalR) are not used: another person's change is seen on reload or after a stale answer.

## 5. Tests

* Vitest: `tests/settings.test.ts` 25 tests (logic); `tests/messages.test.ts` and `tests/messages-scope.test.ts` pass with the new fragment.
* Playwright (desktop project, one worker, throw-away database): `e2e/settings.spec.ts` (13 flows: list and groups, search, read-only entries, number saved + reload + history + reset with confirm, switch saved + reload + undo, client and server validation, stale version, dangerous switch cancel, view-only staff, feature editor, no view permission, menu item) and `e2e/settings-ui-checks.spec.ts` (every view at 360, 768 and 1280 px, plus view-only staff, dark theme and Bengali; overflow, 44 px targets, axe, console errors). Specs restore every setting they change (reset in `afterAll`). Screenshots in `juvo-front/docs/ui-checks/settings/` (key ones only).
* `e2e/shell.spec.ts`: the old test "modules that are not built yet are marked" failed before this slice (no menu item is unbuilt any more: Delivery zones was already built, settings was the last). It now checks that every item is a real link and nothing is marked "Soon".
* Support: `e2e/support/settings.ts` (read, write, reset through the API as admin).

## 6. Traceability suggestions

| FR | Status | Notes |
| --- | --- | --- |
| FR-AF01 | Done (front end) | `/admin/settings`: grouped list from the API, typed controls, save with version, reset with confirm, read-only entries, permission-aware, history. Switches for modules other than rewards and channels do not exist in the registry yet. |
| FR-AF08 | Partial | Shows what the registry has (default message language, fixed currency BDT, read-only). No storefront default language or display currencies (no store). |
| FR-AF (audit view of settings) | Partial | "Recent changes" panel from the audit log; no filter or paging yet. |
