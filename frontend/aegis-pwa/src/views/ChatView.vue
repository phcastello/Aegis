<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue';
import AegisMark from '../components/AegisMark.vue';
import ChatMessage from '../components/ChatMessage.vue';
import { getConversation, sendMessage } from '../services/aegisApi';
import type { LocalChatMessage } from '../types/chat';

const STORAGE_KEY = 'aegis.currentConversationId';

const messages = ref<LocalChatMessage[]>([]);
const conversationId = ref<string | null>(localStorage.getItem(STORAGE_KEY));
const draft = ref('');
const isLoading = ref(false);
const isRestoring = ref(false);
const errorMessage = ref<string | null>(null);
const messagesEnd = ref<HTMLElement | null>(null);
const composerInput = ref<HTMLTextAreaElement | null>(null);
const isComposerScrollable = ref(false);

const canSend = computed(() => draft.value.trim().length > 0 && !isLoading.value);
const conversationLabel = computed(() =>
  conversationId.value ? `Conversa ${conversationId.value.slice(0, 8)}` : 'Nova conversa'
);
const COMPOSER_MAX_HEIGHT = 168;

function scrollToLatest(): void {
  nextTick(() => {
    messagesEnd.value?.scrollIntoView({ behavior: 'smooth', block: 'end' });
  });
}

function resizeComposer(): void {
  nextTick(() => {
    const input = composerInput.value;
    if (!input) {
      return;
    }

    input.style.height = 'auto';
    isComposerScrollable.value = input.scrollHeight > COMPOSER_MAX_HEIGHT;
    input.style.height = `${Math.min(input.scrollHeight, COMPOSER_MAX_HEIGHT)}px`;
  });
}

function createLocalMessageId(): string {
  try {
    const randomUUID = globalThis.crypto?.randomUUID?.bind(globalThis.crypto);
    if (randomUUID) {
      return `local-${randomUUID()}`;
    }
  } catch {
    // The fallback below is enough for a temporary UI key.
  }

  return `local-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function createLocalUserMessage(content: string): LocalChatMessage {
  return {
    id: createLocalMessageId(),
    conversationId: conversationId.value ?? 'pending',
    role: 'user',
    content,
    createdAt: new Date().toISOString(),
    model: null,
    pending: true
  };
}

async function restoreConversation(): Promise<void> {
  if (!conversationId.value) {
    return;
  }

  isRestoring.value = true;
  errorMessage.value = null;

  try {
    const conversation = await getConversation(conversationId.value);
    messages.value = conversation.messages;
    scrollToLatest();
  } catch (error) {
    localStorage.removeItem(STORAGE_KEY);
    conversationId.value = null;
    errorMessage.value =
      error instanceof Error ? error.message : 'Nao foi possivel carregar a conversa anterior.';
  } finally {
    isRestoring.value = false;
  }
}

async function handleSubmit(): Promise<void> {
  if (!canSend.value) {
    return;
  }

  const content = draft.value.trim();
  const localMessage = createLocalUserMessage(content);

  draft.value = '';
  resizeComposer();
  errorMessage.value = null;
  isLoading.value = true;
  messages.value.push(localMessage);
  scrollToLatest();

  try {
    const response = await sendMessage({
      conversationId: conversationId.value,
      content
    });

    conversationId.value = response.conversationId;
    localStorage.setItem(STORAGE_KEY, response.conversationId);

    localMessage.conversationId = response.conversationId;
    localMessage.pending = false;
    messages.value.push(response.message);
    scrollToLatest();
  } catch (error) {
    errorMessage.value =
      error instanceof Error ? error.message : 'Nao foi possivel enviar a mensagem.';
    localMessage.pending = false;
  } finally {
    isLoading.value = false;
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault();
    void handleSubmit();
  }
}

function startNewConversation(): void {
  localStorage.removeItem(STORAGE_KEY);
  conversationId.value = null;
  messages.value = [];
  draft.value = '';
  resizeComposer();
  errorMessage.value = null;
}

onMounted(() => {
  void restoreConversation();
});
</script>

<template>
  <main class="app-shell">
    <section class="chat-panel" aria-label="Chat da Aegis">
      <header class="chat-header">
        <div class="brand-lockup">
          <div class="brand-mark">
            <AegisMark />
          </div>
          <div>
            <h1>Aegis</h1>
            <p>Hello, Aegis</p>
          </div>
        </div>

        <div class="header-actions">
          <span class="conversation-chip">{{ conversationLabel }}</span>
          <button class="ghost-button" type="button" @click="startNewConversation">
            Nova conversa
          </button>
        </div>
      </header>

      <div class="messages" aria-live="polite">
        <div v-if="isRestoring" class="empty-state">
          <AegisMark />
          <span>Carregando conversa...</span>
        </div>

        <div v-else-if="messages.length === 0" class="empty-state">
          <AegisMark />
          <strong>Aegis esta pronta.</strong>
          <span>Escreva a primeira mensagem para iniciar esta conversa.</span>
        </div>

        <template v-else>
          <ChatMessage v-for="message in messages" :key="message.id" :message="message" />
        </template>

        <div v-if="isLoading" class="typing-indicator" role="status">
          <span></span>
          <span></span>
          <span></span>
        </div>

        <div ref="messagesEnd" class="messages-end"></div>
      </div>

      <p v-if="errorMessage" class="error-message" role="alert">{{ errorMessage }}</p>

      <form class="composer" @submit.prevent="handleSubmit">
        <textarea
          v-model="draft"
          rows="1"
          placeholder="Escreva para a Aegis..."
          aria-label="Mensagem"
          :disabled="isLoading || isRestoring"
          :class="{ 'composer-input--scrollable': isComposerScrollable }"
          ref="composerInput"
          @input="resizeComposer"
          @keydown="handleKeydown"
        ></textarea>

        <button class="send-button" type="submit" :disabled="!canSend || isRestoring" aria-label="Enviar">
          <span>Enviar</span>
          <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
            <path d="M3 10h12.2M10.8 5.6 15.2 10l-4.4 4.4" />
          </svg>
        </button>
      </form>
    </section>
  </main>
</template>
