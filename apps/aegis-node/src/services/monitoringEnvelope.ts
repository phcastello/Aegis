import type { Circle, Place } from './domains.js';

// Uniform padding around every member of the real union, matching the backend.
// Small homes start near 200 m; larger areas retain their own geometry and use
// a smaller buffer. This is a reviewable suggestion, never auto-enabled.
export function suggestedMonitoringPadding(circles: Circle[]): number {
  return circles.length ? Math.max(75, Math.min(150, 200 - Math.min(...circles.map(circle => circle.radiusMeters)))) : 150;
}
export function previewMonitoringEnvelope(circles: Circle[], enabled: boolean, paddingMeters: number): Circle[] {
  if (!enabled || !Number.isFinite(paddingMeters) || paddingMeters < 50 || paddingMeters > 1000) return [];
  return circles.map(circle => ({ ...circle, id: `preview-${circle.id}`, radiusMeters: circle.radiusMeters + paddingMeters }));
}
export function placeAreaLabel(circles: Circle[]): string {
  if (!circles.length) return 'Área herdada';
  if (circles.length === 1) return `${Math.round(circles[0]!.radiusMeters)} m`;
  return `${circles.length} círculos · ${circles.map(circle => `${Math.round(circle.radiusMeters)} m`).join(' + ')}`;
}
export function proximityLabel(place: Place): string {
  const envelope = place.monitoringEnvelope;
  if (!envelope?.enabled) return envelope?.approvedAt ? 'Desativada' : 'Aguardando sua aprovação';
  if (envelope.approvalRequired) return 'Confirme a área atual';
  return placeAreaLabel(envelope.circles);
}

export function inheritedPlaceCircles(places: Place[], parentId: string | null): Circle[] {
  const seen = new Set<string>(); let id = parentId;
  while (id && !seen.has(id)) {
    seen.add(id); const parent = places.find(place => place.id === id);
    if (!parent) return [];
    if (parent.circles.length) return parent.circles;
    id = parent.parentId;
  }
  return [];
}
