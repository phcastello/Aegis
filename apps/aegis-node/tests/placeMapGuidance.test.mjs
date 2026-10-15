import assert from 'node:assert/strict';
import { test } from 'node:test';
import { circleConfirmationRadius, placeAccuracyGuidance } from '../.test-build/services/placeMapGuidance.js';

test('place guidance retains the native thirty metre margin and capped uncertainty contribution', () => {
  assert.deepEqual(placeAccuracyGuidance(20), { accuracyMeters: 20, marginMeters: 50, exceedsNativeAccuracyLimit: false });
  assert.equal(placeAccuracyGuidance(100).marginMeters, 130);
  assert.equal(placeAccuracyGuidance(200).marginMeters, 130);
  assert.equal(placeAccuracyGuidance(201).exceedsNativeAccuracyLimit, true);
  assert.equal(circleConfirmationRadius(150, 50), 100);
  assert.equal(circleConfirmationRadius(50, 130), 0);
});

test('missing or invalid accuracy stays unknown and is never represented as zero uncertainty', () => {
  for (const value of [undefined, null, NaN, Infinity, 0, -1]) assert.equal(placeAccuracyGuidance(value), null);
});
