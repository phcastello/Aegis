<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';

const props = defineProps<{ conversationId: string }>();
const root = ref<HTMLElement | null>(null);
const trigger = ref<HTMLButtonElement | null>(null);
const copyButton = ref<HTMLButtonElement | null>(null);
const open = ref(false);
const copyFeedback = ref('');

function close(restoreFocus = false): void {
  open.value = false;
  copyFeedback.value = '';
  if (restoreFocus) trigger.value?.focus({ preventScroll: true });
}

function toggle(): void {
  if (open.value) { close(); return; }
  open.value = true;
  void nextTick(() => copyButton.value?.focus({ preventScroll: true }));
}

async function copyId(): Promise<void> {
  const id = props.conversationId;
  try {
    await navigator.clipboard.writeText(id);
    if (open.value && id === props.conversationId) copyFeedback.value = 'Copiado';
  } catch {
    if (open.value && id === props.conversationId) copyFeedback.value = 'Não foi possível copiar';
  }
}

function outside(event: PointerEvent): void {
  if (open.value && event.target instanceof Node && !root.value?.contains(event.target)) close();
}

function keydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && open.value) {
    event.preventDefault();
    close(true);
  }
}

function focusout(event: FocusEvent): void {
  if (!(event.relatedTarget instanceof Node) || !root.value?.contains(event.relatedTarget)) close();
}

watch(() => props.conversationId, () => close());
onMounted(() => {
  document.addEventListener('pointerdown', outside);
  document.addEventListener('keydown', keydown);
});
onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', outside);
  document.removeEventListener('keydown', keydown);
});
</script>

<template>
  <div ref="root" class="conversation-info" @focusout="focusout">
    <button
      ref="trigger"
      type="button"
      class="conversation-info__trigger"
      aria-label="Informações da conversa"
      aria-controls="conversation-info-panel"
      aria-haspopup="dialog"
      :aria-expanded="open"
      @click="toggle"
    ><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9" /><path d="M12 11v6M12 7h.01" /></svg></button>
    <div v-if="open" id="conversation-info-panel" class="conversation-info__panel" role="dialog" aria-labelledby="conversation-info-label">
      <span id="conversation-info-label">ID da conversa</span>
      <code>{{ conversationId }}</code>
      <button ref="copyButton" type="button" class="conversation-info__copy" @click="copyId">Copiar ID</button>
      <span class="conversation-info__feedback" role="status">{{ copyFeedback }}</span>
    </div>
  </div>
</template>
