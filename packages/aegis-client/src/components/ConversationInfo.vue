<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { navigationLayer } from '../services/navigation';
const props = defineProps<{ conversationId: string; createdAt?: string | null }>();
const emit = defineEmits<{ close: [] }>();
const dialog = ref<HTMLDialogElement | null>(null);
const feedback = ref('');
let release: (() => void) | undefined;
let previousFocus: HTMLElement | null = null;
async function copyId() { try { await navigator.clipboard.writeText(props.conversationId); feedback.value = 'Copiado'; } catch { feedback.value = 'Não foi possível copiar'; } }
onMounted(() => { previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null; dialog.value?.showModal(); release = navigationLayer(() => emit('close')); });
onBeforeUnmount(() => { release?.(); dialog.value?.close(); previousFocus?.focus({ preventScroll: true }); });
</script>
<template>
  <dialog ref="dialog" class="conversation-dialog" aria-labelledby="conversation-info-label" @cancel.prevent="emit('close')">
    <h2 id="conversation-info-label">Informações da conversa</h2>
    <p>ID da conversa</p><code class="conversation-real-id">{{ conversationId }}</code>
    <button type="button" class="conversation-info__copy" @click="copyId">Copiar ID</button><span role="status">{{ feedback }}</span>
    <p v-if="createdAt">Criada em {{ new Date(createdAt).toLocaleString('pt-BR') }}</p>
    <div class="confirm-dialog__actions"><button type="button" class="ghost-button" @click="emit('close')">Fechar</button></div>
  </dialog>
</template>
