<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { dismissKeyboard, navigationLayer } from '../services/navigation';
const props = defineProps<{ title: string; showTitle?: boolean; disabled?: boolean }>();
const emit = defineEmits<{ info: []; rename: []; delete: [] }>();
const root = ref<HTMLElement | null>(null);
const trigger = ref<HTMLButtonElement | null>(null);
const open = ref(false);
const menu = ref<HTMLElement | null>(null);
const position = ref({ top: '0px', left: '0px' });
let release: (() => void) | undefined;
function close(restore = true) { open.value = false; release?.(); release = undefined; if (restore) trigger.value?.focus({ preventScroll: true }); }
async function toggle() {
  if (open.value) return close();
  dismissKeyboard(); open.value = true;
  release = navigationLayer(() => close());
  await nextTick();
  const bounds = trigger.value?.getBoundingClientRect();
  if (bounds && menu.value) {
    const width = menu.value.offsetWidth, height = menu.value.offsetHeight;
    position.value = { left: `${Math.max(8, Math.min(bounds.right - width, window.innerWidth - width - 8))}px`, top: `${Math.max(8, bounds.bottom + height + 8 <= window.innerHeight ? bounds.bottom + 6 : bounds.top - height - 6)}px` };
  }
  menu.value?.querySelector<HTMLButtonElement>('[role="menuitem"]')?.focus({ preventScroll: true });
}
function action(value: 'info' | 'rename' | 'delete') { close(); if (value === 'info') emit('info'); else if (value === 'rename') emit('rename'); else emit('delete'); }
function outside(event: PointerEvent) { if (event.target instanceof Node && !root.value?.contains(event.target) && !menu.value?.contains(event.target)) close(false); }
function focusout(event: FocusEvent) { if (open.value && event.relatedTarget instanceof Node && !root.value?.contains(event.relatedTarget) && !menu.value?.contains(event.relatedTarget)) close(false); }
function keydown(event: KeyboardEvent) {
  if (!open.value) return;
  if (event.key === 'Escape') { event.preventDefault(); close(); }
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault();
    const items = Array.from(menu.value?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? []);
    const index = items.indexOf(document.activeElement as HTMLButtonElement);
    items[(index + (event.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus();
  }
}
watch(() => props.title, () => close(false));
onMounted(() => document.addEventListener('pointerdown', outside));
onBeforeUnmount(() => { release?.(); document.removeEventListener('pointerdown', outside); });
</script>
<template>
  <div ref="root" class="conversation-actions" @keydown="keydown" @focusout="focusout">
    <button ref="trigger" type="button" :class="showTitle ? 'conversation-menu-trigger' : 'history-menu-button'" :aria-label="showTitle ? 'Menu da conversa' : `Ações de ${title}`" aria-haspopup="menu" :aria-expanded="open" :disabled="disabled" @click.stop="toggle"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12h.01M12 12h.01M19 12h.01" /></svg></button>
    <Teleport to="body"><div v-if="open" ref="menu" :style="position" class="history-menu conversation-actions__menu" role="menu" @keydown="keydown" @focusout="focusout" :aria-label="`Ações de ${title}`">
      <strong v-if="showTitle" class="conversation-actions__title">{{ title }}</strong>
      <button type="button" role="menuitem" @click="action('info')">Informações</button>
      <button type="button" role="menuitem" @click="action('rename')">Renomear</button>
      <button type="button" role="menuitem" class="history-menu__delete" @click="action('delete')">Excluir</button>
    </div></Teleport>
  </div>
</template>
