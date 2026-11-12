import type { Template } from "./api";

/** "OrderNumber" or "{{OrderNumber}}" to "{{OrderNumber}}". */
export const placeholderText = (variable: string) => `{{${variable.replace(/^\{\{|\}\}$/g, "").trim()}}}`;

/**
 * Text messages: 160 characters in one part (153 each when split) for plain text; 70 (67) when any character needs the wide
 * alphabet, which Bengali always does. A rough guide for staff, the operator's own count decides the bill.
 */
export function smsParts(text: string): { characters: number; parts: number; limit: number } {
  const wide = /[^\x00-\x7F]/.test(text);
  const single = wide ? 70 : 160;
  const multi = wide ? 67 : 153;
  const characters = [...text].length;
  return { characters, parts: characters === 0 ? 0 : characters <= single ? 1 : Math.ceil(characters / multi), limit: single };
}

/** Groups the flat template list by code, keeping the order of first appearance; variants sorted by channel then language. */
export function groupTemplates(templates: Template[]): { code: string; description: string; category: Template["category"]; variants: Template[] }[] {
  const channelOrder = ["Sms", "Email", "WhatsApp", "InApp"];
  const groups = new Map<string, { code: string; description: string; category: Template["category"]; variants: Template[] }>();
  for (const t of templates) {
    const group = groups.get(t.code) ?? { code: t.code, description: t.description, category: t.category, variants: [] };
    group.variants.push(t);
    groups.set(t.code, group);
  }
  for (const g of groups.values()) g.variants.sort((a, b) => channelOrder.indexOf(a.channel) - channelOrder.indexOf(b.channel) || a.language.localeCompare(b.language));
  return [...groups.values()];
}
