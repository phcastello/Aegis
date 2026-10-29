import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { buildWave } from '../../../scripts/ruri/derive-wave.mjs';
import { compareWave } from '../../../scripts/ruri/quality.mjs';
import { cropPng, decodePng, encodePng } from '../../../scripts/ruri/png.mjs';

const root = new URL('../../../', import.meta.url);
const spec = JSON.parse(readFileSync(new URL('scripts/ruri/wave-spec.json', root), 'utf8'));
const original = decodePng(readFileSync(new URL(spec.canonicalAtlas, root)));
const result = buildWave(original, spec);
const derivedBytes = readFileSync(new URL('../src/assets/ruri-waving-consistent.png', import.meta.url));
const derived = decodePng(derivedBytes);
const report = compareWave(result.canonical, derived, result.allowedMasks, original);

// Integrity checks supplement contact-sheet and timed-playback review. They
// cannot judge whether a sleeve, palm gesture or transition looks natural.
test('the shipped eight drawn poses reproduce from the canonical idle and reviewed contours', () => {
  assert.equal(derived.width, 1536); assert.equal(derived.height, 208);
  assert.equal(createHash('sha256').update(derivedBytes).digest('hex'), 'e87ac0d9e5cb2eafa6af6c39a52b2476bff04e18991c83115b5cba517a16017e');
  assert.deepEqual(derivedBytes, encodePng(result.image), 'reviewed contours reproduce the exact shipped PNG');
  assert.deepEqual(cropPng(derived, 0, 0, 192, 208).data, result.canonical.data, 'rest meets idle0 without an atlas jump');
});

test('facial features and character proportions remain consistent with canonical idle', () => {
  for (const frame of report.frames) {
    for (const [name, region] of Object.entries(frame.namedRegions)) {
      assert.equal(region.changedPixels, 0, `pose ${frame.index} changed protected ${name}`);
    }
    assert.deepEqual([frame.bbox[0], frame.bbox[1], frame.bbox[3]], [51, 52, 202], `pose ${frame.index} changed head/foot alignment`);
    assert.deepEqual(frame.feetBbox, report.referenceFeetBbox, `pose ${frame.index} moved a foot`);
    assert.equal(frame.alphaDecreases, 0, `pose ${frame.index} thinned original body coverage`);
    assert.equal(frame.borderAlpha, 0, `pose ${frame.index} clips the cell boundary`);
  }
});

test('drawn sleeves and mittens use the canonical costume palette and fit the app cell', () => {
  const palette = new Set();
  for (let i = 0; i < original.data.length; i += 4) palette.add(original.data.readUInt32BE(i));
  for (let index = 0; index < 8; index++) {
    const frame = cropPng(derived, index * 192, 0, 192, 208);
    for (let i = 0; i < frame.data.length; i += 4) assert.ok(palette.has(frame.data.readUInt32BE(i)), `pose ${index} introduced an unrelated color`);
    assert.ok(report.frames[index].bbox[2] < 153, 'the small greeting fits within the transparent sprite cell');
  }
});
