/**
 * Render the LUNOS frames to ANSI for terminal playback.
 *
 *   node export-lunos.mjs <outDir>
 *
 * Writes two gzipped streams, both with all 120 frames separated by a form feed:
 *
 *   lunos-24bit.ans.gz   truecolor — what the scene is actually drawn in
 *   lunos-256.ans.gz     256-colour fallback for terminals without COLORTERM
 *
 * Cells are '▀' with a foreground (upper pixel) and a background (lower pixel),
 * so colour runs are emitted only when either side changes — a sky row is one
 * escape and eighty blocks, not eighty escapes.
 */
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { renderFrame, dims, FRAMES } from './lunos-city.mjs';

const parse = (h) => [
  parseInt(h.slice(1, 3), 16),
  parseInt(h.slice(3, 5), 16),
  parseInt(h.slice(5, 7), 16),
];

// ── nearest xterm-256 index ────────────────────────────────────────────────
const CUBE = [0, 95, 135, 175, 215, 255];
const cache256 = new Map();
function to256(hex) {
  if (cache256.has(hex)) return cache256.get(hex);
  const [r, g, b] = parse(hex);
  let best = 16;
  let bestD = Infinity;
  for (let ri = 0; ri < 6; ri++)
    for (let gi = 0; gi < 6; gi++)
      for (let bi = 0; bi < 6; bi++) {
        const d = (CUBE[ri] - r) ** 2 + (CUBE[gi] - g) ** 2 + (CUBE[bi] - b) ** 2;
        if (d < bestD) {
          bestD = d;
          best = 16 + 36 * ri + 6 * gi + bi;
        }
      }
  for (let i = 0; i < 24; i++) {
    const v = 8 + i * 10;
    const d = (v - r) ** 2 + (v - g) ** 2 + (v - b) ** 2;
    if (d < bestD) {
      bestD = d;
      best = 232 + i;
    }
  }
  cache256.set(hex, best);
  return best;
}

// Rounding each channel to the nearest 5/255 (about 2%) merges neighbouring
// gradient steps into colour runs the encoder can skip, which nearly halves the
// truecolor stream. At this step the banding is not visible on the sky.
const Q = 5;
const quant = (v) => Math.min(255, Math.round(v / Q) * Q);
const fg24 = (h) => { const [r, g, b] = parse(h).map(quant); return `\x1b[38;2;${r};${g};${b}m`; };
const bg24 = (h) => { const [r, g, b] = parse(h).map(quant); return `\x1b[48;2;${r};${g};${b}m`; };
const fg256 = (h) => `\x1b[38;5;${to256(h)}m`;
const bg256 = (h) => `\x1b[48;5;${to256(h)}m`;

function frameToAnsi(grid, deep, W, H) {
  const FG = deep ? fg24 : fg256;
  const BG = deep ? bg24 : bg256;
  const lines = [];
  for (let y = 0; y < H; y++) {
    let out = '';
    let curFg = null;
    let curBg = null;
    for (let x = 0; x < W; x++) {
      const c = grid.get(`${x},${y}`);
      if (!c) {
        out += ' ';
        continue;
      }
      const bg = c.bgColor || '#000000';
      if (c.color !== curFg) {
        out += FG(c.color);
        curFg = c.color;
      }
      if (bg !== curBg) {
        out += BG(bg);
        curBg = bg;
      }
      out += c.char;
    }
    lines.push(out + '\x1b[0m\x1b[K');
  }
  return lines.join('\n');
}

const outDir = process.argv[2];
if (!outDir) {
  console.error('usage: node export-lunos.mjs <outDir> [scales...]   (default: 1 2)');
  process.exit(2);
}
const scales = process.argv.slice(3).map(Number).filter(Boolean);
fs.mkdirSync(outDir, { recursive: true });

for (const S of scales.length ? scales : [1, 2]) {
  const { W, H } = dims(S);
  const grids = [];
  for (let f = 0; f < FRAMES; f++) grids.push(renderFrame(f, S));
  for (const [suffix, deep] of [['24bit', true], ['256', false]]) {
    const body = grids.map((g) => frameToAnsi(g, deep, W, H)).join('\f');
    const file = path.join(outDir, `lunos-${W}x${H}-${suffix}.ans.gz`);
    fs.writeFileSync(file, zlib.gzipSync(Buffer.from(body, 'utf8'), { level: 9 }));
    console.log(
      `${W}x${H} ${suffix}: ${(Buffer.byteLength(body) / 1024 / 1024).toFixed(2)} MB raw -> ` +
        `${(fs.statSync(file).size / 1024).toFixed(0)} KB gzipped`
    );
  }
}
