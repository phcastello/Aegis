<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';

const props = defineProps<{ title: string; source?: string | null }>();
const displayed = ref(props.title);
const animating = ref(false);
let timer: ReturnType<typeof setTimeout> | undefined;
let motion: MediaQueryList | undefined;

function finish(): void {
  clearTimeout(timer);
  timer = undefined;
  displayed.value = props.title;
  animating.value = false;
}

watch(() => [props.title, props.source] as const, ([title, source], [previous]) => {
  finish();
  // A title already present on mount is history. Only this observed automatic
  // transition may animate; manual renames and subsequent refreshes never do.
  if (previous !== 'Nova conversa' || title === previous || source !== 'generated' ||
      motion?.matches || document.hidden) return;
  const oldCharacters = Array.from(previous);
  const newCharacters = Array.from(title);
  displayed.value = previous;
  animating.value = true;
  const erase = (): void => {
    oldCharacters.pop();
    displayed.value = oldCharacters.join('');
    if (oldCharacters.length) timer = setTimeout(erase, 25);
    else timer = setTimeout(write, 55);
  };
  let written = 0;
  const write = (): void => {
    displayed.value = newCharacters.slice(0, ++written).join('');
    if (written < newCharacters.length) timer = setTimeout(write, 55);
    else finish();
  };
  timer = setTimeout(erase, 25);
});

function visibilityChanged(): void { if (document.hidden) finish(); }
function motionChanged(): void { if (motion?.matches) finish(); }
onMounted(() => {
  motion = window.matchMedia('(prefers-reduced-motion: reduce)');
  motion.addEventListener('change', motionChanged);
  document.addEventListener('visibilitychange', visibilityChanged);
});
onBeforeUnmount(() => {
  clearTimeout(timer);
  motion?.removeEventListener('change', motionChanged);
  document.removeEventListener('visibilitychange', visibilityChanged);
});
</script>

<template>
  <strong :aria-label="title" :title="title" :data-title-animating="animating || undefined">
    <span aria-hidden="true">{{ displayed }}</span><i v-if="animating" class="title-writing-dot" aria-hidden="true"></i>
  </strong>
</template>
