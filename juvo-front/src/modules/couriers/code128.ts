/**
 * A small Code 128 encoder (ISO/IEC 15417) for the parcel labels, so no barcode package is needed.
 *
 * Code 128 draws every symbol as 11 modules (three bars, three spaces) and the stop symbol as 13. A barcode is: a start symbol that
 * names the code set, the data symbols, a check symbol and the stop symbol, with a quiet zone of at least 10 modules on both sides.
 * This encoder uses set B (printable ASCII, 32 to 126) and switches to set C (digit pairs) when the text is only digits, which
 * halves the width of a long number. The check symbol is (start value + sum of position * symbol value) modulo 103.
 */

/** Element widths (bar, space, bar, space, bar, space) of symbols 0 to 105, then the stop symbol (seven elements) at index 106. */
const PATTERNS = [
  "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
  "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
  "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
  "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
  "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
  "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
  "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
  "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
  "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
  "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
  "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
] as const;

export const START_B = 104;
export const START_C = 105;
export const STOP = 106;
/** Quiet zone, in modules, left empty on both sides of the symbol. */
export const QUIET_ZONE = 10;

/** The element widths of one symbol (exposed for tests). */
export const patternOf = (value: number): string => PATTERNS[value]!;

/** True when the text can be drawn: 1 to 80 printable ASCII characters. */
export function canEncode128(text: string): boolean {
  return text.length > 0 && text.length <= 80 && /^[\x20-\x7e]+$/.test(text);
}

/** The symbol values of the text: start symbol, data, check symbol, stop symbol. */
export function code128Values(text: string): number[] {
  if (!canEncode128(text)) throw new Error("Code 128 text must be 1 to 80 printable ASCII characters.");
  const data: number[] = [];
  let start = START_B;
  if (/^\d+$/.test(text) && text.length >= 4 && text.length % 2 === 0) {
    start = START_C;
    for (let i = 0; i < text.length; i += 2) data.push(Number(text.slice(i, i + 2)));
  } else {
    for (const ch of text) data.push(ch.charCodeAt(0) - 32);
  }
  return [start, ...data, checkValue(start, data), STOP];
}

/** The check symbol: (start + sum of position * value) mod 103, positions counted from 1. */
export function checkValue(start: number, data: readonly number[]): number {
  let sum = start;
  data.forEach((value, index) => {
    sum += value * (index + 1);
  });
  return sum % 103;
}

/** Widths of the alternating bars and spaces (starting with a bar), without the quiet zones. */
export function code128Widths(text: string): number[] {
  return code128Values(text).flatMap((value) => [...PATTERNS[value]!].map(Number));
}

/** The bars as `{x, width}` in modules, with the quiet zone before the first bar. `total` is the full width, quiet zones included. */
export function code128Bars(text: string): { bars: { x: number; width: number }[]; total: number } {
  const bars: { x: number; width: number }[] = [];
  let x = QUIET_ZONE;
  code128Widths(text).forEach((width, index) => {
    if (index % 2 === 0) bars.push({ x, width });
    x += width;
  });
  return { bars, total: x + QUIET_ZONE };
}
