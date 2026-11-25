import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const desktop = readFileSync(new URL('../src/components/thinking-motion.css', import.meta.url), 'utf8');
const component = readFileSync(new URL('../src/components/DesktopVoiceStatus.vue', import.meta.url), 'utf8');
const android = readFileSync(new URL('../src-tauri/gen/android/app/src/main/java/com/aegis/node/HandsFreeThinkingMotion.kt', import.meta.url), 'utf8');

// Both native renderers interpolate the same normalized coordinates and times.
// A mismatch here is visible as a different rhythm or figure between devices.
test('desktop and Android share the same shortened thinking motion', () => {
  const duration = Number(android.match(/DURATION_MS\s*=\s*(\d+)L/)[1]);
  assert.equal(duration, 10334);
  assert.match(component, new RegExp(`animation:\\s*${duration}ms linear infinite`));
  const entries = android.match(/private val knots = intArrayOf\(\s*([\s\S]*?)\s*\)/)[1]
    .match(/-?\d+/g).map(Number);
  assert.equal(entries.length % 7, 0);
  const rows = Array.from({ length: entries.length / 7 }, (_, i) => entries.slice(i * 7, (i + 1) * 7));
  assert.equal(rows[0][0], 0);
  assert.equal(rows.at(-1)[0], duration);
  assert.deepEqual(rows[0].slice(1), rows.at(-1).slice(1), 'closed loop');
  assert.deepEqual(rows[0].slice(1), [-6600,3744,0,-2963,6600,-5000]);
  for (let i = 1; i < rows.length; i++) assert.ok(rows[i][0] > rows[i - 1][0], 'strictly increasing sample times');
  for (const [, ...coordinates] of rows) assert.ok(coordinates.every(v => Math.abs(v) <= 12000), 'dots fit inside 28-unit viewBox');
  for (let dot = 0; dot < 3; dot++) {
    const track = desktop.split(`@keyframes aegis-thought-dot-${dot+1} {`)[1].split('@keyframes')[0];
    const samples = [...track.matchAll(/([\d.]+)%\s*\{\s*transform:\s*translate\((-?[\d.]+)px,\s*(-?[\d.]+)px\);\s*\}/g)];
    assert.equal(samples.length, rows.length);
    for (let i = 0; i < rows.length; i++) {
      const [time, ...coordinates] = rows[i];
      assert.ok(Math.abs(Number(samples[i][1]) * duration / 100 - time) < .001, `dot ${dot}, knot ${i} time`);
      assert.equal(Math.round(Number(samples[i][2]) * 1000), coordinates[dot * 2], `dot ${dot}, knot ${i} x`);
      assert.equal(Math.round(Number(samples[i][3]) * 1000), coordinates[dot * 2 + 1], `dot ${dot}, knot ${i} y`);
    }
  }
});
