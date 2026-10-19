import assert from 'node:assert/strict';
import { test } from 'node:test';
import { suggestedMonitoringPadding, previewMonitoringEnvelope, proximityLabel, inheritedPlaceCircles } from '../.test-build/services/monitoringEnvelope.js';
const circle = { id: 'real-id', latitude: 0, longitude: 0, radiusMeters: 50 };

test('small-home suggestion reviews 200 m without changing the real house', () => {
  assert.equal(suggestedMonitoringPadding([circle]), 150);
  const operational = previewMonitoringEnvelope([circle], true, 150);
  assert.equal(operational[0].radiusMeters, 200);
  assert.notEqual(operational[0].id, circle.id);
  assert.equal(circle.radiusMeters, 50);
  assert.equal(suggestedMonitoringPadding([{ ...circle, radiusMeters: 65 }]), 135);
  assert.equal(suggestedMonitoringPadding([{ ...circle, radiusMeters: 500 }]), 75);
  assert.deepEqual(previewMonitoringEnvelope([circle], false, 150), []);
  assert.equal(proximityLabel({ circles: [circle] }), 'Aguardando sua aprovação');
});

test('the whole real union gets independent preview regions and explicit bounded padding', () => {
  const second = { ...circle, id: 'second-real-id', longitude: .01, radiusMeters: 65 };
  const circles = [circle, second]; const preserved = structuredClone(circles);
  const operational = previewMonitoringEnvelope(circles, true, 135);
  assert.deepEqual(operational.map(item => item.radiusMeters), [185, 200]);
  assert.deepEqual(circles, preserved);
  assert.deepEqual(operational.map(item => item.longitude), [0, .01]);
  for (const invalid of [NaN, Infinity, 0, 49, 1001]) assert.deepEqual(previewMonitoringEnvelope(circles, true, invalid), []);
  assert.equal(proximityLabel({ monitoringEnvelope: { enabled: true, approvalRequired: true, circles: [] } }), 'Confirme a área atual');
});

test('inherited geometry remains on its original parent and cycles stop safely', () => {
  const parent = { id: 'parent', circles: [circle], parentId: null };
  const child = { id: 'child', circles: [], parentId: 'parent' };
  assert.deepEqual(inheritedPlaceCircles([parent, child], 'child'), [circle]);
  assert.deepEqual(inheritedPlaceCircles([{ ...child, parentId: 'child' }], 'child'), []);
  assert.equal(parent.circles[0].id, circle.id);
});
