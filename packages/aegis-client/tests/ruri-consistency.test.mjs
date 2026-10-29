import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { buildWave, armSourcePoint, pointInPolygon } from '../../../scripts/ruri/derive-wave.mjs';
import { compareWave } from '../../../scripts/ruri/quality.mjs';
import { cropPng, decodePng, encodePng } from '../../../scripts/ruri/png.mjs';

const root = new URL('../../../', import.meta.url);
const spec = JSON.parse(readFileSync(new URL('scripts/ruri/wave-spec.json', root), 'utf8'));
const original = decodePng(readFileSync(new URL(spec.canonicalAtlas, root)));
const result = buildWave(original, spec);
const derivedBytes = readFileSync(new URL('../src/assets/ruri-waving-consistent.png', import.meta.url));
const derived = decodePng(derivedBytes);
const report = compareWave(result.canonical, derived, result.allowedMasks, original);

test('the published arm variant reproduces exactly from the immutable reference and reviewed recipe', () => {
  assert.equal(derived.width, 1536); assert.equal(derived.height, 208);
  assert.equal(createHash('sha256').update(derivedBytes).digest('hex'), 'fa110a7a9a7cc624417c24255640419f2d7b5431750e24ed95c6b3f6ad815f0a');
  assert.deepEqual(derivedBytes, encodePng(result.image), 'recipe must reproduce the exact published PNG bytes');
  assert.deepEqual(cropPng(derived, 0, 0, 192, 208).data, result.canonical.data, 'neutral wave pose is byte-identical to idle0');
});

test('eyes, face, hood, clasp, static cape and feet preserve exact RGBA bytes in every arm pose', t => {
  assert.equal(report.changedStaticPixels, 0);
  assert.ok(report.unionMaskPixels <= 1600, 'the editable area stays local to the arm and its occlusions');
  for (const frame of report.frames) {
    assert.equal(frame.outsideMask, 0, `pose ${frame.index} changed static pixels`);
    assert.ok(frame.maskPixels <= 500, `pose ${frame.index} expanded the reviewed local arm mask`);
    assert.equal(frame.maskOutsideReviewedBounds, 0, `pose ${frame.index} moved editing outside the reviewed arm bounds`);
    for (const [name, region] of Object.entries(frame.namedRegions)) {
      assert.equal(region.changedPixels, 0, `pose ${frame.index} changed protected ${name}`);
    }
  }
  t.diagnostic(`${report.checkedStaticPixels} static pixel comparisons; 0 differences; protected semantic regions also exact.`);
});

test('body alignment, foot baseline, transparency and opaque coverage remain stable', () => {
  assert.deepEqual(report.referenceBbox, [51, 52, 140, 202]);
  for (const frame of report.frames) {
    assert.deepEqual([frame.bbox[0], frame.bbox[1], frame.bbox[3]], [51, 52, 202], `pose ${frame.index} changed head/foot alignment`);
    assert.deepEqual(frame.feetBbox, report.referenceFeetBbox, `pose ${frame.index} moved a foot`);
    assert.ok(frame.bbox[2] <= 147, 'only the arm can extend the right silhouette');
    assert.equal(frame.alphaDecreases, 0, `pose ${frame.index} erased or thinned original coverage`);
    assert.equal(frame.borderAlpha, 0, `pose ${frame.index} touches the transparent cell border`);
  }
});

test('new palette entries arise only from the donor arm translucent edge over canonical pixels', t => {
  const palette = new Set();
  for (let i = 0; i < original.data.length; i += 4) palette.add(original.data.readUInt32BE(i));
  const donor = cropPng(original, 192, 624, 192, 208);
  let blendedPixels = 0;
  for (let index = 0; index < 8; index++) {
    const frame = cropPng(derived, index * 192, 0, 192, 208);
    assert.ok(report.frames[index].newColorPixels <= 14, 'unexpected palette expansion');
    for (let y = 0; y < 208; y++) for (let x = 0; x < 192; x++) {
      const i = (y * 192 + x) * 4;
      if (palette.has(frame.data.readUInt32BE(i))) continue;
      assert.notEqual(spec.angles[index], null);
      const [sx, sy] = armSourcePoint(x, y, spec.angles[index], spec);
      const sourceIndex = (Math.floor(sy) * 192 + Math.floor(sx)) * 4;
      const alpha = donor.data[sourceIndex + 3] / 255;
      assert.ok(alpha > 0 && alpha < 1, 'a new color needs a translucent donor edge');
      let baseIndex = i;
      if (pointInPolygon(x + .5, y + .5, spec.gloveFill.polygon)) {
        baseIndex = ((y + 13) * 192 + x) * 4;
      }
      const baseAlpha = result.canonical.data[baseIndex + 3] / 255;
      assert.ok(baseAlpha > 0, 'transparent destinations must retain the donor bytes');
      const outputAlpha = alpha + baseAlpha * (1 - alpha);
      for (let channel = 0; channel < 3; channel++) {
        const expected = Math.round((donor.data[sourceIndex + channel] * alpha
          + result.canonical.data[baseIndex + channel] * baseAlpha * (1 - alpha)) / outputAlpha);
        assert.equal(frame.data[i + channel], expected, 'new colors must be explained by local alpha composition');
      }
      assert.equal(frame.data[i + 3], Math.round(outputAlpha * 255));
      blendedPixels++;
    }
  }
  t.diagnostic(`${blendedPixels} locally blended pixels across all poses; no other new palette entries.`);
});
