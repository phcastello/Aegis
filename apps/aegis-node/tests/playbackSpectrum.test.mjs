import assert from 'node:assert/strict';
import test from 'node:test';
import { PlaybackSpectrumDisplay } from '../.test-build/services/playbackSpectrum.js';

const frame = (...pairs) => Array.from({ length: 32 }, (_, i) => pairs.find(([index]) => i === index)?.[1] ?? 0);

test('speaking display pools adjacent measured bands without changing their frequency order', () => {
  const display = new PlaybackSpectrumDisplay();
  const levels = display.update(frame([6, .8], [7, .9], [23, .5]), 0, false);
  assert.equal(levels.length, 16);
  assert.equal(levels[3], .9);
  assert.equal(levels[11], .5);
  assert.equal(levels.filter(x => x > 0).length, 2);
});

test('asymmetric 75/150ms envelope reduces abrupt jumps and follows real peaks', () => {
  const display = new PlaybackSpectrumDisplay();
  const high = frame([6, 1], [22, .2]);
  const low = frame([6, .1], [22, .2]);
  const first = display.update(high, 1000, true);
  const second = display.update(high, 1040, true);
  const third = display.update(low, 1080, true);
  assert.ok(first[3] > .39 && first[3] < .43);
  assert.ok(second[3] > first[3] && second[3] < 1);
  assert.ok(third[3] < second[3] && third[3] > .1);
  assert.ok(third[3] > first[3], 'decay is gradual');
  assert.ok(third[11] > first[11], 'another measured band updates independently');
});

test('quiet, phase reset, and reduced-motion cannot leave a prior nonzero envelope', () => {
  const display = new PlaybackSpectrumDisplay();
  const high = frame([6, 1]);
  const other = frame([23, .5]);
  display.update(high, 0, true);
  const quiet = display.update(frame(), 40, true);
  assert.ok(quiet[3] > 0 && quiet[3] < .4, 'valid quiet frames have a short release');
  assert.ok(display.update(frame(), 80, true)[3] < quiet[3]);
  assert.deepEqual(display.update(frame(), 120, true), [], 'silence clears by 120ms');
  assert.equal(display.update(other, 200, true)[3], 0);
  display.clear();
  assert.equal(display.update(high, 240, true)[11], 0);
  assert.equal(display.update(other, 280, false)[3], 0);
});
