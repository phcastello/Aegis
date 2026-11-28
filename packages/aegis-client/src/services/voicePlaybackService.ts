export type VoicePlaybackState = 'idle' | 'initializing' | 'buffering' | 'playing' | 'stopping' | 'stopped' | 'failed';

type PlaybackMetrics = {
  queuedAudioSeconds: number;
  playedAudioSeconds: number;
  underrunCount: number;
  droppedAudioFrames: number;
};

const SOURCE_RATE = 24000;
const START_BUFFER_SECONDS = 0.16;
const MAX_BUFFER_SECONDS = 0.4;

type PlaybackEvent =
  | { type: 'consumed'; generation: number; frames: number }
  | { type: 'playing' | 'underrun'; generation: number };

/** Persistent Web Audio PCM player. One instance remains alive for the whole chat view. */
export class VoicePlaybackService {
  private context: AudioContext | null = null;
  private node: AudioWorkletNode | null = null;
  private generation = 0;
  private residual: number | null = null;
  private sourceCarry: number | null = null;
  private resamplePosition = 0;
  private queuedFrames = 0;
  private inputCompletedGeneration: number | null = null;
  private readonly metrics: PlaybackMetrics = { queuedAudioSeconds: 0, playedAudioSeconds: 0, underrunCount: 0, droppedAudioFrames: 0 };
  private state: VoicePlaybackState = 'idle';
  private readonly listeners = new Set<(state: VoicePlaybackState) => void>();
  private drainWaiters: Array<() => void> = [];
  private finishWaiters: Array<() => void> = [];

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
      this.node.port.onmessage = ({ data }: MessageEvent<PlaybackEvent>) => {
        if (data.generation !== this.generation) return;
        if (data.type === 'consumed') {
          const { frames } = data;
          this.queuedFrames = Math.max(0, this.queuedFrames - frames);
          this.metrics.playedAudioSeconds += frames / this.context!.sampleRate;
          this.resolveDrainWaiters();
          if (this.queuedFrames === 0 && this.inputCompletedGeneration === this.generation) {
            this.setState('idle');
            this.resolveFinishWaiters();
          }
        } else if (data.type === 'underrun') {
          if (this.inputCompletedGeneration === this.generation) return;
          this.metrics.underrunCount += 1;
          this.setState('buffering');
        } else if (data.type === 'playing') {
          this.setState('playing');
        }
      };
      this.node.connect(this.context.destination);
    }
    if (this.context.state !== 'running') await this.context.resume();
    if (this.state === 'initializing' || this.state === 'stopped') this.setState('idle');
  }

  begin(): number {
    this.clear();
    this.setState('buffering');
    return this.generation;
  }

  async enqueuePcm(chunk: Uint8Array, generation: number): Promise<void> {
    if (!this.context || generation !== this.generation) return;
    const samples = this.decodeS16Le(chunk);
    if (samples.length === 0) return;
    await this.enqueueFrames(this.resample(samples), generation);
  }

  private async enqueueFrames(samples: Float32Array, generation: number): Promise<void> {
    if (!this.context || !this.node || generation !== this.generation) return;
    // Never drop PCM because a ReadableStream happened to deliver a large chunk.
    // Splitting before transferring is important: transferring a subarray would detach
    // the whole resampled buffer and silently lose its remaining samples.
    // Leave room for a whole packet while the worklet is still building its 160 ms reserve.
    const packetFrames = Math.max(1, Math.floor((MAX_BUFFER_SECONDS / 2) * this.context.sampleRate));
    for (let offset = 0; offset < samples.length; offset += packetFrames) {
      const packet = samples.slice(offset, Math.min(offset + packetFrames, samples.length));
      await this.waitForCapacity(generation, packet.length);
      if (generation !== this.generation) return;
      this.queuedFrames += packet.length;
      this.node.port.postMessage({ type: 'enqueue', generation, samples: packet }, [packet.buffer]);
    }
  }

  stop(): void {
    this.setState('stopping');
    this.clear();
    this.setState('stopped');
  }

  private clear(): void {
    this.generation += 1;
    this.residual = null;
    this.sourceCarry = null;
    this.resamplePosition = 0;
    this.queuedFrames = 0;
    this.inputCompletedGeneration = null;
    this.node?.port.postMessage({ type: 'clear', generation: this.generation });
    this.resolveDrainWaiters();
    this.resolveFinishWaiters();
  }

  fail(): void {
    this.stop();
    this.setState('failed');
  }

  async finish(generation: number): Promise<void> {
    if (generation !== this.generation) return;
    if (this.sourceCarry !== null) {
      // There is no next packet to interpolate against. Hold the final input frame.
      const tail = this.resample(Float32Array.of(this.sourceCarry));
      this.sourceCarry = null;
      await this.enqueueFrames(tail, generation);
      if (generation !== this.generation) return;
    }
    this.inputCompletedGeneration = generation;
    this.node?.port.postMessage({ type: 'end', generation });
    if (this.queuedFrames === 0) {
      this.setState('idle');
      return;
    }
    await new Promise<void>((resolve) => this.finishWaiters.push(resolve));
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

  private async waitForCapacity(generation: number, incomingFrames: number): Promise<void> {
    const context = this.context;
    if (!context) return;
    const targetFrames = Math.floor(MAX_BUFFER_SECONDS * context.sampleRate);
    while (generation === this.generation && this.queuedFrames + incomingFrames > targetFrames) {
      await new Promise<void>((resolve) => this.drainWaiters.push(resolve));
    }
  }

  private resolveDrainWaiters(): void {
    const waiters = this.drainWaiters;
    this.drainWaiters = [];
    waiters.forEach((resolve) => resolve());
  }

  private resolveFinishWaiters(): void {
    const waiters = this.finishWaiters;
    this.finishWaiters = [];
    waiters.forEach((resolve) => resolve());
  }

  private setState(state: VoicePlaybackState): void {
    this.state = state;
    this.listeners.forEach((listener) => listener(state));
  }
}

const workletSource = `
class AegisPcmPlayer extends AudioWorkletProcessor {
  constructor() {
    super();
    this.minimumFrames = Math.ceil(sampleRate * ${START_BUFFER_SECONDS});
    this.clear(0);
    this.port.onmessage = ({ data }) => {
      if (data.type === 'clear') {
        this.clear(data.generation);
      } else if (data.generation === this.generation) {
        if (data.type === 'enqueue') {
          this.queue.push(data.samples);
          this.frames += data.samples.length;
        } else if (data.type === 'end') {
          this.ended = true;
        }
      }
    };
  }

  clear(generation) {
    this.generation = generation;
    this.queue = [];
    this.offset = 0;
    this.frames = 0;
    this.playing = false;
    this.ended = false;
  }

  process(inputs, outputs) {
    const output = outputs[0][0];
    if (!this.playing) {
      // The unfilled output remains silent until the reserve is ready or EOF arrives.
      if (this.frames < this.minimumFrames && !(this.ended && this.frames > 0)) return true;
      this.playing = true;
      this.port.postMessage({ type: 'playing', generation: this.generation });
    }

    let consumed = 0;
    while (consumed < output.length && this.queue.length > 0) {
      const current = this.queue[0];
      const count = Math.min(current.length - this.offset, output.length - consumed);
      for (let index = 0; index < count; index++) output[consumed + index] = current[this.offset + index];
      consumed += count;
      this.offset += count;
      if (this.offset === current.length) {
        this.queue.shift();
        this.offset = 0;
      }
    }
    this.frames -= consumed;
    if (consumed > 0) {
      this.port.postMessage({ type: 'consumed', generation: this.generation, frames: consumed });
    }
    if (consumed < output.length) {
      this.playing = false;
      if (!this.ended) this.port.postMessage({ type: 'underrun', generation: this.generation });
    }
    return true;
  }
}
registerProcessor('aegis-pcm-player', AegisPcmPlayer);`;
