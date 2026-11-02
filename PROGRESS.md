# PROGRESS

Resume from this file alone. Plan: `Documents/juvo-step-prompts.md` (14 steps). Master prompt: `juvo-claude-code-prompt.md`.
Last updated: 2026-11-02.

## Status of the 14 steps

| Step | Title | Status |
| --- | --- | --- |
| 1 | Understand the business, Git and project docs | **Done locally; push pending** (see Known issues) |
| 2 | Backend foundation | Not started |
| 3 | UMS backend: auth, roles, permissions, branches | Not started |
| 4 | Front-end foundation, brand, login, token handling | Not started |
| 5 | UMS user interface | Not started |
| 6 | Catalog, media, branch-wise inventory | Not started |
| 7 | Storefront, cart, customer identity, address, delivery zones | Not started |
| 8 | Checkout, orders, order processing | Not started |
| 9 | Payments, tax/invoices, notifications | Not started |
| 10 | Couriers, real-time status, returns | Not started |
| 11A | Operations part 1: purchasing, expenses, support, reports | Not started |
| 11B | Operations part 2: promotions, pre-order, loyalty, marketing, reseller, POS | Not started |
| 12 | Remaining admin UI, CMS, dashboard, settings, platform services | Not started |
| 13 | Hardening, QA, documentation | Not started |
| 14 | Real providers and go-live preparation | Not started |

## Step 1: what is done

- Git: repo initialised, `origin` = `https://github.com/AshiqurRuhullah/juvo-app.git`, local `main` aligned to the remote's single "Initial commit" (README only). No force operations.
- `.gitignore` and `.gitattributes` (Windows, .NET, Next.js, secrets).
- `docs/source/brd-srs.md`: SRS converted with `scripts/docx_to_md.py`; verified 1 H1, 17 H2, 79 H3, 63 tables and the Mermaid ERD all present (counts equal the original OOXML).
- `docs/understanding.md`: business summary, module matrix A-AF + identity + branches, roles matrix, order/payment/delivery and other state machines, cross-cutting rules, final ERD with branches, traceability table (257 FR/NFR rows = same count as the SRS tables, 15 business rules, all "Not started"). All 4 Mermaid diagrams parse (mermaid 11 `parse`).
- `docs/assumptions.md`: defaults for the 14 open questions (SRS 14.5), technology decisions, conflicts between SRS and master prompt, environment facts, Git remote validation result.
- Brand: palette extracted from `Logo/Official Logo.jpg` (`scripts/extract_palette.py`), `scripts/generate_tokens.py` writes `docs/brand-tokens.md`, `juvo-front/docs/brand-tokens.md` and `juvo-front/src/styles/tokens.css`; every text pair passes WCAG AA in light and dark. Logo copied to `juvo-be/assets/brand/` and `juvo-front/public/brand/` (variants and favicons come in Step 4).
- `scripts/setup-db.ps1`: finds a local SQL Server and creates `JuvoDb`. Ran twice: first "created", then "exists". Server used: `(localdb)\MSSQLLocalDB`, SQL Server 2019 Express (15.0.4382.1).

## Next step

**Step 2: Backend foundation** (create the .NET 10 solution inside `juvo-be`). Before it, fix the push access below and push the Step 1 commits.

## Known issues

1. **Push blocked (HTTP 403):** `Permission to AshiqurRuhullah/juvo-app.git denied to ashiqurgit.` Windows has cached credentials for GitHub user `ashiqurgit`. Fix: give `ashiqurgit` write access, or sign in as `AshiqurRuhullah` (`gh auth login` / remove the cached credential in Windows Credential Manager). Then run `git push` (6+ local commits waiting; `git log origin/main..HEAD`).
2. SQL Server is LocalDB 2019 (compatibility level 150). EF Core 10 must be verified against it in Step 2; install SQL Server 2022 if a feature needs level 160.
3. Provider onboarding (courier, payment gateway, SMS sender ID, map key) is not started by the owner; not needed until Step 14.
4. Tax/VAT rules for Bangladesh are unverified (sample 15% rule seeded inactive); confirm with an accountant before go-live.
5. Brand story, photos, policies text and pickup address must be supplied by the owner.
