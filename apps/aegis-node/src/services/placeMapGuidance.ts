// UI guidance mirrors the native spatial margin; it does not classify presence.
export function placeAccuracyGuidance(accuracyMeters: number | null | undefined) {
  if (typeof accuracyMeters !== 'number' || !Number.isFinite(accuracyMeters) || accuracyMeters <= 0) return null;
  return { accuracyMeters, marginMeters: 30 + Math.min(accuracyMeters, 100), exceedsNativeAccuracyLimit: accuracyMeters > 200 };
}

export function circleConfirmationRadius(radiusMeters: number, marginMeters: number): number {
  return Math.max(0, radiusMeters - marginMeters);
}
