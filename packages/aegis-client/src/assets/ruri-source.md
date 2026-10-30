# Ruri — current PixelLab wave

The shipped wave is the owner's current `ruri-waving.png.gif` +
`ruri-waving.png.pxo`, retained unchanged in
[`docs/ruri-pixellab/source`](../../../../docs/ruri-pixellab/source).
No earlier wave drawing, generated animation or reinterpretation supplies pixels.
The PXO contains the ten full RGBA frames at 192 × 208 and 6 fps. It avoids the
GIF's palette/transparency conversion in frames 0–1; GIF frames 2–9 match the PXO
exactly. The GIF stores 170 ms per frame; the project stores 1/6 second.

## Local corrections

`ruri-waving-pixellab.png` is the reviewed transparent 1920 × 208 wave atlas with those ten frames.
SHA-256: `9debf5085c2b7b994f1677725d847784f5a31c1f92907d2d4624a7bfef0860b6`.

- Frames 0, 1 and 2 retain every source RGBA byte.
- Eyes in frames 3–9 use the almond contours, blue irises and glints of source
  frame 0. Each eye is placed at the corresponding face position, with small
  nearest-neighbor size adjustments. The old eye area is restored from adjacent
  skin pixels, including removal of residual edges in frames 6–7. Mouth and head
  pose stay intact; frame 2 remains the unchanged transition reference.
- The waving gloves in frames 3–8 receive selective slate highlights and dark
  creases for the palm, thumb and finger tips. The existing sleeve/cuff and glove
  attachment stay intact. Frame 8 loses one outer column (10 pixels) to narrow
  the returning glove. The secondary hand is unchanged.
- Every difference is confined to the eyes and waving glove. Body, feet, cape,
  head movement and source poses are preserved. The new atlas does not reconstruct
  those regions from an old idle drawing.

The deterministic pixel recipe is
[`refine-pixellab.mjs`](../../../../scripts/ruri/refine-pixellab.mjs). It reads the
provided PXO with Node's built-in ZIP/deflate support; no image model, external
service or new runtime dependency is involved. Historical wave tooling/assets
remain unused by the component and are not inputs to this recipe.

## Playback and integration

Wave frames: **0,1,2,3,4,5,6,7,6,7,8,9,0**. Only 6–7 repeat: one ascent,
two small central movements, one descent, then rest. All holds are 167 ms,
rounding the project's 6 fps by +0.333 ms/frame. Wave duration is 2171 ms.
There is no extra easing, interpolation or change to the ascent/descent order.

The existing `wave → idle → idle` scheduler is unchanged. Each idle lasts
5000 ms (`0,1,3,4,5`, holds `1800,500,500,500,1700`); the full cycle is 12171 ms.
Typing continuity, offscreen/document pauses, reduced motion, image decoding and
failure fallback are preserved. The component renders at 144 × 156 as before.

The runtime texture is now `ruri-playback.png` (1920 × 416): the unchanged
canonical idle row is on row 0; the unchanged reviewed wave is on row 1.
[`pack-playback.mjs`](../../../../scripts/ruri/pack-playback.mjs) copies RGBA
bytes without resampling or artwork edits. The component keeps its image URL
and background dimensions constant across wave/idle/idle boundaries and changes
only the crop. This removes the texture swap associated with the reported
whole-character flash; no empty source frame or CSS fade was found locally.
The original idle0 remains a static fallback until decoding finishes or if it
fails. The initial switch is between the same neutral pose; subsequent loops
never switch image sources.

`ruri-spritesheet.png` remains the immutable canonical idle atlas (1536 × 2288),
SHA-256 `05c42c26f59ea4a7fae2f8fcdb42d7a3a99087bb052de0407c38f944d6bc2da1`.
The supplied wave frame 0 has identical alpha coverage to canonical idle0; only
61 RGB pixels differ. It is deliberately preserved rather than replaced.

## Review and checks

[Contact sheet](../../../../docs/ruri-pixellab/review/contact-sheet.png),
[source/final details](../../../../docs/ruri-pixellab/review/details.png),
[chronological sheet](../../../../docs/ruri-pixellab/review/chronological-sheet.png),
[interactive preview](../../../../docs/ruri-pixellab/review/animation.html),
[two real-time cycles](../../../../docs/ruri-pixellab/review/animation-two-cycles.webm)
and [timing/hash report](../../../../docs/ruri-pixellab/review/report.json)
were produced before client integration. The local visual review compared eyes,
glove readability, repeated middle and return at native and enlarged sizes;
residual old eye edges were corrected before producing the final artifacts.
The recording and frame inspection do not substitute for owner acceptance on
physical devices. At app scale, gloves remain small and deliberately dark.

```bash
node scripts/ruri/refine-pixellab.mjs /tmp/ruri-pixellab-final
node scripts/ruri/pack-playback.mjs --check
node scripts/ruri/review-pixellab.mjs /tmp/ruri-pixellab-review
node --test packages/aegis-client/tests/ruri-*.test.mjs
npm run test:visual-refinements --prefix packages/aegis-client
npm run build --prefix apps/aegis-node
npm run build --prefix frontend/aegis-pwa
```

Integrity tests verify immutable sources, exact reproduction, unchanged frames
0–2, local edit bounds, mouth/body/feet preservation, the idle transition and byte-exact playback packing.
Browser tests cover two complete cycles, typing/focus, visibility, reduced motion,
and cold/failing image loads at mobile and desktop widths. Compositor screenshots
before, after and 16 ms after each wave/idle/idle boundary verify foreground
coverage and stable rendering while the texture URL/dimensions remain fixed. These are regression
checks, not automatic approval of artistic quality.
