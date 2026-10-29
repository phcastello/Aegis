# Ruri

`ruri-spritesheet.png` is the canonical `ruri/final/spritesheet-extended.png`
provided by the owner in `ruri-projeto.zip`, copied without modification.

- SHA-256: `05c42c26f59ea4a7fae2f8fcdb42d7a3a99087bb052de0407c38f944d6bc2da1`
- Transparent PNG, 1536 × 2288 pixels, 8 columns, 11 rows, 192 × 208 cells.
- Integrated cycle: `waving → idle → idle`, repeating independently of typing.
  `waving` (row 3): columns `0,1,2,1,2,1,3`, holds
  `500,350,400,350,400,350,600 ms` (2.95 s). `idle` (row 0): columns
  `0,1,3,4,5`, holds `1800,500,500,500,1700 ms` (5 s each).
- The closed-eye idle frame (column 2) is omitted because its face changes
  abruptly. The raised-hand pose is revisited before lowering; the atlas has
  no finer arm transitions. No frames were generated or altered.
- A full cycle takes 12.95 s while visible. Offscreen/hidden pauses timers;
  reduced motion keeps the first idle pose static. No model or backend calls.
- The original archive and its scripts are not needed by the application.
