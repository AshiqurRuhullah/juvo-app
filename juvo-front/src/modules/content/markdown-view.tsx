import * as React from "react";
import { cn } from "@/lib/utils";
import { parseMarkdown, type Block, type Inline } from "./markdown";

function Inlines({ nodes }: { nodes: Inline[] }) {
  return (
    <>
      {nodes.map((node, i) => {
        if (node.type === "text") return <React.Fragment key={i}>{node.text}</React.Fragment>;
        if (node.type === "strong") return <strong key={i} className="font-semibold text-strong"><Inlines nodes={node.children} /></strong>;
        if (node.type === "em") return <em key={i}><Inlines nodes={node.children} /></em>;
        const external = /^https?:/i.test(node.href);
        return (
          <a key={i} href={node.href} className="font-medium text-primary underline underline-offset-2 hover:text-primary-dark" {...(external ? { target: "_blank", rel: "noopener noreferrer nofollow" } : {})}>
            <Inlines nodes={node.children} />
          </a>
        );
      })}
    </>
  );
}

const HEADING_CLASS = {
  1: "mt-8 text-3xl font-semibold text-strong first:mt-0",
  2: "mt-8 text-2xl font-semibold text-strong first:mt-0",
  3: "mt-6 text-xl font-semibold text-strong first:mt-0",
  4: "mt-5 text-lg font-semibold text-strong first:mt-0",
} as const;

function BlockView({ block }: { block: Block }) {
  if (block.type === "heading") {
    // A page's own title is the one h1; headings typed in the body start at h2 so the outline stays valid.
    const Tag = (`h${Math.min(block.level + 1, 5)}` as "h2" | "h3" | "h4" | "h5");
    return (
      <Tag className={HEADING_CLASS[block.level]}>
        <Inlines nodes={block.children} />
      </Tag>
    );
  }
  if (block.type === "list") {
    const Tag = block.ordered ? "ol" : "ul";
    return (
      <Tag className={cn("space-y-1 ps-6", block.ordered ? "list-decimal" : "list-disc")}>
        {block.items.map((item, i) => (
          <li key={i}>
            <Inlines nodes={item} />
          </li>
        ))}
      </Tag>
    );
  }
  return (
    <p>
      <Inlines nodes={block.children} />
    </p>
  );
}

/**
 * Renders page markdown as React elements only (no HTML string is ever inserted), so a page cannot run script even if the API let
 * something through. Works in server and client components.
 */
export function Markdown({ source, className, ...props }: { source: string; className?: string } & React.HTMLAttributes<HTMLDivElement>) {
  const blocks = parseMarkdown(source);
  return (
    <div className={cn("space-y-4 leading-relaxed text-strong/90 [overflow-wrap:anywhere]", className)} {...props}>
      {blocks.map((block, i) => (
        <BlockView key={i} block={block} />
      ))}
    </div>
  );
}
