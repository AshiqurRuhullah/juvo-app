"""Seed the FR/NFR/BR traceability table inside docs/understanding.md.

Usage: python -I -X utf8 scripts/gen_traceability.py

Reads docs/source/brd-srs.md, extracts every FR-/NFR- row from the requirement tables and every
BR- rule from the text, and writes a table between the markers
  <!-- TRACEABILITY:START --> ... <!-- TRACEABILITY:END -->
Each row starts as "Not started". The planned step comes from STEP_BY_MODULE (the 14-step plan in
Documents/juvo-step-prompts.md). Later steps edit the Status / Implemented in / Notes columns by hand.
Safety: refuses to overwrite if any row already has a status other than "Not started".
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "docs" / "source" / "brd-srs.md"
DST = ROOT / "docs" / "understanding.md"
START, END = "<!-- TRACEABILITY:START -->", "<!-- TRACEABILITY:END -->"

# Module letter(s) -> planned step in the 14-step plan.
STEP_BY_MODULE = {
    "A": "7", "B": "6", "C": "7", "D": "8", "E": "7", "F": "7-8", "G": "7", "H": "10", "I": "9",
    "J": "10", "K": "9", "L": "10", "M": "12", "N": "6-12", "O": "3-5", "P": "11A", "Q": "12",
    "R": "12", "S": "8", "T": "8", "U": "9", "V": "6", "W": "11A", "X": "11A", "Y": "11A",
    "Z": "11B", "AA": "11B", "AB": "11B", "AC": "11B", "AD": "11B", "AE": "11B", "AF": "12",
}
# Explicit overrides where a requirement belongs to a different step than its module default.
STEP_OVERRIDES = {
    "FR-AF01": "12", "FR-AF02": "12", "FR-AF03": "12", "FR-AF04": "12",
    "FR-D11": "8", "FR-D10": "8", "FR-F02": "8", "FR-F03": "10", "FR-F04": "8", "FR-F10": "9",
    "FR-I09": "9", "FR-G08": "7", "FR-J04": "8", "FR-J01": "8", "FR-J02": "8", "FR-J03": "10",
    "FR-O01": "3", "FR-O02": "3", "FR-O03": "3", "FR-O04": "3", "FR-O05": "3", "FR-O06": "3",
}

ID_RE = re.compile(r"^(FR|NFR)-([A-Z]*)(\d+)$")


def clean(text: str) -> str:
    text = re.sub(r"\*\*", "", text)
    text = text.replace("|", "/").replace("&apos;", "'").replace("<br>", " ")
    return re.sub(r"\s+", " ", text).strip()


def short(text: str, limit: int = 140) -> str:
    text = clean(text)
    return text if len(text) <= limit else text[: limit - 1].rstrip() + "…"


def extract() -> list[tuple[str, str, str, str]]:
    rows: list[tuple[str, str, str, str]] = []  # (id, text, priority, step)
    seen: set[str] = set()
    for line in SRC.read_text(encoding="utf-8").splitlines():
        if not line.startswith("| FR-") and not line.startswith("| NFR-"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split(" | ")]
        rid = cells[0]
        m = ID_RE.match(rid)
        if not m or rid in seen:
            continue
        seen.add(rid)
        kind, mod, _ = m.groups()
        if kind == "NFR":
            # | NFR-001 | Area | Requirement |
            text, pri = f"{cells[1]}: {cells[2]}", "-"
            step = "13"
        else:
            text = cells[1]
            tail = [c for c in cells[2:] if c in {"M", "S", "C"}]
            pri = tail[-1] if tail else "-"
            step = STEP_OVERRIDES.get(rid) or STEP_BY_MODULE.get(mod, "?")
        rows.append((rid, short(text), pri, step))
    return rows


def extract_business_rules() -> list[tuple[str, str]]:
    text = SRC.read_text(encoding="utf-8")
    found: dict[str, str] = {}
    for m in re.finditer(r"(BR-[A-Z]?\d+):\s*(.+?)(?=(?:\s+BR-[A-Z]?\d+:)|\n|$)", text):
        rid = m.group(1)
        if rid not in found:
            found[rid] = short(m.group(2))
    return sorted(found.items())


def build_table() -> str:
    frs = extract()
    brs = extract_business_rules()
    lines = [
        START,
        "",
        f"Generated from `docs/source/brd-srs.md` by `scripts/gen_traceability.py` ({len(frs)} FR/NFR rows, {len(brs)} business rules). "
        "Status values: **Not started**, **In progress**, **Done**, **Partial**, **Deferred** (with reason). "
        "Edit Status, Implemented in and Notes by hand as steps finish. Step = planned step in `Documents/juvo-step-prompts.md`.",
        "",
        "### Functional and non-functional requirements",
        "",
        "| ID | Requirement | Pri | Step | Status | Implemented in (file / endpoint / page) | Notes |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    for rid, text, pri, step in frs:
        lines.append(f"| {rid} | {text} | {pri} | {step} | Not started | | |")
    lines += ["", "### Business rules", "", "| ID | Rule | Status | Enforced in | Notes |", "| --- | --- | --- | --- | --- |"]
    for rid, text in brs:
        lines.append(f"| {rid} | {text} | Not started | | |")
    lines += ["", END]
    return "\n".join(lines)


def main() -> int:
    doc = DST.read_text(encoding="utf-8") if DST.exists() else ""
    if START in doc and END in doc:
        block = doc[doc.index(START): doc.index(END) + len(END)]
        body_rows = [l for l in block.splitlines() if l.startswith("| FR-") or l.startswith("| NFR-") or l.startswith("| BR-")]
        if any("Not started" not in l for l in body_rows):
            print("Refusing to overwrite: some rows already have a status other than 'Not started'.")
            return 1
        new = doc.replace(block, build_table())
    else:
        new = doc.rstrip() + "\n\n" + build_table() + "\n"
    DST.write_text(new, encoding="utf-8", newline="\n")
    frs = extract()
    print(f"wrote traceability: {len(frs)} FR/NFR rows, {len(extract_business_rules())} business rules")
    by = {}
    for rid, *_ in frs:
        key = re.match(r"(FR|NFR)-([A-Z]*)", rid).group(0)
        by[key] = by.get(key, 0) + 1
    print(by)
    return 0


if __name__ == "__main__":
    sys.exit(main())
