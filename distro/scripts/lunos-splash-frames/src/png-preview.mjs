// Still previews of the LUNOS scene as PNGs, for looking at outside a terminal.
//   node png-preview.mjs <outDir> [-s SCALE] <frame> [frame...]
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { renderFrame, dims } from './lunos-city.mjs';

const args = process.argv.slice(2);
const outDir = args.shift();
let S = 1;
const si = args.indexOf('-s');
if (si >= 0) { S = Number(args[si + 1]) || 1; args.splice(si, 2); }
const { W, PH } = dims(S);
const SCALE = Math.max(1, Math.round(8 / S));

const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c;
  }
  return t;
})();
const crc32 = (buf) => {
  let c = -1;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
};
const chunk = (type, data) => {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
};
function png(width, height, rgb) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0); ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; ihdr[9] = 2;
  const raw = Buffer.alloc(height * (width * 3 + 1));
  let o = 0;
  for (let y = 0; y < height; y++) {
    raw[o++] = 0;
    for (let x = 0; x < width; x++) {
      const i = (y * width + x) * 3;
      raw[o++] = rgb[i]; raw[o++] = rgb[i + 1]; raw[o++] = rgb[i + 2];
    }
  }
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}
const parse = (h) => [
  parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16),
];

fs.mkdirSync(outDir, { recursive: true });
for (const f of args.map(Number)) {
  const g = renderFrame(f, S);
  const buf = Array.from({ length: PH }, () => Array(W).fill('#000000'));
  for (const c of g.values()) {
    buf[c.y * 2][c.x] = c.color;
    buf[c.y * 2 + 1][c.x] = c.bgColor || '#000000';
  }
  const width = W * SCALE;
  const height = PH * SCALE;
  const rgb = Buffer.alloc(width * height * 3);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const [r, gg, b] = parse(buf[Math.floor(y / SCALE)][Math.floor(x / SCALE)]);
      const i = (y * width + x) * 3;
      rgb[i] = r; rgb[i + 1] = gg; rgb[i + 2] = b;
    }
  }
  const file = path.join(outDir, `lunos-s${S}-${String(f).padStart(3, '0')}.png`);
  fs.writeFileSync(file, png(width, height, rgb));
  console.log(file);
}
