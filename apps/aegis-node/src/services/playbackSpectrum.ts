/** Presentation-only envelope of the measured, played PCM spectrum. */
export class PlaybackSpectrumDisplay {
  static readonly bandCount = 16;
  private levels: number[];
  private previousMs: number | undefined;
  private quietSinceMs: number | undefined;
  private quietStart: number[];

  constructor(private readonly bandCount = PlaybackSpectrumDisplay.bandCount,
    private readonly attackMs = 75, private readonly releaseMs = 150) {
    this.levels = Array<number>(bandCount).fill(0);
    this.quietStart = Array<number>(bandCount).fill(0);
  }

  clear() {
    this.levels.fill(0);
    this.previousMs = undefined;
    this.quietSinceMs = undefined;
  }

  update(measured: readonly number[], nowMs: number, smooth: boolean): number[] {
    // The bridge still delivers 32 real bands. Peak pooling retains the strongest
    // measured component when a display groups adjacent frequency ranges.
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
      const start = Math.floor(i * measured.length / this.bandCount);
      const stop = Math.max(start + 1, Math.floor((i + 1) * measured.length / this.bandCount));
      let target = 0;
      for (let j = start; j < stop && j < measured.length; j++) target = Math.max(target, measured[j]);
      const current = this.levels[i];
      const tau = target > current ? this.attackMs : this.releaseMs;
      this.levels[i] = smooth ? current + (target - current) * (1 - Math.exp(-elapsed / tau)) : target;
    }
    return this.levels.slice();
  }
}
