<script setup lang="ts">
import { computed } from 'vue';
import type { MemoryActivitySnapshot } from '../types/chat';
import { memoryActivityLabels, memoryActivityTitle, orderedMemorySections,
  remainingMemoryItems, visibleMemoryItems } from '../services/memoryActivityPresentation';

const props = defineProps<{ activity: MemoryActivitySnapshot }>();
const sections = computed(() => orderedMemorySections(props.activity.sections));
const title = computed(() => memoryActivityTitle(sections.value));
</script>

<template>
  <details v-if="sections.length" class="memory-activity">
    <summary class="memory-activity__summary">
      <span>{{ title }}</span>
      <svg aria-hidden="true" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6" /></svg>
    </summary>
    <div class="memory-activity__details">
      <section v-for="section in sections" :key="section.kind" class="memory-activity__section"
        :aria-label="memoryActivityLabels[section.kind].section">
        <div class="memory-activity__label">{{ memoryActivityLabels[section.kind].section }}</div>
        <ul>
          <li v-for="item in visibleMemoryItems(section.items)" :key="item">{{ item }}</li>
        </ul>
        <div v-if="remainingMemoryItems(section.totalCount, section.items)" class="memory-activity__more">
          e mais {{ remainingMemoryItems(section.totalCount, section.items) }}
        </div>
      </section>
    </div>
  </details>
</template>
