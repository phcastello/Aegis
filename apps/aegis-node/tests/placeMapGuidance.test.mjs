import assert from 'node:assert/strict';
import { test } from 'node:test';
import { circleConfirmationRadius, placeAccuracyGuidance } from '../.test-build/services/placeMapGuidance.js';

test('place guidance uses reported uncertainty without an extra thirty metre penalty', () => {
  assert.deepEqual(placeAccuracyGuidance(20), { accuracyMeters: 20, marginMeters: 20, exceedsNativeAccuracyLimit: false });
  assert.equal(placeAccuracyGuidance(1).marginMeters, 3);
  assert.equal(placeAccuracyGuidance(13).marginMeters, 13);
  assert.equal(placeAccuracyGuidance(100).marginMeters, 100);
  assert.equal(placeAccuracyGuidance(200).marginMeters, 200);
  assert.equal(placeAccuracyGuidance(201).exceedsNativeAccuracyLimit, true);
  assert.equal(circleConfirmationRadius(150, 50), 100);
  assert.equal(circleConfirmationRadius(50, 130), 0);
});

test('missing or invalid accuracy stays unknown and is never represented as zero uncertainty', () => {
  for (const value of [undefined, null, NaN, Infinity, 0, -1]) assert.equal(placeAccuracyGuidance(value), null);
});


test('65 metre residence keeps uncertainty with good, intermediate, and battery-saving readings', () => {
  for (const radius of [65, 150, 300]) for (const accuracy of [13, 30, 65, 100, 200]) {
    assert.equal(circleConfirmationRadius(radius, placeAccuracyGuidance(accuracy).marginMeters), Math.max(0, radius - accuracy));
  }
  assert.equal(circleConfirmationRadius(65, placeAccuracyGuidance(13).marginMeters), 52);
  assert.equal(circleConfirmationRadius(65, placeAccuracyGuidance(100).marginMeters), 0);
});
