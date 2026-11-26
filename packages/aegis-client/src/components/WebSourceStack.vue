<script setup lang="ts">
import WebSourceIcon from './WebSourceIcon.vue';
import type { ResearchSite } from '../services/researchSites';

defineProps<{ sites: readonly ResearchSite[]; compact?: boolean; animate?: boolean }>();
</script>

<template>
  <TransitionGroup name="research-site" tag="span" :appear="animate" :css="animate"
    class="web-source-stack" :class="{ 'web-source-stack--compact': compact }" aria-hidden="true">
    <span v-for="site in sites" :key="site.host" class="web-source-stack__disk" :data-site="site.host">
      <WebSourceIcon :host="site.host" :label="site.name" />
    </span>
  </TransitionGroup>
</template>

<style scoped>
.web-source-stack {
  --source-disk: 30px;
  --source-overlap: 9px;
  display: inline-flex;
  align-items: center;
  position: relative;
  isolation: isolate;
  padding-inline-end: 2px;
  line-height: 1;
}
.web-source-stack--compact { --source-disk: 24px; --source-overlap: 7px; }
.web-source-stack__disk {
  position: relative;
  display: inline-grid;
  place-items: center;
  flex: 0 0 var(--source-disk);
  width: var(--source-disk);
  height: var(--source-disk);
  border: 2px solid var(--canvas);
  border-radius: 50%;
  background: var(--surface-raised);
  box-shadow: inset 0 0 0 1px var(--line-strong);
  font-size: 16px;
}
.web-source-stack--compact .web-source-stack__disk { font-size: 13px; }
.web-source-stack__disk + .web-source-stack__disk { margin-inline-start: calc(-1 * var(--source-overlap)); }
.research-site-enter-active { transition: transform 260ms cubic-bezier(.16,.8,.28,1), opacity 180ms ease-out; }
.research-site-enter-from { opacity: 0; transform: translate(11px, 5px) scale(.72); }
.research-site-move { transition: transform 220ms cubic-bezier(.16,.8,.28,1); }
.research-site-leave-active { position: absolute; z-index: -1; transition: transform 140ms ease-in, opacity 140ms ease-in; }
.research-site-leave-to { opacity: 0; transform: translate(-7px, 3px) scale(.82); }
@media (prefers-reduced-motion: reduce) {
  .research-site-enter-active, .research-site-leave-active, .research-site-move { transition: none; }
}
</style>
