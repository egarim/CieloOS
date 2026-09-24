/**
 * CieloOS installer animation — "the system assembling itself".
 *
 * The diagram mirrors the nine steps in distro/install.sh: each step reveals the
 * piece it actually installs, so the picture IS the progress indicator. Stage N
 * is a short pulse loop showing everything installed up to and including step N,
 * with step N's new pieces highlighted; the bash player loops the stage it is on
 * until the installer reports the next one.
 *
 *   node cielo-install.mjs            push all stages to ASCII Motion
 *   node cielo-install.mjs --frames   also write frames.json for the bash player
 */
import fs from 'node:fs';

export const W = 80;
export const H = 20;
export const FRAMES_PER_STAGE = 12;

// ── palette ────────────────────────────────────────────────────────────────
// Indigo/violet, matching --acc/--acc2 in distro/scripts/cielo-install-ui.sh.
const C = {
  found: '#4C4F8A', // the ubuntu foundation bar
  border: '#5B5BD6', // installed, settled
  label: '#C7D2FE',
  activeBorder: '#A78BFA', // the step running right now
  activeLabel: '#F5F3FF',
  shimmer: '#EDE9FE', // bright cell running around an active border
  bus: '#474B7E', // connection lines
  pulse: '#C4B5FD', // data moving along a line
};

// ── layout ─────────────────────────────────────────────────────────────────
const ROW = {
  banner: 0,
  bus1: 1,
  top: 2, // h=3 -> 2,3,4
  bus2: 5,
  svc: 6, // h=3 -> 6,7,8
  bus3: 9,
  rt: 10, // h=4 -> 10..13
  bus4: 14,
  base: 15, // h=3 -> 15,16,17
  bus5: 18,
  found: 19,
};

// Step 6 writes the environment into the runtime box. The real mode/bind/port
// are only known at install time, so the frames carry a placeholder of a fixed
// length and position that the bash player overlays with the actual values.
export const ENV_LINE = 'mode app · 127.0.0.1:5148';
export const ENV_ROW = 12;

const BASE_W = 15;
const BASE_X = [4, 23, 42, 61];
const baseCenter = (i) => BASE_X[i] + Math.floor(BASE_W / 2);

const BOXES = {
  podman: { x: BASE_X[0], y: ROW.base, w: BASE_W, h: 3, lines: ['podman'], stage: 2 },
  sessions: { x: BASE_X[1], y: ROW.base, w: BASE_W, h: 3, lines: ['session img'], stage: 3 },
  search: { x: BASE_X[2], y: ROW.base, w: BASE_W, h: 3, lines: ['search svc'], stage: 3 },
  restart: { x: BASE_X[3], y: ROW.base, w: BASE_W, h: 3, lines: ['restart plcy'], stage: 4 },
  runtime: {
    x: 23,
    y: ROW.rt,
    w: 34,
    h: 4,
    lines: ['/opt/cielo · runtime', ENV_LINE],
    stage: 5,
    // step 6 fills in the second line without redrawing the box
    lineStages: [5, 6],
  },
  service: { x: 26, y: ROW.svc, w: 28, h: 3, lines: ['cielo-runtime.service'], stage: 7 },
  chat: { x: 10, y: ROW.top, w: 24, h: 3, lines: ['chat · Open WebUI'], stage: 8 },
  panel: { x: 46, y: ROW.top, w: 24, h: 3, lines: ['panel · /v1/agent'], stage: 8 },
};

const boxCenter = (b) => b.x + Math.floor(b.w / 2);

// ── drawing primitives ─────────────────────────────────────────────────────
const key = (x, y) => `${x},${y}`;

function put(g, x, y, char, color) {
  if (x < 0 || x >= W || y < 0 || y >= H) return;
  g.set(key(x, y), { x, y, char, color });
}

function text(g, x, y, str, color, fillSpaces) {
  [...str].forEach((ch, i) => {
    if (ch !== ' ') put(g, x + i, y, ch, color);
    else if (fillSpaces) put(g, x + i, y, ' ', color);
  });
}

function drawBox(g, b, active) {
  const border = active ? C.activeBorder : C.border;
  const label = active ? C.activeLabel : C.label;
  const { x, y, w, h } = b;
  const right = x + w - 1;
  const bottom = y + h - 1;

  put(g, x, y, '┌', border);
  put(g, right, y, '┐', border);
  put(g, x, bottom, '└', border);
  put(g, right, bottom, '┘', border);
  for (let i = x + 1; i < right; i++) {
    put(g, i, y, '─', border);
    put(g, i, bottom, '─', border);
  }
  for (let j = y + 1; j < bottom; j++) {
    put(g, x, j, '│', border);
    put(g, right, j, '│', border);
  }
  return { border, label };
}

function drawBoxLabels(g, b, active, stage) {
  const label = active ? C.activeLabel : C.label;
  const inner = b.w - 2;
  b.lines.forEach((line, i) => {
    if (!line) return;
    const lineStage = b.lineStages ? b.lineStages[i] : b.stage;
    if (lineStage > stage) return;
    const lx = b.x + 1 + Math.max(0, Math.round((inner - line.length) / 2));
    // a line that arrives on this very step gets the bright colour even when the
    // box itself was drawn a step earlier (runtime box, step 5 then step 6)
    const isNew = lineStage === stage;
    text(g, lx, b.y + 1 + i, line, isNew || active ? C.activeLabel : label);
  });
}

/** Perimeter of a box, clockwise from the top-left corner — the shimmer path. */
function perimeter(b) {
  const pts = [];
  const right = b.x + b.w - 1;
  const bottom = b.y + b.h - 1;
  for (let i = b.x; i <= right; i++) pts.push([i, b.y]);
  for (let j = b.y + 1; j <= bottom; j++) pts.push([right, j]);
  for (let i = right - 1; i >= b.x; i--) pts.push([i, bottom]);
  for (let j = bottom - 1; j > b.y; j--) pts.push([b.x, j]);
  return pts;
}

// ── buses: one-row trees connecting a trunk to several branches ────────────
/**
 * Draw a single-row bus. `trunk` is the column the line continues toward on
 * `trunkSide`; `taps` are the columns it drops to on the other side.
 */
function drawBus(g, y, trunk, taps, trunkUp) {
  const cols = [...new Set([trunk, ...taps])].sort((a, b) => a - b);
  const min = cols[0];
  const max = cols[cols.length - 1];
  for (let x = min; x <= max; x++) put(g, x, y, '─', C.bus);

  const tapChar = trunkUp ? '┬' : '┴'; // taps point away from the trunk
  const trunkChar = trunkUp ? '┴' : '┬';

  for (const t of taps) {
    let ch = tapChar;
    if (t === min) ch = trunkUp ? '┌' : '└';
    else if (t === max) ch = trunkUp ? '┐' : '┘';
    put(g, t, y, ch, C.bus);
  }
  if (!taps.includes(trunk)) {
    let ch = trunkChar;
    if (trunk === min) ch = trunkUp ? '└' : '┌';
    else if (trunk === max) ch = trunkUp ? '┘' : '┐';
    put(g, trunk, y, ch, C.bus);
  }
  return { min, max };
}

/**
 * Notch a box border where a line meets it, so the join reads as a join.
 * `only` guards the foundation bar, whose centred label must not be chewed up
 * by the stubs that land on it.
 */
function notch(g, x, y, char, only) {
  const cell = g.get(key(x, y));
  if (!cell) return;
  if (only && !only.includes(cell.char)) return;
  put(g, x, y, char, cell.color);
}

// ── stage composition ──────────────────────────────────────────────────────
const revealedBoxes = (stage) =>
  Object.entries(BOXES).filter(([, b]) => b.stage <= stage);

/** Which boxes are "the thing step N just installed". */
function activeBoxNames(stage) {
  return Object.entries(BOXES)
    .filter(([, b]) => b.stage === stage)
    .map(([name]) => name);
}

export function renderFrame(stage, f) {
  const g = new Map();
  const t = f / FRAMES_PER_STAGE;
  const active = new Set(activeBoxNames(stage));

  // [1] foundation
  if (stage >= 1) {
    const bar = ' ubuntu 24.04 · dependencies ';
    const x0 = 4;
    const x1 = W - 5;
    for (let x = x0; x <= x1; x++) put(g, x, ROW.found, '═', C.found);
    const bx = Math.floor((W - bar.length) / 2);
    text(g, bx, ROW.found, bar, stage === 1 ? C.activeLabel : C.label, true);
  }

  // [2][3][4] base row + stubs down to the foundation
  for (const [name, b] of revealedBoxes(stage)) {
    if (b.y !== ROW.base) continue;
    drawBox(g, b, active.has(name));
    drawBoxLabels(g, b, active.has(name), stage);
    const c = boxCenter(b);
    put(g, c, ROW.bus5, '│', C.bus);
    notch(g, c, b.y + b.h - 1, '┬');
    notch(g, c, ROW.found, '╧', '═');
  }

  // [5] runtime + the tree down to whichever base boxes exist
  if (stage >= 5) {
    const rt = BOXES.runtime;
    drawBox(g, rt, active.has('runtime'));
    drawBoxLabels(g, rt, active.has('runtime'), stage);

    const taps = BASE_X.map((_, i) => baseCenter(i)).filter((_, i) => {
      const b = Object.values(BOXES).find((bb) => bb.x === BASE_X[i] && bb.y === ROW.base);
      return b && b.stage <= stage;
    });
    if (taps.length) {
      drawBus(g, ROW.bus4, boxCenter(rt), taps, true);
      notch(g, boxCenter(rt), rt.y + rt.h - 1, '┬');
      for (const t of taps) notch(g, t, ROW.base, '┴');
    }
  }

  // [7] systemd service
  if (stage >= 7) {
    const svc = BOXES.service;
    drawBox(g, svc, active.has('service'));
    drawBoxLabels(g, svc, active.has('service'), stage);
    put(g, boxCenter(svc), ROW.bus3, '│', C.bus);
    notch(g, boxCenter(svc), svc.y + svc.h - 1, '┬');
    notch(g, boxCenter(BOXES.runtime), ROW.rt, '┴');
  }

  // [8] chat + panel
  if (stage >= 8) {
    const taps = [];
    for (const name of ['chat', 'panel']) {
      const b = BOXES[name];
      drawBox(g, b, active.has(name));
      drawBoxLabels(g, b, active.has(name), stage);
      taps.push(boxCenter(b));
      notch(g, boxCenter(b), b.y + b.h - 1, '┬');
    }
    drawBus(g, ROW.bus2, boxCenter(BOXES.service), taps, false);
    notch(g, boxCenter(BOXES.service), ROW.svc, '┴');
  }

  // [9] presentation mode crown
  if (stage >= 9) {
    const x0 = 10;
    const x1 = W - 11;
    for (let x = x0; x <= x1; x++) put(g, x, ROW.banner, '▁', C.activeBorder);
    const cap = ' kiosk · app · headless ';
    const cx = Math.floor((W - cap.length) / 2);
    for (let i = 0; i < cap.length; i++) put(g, cx + i, ROW.banner, ' ', C.activeBorder);
    text(g, cx, ROW.banner, cap, C.activeLabel);
    drawBus(g, ROW.bus1, Math.floor(W / 2), [boxCenter(BOXES.chat), boxCenter(BOXES.panel)], true);
    for (const n of ['chat', 'panel']) notch(g, boxCenter(BOXES[n]), ROW.top, '┴');
  }

  // ── animation layer ──────────────────────────────────────────────────────
  // A bright cell runs clockwise around every box this step just installed.
  for (const name of active) {
    const b = BOXES[name];
    if (!b) continue;
    const pts = perimeter(b);
    const head = Math.floor(t * pts.length);
    for (let k = 0; k < 4; k++) {
      const [px, py] = pts[(head - k + pts.length * 2) % pts.length];
      const existing = g.get(key(px, py));
      if (existing) put(g, px, py, existing.char, k === 0 ? C.shimmer : C.activeBorder);
    }
  }

  // Pulses travelling along the horizontal buses that exist.
  const buses = [];
  if (stage >= 5) buses.push({ y: ROW.bus4, dir: 1 });
  if (stage >= 8) buses.push({ y: ROW.bus2, dir: -1 });
  if (stage >= 9) buses.push({ y: ROW.bus1, dir: 1 });
  for (const { y, dir } of buses) {
    const cells = [...g.values()].filter((c) => c.y === y);
    if (!cells.length) continue;
    const min = Math.min(...cells.map((c) => c.x));
    const max = Math.max(...cells.map((c) => c.x));
    const span = max - min;
    const pos = dir > 0 ? min + Math.floor(t * span) : max - Math.floor(t * span);
    const cell = g.get(key(pos, y));
    if (cell) put(g, pos, y, '●', C.pulse);
  }

  // Vertical stubs blink in sequence so the base row feels alive.
  if (stage >= 2) {
    const lit = Math.floor(t * 4) % 4;
    BASE_X.forEach((_, i) => {
      const b = Object.values(BOXES).find((bb) => bb.x === BASE_X[i] && bb.y === ROW.base);
      if (!b || b.stage > stage) return;
      if (i === lit) put(g, baseCenter(i), ROW.bus5, '●', C.pulse);
    });
  }

  return g;
}

export const STAGES = [
  'Dependencies',
  "Service user 'cielo' + podman",
  'Session images + search',
  'Session restart policy',
  'Install to /opt/cielo',
  'Environment',
  'systemd service',
  'Chat UI',
  'Presentation mode',
];

// ── push to ASCII Motion ───────────────────────────────────────────────────
async function main() {
  const wantFrames = process.argv.includes('--frames');
  // Only the editor push needs the MCP bridge; frame generation is standalone,
  // so this file also works inside the CieloOS repo with nothing else running.
  const { call } = await import('./am.mjs');

  const all = [];
  for (let stage = 1; stage <= 9; stage++) {
    for (let f = 0; f < FRAMES_PER_STAGE; f++) {
      all.push({ stage, f, grid: renderFrame(stage, f) });
    }
  }

  if (wantFrames) {
    const out = all.map(({ stage, f, grid }) => ({
      stage,
      f,
      cells: [...grid.values()],
    }));
    fs.writeFileSync(new URL('./cielo-frames.json', import.meta.url), JSON.stringify(out));
    console.log(`wrote cielo-frames.json (${out.length} frames)`);
  }

  await call('new_project', { name: 'CieloOS Install', width: W, height: H });
  await call('set_frame_rate', { fps: 14 });

  for (let i = 0; i < all.length; i++) {
    const { stage, f, grid } = all[i];
    if (i > 0) await call('add_frame', { duration: 71 });
    await call('go_to_frame', { index: i });
    await call('set_cells_batch', { cells: [...grid.values()] });
    if (f === 0) {
      await call('set_frame_name', { index: i, name: `${stage}. ${STAGES[stage - 1]}` });
      process.stdout.write(`stage ${stage} `);
    }
  }
  await call('go_to_frame', { index: 0 });
  console.log(`\n${all.length} frames pushed.`);
}

if (process.argv[1] && import.meta.url.endsWith('cielo-install.mjs') &&
    process.argv[1].replace(/\\/g, '/').endsWith('cielo-install.mjs')) {
  await main();
}
