import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { cancelSpeech, getVoiceStatus, streamSpeech } from '../services/aegisApi';
import { VoicePlaybackService, type VoicePlaybackState } from '../services/voicePlaybackService';

import { autoSpeak, setAutoSpeakPreference } from '../services/voicePreference';

export function useAegisVoice() {
  const player = new VoicePlaybackService();
  const playbackState = ref<VoicePlaybackState>('idle');
  const voiceAvailable = ref(true);
  const voiceMessage = ref<string | null>(null);
  let activeTurnId: string | null = null;
  let activeSpeechRequestId: string | null = null;
  let controller: AbortController | null = null;
  let playbackRequest = 0;
  player.onState((state) => { playbackState.value = state; });

  const isBusy = computed(() => playbackState.value === 'buffering' || playbackState.value === 'playing' || playbackState.value === 'initializing');

  function setAutoSpeak(value: boolean): void {
    setAutoSpeakPreference(value);
    if (value) void prepare();
  }

  async function prepare(): Promise<boolean> {
    try {
      await player.ensureReady();
      return true;
    } catch {
      voiceAvailable.value = false;
      voiceMessage.value = 'Toque em Ativar áudio para ouvir a Aegis.';
      return false;
    }
  }

  async function speak(turnId: string, assistantMessageId: string, manual = false): Promise<void> {
    const stopping = stop();
    const request = playbackRequest;
    await stopping;
    if (request !== playbackRequest || !(await prepare()) || request !== playbackRequest) return;
    if (!manual && !autoSpeak.value) return;
    const speechRequestId = crypto.randomUUID();
    const generation = player.begin();
    activeTurnId = turnId;
    activeSpeechRequestId = speechRequestId;
    const speechController = new AbortController();
    controller = speechController;
    const ownsSpeech = (): boolean => request === playbackRequest && activeTurnId === turnId &&
      activeSpeechRequestId === speechRequestId && !speechController.signal.aborted;
    voiceMessage.value = 'Preparando voz';
    try {
      await streamSpeech({ turnId, speechRequestId, assistantMessageId }, async (chunk) => {
        if (!ownsSpeech()) return;
        await player.enqueuePcm(chunk, generation);
        if (ownsSpeech()) voiceMessage.value = 'Falando';
      }, speechController.signal);
      await player.finish(generation);
      if (ownsSpeech()) {
        voiceAvailable.value = true;
        voiceMessage.value = null;
      }
    } catch (error) {
      if (ownsSpeech()) {
        player.fail();
        voiceAvailable.value = false;
        voiceMessage.value = error instanceof TypeError ? 'Serviço de voz indisponível.'
          : error instanceof Error ? error.message : 'Não consegui reproduzir a voz.';
      }
    }
  }

  async function stop(): Promise<void> {
    playbackRequest++;
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

  watch(autoSpeak, enabled => { if (!enabled) void stop(); });
  onBeforeUnmount(() => { void stop(); });
  return { autoSpeak, playbackState, voiceAvailable, voiceMessage, isBusy, setAutoSpeak, prepare, speak, stop, refreshStatus };
}
