<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import ConversationMenu from './ConversationMenu.vue';
import type { ConversationSummary } from '../types/chat';
import AegisIdentityCard from './AegisIdentityCard.vue';

const props = defineProps<{
  conversations: ConversationSummary[];
  appVersion?: string;
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
  renameConversation: [conversation: ConversationSummary];
  infoConversation: [conversation: ConversationSummary];
  requestDeleteConversation: [conversation: ConversationSummary];
  loadMore: [];
  retryHistory: [];
}>();

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

  <aside id="conversation-sidebar" class="conversation-sidebar" :class="{ 'conversation-sidebar--open': open }" :inert="!open" :aria-hidden="!open">
    <div class="sidebar-header">
      <AegisIdentityCard :version="appVersion" />
      <button
        id="sidebar-collapse-button"
        type="button"
        class="sidebar-toggle"
        aria-label="Fechar histórico"
        title="Recolher barra lateral"
        aria-controls="conversation-sidebar"
        :aria-expanded="true"
        @click="emit('close')"
      >
        <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="3" /><path d="M9 4v16" /></svg>
      </button>
    </div>

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
          <button type="button" class="history-item__main" :disabled="disabled" @click="emit('openConversation', conversation.id)">
            <strong>{{ getTitle(conversation) }}</strong>
            <span v-if="conversation.hasUnreadProactive" class="unread-dot" role="img" aria-label="Mensagem da Aegis não lida"></span>
          </button>
          <ConversationMenu :title="getTitle(conversation)" :disabled="disabled" @info="emit('infoConversation', conversation)" @rename="emit('renameConversation', conversation)" @delete="emit('requestDeleteConversation', conversation)" />
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

    <nav v-if="$slots['global-navigation']" class="sidebar-global-navigation" aria-label="Aegis"><slot name="global-navigation" /></nav>
  </aside>
</template>
