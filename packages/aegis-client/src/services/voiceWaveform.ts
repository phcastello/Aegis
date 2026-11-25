// Display-only history. Fixed positions and cadence; narrower windows keep
// fewer of the newest measurements instead of stretching bars or compressing time.
export const VOICE_HISTORY_STEP_MS = 60;

export function recentVoiceHistory(levels: readonly number[], width: number): number[] {
  let count = levels.length;
  while (count > 8 && width / count < 5.5) count /= 2;
  return levels.slice(-count);
}

// Visual height only: keep silent dots, the measured level and cadence intact.
export type VoiceMeterProfile = 'desktop' | 'mobile';
export function voiceHistoryBarHeight(level: number, profile: VoiceMeterProfile = 'desktop'): number {
  const normalized = Number.isFinite(level) ? Math.max(0, Math.min(1, level)) : 0;
  // Desktop microphones tend to deliver modest normalized levels. Emphasize
  // smaller real variations without clipping the upper range or changing gain.
  // The compact mobile panel instead keeps the quieter square-root profile.
  return profile === 'mobile' ? 3 + Math.sqrt(normalized) * 39 : 3 + Math.pow(normalized, .32) * 55;
}
