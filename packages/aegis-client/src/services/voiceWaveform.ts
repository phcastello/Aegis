// Display-only history. Fixed positions and cadence; narrower windows keep
// fewer of the newest measurements instead of stretching bars or compressing time.
export const VOICE_HISTORY_STEP_MS = 60;

export function recentVoiceHistory(levels: readonly number[], width: number): number[] {
  let count = levels.length;
  while (count > 8 && width / count < 5.5) count /= 2;
  return levels.slice(-count);
}

// Visual height only: keep silent dots, the measured level and cadence intact.
export function voiceHistoryBarHeight(level: number): number {
  return 3 + Math.sqrt(Math.max(0, Math.min(1, level))) * 49;
}
