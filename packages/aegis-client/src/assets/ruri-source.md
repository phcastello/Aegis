# Ruri

## Immutable canonical reference

`ruri-spritesheet.png` is the canonical `ruri/final/spritesheet-extended.png`
provided by the owner in `ruri-projeto.zip`, copied without modification.

- SHA-256: `05c42c26f59ea4a7fae2f8fcdb42d7a3a99087bb052de0407c38f944d6bc2da1`
- Transparent RGBA PNG, 1536 × 2288, 8 columns, 11 rows, 192 × 208 cells.
- Neutral reference: row 0, column 0 (`idle0`). The approved idle sequence
  remains row 0, columns `0,1,3,4,5`; the closed-eye column 2 remains omitted.

The original waving bodies differ from idle0 throughout the eyes, face, hood,
cloak and feet. They are no longer displayed. The original file remains both
the idle asset and the immutable reference for the derivative below.

## Derived waving variant

`ruri-waving-consistent.png` is a separately named, reproducible derivative.

- SHA-256: `fa110a7a9a7cc624417c24255640419f2d7b5431750e24ed95c6b3f6ad815f0a`
- Transparent RGBA PNG, 1536 × 208, 8 columns, one row, 192 × 208 cells.
- 44,324 bytes. Cell 0 is exactly idle0, including translucent edge pixels.
- Cells 1–7 keep idle0's body and move only a locally isolated arm/sleeve/glove
  from canonical row 3, column 1. The arm uses nearest-neighbor sampling,
  a local scale of 0.86 to match the neutral glove, and angles
  `160°,120°,80°,40°,0°,-12°,8°` around `(117,133)`. The body is never transformed.
- The resting right glove is uncovered only within its explicit local polygon,
  using fabric pixels from idle0 at `(x,y+13)`. This prevents a duplicate hand.
  The raised glove can physically occlude the outer right hood edge; eyes,
  face and the rest of the hood remain unchanged.
- Compositing preserves raw RGBA outside the arm/occlusion mask. Source-over
  blending at local overlaps avoids alpha holes. Its few intermediate edge
  colors are blends of the original pixels, not a new character palette.
- A built-in image-generation experiment was inspected and rejected. No pixels
  from that experiment are present in the published variant.

Exact donor coordinates, polygons and transforms are versioned in
[`wave-spec.json`](../../../../scripts/ruri/wave-spec.json). Rebuild or verify:

```bash
node scripts/ruri/derive-wave.mjs
node scripts/ruri/derive-wave.mjs --check
node --test packages/aegis-client/tests/ruri-*.test.mjs
node scripts/ruri/review.mjs --output /tmp/aegis-ruri-identity-final
```

The PNG tooling is development-only. It reads raw RGBA to avoid changing
semitransparent outlines through a canvas color-conversion roundtrip. The
original ZIP and tooling are not needed by Android, Windows or the PWA.
The review command creates a chronological contact sheet, protected-region
overlay, JSON comparison report and standalone `idle → waving → idle` HTML
playback outside the repository. These previews are not packaged with the app.

## Playback and validation

The runtime uses `waving → idle → idle`, repeating independently of typing.

- Wave columns: `0,1,2,3,4,5,6,7,6,5,4,3,2,1,0`.
- Wave holds in milliseconds:
  `350,120,120,120,120,180,220,220,220,180,120,120,120,120,670` (3 seconds).
- Each approved idle cycle: `0,1,3,4,5`, holds
  `1800,500,500,500,1700` (5 seconds).
- Full visible cycle: 13 seconds. The arm returns through the intermediate
  poses and reaches idle0 before switching atlases. Existing idle frames retain
  their approved breathing differences; exact protected-pixel comparisons apply
  to every derived wave cell against idle0.

Both atlases must decode before starting. If the derivative fails to load, the
canonical neutral pose stays static. Offscreen/hidden pauses timers; reduced
motion selects static idle0. There are no backend or model calls at runtime.

Image tests compare every protected RGBA pixel, with independent regions for
eyes, face, hood, brooch, static cloak, legs and boots. They also check alignment,
alpha, palette provenance and the feet baseline. Browser tests verify actual
atlas selection, transitions, the complete repeating cycle, typing, pausing,
reduced motion and cold/failing image loads. Pixel checks complement visual
inspection of the chronological contact sheet and complete playback; they do
not establish aesthetic perfection or physical-device acceptance.
