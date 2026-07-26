export type VoicePlaybackState = 'idle' | 'initializing' | 'buffering' | 'playing' | 'stopping' | 'stopped' | 'failed';

type PlaybackMetrics = {
  queuedAudioSeconds: number;
  playedAudioSeconds: number;
  underrunCount: number;
  droppedAudioFrames: number;
};

const SOURCE_RATE = 24000;
const TARGET_BUFFER_SECONDS = 0.4;
const WARNING_BUFFER_SECONDS = 2;
const HARD_MAX_BUFFER_SECONDS = 5;

/** Persistent Web Audio PCM player. One instance remains alive for the whole chat view. */
export class VoicePlaybackService {
  private context: AudioContext | null = null;
  private node: AudioWorkletNode | null = null;
  private generation = 0;
  private residual: number | null = null;
  private sourceCarry: number | null = null;
  private resamplePosition = 0;
  private queuedFrames = 0;
  private readonly metrics: PlaybackMetrics = { queuedAudioSeconds: 0, playedAudioSeconds: 0, underrunCount: 0, droppedAudioFrames: 0 };
  private state: VoicePlaybackState = 'idle';
  private readonly listeners = new Set<(state: VoicePlaybackState) => void>();
  private drainWaiters: Array<() => void> = [];

  onState(listener: (state: VoicePlaybackState) => void): () => void {
    this.listeners.add(listener);
    listener(this.state);
    return () => this.listeners.delete(listener);
  }

  getMetrics(): PlaybackMetrics {
    return { ...this.metrics, queuedAudioSeconds: this.queuedFrames / (this.context?.sampleRate ?? SOURCE_RATE) };
  }

  async ensureReady(): Promise<void> {
    if (!this.context) {
      this.setState('initializing');
      this.context = new AudioContext();
      const module = URL.createObjectURL(new Blob([workletSource], { type: 'application/javascript' }));
      try {
        await this.context.audioWorklet.addModule(module);
      } finally {
        URL.revokeObjectURL(module);
      }
      this.node = new AudioWorkletNode(this.context, 'aegis-pcm-player', { outputChannelCount: [1] });
      this.node.port.onmessage = (event: MessageEvent<{ type: string; frames?: number }>) => {
        if (event.data.type === 'consumed') {
          const frames = event.data.frames ?? 0;
          this.queuedFrames = Math.max(0, this.queuedFrames - frames);
          this.metrics.playedAudioSeconds += frames / this.context!.sampleRate;
          this.resolveDrainWaiters();
        } else if (event.data.type === 'underrun') {
          this.metrics.underrunCount += 1;
        }
      };
      this.node.connect(this.context.destination);
    }
    if (this.context.state !== 'running') await this.context.resume();
    if (this.state === 'initializing' || this.state === 'stopped') this.setState('idle');
  }

  begin(): number {
    this.generation += 1;
    this.residual = null;
    this.sourceCarry = null;
    this.resamplePosition = 0;
    this.queuedFrames = 0;
    this.node?.port.postMessage({ type: 'clear', generation: this.generation });
    this.setState('buffering');
    return this.generation;
  }

  async enqueuePcm(chunk: Uint8Array, generation: number): Promise<void> {
    if (!this.context || !this.node || generation !== this.generation) return;
    const samples = this.decodeS16Le(chunk);
    if (samples.length === 0) return;
    const resampled = this.resample(samples);
    if (resampled.length === 0 || generation !== this.generation) return;
    await this.waitForCapacity(generation);
    if (generation !== this.generation) return;
    const hardFrames = Math.floor(HARD_MAX_BUFFER_SECONDS * this.context.sampleRate);
    if (this.queuedFrames + resampled.length > hardFrames) {
      this.metrics.droppedAudioFrames += resampled.length;
      return;
    }
    this.queuedFrames += resampled.length;
    this.metrics.queuedAudioSeconds = this.queuedFrames / this.context.sampleRate;
    this.node.port.postMessage({ type: 'enqueue', generation, samples: resampled }, [resampled.buffer]);
    if (this.queuedFrames >= Math.floor(TARGET_BUFFER_SECONDS * this.context.sampleRate)) this.setState('playing');
  }

  stop(): void {
    this.setState('stopping');
    this.generation += 1;
    this.residual = null;
    this.sourceCarry = null;
    this.resamplePosition = 0;
    this.queuedFrames = 0;
    this.node?.port.postMessage({ type: 'clear', generation: this.generation });
    this.resolveDrainWaiters();
    this.setState('stopped');
  }

  fail(): void {
    this.stop();
    this.setState('failed');
  }

  private decodeS16Le(chunk: Uint8Array): Float32Array {
    const bytes = this.residual === null ? chunk : new Uint8Array([this.residual, ...chunk]);
    this.residual = bytes.length % 2 === 1 ? bytes[bytes.length - 1] : null;
    const count = Math.floor(bytes.length / 2);
    const result = new Float32Array(count);
    for (let index = 0; index < count; index += 1) {
      const value = bytes[index * 2] | (bytes[index * 2 + 1] << 8);
      result[index] = (value >= 0x8000 ? value - 0x10000 : value) / 0x8000;
    }
    return result;
  }

  private resample(input: Float32Array): Float32Array {
    const targetRate = this.context!.sampleRate;
    if (targetRate === SOURCE_RATE) return input;
    const values = this.sourceCarry === null ? input : new Float32Array([this.sourceCarry, ...input]);
    const ratio = SOURCE_RATE / targetRate;
    const output: number[] = [];
    for (let position = this.resamplePosition; position + 1 < values.length; position += ratio) {
      const base = Math.floor(position);
      const fraction = position - base;
      output.push(values[base] + (values[base + 1] - values[base]) * fraction);
      this.resamplePosition = position + ratio;
    }
    this.resamplePosition -= Math.max(0, values.length - 1);
    this.sourceCarry = values[values.length - 1] ?? this.sourceCarry;
    return Float32Array.from(output);
  }

  private async waitForCapacity(generation: number): Promise<void> {
    const context = this.context;
    if (!context || this.queuedFrames < WARNING_BUFFER_SECONDS * context.sampleRate) return;
    await new Promise<void>((resolve) => this.drainWaiters.push(resolve));
    if (generation !== this.generation) return;
  }

  private resolveDrainWaiters(): void {
    if (!this.context || this.queuedFrames >= WARNING_BUFFER_SECONDS * this.context.sampleRate) return;
    const waiters = this.drainWaiters;
    this.drainWaiters = [];
    waiters.forEach((resolve) => resolve());
  }

  private setState(state: VoicePlaybackState): void {
    this.state = state;
    this.listeners.forEach((listener) => listener(state));
  }
}

const workletSource = `
class AegisPcmPlayer extends AudioWorkletProcessor {
  constructor() { super(); this.queue = []; this.offset = 0; this.generation = 0; this.lastUnderrun = 0;
    this.port.onmessage = (event) => { const data = event.data;
      if (data.type === 'clear') { this.generation = data.generation; this.queue = []; this.offset = 0; }
      if (data.type === 'enqueue' && data.generation === this.generation) this.queue.push(data.samples);
    };
  }
  process(inputs, outputs) { const output = outputs[0][0]; let consumed = 0;
    for (let index = 0; index < output.length; index += 1) {
      const current = this.queue[0];
      if (!current) { output[index] = 0; continue; }
      output[index] = current[this.offset++]; consumed += 1;
      if (this.offset >= current.length) { this.queue.shift(); this.offset = 0; }
    }
    if (consumed) this.port.postMessage({ type: 'consumed', frames: consumed });
    else if (currentTime - this.lastUnderrun > 1) { this.lastUnderrun = currentTime; this.port.postMessage({ type: 'underrun' }); }
    return true;
  }
}
registerProcessor('aegis-pcm-player', AegisPcmPlayer);`;
