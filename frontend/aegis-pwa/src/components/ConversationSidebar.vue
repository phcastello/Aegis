<script setup lang="ts">
import AegisIdentityCard from './AegisIdentityCard.vue';

defineProps<{
  currentTitle: string;
  hasConversation: boolean;
  disabled?: boolean;
  open?: boolean;
}>();

const emit = defineEmits<{
  close: [];
  newConversation: [];
}>();
</script>

<template>
  <div class="sidebar-backdrop" :class="{ 'sidebar-backdrop--visible': open }" @click="emit('close')"></div>

  <aside class="conversation-sidebar" :class="{ 'conversation-sidebar--open': open }">
    <div class="sidebar-mobile-header">
      <span>Seu refúgio</span>
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
        <span class="history-count">{{ hasConversation ? '1 ativa' : 'Vazio' }}</span>
      </header>

      <button v-if="hasConversation" type="button" class="history-item history-item--active" @click="emit('close')">
        <span class="history-item__state">
          <i></i>
          Conversa ativa
        </span>
        <strong>{{ currentTitle }}</strong>
        <small>Agora</small>
      </button>

      <div v-else class="history-empty">
        <span class="history-empty__line"></span>
        <p>Suas conversas começam por aqui.</p>
      </div>
    </section>

    <p class="sidebar-footer">Presente, discreta e pronta para continuar.</p>
  </aside>
</template>
