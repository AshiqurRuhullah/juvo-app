import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { loadMessages } from "@/messages/load";
import { SCOPES, type ScopeName } from "@/messages/scopes";

/**
 * Guards the per-area message scopes (src/messages/scopes.ts). For every area it follows the imports of the pages and layouts
 * (static and dynamic) and collects each namespace passed to useTranslations(...) in that code. An area that lists fewer namespaces
 * than its components use would show raw keys such as "orders.title" in the browser, so this test fails first.
 */

const SRC = path.resolve(__dirname, "../src");
const EXT = [".ts", ".tsx"];

function resolveImport(from: string, spec: string): string | null {
  let base: string;
  if (spec.startsWith("@/")) base = path.join(SRC, spec.slice(2));
  else if (spec.startsWith(".")) base = path.resolve(path.dirname(from), spec);
  else return null; // a package
  for (const candidate of [base, ...EXT.map((e) => base + e), ...EXT.map((e) => path.join(base, "index" + e))]) {
    if (fs.existsSync(candidate) && fs.statSync(candidate).isFile()) return candidate;
  }
  return null;
}

function importsOf(file: string, source: string): string[] {
  const specs = new Set<string>();
  for (const m of source.matchAll(/(?:^|\n)\s*(?:import|export)\s+(?!type\b)[^"';]*?from\s+["']([^"']+)["']/g)) specs.add(m[1]);
  for (const m of source.matchAll(/(?:^|\n)\s*import\s+["']([^"']+)["']/g)) specs.add(m[1]);
  for (const m of source.matchAll(/import\(\s*["']([^"']+)["']\s*\)/g)) specs.add(m[1]);
  return [...specs].map((s) => resolveImport(file, s)).filter((f): f is string => !!f);
}

function walk(dir: string): string[] {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(path.join(dir, e.name)) : [path.join(dir, e.name)]));
}

type Usage = { namespaces: Map<string, string>; dynamic: string[] };

function namespacesUsedBy(entries: string[]): Usage {
  const seen = new Set<string>();
  const usage: Usage = { namespaces: new Map(), dynamic: [] };
  const queue = [...entries];
  while (queue.length) {
    const file = queue.pop()!;
    if (seen.has(file)) continue;
    seen.add(file);
    if (!EXT.includes(path.extname(file))) continue;
    const source = fs.readFileSync(file, "utf8");
    for (const m of source.matchAll(/\buseTranslations\(([^)]*)\)/g)) {
      const arg = m[1].trim();
      const literal = /^["'`]([A-Za-z0-9]+)/.exec(arg);
      if (literal) usage.namespaces.set(literal[1], path.relative(SRC, file));
      else usage.dynamic.push(`${path.relative(SRC, file)}: useTranslations(${arg})`);
    }
    queue.push(...importsOf(file, source));
  }
  return usage;
}

const app = path.join(SRC, "app");
const AREAS: Record<Exclude<ScopeName, "admin">, string[]> = {
  root: [
    ...fs.readdirSync(app).filter((f) => /\.(ts|tsx)$/.test(f)).map((f) => path.join(app, f)),
    ...walk(path.join(app, "forbidden")),
    ...walk(path.join(app, "unauthorized")),
  ],
  auth: walk(path.join(app, "(auth)")),
  storefront: walk(path.join(app, "(storefront)")),
  account: walk(path.join(app, "(account)")),
};

describe("message scopes", () => {
  for (const [area, entries] of Object.entries(AREAS) as [keyof typeof AREAS, string[]][]) {
    it(`${area}: lists every namespace its client code uses`, () => {
      const used = namespacesUsedBy(entries);
      if (process.env.PRINT_SCOPES) console.log(`SCOPE ${area}: ${[...used.namespaces.keys()].sort().join(", ")}`);
      expect(used.dynamic, "a namespace built at run time cannot be checked; use a literal").toEqual([]);
      const listed = new Set<string>(SCOPES[area] as readonly string[]);
      const missing = [...used.namespaces].filter(([ns]) => !listed.has(ns)).map(([ns, file]) => `${ns} (first used in ${file})`);
      expect(missing).toEqual([]);
    });
  }

  it("every listed namespace exists in the catalogue", async () => {
    const all = await loadMessages("en");
    for (const [area, list] of Object.entries(SCOPES)) {
      if (list === "all") continue;
      const unknown = (list as readonly string[]).filter((ns) => !(ns in all));
      expect(unknown, `${area} lists a namespace that does not exist`).toEqual([]);
    }
  });
});
