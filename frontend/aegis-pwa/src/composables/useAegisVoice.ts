import { computed, onBeforeUnmount, ref } from 'vue';
import { cancelSpeech, getVoiceStatus, streamSpeech } from '../services/aegisApi';
import { VoicePlaybackService, type VoicePlaybackState } from '../services/voicePlaybackService';

const AUTO_SPEAK_KEY = 'aegis.voice.autoSpeak';

export function useAegisVoice() {
  const player = new VoicePlaybackService();
  const autoSpeak = ref(localStorage.getItem(AUTO_SPEAK_KEY) !== 'false');
  const playbackState = ref<VoicePlaybackState>('idle');
  const voiceAvailable = ref(true);
  const voiceMessage = ref<string | null>(null);
  let activeTurnId: string | null = null;
  let activeSpeechRequestId: string | null = null;
  let controller: AbortController | null = null;
  player.onState((state) => { playbackState.value = state; });

  const isBusy = computed(() => playbackState.value === 'buffering' || playbackState.value === 'playing' || playbackState.value === 'initializing');

  function setAutoSpeak(value: boolean): void {
    autoSpeak.value = value;
    localStorage.setItem(AUTO_SPEAK_KEY, String(value));
    if (!value) void stop();
    if (value) void prepare();
  }

  async function prepare(): Promise<void> {
    try { await player.ensureReady(); } catch { voiceMessage.value = 'Toque em Ativar áudio para ouvir a Aegis.'; }
  }

  async function speak(turnId: string, assistantMessageId: string, manual = false): Promise<void> {
    await stop();
    await prepare();
    if (!manual && !autoSpeak.value) return;
    const speechRequestId = crypto.randomUUID();
    const generation = player.begin();
    activeTurnId = turnId;
    activeSpeechRequestId = speechRequestId;
    controller = new AbortController();
    voiceMessage.value = 'Preparando voz';
    try {
      await streamSpeech({ turnId, speechRequestId, assistantMessageId }, async (chunk) => {
        if (activeTurnId !== turnId || activeSpeechRequestId !== speechRequestId || controller?.signal.aborted) return;
        await player.enqueuePcm(chunk, generation);
        voiceMessage.value = 'Falando';
      }, controller.signal);
      await player.finish(generation);
      if (activeTurnId === turnId) {
        voiceAvailable.value = true;
        voiceMessage.value = null;
      }
    } catch (error) {
      if (!controller?.signal.aborted && activeTurnId === turnId) {
        player.fail();
        voiceAvailable.value = false;
        voiceMessage.value = 'Não consegui reproduzir a voz';
      }
    }
  }

  async function stop(): Promise<void> {
    const speechRequestId = activeSpeechRequestId;
    activeTurnId = null;
    activeSpeechRequestId = null;
    player.stop();
    controller?.abort();
    controller = null;
    voiceMessage.value = null;
    if (speechRequestId) {
      try { await cancelSpeech(speechRequestId); } catch { /* local stop is already complete */ }
    }
  }

  async function refreshStatus(): Promise<void> {
    try { voiceAvailable.value = (await getVoiceStatus()).available; } catch { voiceAvailable.value = false; }
  }

  onBeforeUnmount(() => { void stop(); });
  return { autoSpeak, playbackState, voiceAvailable, voiceMessage, isBusy, setAutoSpeak, prepare, speak, stop, refreshStatus };
}
