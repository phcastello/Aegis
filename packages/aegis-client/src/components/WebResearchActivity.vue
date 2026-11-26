<script setup lang="ts">
import WebSourceIcon from './WebSourceIcon.vue';
import type { ResearchSite } from '../services/researchSites';

defineProps<{ sites: readonly ResearchSite[]; state: 'working' | 'completed' | 'failed'; label: string }>();
</script>

<template>
  <span class="web-research-activity" :class="'web-research-activity--' + state" role="status" :aria-label="label" :title="label">
    <TransitionGroup v-if="sites.length && state !== 'failed'" name="research-site" tag="span" appear
      class="web-research-activity__stack" aria-hidden="true">
      <span v-for="site in sites" :key="site.host" class="web-research-activity__disk" :data-site="site.host">
        <WebSourceIcon :host="site.host" :label="site.name" />
      </span>
    </TransitionGroup>
    <span v-else class="web-research-activity__disk web-research-activity__signal" aria-hidden="true">
      <svg viewBox="0 0 24 24">
        <path v-if="state === 'completed'" d="m5 12 4 4 10-10" />
        <path v-else-if="state === 'failed'" d="M12 6v7m0 4h.01" />
        <template v-else><circle cx="10.7" cy="10.7" r="4.7"/><path d="m14.1 14.1 4.1 4.1"/></template>
      </svg>
    </span>
  </span>
</template>

<style scoped>
.web-research-activity {
  display: inline-flex;
  align-items: center;
  min-height: 34px;
  line-height: 1;
  vertical-align: middle;
}
.web-research-activity__stack { display: inline-flex; align-items: center; position: relative; isolation: isolate; padding-inline-end: 2px; }
.web-research-activity__disk {
  position: relative;
  display: inline-grid;
  place-items: center;
  flex: 0 0 30px;
  width: 30px;
  height: 30px;
  border-radius: 50%;
  font-size: 16px;
  border: 2px solid var(--canvas);
  background: var(--surface-raised);
  box-shadow: inset 0 0 0 1px var(--line-strong);
}
.web-research-activity__disk + .web-research-activity__disk { margin-inline-start: -9px; }
.web-research-activity__signal { color: var(--accent-strong); }
.web-research-activity--working .web-research-activity__signal { animation: research-pulse 1.5s ease-in-out infinite; }
.web-research-activity--completed .web-research-activity__signal { color: #b8e7cf; }
.web-research-activity--failed .web-research-activity__signal { color: var(--warn); }
.web-research-activity__signal svg { width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-width: 1.9; stroke-linecap: round; stroke-linejoin: round; }
.research-site-enter-active { transition: transform 260ms cubic-bezier(.16,.8,.28,1), opacity 180ms ease-out; }
.research-site-enter-from { opacity: 0; transform: translate(11px, 5px) scale(.72); }
.research-site-move { transition: transform 220ms cubic-bezier(.16,.8,.28,1); }
.research-site-leave-active { position: absolute; z-index: -1; transition: transform 140ms ease-in, opacity 140ms ease-in; }
.research-site-leave-to { opacity: 0; transform: translate(-7px, 3px) scale(.82); }
@keyframes research-pulse { 50% { box-shadow: inset 0 0 0 1px var(--accent), 0 0 0 3px rgba(145, 169, 238, .10); } }
@media (prefers-reduced-motion: reduce) {
  .web-research-activity--working .web-research-activity__signal { animation: none; }
  .research-site-enter-active, .research-site-leave-active, .research-site-move { transition: none; }
}
</style>
