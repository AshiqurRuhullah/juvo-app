import { deflateSync } from "node:zlib";

/** CRC-32 as PNG chunks need it. */
function crc32(buffer: Buffer): number {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let i = 0; i < 8; i++) crc = crc & 1 ? (crc >>> 1) ^ 0xedb88320 : crc >>> 1;
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Buffer): Buffer {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

/**
 * A real PNG picture without any image library: the left half is `left`, the right half is `right` (RGB), so a crop shows which side
 * the focal point picked. Used to upload pictures that meet (or deliberately miss) the 1000 px rule.
 */
export function makePng(width: number, height: number, left: [number, number, number] = [200, 30, 30], right: [number, number, number] = [30, 60, 200]): Buffer {
  const stride = width * 3 + 1;
  const raw = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) {
    const row = y * stride; // filter byte 0 (none)
    for (let x = 0; x < width; x++) {
      const colour = x < width / 2 ? left : right;
      raw[row + 1 + x * 3] = colour[0];
      raw[row + 2 + x * 3] = colour[1];
      raw[row + 3 + x * 3] = colour[2];
    }
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8; // bit depth
  header[9] = 2; // colour type: RGB
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", header), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}
