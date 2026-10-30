<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { dismissKeyboard, navigationLayer } from '../services/navigation';
const props = defineProps<{ title: string; saving: boolean; error: string | null }>();
const emit = defineEmits<{ cancel: []; save: [title: string] }>();
const dialog = ref<HTMLDialogElement | null>(null);
const input = ref<HTMLInputElement | null>(null);
const title = ref(props.title);
const valid = computed(() => !!title.value.trim() && title.value.trim().length <= 80 && title.value.trim() !== props.title.trim());
let release: (() => void) | undefined;
let disposed = false;
let previousFocus: HTMLElement | null = null;
function cancel() { if (!props.saving) emit('cancel'); }
function save() { if (valid.value && !props.saving) emit('save', title.value.trim()); }
onMounted(async () => {
  previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  await dismissKeyboard(); if (disposed) return; dialog.value?.showModal(); release = navigationLayer(cancel);
  await nextTick(); input.value?.focus({ preventScroll: true }); input.value?.select();
});
onBeforeUnmount(() => { disposed = true; release?.(); dismissKeyboard(); dialog.value?.close(); if (previousFocus?.isConnected && !previousFocus.closest('[inert]')) previousFocus.focus({ preventScroll: true }); });
</script>
<template>
  <dialog ref="dialog" class="conversation-dialog" aria-labelledby="rename-title" @cancel.prevent="cancel">
    <form @submit.prevent="save">
      <h2 id="rename-title">Renomear conversa</h2>
      <label for="conversation-title">Título</label>
      <input id="conversation-title" ref="input" v-model="title" maxlength="80" autocomplete="off" :disabled="saving" />
      <p v-if="error" class="composer-error" role="alert">{{ error }}</p>
      <div class="confirm-dialog__actions"><button type="button" class="ghost-button" :disabled="saving" @click="cancel">Cancelar</button><button type="submit" :disabled="!valid || saving">{{ saving ? 'Salvando…' : 'Salvar' }}</button></div>
    </form>
  </dialog>
</template>
