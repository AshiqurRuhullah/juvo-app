"""Convert the Juvo BRD/SRS .docx (OOXML) to Markdown.

Usage: python -I -X utf8 scripts/docx_to_md.py <input.docx> <output.md>

Keeps: headings, paragraphs, bold runs, bullet/numbered lists, tables (as
GitHub-style pipe tables), and the Code-style paragraph (as a ```mermaid block).
Uses only the Python standard library. Prints a verification summary so the
result can be checked against the original (heading and table counts).
"""
from __future__ import annotations

import datetime
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"


def tag(name: str) -> str:
    return W + name


def run_text(run: ET.Element) -> str:
    out = []
    for child in run:
        if child.tag == tag("t"):
            out.append(child.text or "")
        elif child.tag == tag("br"):
            out.append("\n")
        elif child.tag == tag("tab"):
            out.append("\t")
    return "".join(out)


def is_bold(run: ET.Element) -> bool:
    rpr = run.find(tag("rPr"))
    return rpr is not None and rpr.find(tag("b")) is not None


def para_text(p: ET.Element, *, inline: bool = True) -> str:
    """Text of a paragraph with **bold** markers. Line breaks become <br> when inline."""
    parts: list[str] = []
    for run in p.iter(tag("r")):
        text = run_text(run)
        if not text:
            continue
        if inline:
            text = text.replace("\n", "<br>")
        if is_bold(run) and text.strip():
            lead = text[: len(text) - len(text.lstrip())]
            trail = text[len(text.rstrip()):]
            parts.append(f"{lead}**{text.strip()}**{trail}")
        else:
            parts.append(text)
    return "".join(parts)


def para_style(p: ET.Element) -> str:
    ppr = p.find(tag("pPr"))
    if ppr is None:
        return ""
    st = ppr.find(tag("pStyle"))
    return st.get(tag("val"), "") if st is not None else ""


def list_info(p: ET.Element, numbering: dict[str, str]) -> tuple[str, int] | None:
    ppr = p.find(tag("pPr"))
    if ppr is None:
        return None
    num = ppr.find(tag("numPr"))
    if num is None:
        return None
    num_id = num.find(tag("numId"))
    ilvl = num.find(tag("ilvl"))
    kind = numbering.get(num_id.get(tag("val")) if num_id is not None else "", "bullet")
    level = int(ilvl.get(tag("val"))) if ilvl is not None else 0
    return kind, level


def load_numbering(z: zipfile.ZipFile) -> dict[str, str]:
    """numId -> 'bullet' | 'decimal'."""
    if "word/numbering.xml" not in z.namelist():
        return {}
    root = ET.fromstring(z.read("word/numbering.xml"))
    abstract: dict[str, str] = {}
    for a in root.findall(tag("abstractNum")):
        lvl = a.find(tag("lvl"))
        fmt = lvl.find(tag("numFmt")) if lvl is not None else None
        abstract[a.get(tag("abstractNumId"))] = fmt.get(tag("val")) if fmt is not None else "bullet"
    result: dict[str, str] = {}
    for n in root.findall(tag("num")):
        aid = n.find(tag("abstractNumId"))
        result[n.get(tag("numId"))] = abstract.get(aid.get(tag("val")), "bullet")
    return result


def table_to_md(tbl: ET.Element) -> list[str]:
    rows: list[list[str]] = []
    for tr in tbl.findall(tag("tr")):
        cells = []
        for tc in tr.findall(tag("tc")):
            texts = [para_text(p) for p in tc.findall(tag("p"))]
            cell = "<br>".join(t for t in texts if t.strip())
            cells.append(cell.replace("|", "\\|").strip())
        rows.append(cells)
    if not rows:
        return []
    width = max(len(r) for r in rows)
    rows = [r + [""] * (width - len(r)) for r in rows]
    lines = ["| " + " | ".join(rows[0]) + " |", "| " + " | ".join(["---"] * width) + " |"]
    lines += ["| " + " | ".join(r) + " |" for r in rows[1:]]
    return lines


def convert(src: Path, dst: Path) -> None:
    with zipfile.ZipFile(src) as z:
        numbering = load_numbering(z)
        body = ET.fromstring(z.read("word/document.xml")).find(tag("body"))

    out: list[str] = []
    counters: dict[str, int] = {}
    prev_was_list = False
    stats = {"h1": 0, "h2": 0, "h3": 0, "tables": 0, "code": 0, "lists": 0}

    def blank() -> None:
        if out and out[-1] != "":
            out.append("")

    for el in body:
        if el.tag == tag("tbl"):
            blank()
            out.extend(table_to_md(el))
            out.append("")
            stats["tables"] += 1
            prev_was_list = False
            continue
        if el.tag != tag("p"):
            continue
        style = para_style(el)
        if style == "Code":
            blank()
            out.append("```mermaid")
            out.extend("".join(run_text(r) for r in el.iter(tag("r"))).split("\n"))
            out.append("```")
            out.append("")
            stats["code"] += 1
            prev_was_list = False
            continue
        text = para_text(el).strip()
        if not text:
            continue
        if style.startswith("Heading"):
            level = int(style[len("Heading"):])
            blank()
            out.append("#" * level + " " + text.replace("**", ""))
            out.append("")
            stats[f"h{level}"] = stats.get(f"h{level}", 0) + 1
            prev_was_list = False
            continue
        info = list_info(el, numbering)
        if info:
            kind, level = info
            if not prev_was_list:
                blank()
                counters.clear()
            indent = "  " * level
            if kind == "decimal":
                counters[str(level)] = counters.get(str(level), 0) + 1
                out.append(f"{indent}{counters[str(level)]}. {text}")
            else:
                out.append(f"{indent}- {text}")
            stats["lists"] += 1
            prev_was_list = True
            continue
        blank()
        out.append(text)
        out.append("")
        prev_was_list = False

    notice = (
        f"> Generated copy of `{src.name}` (source of truth), converted on "
        f"{datetime.date.today().isoformat()} by scripts/docx_to_md.py. "
        "Do not edit by hand; re-run the script."
    )
    out = [notice, ""] + out
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text("\n".join(out).rstrip() + "\n", encoding="utf-8", newline="\n")
    print(f"written {dst} ({len(out)} lines)")
    print("stats:", stats)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    convert(Path(sys.argv[1]), Path(sys.argv[2]))
