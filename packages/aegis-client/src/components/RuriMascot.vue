<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import spritesheet from '../assets/ruri-spritesheet.png';

// Canonical v2 atlas: eight 192×208 cells per row. The selected sequences and
// timings are supplied by Ruri's package; no image or animation is generated.
const idleDurations = [280, 110, 110, 140, 140, 320];
const wavingDurations = [140, 140, 140, 280];
const element = ref<HTMLElement | null>(null);
const state = ref<'idle' | 'waving'>('idle');
const frame = ref(0);
let timer: ReturnType<typeof setTimeout> | undefined;
let motion: MediaQueryList | undefined;
let observer: IntersectionObserver | undefined;
let visible = true;
let ready = false;
let disposed = false;
let greeted = false;

const spriteStyle = computed(() => ({
  backgroundImage: `url(${spritesheet})`,
  backgroundPosition: `${frame.value * -144}px ${state.value === 'waving' ? -468 : 0}px`
}));

function schedule(): void {
  clearTimeout(timer);
  timer = undefined;
  if (!ready || !visible || document.hidden || motion?.matches) return;
  const durations = state.value === 'waving' ? wavingDurations : idleDurations;
  timer = setTimeout(() => {
    if (frame.value + 1 < durations.length) frame.value++;
    else {
      if (state.value === 'waving') state.value = 'idle';
      frame.value = 0;
    }
    schedule();
  }, durations[frame.value]);
}

function visibilityChanged(): void { schedule(); }
function motionChanged(): void {
  if (motion?.matches) { state.value = 'idle'; frame.value = 0; }
  schedule();
}

onMounted(() => {
  motion = window.matchMedia('(prefers-reduced-motion: reduce)');
  motion.addEventListener('change', motionChanged);
  document.addEventListener('visibilitychange', visibilityChanged);
  const atlas = new Image();
  atlas.src = spritesheet;
  // A cold mobile fetch can outlast the greeting itself. Start the sequence only
  // once these exact pixels are decoded, and ignore a late load after unmount.
  void atlas.decode().then(() => {
    if (disposed) return;
    ready = true;
    if (!greeted) {
      greeted = true;
      state.value = motion?.matches ? 'idle' : 'waving';
      frame.value = 0;
    }
    schedule();
  }).catch(() => { /* A missing image must not start a recurring timer. */ });
  observer = new IntersectionObserver(entries => {
    visible = entries.some(entry => entry.isIntersecting);
    schedule();
  });
  if (element.value) observer.observe(element.value);
  schedule();
});

onBeforeUnmount(() => {
  disposed = true;
  clearTimeout(timer);
  observer?.disconnect();
  motion?.removeEventListener('change', motionChanged);
  document.removeEventListener('visibilitychange', visibilityChanged);
});
</script>

<template>
  <div ref="element" class="ruri-mascot" role="img" aria-label="Ruri, mascote da Aegis"
    :data-state="state" :data-frame="frame" :style="spriteStyle"></div>
</template>

<style scoped>
.ruri-mascot {
  width: 144px;
  height: 156px;
  background-repeat: no-repeat;
  background-size: 1152px 1716px;
  image-rendering: pixelated;
}
</style>
