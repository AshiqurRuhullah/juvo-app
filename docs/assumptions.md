# Assumptions and decisions

Every decision the build needed that the SRS left open, or where the master prompt overrides the SRS.
Format: **decision**, reason, how to change it. Business rules in the SRS win over this file; the master prompt wins on technology and structure.

Last updated: 2026-11-02 (Step 1).

---

## A. Environment and Git validation (recorded in Step 1)

| Item | Result |
| --- | --- |
| Machine | Windows 10 Pro 10.0.19045, Git 2.51.0, .NET SDK 10.0.301, Node v26.3.0, npm 11.16.0, Python 3.14.7 |
| Remote | `https://github.com/AshiqurRuhullah/juvo-app.git` |
| `git ls-remote` | Reachable. Default branch `main`. Not empty: one commit `0ec0117 Initial commit` containing only `README.md` (`# juvo-app`). |
| Integration | Local repo created with `git init -b main`, `origin` added, `git fetch`, then `git checkout -B main origin/main` (no local commits existed, so nothing was rewritten). No force operations. |
| Push | **BLOCKED (HTTP 403).** `remote: Permission to AshiqurRuhullah/juvo-app.git denied to ashiqurgit.` The credential cached in Windows belongs to GitHub user `ashiqurgit`, which has no write access. Local commits are kept; push once access is fixed (add `ashiqurgit` as a collaborator with write access, or sign in as `AshiqurRuhullah` via Git Credential Manager / `gh auth login`). |
| Git identity | `H.N.ASHIQUR RUHULLAH <hnashiq@erainfotechbd.com>` (existing global config, unchanged). |
| Source files committed | **Yes.** `Documents/*` and `Logo/*` are small (80 KB and 60 KB), so they are committed as source material. No Git LFS needed. |
| Global git setting | `core.autocrlf=true` is set machine-wide; `.gitattributes` in the repo enforces LF for source files and CRLF for `.ps1`/`.sln`. |

## B. Technology decisions (master prompt overrides SRS section 4.3)

| Decision | Reason | How to change |
| --- | --- | --- |
| **Microsoft SQL Server** instead of PostgreSQL | Master prompt requires it. | Different EF provider + migrations rebuild. Not planned. |
| **No Redis, no Docker.** `IMemoryCache` for caching; SignalR without a backplane; background jobs through the transactional outbox + hosted service. | Master prompt rule 0.7. Fine for one server. | Add a Redis backplane/cache adapter behind the same interfaces if the site is scaled to several servers. |
| Local database: **SQL Server LocalDB `(localdb)\MSSQLLocalDB`, version 15.0.4382 (SQL Server 2019 Express)**, compatibility level 150. It was the only reachable instance (`localhost` and `.\SQLEXPRESS` are not installed). Database `JuvoDb` created by `scripts/setup-db.ps1` with the UTF-8 collation `Latin1_General_100_CI_AS_SC_UTF8`; all text columns use `nvarchar` so Bengali is safe regardless. | Express/LocalDB is enough for development and demos. | Pass `-Server` to the script for another instance. For production use SQL Server 2022+ (Express limits: 10 GB per database, 1 GB RAM). |
| **EF Core 10 vs SQL Server 2019:** EF Core's newer JSON/vector features need compatibility level 160+. The data model will not depend on those features, so level 150 works. | Avoid blocking on a SQL Server install. | Install SQL Server 2022 Developer and rerun `setup-db.ps1 -Server localhost`; the schema is unchanged. Verify in Step 2 that the chosen EF version runs against 2019. |
| Integration tests use a **separate local database** (`JuvoDb_Test`, created and dropped by the test fixture). | Master prompt: real SQL Server, no containers. | `scripts/setup-db.ps1 -Database JuvoDb_Test`. |
| Front end: Next.js latest stable (App Router). `npm view next version` returned **16.4.3** on 2026-11-02; confirmed again at scaffold time in Step 4. Node v26.3.0 is installed. | Master prompt: latest stable. | Pin versions in `package.json`; upgrade deliberately. |
| Backend: .NET **10.0.301** SDK installed; target `net10.0`. | Installed SDK. | n/a |
| Python is used only for helper scripts (`docx_to_md.py`, `extract_palette.py`, `generate_tokens.py`). `pillow 13.0.0` was installed with `pip` for palette extraction. | No pandoc on the machine. | Scripts are stdlib-only except `extract_palette.py` (Pillow). |
| Maps: **Leaflet + OpenStreetMap by default**, Google Maps switchable by environment flag. | SRS 14.5 Q10: lower cost, no key needed for sandbox. OSM address quality in Bangladesh is weaker, so the pin always needs a text landmark (SRS 14.4). | `NEXT_PUBLIC_MAP_PROVIDER=google` plus a key. |
| Provider adapters (courier, payment, SMS, email, WhatsApp, maps): interface + **Sandbox adapter on by default**; named real adapters (Pathao, Steadfast, RedX, Paperfly, eCourier, DHL, bKash, Nagad, SSLCommerz, aamarPay, Stripe, PayPal) are configuration slots, **disabled**, built in Step 14 only against provider documentation and credentials supplied by the owner. | SRS section 4.6: provider details are unverified. | Step 14. |
| Provider onboarding (courier, gateway, SMS sender ID, map key) is **not needed until Step 14**, but takes time; the owner should start it now. | Step 0 note. | n/a |

## C. Answers to the SRS open questions (section 14.5)

| # | Question | Decision (default) | Change by |
| --- | --- | --- | --- |
| 1 | Logo and colours | Extracted from `Logo/Official Logo.jpg`: swoosh red `#DB0100` is the primary, near-black `#1C0807` the secondary, cream `#EBE5DB` the accent. The SRS plum/gold starter palette is **not used**. Full table with contrast checks in `docs/brand-tokens.md`. | Edit palette in `scripts/generate_tokens.py` and rerun. |
| 2 | Categories and sizes | Seed top-level categories **Clothing, Shoes, Ladies' Dresses, Purses** with sub-categories. Clothing sizes: XS, S, M, L, XL, XXL, Free size. Shoe sizes: EU 35-46. Colour as free label plus swatch hex. | Admin > Categories / size charts. |
| 3 | Couriers | Sandbox courier enabled. Pathao, Steadfast, RedX, Paperfly, eCourier (domestic) and DHL (international) exist as disabled placeholders. | Admin > Couriers (Step 10/14). |
| 4 | Payment methods at launch | **COD** and the **Sandbox online gateway** enabled. bKash, Nagad, Rocket, SSLCommerz, aamarPay, Stripe, PayPal and bank transfer (manual verification) are disabled slots. Trade licence / merchant account status unknown. | Admin > Payments. |
| 5 | COD advance rules | COD above **BDT 10,000** requires an advance of the delivery charge (BR-D1); first-time-customer COD is flagged for review (module T) but not forced. OTP before confirm: **COD only** (FR-D04 switch = "COD only"). | Settings centre. |
| 6 | International | Module is built; one sample destination (**India**) is enabled in Sandbox so the acceptance test can run; all other countries disabled. Duties and taxes are **paid by the receiver (DAP)**, stated clearly at checkout. International max parcel weight 20 kg, restricted-items list starts empty and is admin-editable (BR-G3). | Admin > Delivery > Countries. |
| 7 | Stock deduction point | **Reserve at order placement, deduct at packing**, release on cancel/failure/timeout. Applied everywhere (SRS 12.2 asks to decide once). | Setting `Inventory.DeductAt = Packing|Delivery`. |
| 8 | Returns | Return window **7 days from delivery**, per-category override. Non-returnable: none by default (owner to list). Delivery charge on a customer-reason return is not refunded; on a seller-fault return it is. | Admin > Settings > Returns, category policy. |
| 9 | VAT / tax | Prices stored **tax-inclusive**. A sample tax rule of **15%** is seeded **inactive** and marked "unverified: confirm with an accountant" (SRS 15, Module U note). Invoices and credit notes are issued regardless; the VAT lines appear once a rule is activated. | Admin > Tax. |
| 10 | Maps | See section B: Leaflet + OSM. | n/a |
| 11 | Language | **English default, Bengali switchable from launch** (full translation pass in Step 12). | Settings. |
| 12 | Staff and roles at launch | The seven default roles (Super Admin, Manager, Order Manager, Inventory Manager, Content Editor, Support, Accountant). | UMS. |
| 13 | Brand content | The About page, policies and FAQ are seeded as **editable drafts built only from facts in the SRS** (business name, Facebook `facebook.com/juvobd`, email `juvobd@gmail.com`, what is sold). The owner replaces story text, photos, tagline and the pickup address in the CMS. Nothing invented about the brand's history. | Admin > CMS. |
| 14 | Reviews | Enabled at launch, **verified buyers only, moderated** (SRS FR-A12). | Settings. |

## D. Other decisions and conflicts

| Topic | Decision | Reason |
| --- | --- | --- |
| Loyalty | SRS 2.5 lists loyalty points as out of scope for v1, but SRS section 15 defines module AB (Phase 4, priority Could). **Built in Step 11B** as an optional module behind a feature switch, default off. | Master prompt and step plan include it; the feature flag keeps v1 behaviour unchanged. |
| POS / multi-branch | SRS module AE is "Phase 5, only if needed". The master prompt makes **multi-branch part of the data model from the first migration** (section 4A); a basic POS is built in Step 11B. | Master prompt overrides. |
| Branch numbering | Order numbers: `JV-<BRANCHCODE>-<YYMM>-<6-digit sequence>`, e.g. `JV-DHK01-2611-000123` (SRS example `JV-2611-000123` plus branch prefix per master prompt 4A.3). Sequence is gap-free per branch (invoice and credit-note numbers) and unique per branch+month for orders. | BR-D3 + 4A. |
| Single-branch default | Seed **Head Office (`HO`)** (default, global scope for Super Admin) and **Online Fulfilment**. UI hides branch controls when only one active branch exists. | 4A.3. |
| Admin app | One Next.js app with `(admin)` route group, not a separate SPA repo. | Master prompt section 5.2; simpler, shared design system. |
| Repositories | One Git repository with `juvo-be` and `juvo-front` folders (SRS 16.2 suggests several repos). | Master prompt section 0A. |
| Time and money | Store `datetimeoffset` in UTC, display Asia/Dhaka; money `decimal(18,2)` plus currency code. Mobile numbers normalised to `+880...`. | SRS 12.2. |
| Order timeout | Unpaid online-payment orders are cancelled after **30 minutes**, stock released (BR-D2). | SRS example value. |
| Cart/reservation | Cart does not reserve stock; stock is reserved at order placement only. | Keeps abandoned carts from blocking stock. |
| Invoice PDF library | Chosen in Step 9 after checking its current licence (QuestPDF has a Community licence with revenue limits: verify). | Master prompt rule 0.2. |
| SMS / email | Sandbox senders write to the notification log (viewable in admin). Real SMS needs a provider with a registered sender ID (Step 14). | SRS 13.2. |
| Dataset of Bangladesh divisions/districts/upazilas | Chosen and sourced in Step 7 (verified against a maintained open dataset); the source is recorded in this file then. If upazila data is incomplete, divisions and districts are complete and upazilas importable. | SRS FR-G02. |
| Default admin credentials | Development-only Super Admin seeded in Step 3 with a forced password change at first login; credentials documented in the README, never reused in production. | Master prompt section 7. |
| Dark mode | Light is the default; a dark theme is included in the generated tokens (admin first). | SRS 3.3 "Could". |
| Danger colour | The brand colour is red, so the danger/error colour is a deeper crimson and is always paired with an icon and text. | `docs/brand-tokens.md`. |
