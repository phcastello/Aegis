// Device IDs stay on this installation and are never sent with transcription metadata.
const PREFERENCE_KEY = 'aegis.voice.input.v1';

export function getPreferredVoiceInput(): string {
  try { return localStorage.getItem(PREFERENCE_KEY) ?? ''; } catch { return ''; }
}

export function setPreferredVoiceInput(deviceId: string): void {
  try {
    if (deviceId) localStorage.setItem(PREFERENCE_KEY, deviceId);
    else localStorage.removeItem(PREFERENCE_KEY);
  } catch { /* Recording still works when local preferences cannot be persisted. */ }
}

export async function listVoiceInputs(): Promise<MediaDeviceInfo[]> {
  if (!navigator.mediaDevices?.enumerateDevices) return [];
  return (await navigator.mediaDevices.enumerateDevices()).filter(device => device.kind === 'audioinput');
}

// Opus/WebAudio can decode digital zero to tiny floating point residuals.
// -600 dBFS is far below even 24-bit PCM resolution; this numerical floor is
// deliberately unrelated to a speech/volume threshold. Quiet input is kept.
export const DIGITAL_ZERO_FLOOR = 1e-30;
export function classifyPcmSignal(channels: readonly Float32Array[]): 'silent' | 'signal' | 'unknown' {
  let samples = 0;
  for (const channel of channels) {
    for (const sample of channel) {
      if (!Number.isFinite(sample)) return 'unknown';
      if (Math.abs(sample) > DIGITAL_ZERO_FLOOR) return 'signal';
      samples += 1;
    }
  }
  return samples > 0 ? 'silent' : 'unknown';
}
