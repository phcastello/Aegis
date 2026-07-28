<script setup lang="ts">
import { ref } from 'vue';
import AegisMark from './AegisMark.vue';
import MarkdownMessage from './MarkdownMessage.vue';
import type { FeedbackRating, LocalChatMessage } from '../types/chat';

const props = defineProps<{ message: LocalChatMessage; feedbackStatus?: string | null; isPlaying?: boolean }>();
const emit = defineEmits<{
  feedback: [message: LocalChatMessage, rating: FeedbackRating];
  replay: [message: LocalChatMessage];
  stopPlayback: [];
}>();
const copied = ref(false);
const copyError = ref(false);

async function copy(): Promise<void> {
  try {
    await navigator.clipboard.writeText(props.message.content);
    copied.value = true;
    copyError.value = false;
    window.setTimeout(() => copied.value = false, 1800);
  } catch {
    copyError.value = true;
    window.setTimeout(() => copyError.value = false, 3000);
  }
}
</script>

<template>
  <article class="message-row" :class="`message-row--${message.role}`">
    <div v-if="message.role !== 'user'" class="message-avatar message-avatar--aegis"><AegisMark /></div>
    <div class="message-stack">
      <div class="message-bubble" :class="{ 'message-bubble--streaming': message.streaming }">
        <p v-if="message.role === 'user'">{{ message.content }}</p>
        <MarkdownMessage v-else :content="message.content" :animate-changes="message.streaming" />
        <span v-if="message.streaming" class="generation-marker" role="status" aria-label="Aegis está respondendo"></span>
      </div>
      <p v-if="message.interrupted" class="message-generation-status">Geração interrompida</p>
      <div v-if="!message.pending && !message.streaming" class="message-actions">
        <template v-if="message.role === 'assistant'">
          <button class="message-action" aria-label="Marcar resposta como boa" @click="emit('feedback', message, 'good')"><svg viewBox="0 0 24 24"><path d="M7 10v10M4 10h3v10H4zM7 20h11l2-7a2 2 0 0 0-2-2h-3l1-4a3 3 0 0 0-3-3l-5 6Z" /></svg></button>
          <button class="message-action" aria-label="Marcar resposta como ruim" @click="emit('feedback', message, 'bad')"><svg viewBox="0 0 24 24"><path d="M7 14V4M4 4h3v10H4zM7 4h11l2 7a2 2 0 0 1-2 2h-3l1 4a3 3 0 0 1-3 3l-5-6Z" /></svg></button>
          <button class="message-action" :aria-label="isPlaying ? 'Interromper reprodução' : 'Ouvir resposta'" @click="isPlaying ? emit('stopPlayback') : emit('replay', message)">
            <span v-if="isPlaying" class="audio-bars"><i/><i/><i/><i/></span>
            <svg v-else viewBox="0 0 24 24"><path d="M4 10v4h4l5 4V6l-5 4H4Zm12-1a5 5 0 0 1 0 6m3-9a9 9 0 0 1 0 12" /></svg>
          </button>
        </template>
        <button class="message-action" :aria-label="copied ? 'Mensagem copiada' : 'Copiar mensagem'" @click="copy"><svg v-if="copied" viewBox="0 0 24 24"><path d="m5 12 4 4L19 6" /></svg><svg v-else viewBox="0 0 24 24"><rect x="8" y="8" width="11" height="11" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3"/></svg></button>
        <span v-if="copyError" class="message-action-error" role="status">Não foi possível copiar.</span>
      </div>
    </div>
  </article>
</template>
