<script setup lang="ts">
import AegisMark from './AegisMark.vue';
import type { LocalChatMessage } from '../types/chat';

defineProps<{
  message: LocalChatMessage;
}>();
</script>

<template>
  <article class="message-row" :class="`message-row--${message.role}`">
    <div v-if="message.role !== 'user'" class="message-avatar message-avatar--aegis">
      <AegisMark />
    </div>

    <div class="message-bubble">
      <p>{{ message.content }}</p>
    </div>
  </article>
</template>
