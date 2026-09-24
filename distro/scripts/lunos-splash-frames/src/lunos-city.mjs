/**
 * LUNOS — the city, the river, and a full moon, watched from a ledge.
 *
 * Composed after the Undertale surface scene: a mountain and a skyline flat in
 * silhouette, a sky of streaked cloud bands, a large and nearly empty foreground
 * ledge, and three figures with their backs to us looking at it. The emotional
 * content is the watching, not the view — which is why the figures are the only
 * thing in frame that never moves.
 *
 * THE PALETTE IS LOCKED. Four keyed palettes — day, golden, dusk, night — of
 * twenty slots each, interpolated slot by slot as the day turns, and nothing is
 * drawn in a colour that is not a slot. Any single frame holds at most twenty
 * colours. That constraint is the whole look: an earlier version rendered
 * free-form colour with smooth gradients and came out reading as a photograph
 * with the contrast turned down, which is the opposite of this. If you need a
 * new tone, add a slot; do not mix one inline.
 *
 * SCALE. The scene is authored once, in scale-1 units, and drawn at any integer
 * SCALE. Every cell is a '▀' whose foreground is the upper pixel and background
 * the lower, so a canvas of 80*S by 24*S characters holds 80*S by 48*S pixels,
 * and those pixels stay square because a terminal cell is about twice as tall as
 * it is wide.
 *
 *   S=1   80x24 chars    80x48 px    fits any terminal
 *   S=2  160x48 chars   160x96 px    four times the pixels
 *
 * Quadrant blocks were the other way to get more resolution and are not used:
 * they subdivide horizontally as well, which makes subpixels 1:2 rather than
 * square, buying horizontal detail only. Doubling the canvas keeps them square.
 *
 * 120 frames at 12fps. 0..95 is the transition; 96..119 is a seamless night-idle
 * loop, so a player can run it once and then hold on the tail forever.
 *
 * EVERYTHING THAT MOVES IS A SINE OF `phase`. Nothing quantises that clock — an
 * earlier version used Math.floor(beat * k) and the water glints teleported once
 * every few frames, reading as a fault rather than as water.
 *
 *   node lunos-city.mjs [scale]           push to ASCII Motion
 *   node png-preview.mjs <dir> [-s N] 0 60 119
 */

export const FRAMES = 120;
export const TRANSITION_END = 96;
export const LOOP = 24;

/** Character and pixel dimensions at a given scale. */
export const dims = (S = 1) => ({
  S,
  W: 80 * S, // pixel columns
  H: 24 * S, // character rows
  PH: 48 * S, // pixel rows
});

// ── colour ─────────────────────────────────────────────────────────────────
const hex = (r, g, b) =>
  '#' +
  [r, g, b]
    .map((v) => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0'))
    .join('');

const parse = (h) => [
  parseInt(h.slice(1, 3), 16),
  parseInt(h.slice(3, 5), 16),
  parseInt(h.slice(5, 7), 16),
];

const clamp01 = (v) => Math.max(0, Math.min(1, v));

const mix = (a, b, t) => {
  const A = parse(a);
  const B = parse(b);
  const k = clamp01(t);
  return hex(A[0] + (B[0] - A[0]) * k, A[1] + (B[1] - A[1]) * k, A[2] + (B[2] - A[2]) * k);
};

const smoothstep = (a, b, v) => {
  const t = clamp01((v - a) / (b - a));
  return t * t * (3 - 2 * t);
};

// ── the four palettes ──────────────────────────────────────────────────────
const SLOTS = [
  'sky0', 'sky1', 'sky2', 'sky3', 'sky4', // top of frame down to the horizon
  'cloudLt', 'cloudDk',
  'orb', 'orbGlow', 'orbDim',
  'far', 'near', // silhouettes: mountain and skyline / the closer bank
  'water', 'waterLt',
  'ground', 'groundDk',
  'foliage',
  'figure',
  'accent',
  'light', // lit windows, and the moon's glitter on the water
];

const PALETTES = {
  day: ['#4FA8E0', '#7CC4EC', '#A9DAF2', '#CFE8F5', '#EFE4C0',
    '#FFFFFF', '#BFDCEF', '#FFFBE8', '#FFF3B0', '#E8DCB0', '#5B6E8C', '#33455C',
    '#6FB8E0', '#A8DCF2', '#B8946A', '#7A5C3E', '#4A6B35', '#22303A',
    '#B5452F', '#FFD24A'],
  golden: ['#F2B72A', '#FFC935', '#FFD93B', '#FFE469', '#FFF0A8',
    '#FFE87A', '#E8A62E', '#FFFDF0', '#FFE9A0', '#E8C87A', '#6B2D5C', '#4A1E42',
    '#F7C851', '#FFEFB0', '#D2A06A', '#9A6A40', '#6E7A2E', '#2A1420',
    '#C0392B', '#FFE9A0'],
  dusk: ['#2B1B50', '#4A2263', '#7A2C63', '#B03F5A', '#E0705A',
    '#C0567A', '#5A2A5E', '#FFF5D0', '#E8C089', '#D8B080', '#3A1840', '#22102A',
    '#8A3A62', '#C0567A', '#5A3E46', '#32222C', '#34342A', '#150A14',
    '#C0392B', '#FFC24A'],
  night: ['#0A0620', '#150D34', '#241350', '#351A62', '#472270',
    '#3E2064', '#1B0E3C', '#FFF8DC', '#6E5E9E', '#C8BFA0', '#170A2E', '#0D0620',
    '#3A2668', '#6B58A8', '#1A1228', '#0E0A18', '#1E2422', '#050208',
    '#8A2A22', '#FFD24A'],
};

const SEQ = [
  [0.0, PALETTES.day],
  [0.36, PALETTES.golden],
  [0.58, PALETTES.dusk],
  [0.84, PALETTES.night],
];

/** The active palette at time t, as {slotName: hex}. */
function paletteAt(t) {
  let arr = SEQ[SEQ.length - 1][1];
  for (let i = SEQ.length - 1; i >= 0; i--) {
    if (t >= SEQ[i][0]) {
      arr =
        i < SEQ.length - 1
          ? SEQ[i][1].map((c, j) =>
              mix(c, SEQ[i + 1][1][j], smoothstep(SEQ[i][0], SEQ[i + 1][0], t))
            )
          : SEQ[i][1];
      break;
    }
  }
  const P = {};
  SLOTS.forEach((name, i) => (P[name] = arr[i]));
  return P;
}

// ── deterministic noise ────────────────────────────────────────────────────
function rng(seed) {
  let s = seed >>> 0;
  return () => {
    s = (s * 1664525 + 1013904223) >>> 0;
    return s / 4294967296;
  };
}

// ── the scene, authored in scale-1 units ───────────────────────────────────
const BASE = {
  farBank: 24,
  lip: 36, // where the near bank ends and the figures stand
  ledgeTop: 37,
  skyBands: [0, 6, 12, 18, 23],
  mountain: { peak: 13, apex: 5, halfWidth: 15 },
  city: [
    { x: 47, w: 5, roof: 18 },
    { x: 53, w: 7, roof: 13 },
    { x: 61, w: 4, roof: 16 },
    { x: 66, w: 8, roof: 10 },
    { x: 75, w: 5, roof: 15 },
  ],
  cityNear: [
    { x: 42, w: 4, roof: 20 },
    { x: 58, w: 3, roof: 19 },
    { x: 70, w: 4, roof: 18 },
  ],
  shelfX: 52, // where the ledge drops to a lower, shadowed shelf
  shelfDrop: 3,
};

// Figures, drawn at each scale rather than pixel-doubled: a doubled 4x9 sprite
// is a blocky 4x9, not a better one, and at 8x18 there is room for shoulders and
// legs to actually read.
const SPRITES = {
  1: {
    tall: ['.##.', '.##.', '####', '####', '####', '####', '.##.', '.##.', '#..#'],
    small: ['###', '###', '###', '###', '.#.', '#.#'],
    creature: ['.###.', '#####', '#####', '#.#.#'],
    accent: [1, 4],
  },
  2: {
    tall: [
      '..####..', '.######.', '.######.', '..####..',
      '.######.', '########', '########', '########',
      '########', '########', '########', '########',
      '.######.', '.######.', '.##..##.', '.##..##.',
      '.##..##.', '###..###',
    ],
    small: [
      '.####.', '######', '######', '.####.',
      '######', '######', '######', '######',
      '######', '.####.', '.#..#.', '##..##',
    ],
    creature: [
      '..######..', '.########.', '##########', '##########',
      '##########', '##########', '.##....##.', '.##....##.',
    ],
    accent: [3, 8],
  },
};

const FIGURE_X = { creature: 17, small: 24, tall: 29 };

// Five letters is the whole font. Drawn into the pixel buffer rather than set as
// terminal text: text does not scale with S, and a terminal glyph is a different
// typeface from the rest of the picture.
const FONT = {
  L: ['#....', '#....', '#....', '#....', '#....', '#....', '#####'],
  U: ['#...#', '#...#', '#...#', '#...#', '#...#', '#...#', '.###.'],
  N: ['#...#', '##..#', '##..#', '#.#.#', '#..##', '#..##', '#...#'],
  O: ['.###.', '#...#', '#...#', '#...#', '#...#', '#...#', '.###.'],
  S: ['.####', '#....', '#....', '.###.', '....#', '....#', '####.'],
};

/** Everything that depends on the scale, built once per scale. */
const SCENES = new Map();

function buildScene(S) {
  const { W, H, PH } = dims(S);
  const farBank = BASE.farBank * S;
  const lip = BASE.lip * S;
  const ledgeTop = BASE.ledgeTop * S;
  const riverTop = farBank + 1;

  const city = BASE.city.map((b) => ({ x: b.x * S, w: b.w * S, roof: b.roof * S }));
  const cityNear = BASE.cityNear.map((b) => ({ x: b.x * S, w: b.w * S, roof: b.roof * S }));
  const mountain = {
    peak: BASE.mountain.peak * S,
    apex: BASE.mountain.apex * S,
    halfWidth: BASE.mountain.halfWidth * S,
  };

  // Windows keep their apparent size and spacing: at twice the resolution a
  // window is a 2x2 block placed every four pixels, not a lone pixel every two.
  const windows = (() => {
    const rand = rng(31415);
    const out = [];
    for (const b of city) {
      for (let y = b.roof + 2 * S; y < farBank - S; y += 2 * S) {
        for (let x = b.x + S; x < b.x + b.w - S; x += 2 * S) {
          if (rand() < 0.42) continue;
          out.push({
            x, y,
            at: 0.5 + rand() * 0.34,
            seed: rand() * Math.PI * 2,
            flicker: rand() < 0.12,
          });
        }
      }
    }
    return out;
  })();

  // Stars stay one pixel at every scale — finer is better for a star — so their
  // count grows with the area rather than the width.
  const stars = (() => {
    const rand = rng(2718);
    return Array.from({ length: Math.round(46 * S * S) }, () => ({
      x: Math.floor(rand() * W),
      y: Math.floor(rand() * 22 * S),
      seed: rand() * Math.PI * 2,
      mag: 0.5 + rand() * 0.5,
    }));
  })();

  const streaks = (() => {
    const rand = rng(1618);
    const out = [];
    for (let i = 0; i < 26 * S; i++) {
      out.push({
        y: (2 + Math.floor(rand() * 22)) * S + Math.floor(rand() * S),
        x: rand() * W,
        len: (6 + Math.floor(rand() * 22)) * S,
        light: rand() < 0.55,
        speed: 0.4 + rand() * 1.1,
        seed: rand() * Math.PI * 2,
        tail: rand() < 0.45 ? (3 + Math.floor(rand() * 8)) * S : 0,
        thick: S,
      });
    }
    return out;
  })();

  const reflectDepth = (() => {
    const rand = rng(8080);
    return Array.from({ length: W }, () => S * (1 + Math.floor(rand() * 3)));
  })();

  const tufts = (() => {
    const rand = rng(999);
    return Array.from({ length: 16 * S }, () => ({
      x: Math.floor(rand() * W),
      seed: rand() * Math.PI * 2,
    }));
  })();

  const spr = SPRITES[S] || SPRITES[1];
  const figures = Object.entries(FIGURE_X).map(([name, x]) => ({
    rows: spr[name],
    x: x * S,
    accent: name === 'tall' ? spr.accent : null,
  }));

  const mountainTop = (x) => {
    const d = Math.abs(x - mountain.peak);
    if (d > mountain.halfWidth) return null;
    return Math.round(mountain.apex + (d / mountain.halfWidth) * (farBank - mountain.apex));
  };

  const skylineTop = (x) => {
    let top = mountainTop(x);
    for (const b of [...city, ...cityNear]) {
      if (x >= b.x && x < b.x + b.w && (top === null || b.roof < top)) top = b.roof;
    }
    return top;
  };

  return {
    S, W, H, PH, farBank, riverTop, lip, ledgeTop, city, cityNear,
    skyBands: BASE.skyBands.map((v) => v * S),
    shelfX: BASE.shelfX * S,
    shelfDrop: BASE.shelfDrop * S,
    windows, stars, streaks, reflectDepth, tufts, figures,
    mountainTop, skylineTop,
  };
}

const getScene = (S) => {
  if (!SCENES.has(S)) SCENES.set(S, buildScene(S));
  return SCENES.get(S);
};

// ── the frame ──────────────────────────────────────────────────────────────
export function renderFrame(f, S = 1) {
  const sc = getScene(S);
  const { W, H, PH, farBank, riverTop, lip, ledgeTop } = sc;

  const buf = Array.from({ length: PH }, () => Array(W).fill('#000000'));
  const px = (x, y, color) => {
    const xi = Math.round(x);
    const yi = Math.round(y);
    if (xi < 0 || xi >= W || yi < 0 || yi >= PH) return;
    buf[yi][xi] = color;
  };
  const block = (x, y, w, h, color) => {
    for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) px(x + i, y + j, color);
  };
  /** Flat disc — no soft falloff anywhere; the glow is a second, larger disc. */
  const disc = (cx, cy, r, color) => {
    for (let y = Math.floor(cy - r); y <= Math.ceil(cy + r); y++) {
      for (let x = Math.floor(cx - r); x <= Math.ceil(cx + r); x++) {
        if (Math.hypot(x - cx, y - cy) <= r) px(x, y, color);
      }
    }
  };

  const t = Math.min(1, f / TRANSITION_END);
  const phase = ((f % LOOP) / LOOP) * Math.PI * 2;
  const night = smoothstep(0.5, 0.86, t);
  const P = paletteAt(t);

  // The sun sets in the gap between the mountain and the city; the moon comes up
  // to the right of it, with a stretch of proper twilight between them.
  const sunT = clamp01(t / 0.46);
  const sunX = (38 - sunT * 4) * S;
  const sunY = (7 + sunT * 19) * S;
  const moonT = clamp01((t - 0.52) / 0.48);
  const moonX = (57 - moonT * 4) * S;
  const moonY = (32 - moonT * 24) * S;
  const moonUp = moonT > 0.02;
  const orbR = 5 * S;
  const glowR = 6.5 * S;

  // — sky: flat bands, not a gradient —
  for (let y = 0; y < farBank; y++) {
    let band = 0;
    for (let i = 0; i < sc.skyBands.length; i++) if (y >= sc.skyBands[i]) band = i;
    const c = P[`sky${band}`];
    for (let x = 0; x < W; x++) px(x, y, c);
  }

  // — the orb's halo: one flat disc just larger than the orb —
  if (moonUp && moonY < farBank) disc(moonX, moonY, glowR, P.orbGlow);
  if (sunY < farBank + 4 * S && night < 0.9) disc(sunX, sunY, glowR + 0.5 * S, P.orbGlow);

  // — cloud streaks: fixed runs, so they read as drawn rather than as noise —
  for (const s of sc.streaks) {
    // drifts through the day, then only sways, so the night loop stays seamless
    const shift = s.speed * 26 * S * t + Math.sin(phase + s.seed) * S;
    const c = s.light ? P.cloudLt : P.cloudDk;
    for (let i = 0; i < s.len; i++) {
      const x = ((Math.round(s.x + shift + i) % W) + W) % W;
      block(x, s.y, 1, s.thick, c);
    }
    for (let i = 0; i < s.tail; i++) {
      const x = ((Math.round(s.x + shift + 2 * S + i) % W) + W) % W;
      block(x, s.y + s.thick, 1, s.thick, c);
    }
  }

  // — stars: single pixels punched through the sky —
  if (night > 0.05) {
    for (const s of sc.stars) {
      const a = night * s.mag * (0.5 + 0.5 * Math.sin(phase + s.seed));
      if (a < 0.4) continue; // flat art: a star is on, or it is not
      const top = sc.skylineTop(s.x);
      if (top !== null && s.y >= top) continue;
      px(s.x, s.y, P.orb);
    }
  }

  // — sun and moon, flat discs —
  if (sunY < farBank + 4 * S && night < 0.9) disc(sunX, sunY, orbR, P.orb);
  if (moonUp && moonY < farBank) {
    disc(moonX, moonY, orbR, P.orb);
    for (const [cx, cy, cr] of [[-2, -1, 1.4], [2, 1.4, 1.0], [1.4, -2.6, 0.8]]) {
      disc(moonX + cx * S, moonY + cy * S, cr * S, P.orbDim);
    }
  }

  // — mountain —
  for (let x = 0; x < W; x++) {
    const top = sc.mountainTop(x);
    if (top === null) continue;
    const lit = x >= 13 * S ? P.far : P.near; // the sun is to its right
    for (let y = top; y < farBank; y++) px(x, y, lit);
  }

  // — city —
  for (const b of sc.city) block(b.x, b.roof, b.w, farBank - b.roof, P.far);
  for (const w of sc.windows) {
    if (t < w.at) continue;
    let on = smoothstep(w.at, w.at + 0.03, t);
    if (w.flicker) on *= 0.6 + 0.4 * Math.sin(phase * 2 + w.seed);
    if (on > 0.5) block(w.x, w.y, S, S, P.light);
  }
  for (const b of sc.cityNear) block(b.x, b.roof, b.w, farBank - b.roof, P.near);

  // — the far bank —
  block(0, farBank, W, S, P.near);

  // — river: flat, with a few drifting highlight runs —
  for (let y = riverTop; y < lip; y++) for (let x = 0; x < W; x++) px(x, y, P.water);
  for (let i = 0; i < 7 * S; i++) {
    const y = riverTop + S + ((i * 3 * S) % (lip - riverTop - S));
    const len = (9 + ((i * 7) % 13)) * S;
    const start = (i * 17 * S + Math.sin(phase + i) * 3 * S + 40 * S) % W;
    for (let k = 0; k < len; k++) {
      const x = ((Math.round(start + k) % W) + W) % W;
      block(x, y, 1, S, P.waterLt);
    }
  }

  // — the skyline's reflection: an irregular fringe under the bank —
  for (let x = 0; x < W; x++) {
    const top = sc.skylineTop(x);
    if (top === null) continue;
    const reach = Math.min(sc.reflectDepth[x], farBank - top);
    for (let k = 1; k <= reach; k++) {
      const y = farBank + k;
      if (y >= lip) break;
      px(x + Math.sin(y * 0.8 / S + phase) * 0.6 * S, y, P.near);
    }
  }

  // — the moon's glitter: pale dashes whose width pulses down the river —
  if (moonUp && moonY < farBank - 6 * S) {
    for (let y = riverTop; y < lip; y++) {
      const depth = (y - riverTop) / (lip - 1 - riverTop);
      // the width going negative is what breaks the column into glints, so the
      // gaps travel smoothly instead of flickering on a threshold
      const pulse =
        0.45 * Math.sin((y / S) * 1.3 + phase) + 0.3 * Math.sin((y / S) * 2.9 - phase * 1.5);
      const half = Math.round((0.25 + depth * 0.9 + pulse) * S);
      if (half < 0) continue;
      const centre = moonX + Math.sin((y / S) * 0.9 + phase) * (0.2 + depth * 0.4) * S;
      for (let dx = -half; dx <= half; dx++) px(centre + dx, y, P.orb);
    }
  }

  // — the city's lights on the water: a trail under each building —
  for (const w of sc.windows) {
    if (w.x % (3 * S) !== 0) continue;
    if (t < w.at) continue;
    if (smoothstep(w.at, w.at + 0.03, t) < 0.6) continue;
    const y = riverTop + S + Math.min(lip - riverTop - 2 * S, farBank - w.y);
    block(w.x + Math.sin((y / S) * 0.9 + phase) * 1.1 * S, y, S, S, P.light);
  }

  // — the near bank lip, then the big empty ledge —
  block(0, lip, W, S, P.foliage);
  for (let y = ledgeTop; y < PH; y++) {
    for (let x = 0; x < W; x++) px(x, y, y > ledgeTop + 7 * S ? P.groundDk : P.ground);
  }
  // one shadowed shelf on the right, the way the reference drops to a lower ledge
  for (let y = ledgeTop + sc.shelfDrop; y < PH; y++) {
    for (let x = sc.shelfX; x < W; x++) px(x, y, P.groundDk);
  }
  block(sc.shelfX, ledgeTop + sc.shelfDrop, W - sc.shelfX, S, P.foliage);

  // — grass tufts, swaying —
  for (const g of sc.tufts) {
    const lean = Math.sin(phase + g.seed) > 0 ? S : 0;
    const base = g.x < sc.shelfX ? lip : ledgeTop + sc.shelfDrop;
    block(g.x, base - S, S, S, P.foliage);
    block(g.x + lean, base - 2 * S, S, S, P.foliage);
  }

  // — the figures, last, so nothing draws over them —
  for (const fig of sc.figures) {
    fig.rows.forEach((row, ry) => {
      [...row].forEach((ch, rx) => {
        if (ch !== '#') return;
        const isAccent = fig.accent && fig.accent[0] === rx && fig.accent[1] === ry;
        px(fig.x + rx, lip - fig.rows.length + ry, isAccent ? P.accent : P.figure);
      });
    });
  }

  // — wordmark, low on the empty ledge. Flat art does not cross-fade text: it
  //   is off, then on. A fade would also mix tones that are not palette slots.
  if (t > 0.9) {
    let wx = 6 * S;
    const wy = ledgeTop + 3 * S;
    for (const ch of 'LUNOS') {
      const rows = FONT[ch];
      rows.forEach((row, ry) => {
        [...row].forEach((c, rx) => {
          if (c === '#') block(wx + rx * S, wy + ry * S, S, S, P.light);
        });
      });
      wx += 7 * S; // five wide plus two of spacing
    }
  }

  // ── pixels -> cells ──────────────────────────────────────────────────────
  const g = new Map();
  for (let row = 0; row < H; row++) {
    for (let x = 0; x < W; x++) {
      g.set(`${x},${row}`, {
        x, y: row, char: '▀',
        color: buf[row * 2][x],
        bgColor: buf[row * 2 + 1][x],
      });
    }
  }

  return g;
}

// ── push to ASCII Motion ───────────────────────────────────────────────────
async function main() {
  const S = Number(process.argv[2]) || 1;
  const { W, H } = dims(S);
  const { call } = await import('./am.mjs');
  await call('new_project', { name: `LUNOS — the ledge (${W}x${H})`, width: W, height: H });
  await call('set_frame_rate', { fps: 12 });

  for (let f = 0; f < FRAMES; f++) {
    if (f > 0) await call('add_frame', { duration: 83 });
    await call('go_to_frame', { index: f });
    const cells = [...renderFrame(f, S).values()];
    // set_cells_batch's declared cap is 10,000 cells, but the limit that bites
    // first is payload size. Somewhere above ~130 KB per request the MCP
    // server's stdin reader corrupts a message, which surfaces as one cell in
    // the batch failing character validation at an index that moves with the
    // chunk size. Escaping the wire to pure ASCII in bridge.mjs raised the
    // ceiling a long way but did not remove it, and the threshold also depends
    // on how much has already been sent, so this stays well under it rather
    // than sitting near the edge. Only affects pushing to the editor; the
    // shipped .ans.gz streams are written straight to disk.
    for (let i = 0; i < cells.length; i += 1000) {
      await call('set_cells_batch', { cells: cells.slice(i, i + 1000) });
    }
    if (f % 20 === 0) process.stdout.write(`${f} `);
  }
  await call('go_to_frame', { index: 0 });
  console.log(`\n${FRAMES} frames pushed at ${W}x${H}.`);
}

if (process.argv[1] && process.argv[1].replace(/\\/g, '/').endsWith('lunos-city.mjs')) {
  await main();
}
