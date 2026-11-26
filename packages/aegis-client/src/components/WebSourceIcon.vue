<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { isUsableResearchFavicon, researchFavicon } from '../services/researchSites';

const props = defineProps<{ host: string; label?: string }>();
const icon = computed(() => researchFavicon(props.host));
const imageElement = ref<HTMLImageElement | null>(null);
const ready = ref(false);
const initial = computed(() => Array.from((props.label || props.host).trim())[0]?.toLocaleUpperCase('pt-BR') || '·');

function updateIcon(): void {
  ready.value = imageElement.value !== null && isUsableResearchFavicon(imageElement.value);
}

watch(imageElement, updateIcon, { flush: 'post' });
</script>

<template>
  <span class="web-source-icon" :class="{ 'web-source-icon--ready': ready }" :data-monogram="initial" aria-hidden="true">
    <img v-if="icon" :key="icon" ref="imageElement" :src="icon" alt="" referrerpolicy="no-referrer" decoding="async"
      @load="updateIcon" @error="updateIcon" />
  </span>
</template>

<style scoped>
.web-source-icon {
  position: relative;
  display: inline-grid;
  width: 1em;
  height: 1em;
  flex: 0 0 auto;
  place-items: center;
  color: var(--accent-strong);
  vertical-align: middle;
}
.web-source-icon img {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
  visibility: hidden;
}
.web-source-icon::before {
  content: attr(data-monogram);
  font-size: 0.70em;
  font-weight: 750;
  line-height: 1.25;
}
.web-source-icon--ready img { visibility: visible; }
.web-source-icon--ready::before { visibility: hidden; }
</style>
