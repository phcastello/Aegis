# Ruri

## Immutable canonical reference

`ruri-spritesheet.png` is the owner's original
`ruri/final/spritesheet-extended.png` from `ruri-projeto.zip`, without modification.

- SHA-256: `05c42c26f59ea4a7fae2f8fcdb42d7a3a99087bb052de0407c38f944d6bc2da1`.
- Transparent RGBA PNG, 1536 × 2288, 8 columns, 11 rows, 192 × 208 cells.
- Appearance reference: row 0, column 0 (`idle0`). Approved idle playback remains
  row 0, columns `0,1,3,4,5`; closed-eye column 2 remains omitted.

The canonical wave row changes the face, eyes and body between poses, so the app
uses the original atlas only for idle. The original remains an immutable reference.

## Individually drawn waving variant

`ruri-waving-consistent.png` replaces the previous rotated-arm derivative.

- SHA-256: `e87ac0d9e5cb2eafa6af6c39a52b2476bff04e18991c83115b5cba517a16017e`.
- Transparent RGBA PNG, 1536 × 208, 8 columns, 40,199 bytes.
- Cell 0 is canonical idle0. Cells 1–7 are seven new local arm drawings, replacing
  the seven angle-generated poses; these are eight unique poses, not seventeen
  distinct drawings.
- Pose order: rest, preparation, bent-elbow lift, opening palm approaching the
  greeting, upright palm, outward wrist/open palm, inward wrist/cupped palm,
  relaxed descent.

Each sleeve contour, fabric facet, elbow crease, cuff and mitten contour is
individually authored at the native pixel resolution in
[`wave-spec.json`](../../../../scripts/ruri/wave-spec.json). The development-only
rasterizer fills these contours; it does not rotate, scale or resample a donor
arm. The raised wrist gestures have different palm/thumb silhouettes and sleeve
folds. The elbow remains bent during the small wrist gestures. The return uses
its own relaxed pose rather than reversing the entire ascent.

The same canonical idle body underlies each drawing. Eyes, face, hood, torso,
clasp, lower cloak, legs and boots retain their appearance and proportions.
Local shoulder/arm contours may cover the cape edge or outer right hood edge as
appropriate. The resting right mitten is covered with nearby canonical cloak
fabric before the moving arm is drawn, avoiding a duplicate hand. Palette colors
are eyedropped from the canonical sleeve and mitten; the exact coordinates and
RGBA values are recorded in the recipe. There is no model/API call at runtime.

### Drafts and visual corrections

A built-in `image_gen` storyboard was requested as an anatomy draft, using
canonical idle0 as its reference: eight poses from preparation through elbow
bend, small wrist gestures and relaxed descent; only the viewer-right sleeve
and dark mitten were to change, preserving Ruri's face, hood, costume and scale.
It was rejected because it raised the wrong arm and duplicated the resting hand.
No generated pixels are present in the shipped PNG. An earlier generation
experiment and the previous rigid, angle-driven recipe were also discarded.

The first hand-drawn draft was reviewed and rejected for an overlong arm, tubular
sleeve highlights, an excessive palm arc and a fist-like approach pose. The final
revision shortens the upper-arm reach, widens and facets the elbow fold, breaks
the continuous highlight, opens the approach palm and reduces the wrist arc to
roughly eight source pixels. Temporary drafts, contact sheets and recordings
stay outside the repository; only the runtime PNG and its provenance/tooling
are versioned.

## Playback

The approved repeating behavior remains `waving → idle → idle`, independent of
typing. Idle timing and visibility/reduced-motion behavior are unchanged.

- Wave cells: `0,1,2,3,4,5,4,6,4,5,4,6,4,3,7,1,0`.
- Holds in ms: `350,140,160,160,180,200,120,200,120,200,120,200,160,160,200,140,500`.
- Wave: 3,310 ms. Two small outward/inward wrist gestures follow the lift.
- Each idle: `0,1,3,4,5`, holds `1800,500,500,500,1700` ms, totaling 5,000 ms.
- Full cycle: 13,310 ms. The final neutral hold meets canonical idle0 without an
  atlas jump.

Both atlases decode before greeting. Failed derivative loads leave static idle0.
Offscreen or hidden documents pause work; reduced motion selects static idle0.

## Reproduction and review

```bash
node scripts/ruri/derive-wave.mjs
node scripts/ruri/derive-wave.mjs --check
node --test packages/aegis-client/tests/ruri-*.test.mjs
node packages/aegis-client/tests/visual-refinements-browser.mjs
node scripts/ruri/review.mjs --record --output /tmp/aegis-ruri-wave-review
```

The review command displays the shipped PNG, generates contact and chronological
sheets plus individual pose screenshots, and creates standalone `animation.html`
with synchronized app-size (144 × 156) and enlarged playback, step/pose inspection
and a completed-cycle counter. `--record` executes at least two full real-time
cycles and writes `animation-two-cycles.webm`. Artifacts are not packaged.

Before committing this revision, the eight final poses were visually inspected
against canonical idle at app size and enlarged size. Timed browser screenshots
of two full cycles were inspected for preparation, elbow bend, palm changes,
return, silhouette continuity and duplicate/detached hands. No concrete visual
blocker remained after the corrections above. The tooling records real-time
playback; screenshot inspection does not claim continuous human video viewing
or physical-device acceptance. The canonical dark glove has limited contrast
at app size.

Automated tests are secondary integrity checks: reference hash, reproducibility,
facial/structural regions, alignment, transparency, palette and cell boundaries.
There is no zero-difference-outside-a-mask approval gate or tiny editable-area
budget. Diagnostics cannot approve animation quality. Browser checks cover two
complete cycles, typing/focus, offscreen/document pause, reduced motion, and
cold/failing asset loads at mobile and desktop widths.
