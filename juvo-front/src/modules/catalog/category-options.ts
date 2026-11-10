import type { CategoryNode } from "./api";

export type CategoryOption = { id: string; label: string; depth: number; isActive: boolean };

/** The category tree as a flat list in tree order, labelled with the path ("Clothing > T-shirts"), for pickers and filters. */
export function flattenCategories(nodes: readonly CategoryNode[], parents: string[] = [], depth = 0): CategoryOption[] {
  return nodes.flatMap((node) => {
    const path = [...parents, node.nameEn];
    return [{ id: node.id, label: path.join(" > "), depth, isActive: node.isActive }, ...flattenCategories(node.children, path, depth + 1)];
  });
}
