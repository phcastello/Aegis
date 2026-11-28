<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, useId } from 'vue';
import { attachmentLinkUrl } from '../services/chatAttachments';
import { navigationLayer } from '../services/navigation';

const emit = defineEmits<{ files: [files: File[]]; link: [url: string] }>();
const menuId = useId();
const titleId = useId();
const trigger = ref<HTMLButtonElement | null>(null);
const picker = ref<HTMLInputElement | null>(null);
const linkDialog = ref<HTMLDialogElement | null>(null);
const menuOpen = ref(false);
const linkOpen = ref(false);
const url = ref('');
const error = ref('');
let dismissLayer: (() => void) | undefined;

function selectFiles(event: Event): void {
  const input = event.target as HTMLInputElement;
  if (input.files?.length) emit('files', Array.from(input.files));
  input.value = '';
}

function openFilePicker(): void {
  menuOpen.value = false;
  picker.value?.click();
}

function closeMenu(event: FocusEvent): void {
  if (event.relatedTarget instanceof Node && (event.currentTarget as HTMLElement).contains(event.relatedTarget)) return;
  menuOpen.value = false;
}

async function openLinkDialog(): Promise<void> {
  menuOpen.value = false;
  linkOpen.value = true;
  url.value = '';
  error.value = '';
  await nextTick();
  if (!linkDialog.value) return;
  linkDialog.value.showModal();
  dismissLayer = navigationLayer(closeLinkDialog);
}

function closeLinkDialog(): void {
  dismissLayer?.();
  dismissLayer = undefined;
  linkDialog.value?.close();
  linkOpen.value = false;
  trigger.value?.focus({ preventScroll: true });
}

function attachLink(): void {
  const link = attachmentLinkUrl(url.value);
  if (!link) {
    error.value = 'Informe um endereço HTTP ou HTTPS válido, sem credenciais.';
    return;
  }
  emit('link', link.href);
  closeLinkDialog();
}

onBeforeUnmount(() => dismissLayer?.());
</script>

<template>
  <div class="attachment-picker" @focusout="closeMenu" @keydown.esc.stop="menuOpen = false; trigger?.focus()">
    <input ref="picker" type="file" multiple hidden aria-label="Selecionar arquivos" @change="selectFiles" />
    <button ref="trigger" type="button" class="attachment-button" title="Adicionar anexo" aria-label="Adicionar anexo"
      :aria-expanded="menuOpen" :aria-controls="menuOpen ? menuId : undefined" @click="menuOpen = !menuOpen">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4v16M4 12h16" /></svg>
    </button>
    <div v-if="menuOpen" :id="menuId" class="attachment-picker__options">
      <button type="button" class="ghost-button" @click="openFilePicker">Anexar arquivos</button>
      <button type="button" class="ghost-button" @click="openLinkDialog">Anexar link</button>
    </div>
  </div>
  <Teleport to="body">
    <dialog v-if="linkOpen" ref="linkDialog" class="conversation-dialog" :aria-labelledby="titleId"
      @cancel.prevent="closeLinkDialog">
      <form novalidate @submit.prevent="attachLink">
        <h2 :id="titleId">Anexar link</h2>
        <label>Endereço
          <input v-model="url" type="url" inputmode="url" autocomplete="url" placeholder="https://" maxlength="2048" autofocus />
        </label>
        <p v-if="error" role="alert">{{ error }}</p>
        <div class="attachment-picker__actions">
          <button type="button" class="ghost-button" @click="closeLinkDialog">Cancelar</button>
          <button type="submit" class="ghost-button">Anexar</button>
        </div>
      </form>
    </dialog>
  </Teleport>
</template>

<style scoped>
.attachment-picker { position: relative; flex: 0 0 auto; }
.attachment-picker__options {
  position: absolute;
  inset: auto auto calc(100% + 6px) 0;
  z-index: 2;
  display: grid;
  min-width: max-content;
  padding: 4px;
  border: 1px solid var(--line);
  border-radius: 12px;
  background: var(--surface);
  box-shadow: 0 8px 24px #0003;
}
.attachment-picker__options button { min-height: 44px; text-align: start; }
.attachment-picker__actions { display: flex; flex-wrap: wrap; justify-content: end; gap: 8px; }
</style>
