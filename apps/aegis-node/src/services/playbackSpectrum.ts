/** Presentation-only envelope of the measured, played PCM spectrum. */
export class PlaybackSpectrumDisplay {
  static readonly bandCount = 16;
  private levels = Array<number>(PlaybackSpectrumDisplay.bandCount).fill(0);
  private previousMs: number | undefined;
  private quietSinceMs: number | undefined;
  private quietStart = Array<number>(PlaybackSpectrumDisplay.bandCount).fill(0);

  clear() {
    this.levels.fill(0);
    this.previousMs = undefined;
    this.quietSinceMs = undefined;
  }

  update(measured: readonly number[], nowMs: number, smooth: boolean): number[] {
    // The bridge still delivers 32 real bands. Peak pooling retains the strongest
    // measured component in each pair of adjacent frequency ranges.
    const quiet = !measured.some(level => level > 0.002);
    if (quiet) {
      if (this.quietSinceMs === undefined) {
        this.quietSinceMs = this.previousMs ?? nowMs;
        this.quietStart = this.levels.slice();
      }
      const progress = Math.min(1, Math.max(0, (nowMs - this.quietSinceMs) / 120));
      if (!smooth || progress >= 1 || !this.levels.some(level => level > 0.002)) {
        this.clear();
        return [];
      }
      // A bounded ease-out reaches zero instead of an abrupt exponential cutoff.
      const gain = (1 - progress) ** 2;
      this.previousMs = nowMs;
      for (let i = 0; i < this.levels.length; i++) this.levels[i] = this.quietStart[i] * gain;
      return this.levels.slice();
    }
    this.quietSinceMs = undefined;
    const elapsed = this.previousMs === undefined ? 40 : Math.max(0, Math.min(120, nowMs - this.previousMs));
    this.previousMs = nowMs;
    for (let i = 0; i < this.levels.length; i++) {
      const target = Math.max(measured[i * 2], measured[i * 2 + 1]);
      const current = this.levels[i];
      const tau = target > current ? 75 : 150;
      this.levels[i] = smooth ? current + (target - current) * (1 - Math.exp(-elapsed / tau)) : target;
    }
    return this.levels.slice();
  }
}
