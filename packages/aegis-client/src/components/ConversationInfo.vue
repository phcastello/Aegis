<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { navigationLayer } from '../services/navigation';
const props = defineProps<{ conversationId: string; createdAt?: string | null }>();
const emit = defineEmits<{ close: [] }>();
const dialog = ref<HTMLDialogElement | null>(null);
const feedback = ref('');
const copyFailed = ref(false);
const isCopying = ref(false);
let release: (() => void) | undefined;
let previousFocus: HTMLElement | null = null;
async function copyId(): Promise<void> {
  if (isCopying.value) return;
  isCopying.value = true;
  feedback.value = '';
  copyFailed.value = false;
  try {
    await navigator.clipboard.writeText(props.conversationId);
    feedback.value = 'Copiado';
  } catch {
    copyFailed.value = true;
    feedback.value = 'Não foi possível copiar. Selecione o ID e copie manualmente.';
  } finally {
    isCopying.value = false;
  }
}
onMounted(() => { previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null; dialog.value?.showModal(); release = navigationLayer(() => emit('close')); });
onBeforeUnmount(() => { release?.(); dialog.value?.close(); previousFocus?.focus({ preventScroll: true }); });
</script>
<template>
  <dialog ref="dialog" class="conversation-dialog" aria-labelledby="conversation-info-label" @cancel.prevent="emit('close')">
    <h2 id="conversation-info-label">Informações da conversa</h2>
    <p id="conversation-id-label" class="conversation-info__label">ID da conversa</p>
    <div class="conversation-info__id-box" role="group" aria-labelledby="conversation-id-label">
      <code class="conversation-real-id" :title="conversationId">{{ conversationId }}</code>
      <button type="button" class="conversation-info__copy" aria-label="Copiar ID" :disabled="isCopying" @click="copyId">
        <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="8" y="8" width="12" height="12" rx="2" /><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3" /></svg>
      </button>
    </div>
    <p class="conversation-info__feedback" :class="{ 'conversation-info__feedback--error': copyFailed }" role="status" aria-live="polite" aria-atomic="true">{{ feedback }}</p>
    <p v-if="createdAt" class="conversation-info__created">Criada em {{ new Date(createdAt).toLocaleString('pt-BR') }}</p>
    <div class="confirm-dialog__actions"><button type="button" class="ghost-button" @click="emit('close')">Fechar</button></div>
  </dialog>
</template>
