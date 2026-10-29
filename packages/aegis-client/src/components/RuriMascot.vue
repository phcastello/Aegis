<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import spritesheet from '../assets/ruri-spritesheet.png';
import wavingSpritesheet from '../assets/ruri-waving-consistent.png';

// Idle keeps the approved canonical sequence. The derived wave uses idle0's
// unchanged body plus a local arm layer; it never uses the old waving bodies.
const idleFrames = [0, 1, 3, 4, 5];
const idleDurations = [1800, 500, 500, 500, 1700];
const wavingFrames = [0, 1, 2, 3, 4, 5, 6, 7, 6, 5, 4, 3, 2, 1, 0];
const wavingDurations = [350, 120, 120, 120, 120, 180, 220, 220, 220, 180, 120, 120, 120, 120, 670];
const element = ref<HTMLElement | null>(null);
const state = ref<'idle' | 'waving'>('idle');
const step = ref(0);
const frame = computed(() => (state.value === 'waving' ? wavingFrames : idleFrames)[step.value]!);
let timer: ReturnType<typeof setTimeout> | undefined;
let motion: MediaQueryList | undefined;
let observer: IntersectionObserver | undefined;
let visible = true;
let ready = false;
let disposed = false;
let idleCycles = 0;

const spriteStyle = computed(() => ({
  backgroundImage: `url(${state.value === 'waving' ? wavingSpritesheet : spritesheet})`,
  backgroundSize: `1152px ${state.value === 'waving' ? 156 : 1716}px`,
  backgroundPosition: `${frame.value * -144}px 0px`
}));

function schedule(): void {
  clearTimeout(timer);
  timer = undefined;
  if (disposed || !ready || !visible || document.hidden || motion?.matches) return;
  const durations = state.value === 'waving' ? wavingDurations : idleDurations;
  timer = setTimeout(() => {
    if (step.value + 1 < durations.length) step.value++;
    else {
      if (state.value === 'waving') {
        state.value = 'idle';
        idleCycles = 0;
      } else if (++idleCycles === 2) {
        state.value = 'waving';
      }
      step.value = 0;
    }
    schedule();
  }, durations[step.value]);
}

function visibilityChanged(): void { schedule(); }
function motionChanged(): void {
  if (motion?.matches) { state.value = 'idle'; step.value = 0; idleCycles = 0; }
  schedule();
}

onMounted(() => {
  motion = window.matchMedia('(prefers-reduced-motion: reduce)');
  motion.addEventListener('change', motionChanged);
  document.addEventListener('visibilitychange', visibilityChanged);
  // Start only after both atlases decode, including on a cold mobile fetch.
  // A missing derivative leaves the canonical neutral pose static.
  const atlases = [spritesheet, wavingSpritesheet].map(src => {
    const image = new Image();
    image.src = src;
    return image.decode();
  });
  void Promise.all(atlases).then(() => {
    if (disposed) return;
    ready = true;
    state.value = motion?.matches ? 'idle' : 'waving';
    step.value = 0;
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
  image-rendering: pixelated;
}
</style>
