<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue';
export type SettingsSection = 'devices' | 'reminders' | 'places' | 'updates' | 'about';
const props = defineProps<{ section: SettingsSection | null; dirty?: boolean }>();
const emit = defineEmits<{ close: []; navigate: [section: SettingsSection]; discard: [] }>();
const dialog = ref<HTMLDialogElement | null>(null);
const pending = ref<SettingsSection | 'close' | null>(null);
const sections: { id: SettingsSection; label: string }[] = [
  { id: 'devices', label: 'Dispositivos' }, { id: 'reminders', label: 'Lembretes' },
  { id: 'places', label: 'Lugares' }, { id: 'updates', label: 'Atualizações' }, { id: 'about', label: 'Sobre' }
];
let previousFocus: HTMLElement | null = null;
let confirmationFocus: HTMLElement | null = null;
function navigate(section: SettingsSection) {
  if (section === props.section) return;
  if (props.dirty) { pending.value = section; return; }
  emit('navigate', section);
}
function requestClose() {
  if (pending.value) { pending.value = null; return; }
  if (props.dirty) { pending.value = 'close'; return; }
  emit('close');
}
function discard() {
  const destination = pending.value;
  pending.value = null;
  emit('discard');
  if (destination === 'close') emit('close'); else if (destination) emit('navigate', destination);
}
function keepFocus(event: KeyboardEvent) {
  if (event.key !== 'Tab' || !dialog.value?.open) return;
  const controls = Array.from(dialog.value.querySelectorAll<HTMLElement>('button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'))
    .filter(element => !element.closest('[inert]') && element.getClientRects().length > 0);
  const first = controls[0], last = controls.at(-1);
  if (!first || !last) { event.preventDefault(); dialog.value.focus(); return; }
  if (event.shiftKey && (document.activeElement === first || !controls.includes(document.activeElement as HTMLElement))) { event.preventDefault(); last.focus(); }
  else if (!event.shiftKey && (document.activeElement === last || !controls.includes(document.activeElement as HTMLElement))) { event.preventDefault(); first.focus(); }
}
function backdrop(event: MouseEvent) {
  const element = dialog.value;
  if (!element || event.target !== element) return;
  const bounds = element.getBoundingClientRect();
  if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) requestClose();
}
watch(pending, async (value, before) => {
  if (value && !before) { confirmationFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null; await nextTick(); dialog.value?.querySelector<HTMLButtonElement>('.settings-confirm button')?.focus(); }
  else if (!value && before) { await nextTick(); confirmationFocus?.focus({preventScroll:true}); }
});
watch(() => props.section, async (section, before) => {
  await nextTick();
  if (section && !before) {
    previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    dialog.value?.showModal();
  } else if (!section && before) {
    dialog.value?.close();
    previousFocus?.focus({ preventScroll: true });
    pending.value = null;
  }
}, { immediate: true });
onBeforeUnmount(() => { dialog.value?.close(); previousFocus?.focus({ preventScroll: true }); });
defineExpose({ requestClose, navigate });
</script>
<template>
  <dialog ref="dialog" class="settings-dialog" aria-labelledby="settings-title" @cancel.prevent="requestClose" @click="backdrop" @keydown="keepFocus">
    <header class="settings-header" :inert="pending !== null"><div><span class="settings-eyebrow">Aegis</span><h1 id="settings-title">Configurações</h1></div><button class="settings-icon-button" type="button" aria-label="Fechar configurações" @click="requestClose"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M6 18 18 6" /></svg></button></header>
    <div class="settings-layout" :inert="pending !== null">
      <nav class="settings-navigation" aria-label="Seções de configurações"><button v-for="item in sections" :key="item.id" type="button" :aria-current="section === item.id ? 'page' : undefined" @click="navigate(item.id)">{{ item.label }}</button></nav>
      <div class="settings-content"><slot /></div>
    </div>
    <div v-if="pending" class="settings-confirm-backdrop">
      <section class="settings-confirm" role="alertdialog" aria-modal="true" aria-labelledby="discard-title"><h2 id="discard-title">Descartar alterações?</h2><p>Há um formulário que ainda não foi salvo.</p><div class="settings-actions"><button type="button" autofocus @click="pending = null">Continuar editando</button><button type="button" class="settings-danger" @click="discard">Descartar alterações</button></div></section>
    </div>
  </dialog>
</template>
