<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { recentVoiceHistory, voiceHistoryBarHeight, VOICE_HISTORY_STEP_MS } from '../services/voiceWaveform';
import { clientEnvironment } from '../services/clientEnvironment';
import type { TranscriptionState } from '../composables/useAegisTranscription';

const props = defineProps<{ state: TranscriptionState; levels: number[] }>();
defineEmits<{ cancel: []; review: []; send: [] }>();
const composer = ref<HTMLElement | null>(null);
const cancelButton = ref<HTMLButtonElement | null>(null);
const waveform = ref<HTMLElement | null>(null);
const measuredWidth = ref(0);
let observer: ResizeObserver | undefined;
const visibleLevels = computed(() => {
  const levels = props.levels;
  return recentVoiceHistory(levels, measuredWidth.value);
});
watch(waveform, area => {
  observer?.disconnect(); observer = undefined;
  measuredWidth.value = area?.getBoundingClientRect().width ?? 0;
  if (!area) return;
  observer = new ResizeObserver(entries => { measuredWidth.value = entries[0]?.contentRect.width ?? 0; });
  observer.observe(area);
}, { flush: 'post' });
onBeforeUnmount(() => observer?.disconnect());

function focusCancel(): void {
  void nextTick(() => {
    const active = document.activeElement;
    if (composer.value && (!active || active === document.body || composer.value.contains(active))) {
      cancelButton.value?.focus({ preventScroll: true });
    }
  });
}
onMounted(focusCancel);
// Recording controls disappear while uploading. Keep keyboard cancellation
// reachable without returning focus to the hidden textarea or opening a keyboard.
watch(() => props.state, focusCancel);

function barHeight(level: number): string {
  const profile = clientEnvironment().voiceMeterProfile ?? (window.matchMedia('(pointer: coarse)').matches ? 'mobile' : 'desktop');
  return `${voiceHistoryBarHeight(level, profile)}px`;
}
</script>

<template>
  <div ref="composer" class="composer-field recording-composer" :aria-label="state === 'recording' ? 'Gravação de voz' : 'Transcrição de voz'" @keydown.esc.prevent.stop="$emit('cancel')">
    <button
      ref="cancelButton"
      class="record-discard-button" type="button"
      :aria-label="state === 'transcribing' ? 'Cancelar transcrição' : 'Descartar gravação'"
      :title="state === 'transcribing' ? 'Cancelar transcrição' : 'Descartar gravação'"
      @click="$emit('cancel')"
    >
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M6 18 18 6" /></svg>
    </button>

    <template v-if="state === 'recording'">
      <div ref="waveform" class="recording-waveform" aria-hidden="true" :style="{ '--voice-history-step': `${VOICE_HISTORY_STEP_MS}ms` }">
        <i v-for="(level, index) in visibleLevels" :key="index" class="recording-waveform-bar" :style="{ height: barHeight(level) }" />
      </div>
      <span class="sr-only" role="status">Gravando…</span>
      <button class="record-button" type="button" aria-label="Parar e transcrever para revisão" title="Transcrever para revisão" @click="$emit('review')">
        <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="3" width="6" height="12" rx="3" /><path d="M6 11a6 6 0 0 0 12 0M12 17v4m-4 0h8" /></svg>
      </button>
      <button class="send-button" type="button" aria-label="Parar, transcrever e enviar" title="Transcrever e enviar" @click="$emit('send')">
        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 19V5m-6 6 6-6 6 6" /></svg>
      </button>
    </template>
    <span v-else class="recording-composer-status" role="status">{{ state === 'transcribing' ? 'Transcrevendo…' : 'Aguardando microfone…' }}</span>
  </div>
</template>
