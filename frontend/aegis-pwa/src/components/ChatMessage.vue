<script setup lang="ts">
import AegisMark from './AegisMark.vue';
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
      <div class="message-bubble">
        <p>{{ message.content }}</p>
      </div>

      <div v-if="message.role === 'assistant' && !message.pending" class="message-feedback">
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
