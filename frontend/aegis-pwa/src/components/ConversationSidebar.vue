<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { ConversationSummary } from '../types/chat';
import AegisIdentityCard from './AegisIdentityCard.vue';

const props = defineProps<{
  conversations: ConversationSummary[];
  activeConversationId?: string | null;
  disabled?: boolean;
  open?: boolean;
  hasMore?: boolean;
  isLoadingMore?: boolean;
  historyError?: string | null;
}>();

const emit = defineEmits<{
  close: [];
  newConversation: [];
  openConversation: [conversationId: string];
  renameConversation: [conversationId: string, title: string];
  requestDeleteConversation: [conversation: ConversationSummary];
  loadMore: [];
  retryHistory: [];
}>();

const menuConversationId = ref<string | null>(null);
const editingConversationId = ref<string | null>(null);
const editTitle = ref('');
const editError = ref<string | null>(null);
const renameInput = ref<HTMLInputElement | null>(null);
const loadMoreSentinel = ref<HTMLElement | null>(null);
let observer: IntersectionObserver | null = null;

const historyCountLabel = computed(() => {
  if (props.conversations.length === 0) {
    return 'Vazio';
  }

  return props.hasMore ? 'Recentes' : `${props.conversations.length}`;
});

function getTitle(conversation: ConversationSummary): string {
  return conversation.title?.trim() || 'Nova conversa';
}

function getDateLabel(value: string): string {
  const date = new Date(value);
  const today = new Date();
  const startOfToday = new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime();
  const startOfDate = new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
  const dayDifference = Math.round((startOfToday - startOfDate) / 86_400_000);

  if (dayDifference === 0) {
    return 'Hoje';
  }

  if (dayDifference === 1) {
    return 'Ontem';
  }

  return 'Recentes';
}

function toggleMenu(conversationId: string): void {
  menuConversationId.value = menuConversationId.value === conversationId ? null : conversationId;
  editError.value = null;
}

function startRename(conversation: ConversationSummary): void {
  editingConversationId.value = conversation.id;
  editTitle.value = getTitle(conversation);
  editError.value = null;
  menuConversationId.value = null;

  void nextTick(() => {
    renameInput.value?.focus();
    renameInput.value?.select();
  });
}

function cancelRename(): void {
  editingConversationId.value = null;
  editTitle.value = '';
  editError.value = null;
}

function saveRename(conversationId: string): void {
  const normalizedTitle = editTitle.value.trim();
  if (!normalizedTitle) {
    editError.value = 'Escolha um título para continuar.';
    return;
  }

  emit('renameConversation', conversationId, normalizedTitle);
  cancelRename();
}

function configureObserver(): void {
  observer?.disconnect();
  observer = null;

  if (!loadMoreSentinel.value) {
    return;
  }

  observer = new IntersectionObserver(
    (entries) => {
      if (entries.some((entry) => entry.isIntersecting) && props.hasMore && !props.isLoadingMore) {
        emit('loadMore');
      }
    },
    { root: null, rootMargin: '120px' }
  );
  observer.observe(loadMoreSentinel.value);
}

watch(
  () => [props.hasMore, props.isLoadingMore, props.conversations.length, props.open],
  () => {
    void nextTick(configureObserver);
  }
);

onMounted(() => {
  configureObserver();
});

onBeforeUnmount(() => {
  observer?.disconnect();
});
</script>

<template>
  <div class="sidebar-backdrop" :class="{ 'sidebar-backdrop--visible': open }" @click="emit('close')"></div>

  <aside class="conversation-sidebar" :class="{ 'conversation-sidebar--open': open }">
    <div class="sidebar-mobile-header">
      <span>Histórico</span>
      <button type="button" class="icon-button" aria-label="Fechar histórico" @click="emit('close')">
        <svg viewBox="0 0 20 20" aria-hidden="true">
          <path d="m5 5 10 10M15 5 5 15" />
        </svg>
      </button>
    </div>

    <AegisIdentityCard />

    <button
      type="button"
      class="new-conversation-button"
      :disabled="disabled"
      @click="emit('newConversation')"
    >
      <span>Nova conversa</span>
      <svg viewBox="0 0 20 20" aria-hidden="true">
        <path d="M10 4v12M4 10h12" />
      </svg>
    </button>

    <section class="history-panel" aria-label="Histórico de conversas">
      <header class="history-panel__header">
        <div>
          <span>Histórico</span>
          <h2>Conversas</h2>
        </div>
        <span class="history-count">{{ historyCountLabel }}</span>
      </header>

      <div v-if="conversations.length > 0" class="history-list">
        <div
          v-for="conversation in conversations"
          :key="conversation.id"
          class="history-item"
          :class="{ 'history-item--active': conversation.id === activeConversationId }"
        >
          <template v-if="editingConversationId === conversation.id">
            <input
              ref="renameInput"
              v-model="editTitle"
              class="history-rename-input"
              maxlength="80"
              aria-label="Renomear conversa"
              @keydown.enter.prevent="saveRename(conversation.id)"
              @keydown.esc.prevent="cancelRename"
              @blur="cancelRename"
            />
            <span v-if="editError" class="history-inline-error">{{ editError }}</span>
          </template>

          <template v-else>
            <button
              type="button"
              class="history-item__main"
              :disabled="disabled"
              @click="emit('openConversation', conversation.id)"
            >
              <span class="history-item__state">
                <i></i>
                {{ getDateLabel(conversation.updatedAt) }}
              </span>
              <strong>{{ getTitle(conversation) }}</strong>
              <small>{{ conversation.lastMessagePreview || 'Conversa salva' }}</small>
            </button>

            <button
              type="button"
              class="history-menu-button"
              :aria-label="`Ações de ${getTitle(conversation)}`"
              @click.stop="toggleMenu(conversation.id)"
            >
              <svg viewBox="0 0 20 20" aria-hidden="true">
                <path d="M5 10h.01M10 10h.01M15 10h.01" />
              </svg>
            </button>

            <div v-if="menuConversationId === conversation.id" class="history-menu">
              <button type="button" @click="startRename(conversation)">Renomear</button>
              <button type="button" @click="emit('requestDeleteConversation', conversation); menuConversationId = null">
                Apagar
              </button>
            </div>
          </template>
        </div>

        <div ref="loadMoreSentinel" class="history-sentinel" aria-hidden="true"></div>

        <button v-if="historyError" type="button" class="history-retry" @click="emit('retryHistory')">
          Tentar novamente
        </button>
        <span v-else-if="isLoadingMore" class="history-loading">Carregando...</span>
      </div>

      <div v-else class="history-empty">
        <span class="history-empty__line"></span>
        <p>{{ historyError || 'Suas conversas começam por aqui.' }}</p>
        <button v-if="historyError" type="button" class="history-retry" @click="emit('retryHistory')">
          Tentar novamente
        </button>
      </div>
    </section>

    <p class="sidebar-footer">Discreta e pronta para continuar.</p>
  </aside>
</template>
