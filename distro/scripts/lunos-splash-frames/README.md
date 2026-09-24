# LUNOS splash frames

Pre-rendered frames for `lunos-splash.sh`: the city, the river, and a full moon,
watched from a ledge. 120 frames at 12fps — **0..95** is the transition from day
to night, **96..119** is a seamless night-idle loop the player falls back into,
so it can hold on night forever without ever cutting back to daylight.

Composed after the Undertale surface scene: a mountain and a skyline flat in
silhouette, a sky of streaked cloud bands, a large and nearly empty foreground
ledge, and three figures with their backs to us looking at it. The emotional
content is the watching, not the view — which is why the figures are the only
thing in frame that never moves.

## The palette is locked

Four keyed palettes — day, golden, dusk, night — of twenty slots each,
interpolated slot by slot as the day turns. **Nothing is drawn in a colour that
is not a slot.** Any single frame contains at most twenty colours.

That constraint is the whole look. An earlier version rendered free-form colour
with smooth gradients everywhere and came out reading as a photograph with the
contrast turned down, which is the opposite of this style. If you need a new
tone, add a slot; do not mix one inline.

It pays for itself twice: flat colour fields compress into long runs, so all four
streams here total **495 KB** where a single gradient version needed 616 KB.

## Two sizes

```
lunos-160x48-24bit.ans.gz   160x96 px, truecolor                   212 KB
lunos-160x48-256.ans.gz     160x96 px, 256-colour fallback         188 KB
lunos-80x24-24bit.ans.gz     80x48 px, truecolor                    62 KB
lunos-80x24-256.ans.gz       80x48 px, 256-colour fallback          33 KB
```

`lunos-splash.sh` uses the largest set that fits the terminal and falls back to
the small one; `LUNOS_SIZE=80x24` forces it. Each holds all 120 frames separated
by a form feed, so the player loads one with a single `mapfile -d $'\f'`.

The Linux virtual console can do neither per-cell background colour nor these
palettes, so the splash is for a terminal emulator or a kiosk session, not tty1.

## How N character rows hold 2N pixel rows

Every cell is `▀` with a foreground colour (the upper pixel) and a background
colour (the lower one), which doubles the vertical resolution a terminal usually
gives you and keeps pixels **square**, because a cell is about twice as tall as
it is wide.

Quadrant blocks (`▘▝▖▗▚▞`) were the other way to add resolution and are not used:
they subdivide horizontally too, making subpixels 1:2 rather than square, so they
buy horizontal detail only. Doubling the character canvas keeps them square,
which is why the large set is 160x48 characters rather than a cleverer 80x24.

The scene is authored once in scale-1 units and drawn at any integer scale, so
adding a size is one number. Two things are hand-made per scale rather than
scaled up, because doubling them would only make them blocky: the **figure
sprites** (`SPRITES`), and the **window blocks**, which stay the same apparent
size by growing with the scale instead of multiplying in number.

## Motion

Everything that moves is a sine of one `phase` value, periodic over the 24-frame
loop. Nothing quantises that clock: an earlier version used
`Math.floor(beat * k)` for the water glints and the reflection's dash pattern and
both teleported every few frames, reading as a fault rather than as water.

Note that this version is flat pixel art, so a star, a lit window or a grass
tuft's lean is genuinely on or off — there are no half-opacity pixels to fade
through. Frame-to-frame pixel-change counts are therefore higher than they were
for the gradient version and that is correct, not a regression. The loop seam
(frame 119 back to 96) changes no more than any other frame pair.

## Regenerating

Generated with [ASCII Motion](https://github.com/CameronFoxly/Ascii-Motion).
`src/lunos-city.mjs` holds the whole scene — palettes, skyline, figures, the sun
and moon paths — and renders every frame; `src/export-lunos.mjs` writes the two
streams here.

```bash
cd src
node export-lunos.mjs ..                      # rewrite all four .ans.gz
node export-lunos.mjs .. 1                    # just the small set
node png-preview.mjs /tmp -s 2 0 38 62 110    # PNG stills to look at
node lunos-city.mjs 2                         # push 160x48 into the editor
```

Neither needs anything installed. To edit in the ASCII Motion editor instead, run
`node lunos-city.mjs` with an `ascii-motion-mcp --live` bridge up — it pushes all
120 frames in as one timeline.

Things worth knowing if you change it:

- **`PALETTES` is the first place to look.** Twenty entries, same order, four
  times. Recolouring the whole piece means editing eighty hex values and nothing
  else.
- **Regular patterns read as architecture.** The skyline's reflection was first a
  dashed line, which looked like railings laid across the river, then a stipple,
  which looked like a white picket fence. It is now an irregular per-column depth
  from a fixed seed (`REFLECT_DEPTH`). Do not replace that with a modulo.
- **The water must stay brighter than the ground.** A river at sunset is a sheet
  of reflected sky. When it was darker than the bank it read as a dirt path, and
  the figures lost the bright field they are silhouetted against.
- **There is deliberately no bridge.** One was tried: at eighty pixels wide a
  full-width deck reads as a bar laid over the picture, and there is no room for
  the perspective that would sell it.
- **The wordmark** switches on at `t > 0.9` — no fade, because a fade mixes tones
  that are not palette slots. It is drawn with `FONT`, a five-letter pixel font,
  rather than set as terminal text: terminal text does not scale with the canvas,
  and a terminal glyph is a different typeface from the rest of the picture.
  Delete that block for a plain scene.
- The `.ans.gz` files are build output; edit `src/` and re-export instead.

## A limit worth knowing about

Pushing to the ASCII Motion editor goes through an MCP server over stdio, and
somewhere above roughly 130 KB per request its stdin reader corrupts a message.
It surfaces as a single cell in a large `set_cells_batch` failing character
validation, at an index that moves when the batch size changes — which looks like
a data bug and is not one. Escaping the wire to pure ASCII in the bridge raised
the ceiling a long way but did not remove it, and the threshold also depends on
how much has already been sent, so `lunos-city.mjs` sends 1,000 cells at a time.

This only affects the editor push. The `.ans.gz` streams are written straight to
disk and never touch it.
