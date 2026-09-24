// Quick terminal preview of the CieloOS install frames (no colour).
import { renderFrame, W, H } from './cielo-install.mjs';

const stages = process.argv.slice(2).map(Number);
for (const s of stages.length ? stages : [1, 5, 9]) {
  const g = renderFrame(s, 0);
  const rows = Array.from({ length: H }, () => Array(W).fill(' '));
  for (const c of g.values()) rows[c.y][c.x] = c.char;
  console.log(`--- stage ${s} ---`);
  console.log(rows.map((r) => r.join('').replace(/\s+$/, '')).join('\n'));
  console.log();
}
