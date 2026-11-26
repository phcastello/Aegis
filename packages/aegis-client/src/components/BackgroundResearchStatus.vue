<script setup lang="ts">
import { computed } from 'vue';
import WebResearchActivity from './WebResearchActivity.vue';
import { isResearchPending, type ResearchJob } from '../services/backgroundResearch';
import { mergeResearchSites, topResearchSites } from '../services/researchSites';
import { parseWebResearchSourceCounts } from '../services/streamTextPresentation';

const props = defineProps<{ job?: ResearchJob; cancelling: ReadonlySet<string> }>();
const emit = defineEmits<{ cancel: [job: ResearchJob] }>();
const sites = computed(() => topResearchSites(mergeResearchSites(new Map(), parseWebResearchSourceCounts(props.job?.sources, 128))));
const statusLabel = computed(() => {
  const job = props.job;
  if (job?.state === 'completed') return 'Pesquisa concluída';
  if (job?.state === 'failed') return 'Pesquisa interrompida';
  if (job?.phase === 'queued') return 'Pesquisa na fila';
  if (job?.phase === 'finishing') return 'Finalizando pesquisa';
  return 'Pesquisa em segundo plano';
});
</script>

<template>
  <div v-if="job" class="message-row message-row--assistant">
    <div class="research-job">
      <WebResearchActivity v-if="job.state !== 'cancelled'" :sites="sites" :label="statusLabel"
        :state="job.state === 'failed' ? 'failed' : job.state === 'completed' ? 'completed' : 'working'" />
      <span v-if="job.state === 'failed'" role="status">A pesquisa foi interrompida.</span>
      <span v-else-if="job.state === 'cancelled'" role="status">Pesquisa cancelada.</span>
      <button v-else-if="isResearchPending(job)" type="button" class="research-job__cancel"
        :disabled="cancelling.has(job.id)" aria-label="Cancelar pesquisa em segundo plano"
        @click="emit('cancel', job)">Cancelar</button>
    </div>
  </div>
</template>

<style scoped>
.research-job {
  display: inline-flex;
  align-items: center;
  min-height: 34px;
  gap: 8px;
  color: var(--muted);
  font-size: .8rem;
}
.research-job__cancel {
  padding: 3px 8px;
  color: var(--muted);
  background: transparent;
  border: 1px solid var(--line-strong);
  border-radius: var(--radius-inline);
  cursor: pointer;
  font-size: .72rem;
}
.research-job__cancel:hover:not(:disabled) { background: var(--surface-raised); }
</style>
