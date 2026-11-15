import { canEncode128, code128Bars } from "./code128";

/**
 * A Code 128 barcode as inline SVG: one path, scales to its box without blur, no image to download. The bars use the current text
 * colour, which on a label is always black. A text that cannot be encoded falls back to showing the text alone.
 */
export function Barcode({ value, height = 64, className }: { value: string; height?: number; className?: string }) {
  if (!canEncode128(value)) return <p className="font-mono text-lg font-bold">{value}</p>;
  const { bars, total } = code128Bars(value);
  const d = bars.map((b) => `M${b.x} 0h${b.width}v${height}h-${b.width}z`).join("");
  return (
    <svg
      role="img"
      aria-label={value}
      viewBox={`0 0 ${total} ${height}`}
      preserveAspectRatio="none"
      width="100%"
      height={height}
      shapeRendering="crispEdges"
      className={className}
      data-testid="barcode"
      data-value={value}
      data-bars={bars.length}
    >
      <path d={d} fill="currentColor" />
    </svg>
  );
}
