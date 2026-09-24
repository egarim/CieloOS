/**
 * Render the CieloOS install frames to per-stage ANSI files for the bash player.
 *
 *   node export-ansi.mjs <outDir>
 *
 * Writes <outDir>/stage-1.ans .. stage-9.ans. Each file holds FRAMES_PER_STAGE
 * frames separated by a form feed, so bash can load one stage with a single
 * `mapfile -d $'\f'`.
 *
 * Colours are emitted as 256-colour SGR rather than truecolor: a kiosk or
 * headless install runs on the Linux virtual console, which has no truecolor
 * but does understand \e[38;5;Nm.
 */
import fs from 'node:fs';
import path from 'node:path';
import { renderFrame, W, H, FRAMES_PER_STAGE } from './cielo-install.mjs';

// ── nearest xterm-256 index for a hex colour ───────────────────────────────
const CUBE = [0, 95, 135, 175, 215, 255];

function xterm256(hex) {
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);

  let best = 16;
  let bestD = Infinity;
  for (let ri = 0; ri < 6; ri++) {
    for (let gi = 0; gi < 6; gi++) {
      for (let bi = 0; bi < 6; bi++) {
        const d =
          (CUBE[ri] - r) ** 2 + (CUBE[gi] - g) ** 2 + (CUBE[bi] - b) ** 2;
        if (d < bestD) {
          bestD = d;
          best = 16 + 36 * ri + 6 * gi + bi;
        }
      }
    }
  }
  // the 24-step grey ramp often wins for the near-neutral tones
  for (let i = 0; i < 24; i++) {
    const v = 8 + i * 10;
    const d = (v - r) ** 2 + (v - g) ** 2 + (v - b) ** 2;
    if (d < bestD) {
      bestD = d;
      best = 232 + i;
    }
  }
  return best;
}

const idxCache = new Map();
const idx = (hex) => {
  if (!idxCache.has(hex)) idxCache.set(hex, xterm256(hex));
  return idxCache.get(hex);
};

/** One frame as a string of H lines, colour changes emitted only when needed. */
function frameToAnsi(grid) {
  const rows = Array.from({ length: H }, () => Array(W).fill(null));
  for (const c of grid.values()) rows[c.y][c.x] = c;

  const lines = [];
  for (const row of rows) {
    let out = '';
    let cur = null;
    // trailing blanks are dropped; \e[K at the end clears the rest of the line
    let last = -1;
    for (let x = W - 1; x >= 0; x--) {
      if (row[x]) {
        last = x;
        break;
      }
    }
    for (let x = 0; x <= last; x++) {
      const cell = row[x];
      if (!cell) {
        if (cur !== null) {
          out += '\x1b[0m';
          cur = null;
        }
        out += ' ';
        continue;
      }
      const want = idx(cell.color);
      if (want !== cur) {
        out += `\x1b[38;5;${want}m`;
        cur = want;
      }
      out += cell.char;
    }
    if (cur !== null) out += '\x1b[0m';
    lines.push(out);
  }
  return lines.join('\n');
}

const outDir = process.argv[2];
if (!outDir) {
  console.error('usage: node export-ansi.mjs <outDir>');
  process.exit(2);
}
fs.mkdirSync(outDir, { recursive: true });

let total = 0;
for (let stage = 1; stage <= 9; stage++) {
  const frames = [];
  for (let f = 0; f < FRAMES_PER_STAGE; f++) {
    frames.push(frameToAnsi(renderFrame(stage, f)));
    total++;
  }
  const file = path.join(outDir, `stage-${stage}.ans`);
  fs.writeFileSync(file, frames.join('\f'), 'utf8');
}

const bytes = fs
  .readdirSync(outDir)
  .filter((f) => f.endsWith('.ans'))
  .reduce((n, f) => n + fs.statSync(path.join(outDir, f)).size, 0);
console.log(`wrote ${total} frames to ${outDir} (${(bytes / 1024).toFixed(1)} KB)`);
