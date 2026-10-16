// Confidence in a position is distinct from the saved geometry and Android registration.
// This guidance mirrors the native uncertainty disk; it does not classify presence.
export function placeAccuracyGuidance(accuracyMeters: number | null | undefined) {
  if (typeof accuracyMeters !== 'number' || !Number.isFinite(accuracyMeters) || accuracyMeters <= 0) return null;
  return { accuracyMeters, marginMeters: Math.max(3, accuracyMeters), exceedsNativeAccuracyLimit: accuracyMeters > 200 };
}

export function circleConfirmationRadius(radiusMeters: number, marginMeters: number): number {
  return Math.max(0, radiusMeters - marginMeters);
}
