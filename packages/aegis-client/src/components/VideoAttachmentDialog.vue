<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, useId } from 'vue';
import { chatAttachmentUrl } from '../services/chatAttachments';
import { clientEnvironment, openClientLink } from '../services/clientEnvironment';
import { dismissKeyboard, navigationLayer } from '../services/navigation';
import type { VideoCitation } from '../services/chatVideo';

const props = defineProps<{ citation: VideoCitation; fileName: string }>();
const emit = defineEmits<{ close: [] }>();
const dialog = ref<HTMLDialogElement | null>(null);
const player = ref<HTMLVideoElement | null>(null);
const titleId = useId();
const url = chatAttachmentUrl(props.citation.messageId, props.citation.attachmentId);
const mediaUrl = `${url}?inline=true`;
const playbackError = ref(false);
const downloadError = ref(false);
let releaseLayer: (() => void) | undefined;
let previousFocus: HTMLElement | null = null;

function startAtCitation(): void {
  const video = player.value;
  if (!video) return;
  video.currentTime = Math.min(props.citation.seconds, Number.isFinite(video.duration) ? video.duration : Infinity);
}

async function download(event: MouseEvent): Promise<void> {
  if (clientEnvironment().externalLinks !== 'system') return;
  event.preventDefault();
  try {
    await openClientLink(url);
    downloadError.value = false;
  } catch { downloadError.value = true; }
}

onMounted(() => {
  previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  void dismissKeyboard();
  dialog.value?.showModal();
  releaseLayer = navigationLayer(() => emit('close'));
});
onBeforeUnmount(() => {
  releaseLayer?.();
  player.value?.pause();
  player.value?.removeAttribute('src');
  player.value?.load();
  dialog.value?.close();
  if (previousFocus?.isConnected) previousFocus.focus({ preventScroll: true });
});
</script>

<template>
  <dialog ref="dialog" class="conversation-dialog attachment-video-dialog" :aria-labelledby="titleId"
    @cancel.prevent="emit('close')">
    <header>
      <h2 :id="titleId">{{ fileName }}</h2>
      <button type="button" class="ghost-button" autofocus aria-label="Fechar vídeo" @click="emit('close')">Fechar</button>
    </header>
    <video v-if="!playbackError" ref="player" :src="mediaUrl" controls playsinline preload="metadata"
      @loadedmetadata="startAtCitation" @error="playbackError = true" />
    <div v-else role="status">
      Não foi possível reproduzir este vídeo aqui.
      <a :href="url" :download="fileName" @click="download">Baixar original</a>
      <p v-if="downloadError">Não foi possível abrir o anexo.</p>
    </div>
  </dialog>
</template>

<style scoped>
.attachment-video-dialog { width: min(56rem, calc(100vw - 24px)); padding: clamp(12px, 3vw, 24px); }
.attachment-video-dialog header { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-block-end: 12px; }
.attachment-video-dialog h2 { margin: 0; overflow-wrap: anywhere; }
.attachment-video-dialog video { display: block; width: 100%; max-height: calc(var(--app-viewport-height, 100dvh) - 112px); border-radius: var(--radius-sm); background: #000; }
.attachment-video-dialog a { color: var(--accent-strong); text-decoration: underline; }
</style>
