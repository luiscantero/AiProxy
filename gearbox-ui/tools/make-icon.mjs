// Generates icon-source.png: the 512x512 master image that `tauri icon` slices into every
// platform icon. Written by hand (raw PNG chunks + zlib) so the app needs no image tooling.
import { deflateSync } from "node:zlib";
import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const SIZE = 512;
const BG = [23, 27, 34, 255];        // panel
const EDGE = [43, 50, 61, 255];      // border
const AMBER = [242, 183, 5, 255];    // engaged gear
const KNOB = [233, 237, 242, 255];   // shifter knob

const pixels = new Uint8Array(SIZE * SIZE * 4);

function set(x, y, [r, g, b, a]) {
  if (x < 0 || y < 0 || x >= SIZE || y >= SIZE) return;
  const i = (y * SIZE + x) * 4;
  pixels[i] = r;
  pixels[i + 1] = g;
  pixels[i + 2] = b;
  pixels[i + 3] = a;
}

function roundedRect(x0, y0, x1, y1, radius, color) {
  for (let y = y0; y < y1; y++) {
    for (let x = x0; x < x1; x++) {
      const dx = Math.max(x0 + radius - x, 0, x - (x1 - radius - 1));
      const dy = Math.max(y0 + radius - y, 0, y - (y1 - radius - 1));
      if (dx * dx + dy * dy <= radius * radius) set(x, y, color);
    }
  }
}

function disc(cx, cy, radius, color) {
  for (let y = cy - radius; y <= cy + radius; y++) {
    for (let x = cx - radius; x <= cx + radius; x++) {
      const dx = x - cx;
      const dy = y - cy;
      if (dx * dx + dy * dy <= radius * radius) set(x, y, color);
    }
  }
}

// Rounded dark plate with a subtle edge.
roundedRect(0, 0, SIZE, SIZE, 96, EDGE);
roundedRect(6, 6, SIZE - 6, SIZE - 6, 90, BG);

// H-pattern shift gate.
const top = 150;
const bottom = 350;
const left = 128;
const right = 384;
const bar = 22;

roundedRect(left, top, right, top + bar, bar / 2, AMBER);
roundedRect(left, bottom - bar, right, bottom, bar / 2, AMBER);
roundedRect(SIZE / 2 - bar / 2, top, SIZE / 2 + bar / 2, bottom, bar / 2, AMBER);

// Knob resting in first gear (top-left of the gate).
disc(left + bar / 2, top + bar / 2, 46, KNOB);

// ----------------------------------------------------------------------
// PNG encoding
// ----------------------------------------------------------------------

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buffer) {
  let c = 0xffffffff;
  for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(SIZE, 0);
ihdr.writeUInt32BE(SIZE, 4);
ihdr[8] = 8;  // bit depth
ihdr[9] = 6;  // RGBA
ihdr[10] = 0; // deflate
ihdr[11] = 0; // adaptive filtering
ihdr[12] = 0; // no interlace

// One filter byte (0 = none) per scanline.
const raw = Buffer.alloc(SIZE * (SIZE * 4 + 1));
for (let y = 0; y < SIZE; y++) {
  const offset = y * (SIZE * 4 + 1);
  raw[offset] = 0;
  Buffer.from(pixels.buffer, y * SIZE * 4, SIZE * 4).copy(raw, offset + 1);
}

const png = Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk("IHDR", ihdr),
  chunk("IDAT", deflateSync(raw, { level: 9 })),
  chunk("IEND", Buffer.alloc(0))
]);

const out = join(dirname(fileURLToPath(import.meta.url)), "..", "icon-source.png");
writeFileSync(out, png);
console.log(`wrote ${out} (${SIZE}x${SIZE})`);
