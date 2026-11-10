import { describe, expect, it } from "vitest";
import { loadMessages } from "@/messages/load";

const en = await loadMessages("en");
const bn = await loadMessages("bn");

function leaves(value: unknown, prefix = ""): [string, unknown][] {
  if (value && typeof value === "object") {
    return Object.entries(value).flatMap(([k, v]) => leaves(v, prefix ? `${prefix}.${k}` : k));
  }
  return [[prefix, value]];
}

describe("translations", () => {
  it("English and Bengali have exactly the same keys", () => {
    const e = new Set(leaves(en).map(([k]) => k));
    const b = new Set(leaves(bn).map(([k]) => k));
    expect([...e].filter((k) => !b.has(k))).toEqual([]);
    expect([...b].filter((k) => !e.has(k))).toEqual([]);
  });

  it("no message is empty", () => {
    for (const messages of [en, bn]) {
      for (const [key, value] of leaves(messages)) expect(typeof value === "string" && value.trim().length > 0, key).toBe(true);
    }
  });

  it("placeholders such as {name} match between the languages", () => {
    const placeholders = (s: unknown) => [...String(s).matchAll(/\{(\w+)\s*[,}]/g)].map((m) => m[1]).sort();
    const bnMap = new Map(leaves(bn));
    for (const [key, value] of leaves(en)) expect(placeholders(bnMap.get(key)), key).toEqual(placeholders(value));
  });
});
