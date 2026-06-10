<script setup lang="ts">
import AegisMark from './AegisMark.vue';
import MarkdownMessage from './MarkdownMessage.vue';
import type { FeedbackRating, LocalChatMessage } from '../types/chat';

defineProps<{
  message: LocalChatMessage;
  feedbackStatus?: string | null;
}>();

const emit = defineEmits<{
  feedback: [message: LocalChatMessage, rating: FeedbackRating];
}>();
</script>

<template>
  <article class="message-row" :class="`message-row--${message.role}`">
    <div v-if="message.role !== 'user'" class="message-avatar message-avatar--aegis">
      <AegisMark />
    </div>

    <div class="message-stack">
      <div class="message-bubble" :class="{ 'message-bubble--streaming': message.streaming }">
        <p v-if="message.role === 'user'">{{ message.content }}</p>
        <MarkdownMessage
          v-else
          :content="message.content"
          :animate-changes="message.streaming"
        />
        <span
          v-if="message.streaming"
          class="generation-marker"
          role="status"
          aria-label="Aegis está gerando a resposta"
        ></span>
      </div>

      <div
        v-if="message.role === 'assistant' && !message.pending && !message.streaming && message.serverId"
        class="message-feedback"
      >
        <button type="button" class="feedback-chip" @click="emit('feedback', message, 'good')">
          Boa
        </button>
        <button type="button" class="feedback-chip" @click="emit('feedback', message, 'bad')">
          Ruim
        </button>
        <span v-if="feedbackStatus" class="feedback-saved">{{ feedbackStatus }}</span>
      </div>
    </div>
  </article>
</template>
