<script setup lang="ts">
import { computed, ref } from 'vue';
import { researchFavicon } from '../services/researchSites';

const props = defineProps<{ host: string; label?: string }>();
const failed = ref<string>();
const icon = computed(() => failed.value === props.host ? undefined : researchFavicon(props.host));
const initial = computed(() => Array.from((props.label || props.host).trim())[0]?.toLocaleUpperCase('pt-BR') || '·');
</script>

<template>
  <span class="web-source-icon" aria-hidden="true">
    <img v-if="icon" :key="icon" :src="icon" alt="" referrerpolicy="no-referrer" decoding="async"
      @error="failed = host" />
    <span v-else>{{ initial }}</span>
  </span>
</template>

<style scoped>
.web-source-icon {
  display: inline-grid;
  width: 1em;
  height: 1em;
  flex: 0 0 auto;
  place-items: center;
  color: var(--accent-strong);
  vertical-align: middle;
}
.web-source-icon img { width: 100%; height: 100%; object-fit: contain; }
.web-source-icon > span { font-size: 0.70em; font-weight: 750; line-height: 1; }
</style>
