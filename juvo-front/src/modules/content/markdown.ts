/**
 * A small, safe markdown reader for site pages. It understands headings (# to ####), paragraphs, bulleted and numbered lists, bold, italic and links.
 * Everything else (including raw HTML and images) stays plain text. It produces a tree, never HTML text, so the renderer can only
 * emit React elements (which escape everything). Links are kept only when the address is https, http, mailto, tel or a path on this site.
 * No package is used.
 */

export type Inline = { type: "text"; text: string } | { type: "strong"; children: Inline[] } | { type: "em"; children: Inline[] } | { type: "link"; href: string; children: Inline[] };
export type Block =
  | { type: "heading"; level: 1 | 2 | 3 | 4; children: Inline[] }
  | { type: "paragraph"; children: Inline[] }
  | { type: "list"; ordered: boolean; items: Inline[][] };

/** Addresses a page may link to. Anything else (javascript:, data:, vbscript:, file:, protocol-relative) is not a link. */
export function safeHref(raw: string): string | null {
  const href = raw.trim();
  if (!href || /[\s<>"]/.test(href)) return null;
  if (/^https?:\/\/[^/\s]+/i.test(href)) return href;
  if (/^mailto:[^\s@]+@[^\s@]+$/i.test(href)) return href;
  if (/^tel:\+?[0-9\-()]+$/i.test(href)) return href;
  if (/^\/(?!\/)/.test(href) || /^#[\w-]+$/.test(href)) return href;
  return null;
}

/** Index of the `)` that closes an address, allowing balanced parentheses inside it; -1 when it never closes. */
function closingParen(source: string, from: number): number {
  let depth = 0;
  for (let i = from; i < source.length; i++) {
    if (source[i] === "(") depth++;
    else if (source[i] === ")") {
      if (depth === 0) return i;
      depth--;
    }
  }
  return -1;
}

const push = (out: Inline[], text: string) => {
  if (!text) return;
  const last = out[out.length - 1];
  if (last && last.type === "text") last.text += text;
  else out.push({ type: "text", text });
};

/** Inline markup of one line or paragraph. An unmatched marker is shown as typed. */
export function parseInline(source: string): Inline[] {
  const out: Inline[] = [];
  let i = 0;
  while (i < source.length) {
    const ch = source[i]!;
    if (ch === "\\" && i + 1 < source.length && /[\\*_[\]()#`]/.test(source[i + 1]!)) {
      push(out, source[i + 1]!);
      i += 2;
      continue;
    }
    if (ch === "[") {
      const close = source.indexOf("](", i + 1);
      const end = close === -1 ? -1 : closingParen(source, close + 2);
      if (close !== -1 && end !== -1) {
        const label = source.slice(i + 1, close);
        const href = safeHref(source.slice(close + 2, end));
        if (href && label.trim()) {
          out.push({ type: "link", href, children: parseInline(label) });
          i = end + 1;
          continue;
        }
        // Not a safe link: keep the label as plain text and drop the address.
        if (label.trim()) {
          for (const part of parseInline(label)) out.push(part);
          i = end + 1;
          continue;
        }
      }
    }
    if (source.startsWith("**", i) || source.startsWith("__", i)) {
      const marker = source.slice(i, i + 2);
      const end = source.indexOf(marker, i + 2);
      if (end > i + 2) {
        out.push({ type: "strong", children: parseInline(source.slice(i + 2, end)) });
        i = end + 2;
        continue;
      }
    }
    if (ch === "*" || ch === "_") {
      const end = source.indexOf(ch, i + 1);
      const inner = end === -1 ? "" : source.slice(i + 1, end);
      // `_` inside a word (snake_case) is not emphasis; an emphasis needs text that starts and ends without a space.
      const wordInside = ch === "_" && i > 0 && /\w/.test(source[i - 1]!);
      if (end > i + 1 && !wordInside && inner === inner.trim()) {
        out.push({ type: "em", children: parseInline(inner) });
        i = end + 1;
        continue;
      }
    }
    push(out, ch);
    i++;
  }
  return out;
}

const HEADING = /^(#{1,4})\s+(.*?)\s*#*\s*$/;
const BULLET = /^\s{0,3}[-*+]\s+(.*)$/;
const NUMBER = /^\s{0,3}\d{1,3}[.)]\s+(.*)$/;

/** Splits markdown into blocks. Blank lines separate paragraphs; consecutive list lines form one list; a heading ends a paragraph. */
export function parseMarkdown(source: string): Block[] {
  const lines = source.replace(/\r\n?/g, "\n").split("\n");
  const blocks: Block[] = [];
  let paragraph: string[] = [];
  let list: { ordered: boolean; items: string[] } | null = null;

  const flushParagraph = () => {
    if (paragraph.length) blocks.push({ type: "paragraph", children: parseInline(paragraph.join(" ")) });
    paragraph = [];
  };
  const flushList = () => {
    if (list) blocks.push({ type: "list", ordered: list.ordered, items: list.items.map(parseInline) });
    list = null;
  };

  for (const line of lines) {
    if (line.trim() === "") {
      flushParagraph();
      flushList();
      continue;
    }
    const heading = HEADING.exec(line);
    if (heading) {
      flushParagraph();
      flushList();
      blocks.push({ type: "heading", level: heading[1]!.length as 1 | 2 | 3 | 4, children: parseInline(heading[2]!) });
      continue;
    }
    const bullet = BULLET.exec(line);
    const number = bullet ? null : NUMBER.exec(line);
    const item = bullet ?? number;
    if (item) {
      flushParagraph();
      const ordered = !!number;
      if (list && list.ordered !== ordered) flushList();
      if (!list) list = { ordered, items: [] };
      list.items.push(item[1]!);
      continue;
    }
    if (list && /^\s{2,}\S/.test(line)) {
      // A wrapped list line belongs to the last item.
      list.items[list.items.length - 1] += " " + line.trim();
      continue;
    }
    flushList();
    paragraph.push(line.trim());
  }
  flushParagraph();
  flushList();
  return blocks;
}

/** Plain text of markdown (for a page summary): markers removed. */
export function markdownToText(source: string): string {
  const text = (inline: Inline[]): string => inline.map((x) => (x.type === "text" ? x.text : text(x.children))).join("");
  return parseMarkdown(source)
    .map((b) => (b.type === "list" ? b.items.map(text).join(" ") : text(b.children)))
    .join(" ");
}
