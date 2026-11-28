<script setup lang="ts">
import { computed } from 'vue';
import WebResearchActivity from './WebResearchActivity.vue';
import { isResearchPending, type ResearchJob } from '../services/backgroundResearch';
import { mergeResearchSites, topResearchSites } from '../services/researchSites';
import { parseWebResearchSourceCounts } from '../services/streamTextPresentation';

const props = defineProps<{ job?: ResearchJob; cancelling: ReadonlySet<string> }>();
const emit = defineEmits<{ cancel: [job: ResearchJob] }>();
const isPython = computed(() => props.job?.kind === 'python');
const isVideo = computed(() => props.job?.kind === 'video');
const cancelLabel = computed(() => isVideo.value ? 'Cancelar análise de vídeo'
  : isPython.value ? 'Cancelar execução em Python' : 'Cancelar pesquisa em segundo plano');
const sites = computed(() => topResearchSites(mergeResearchSites(new Map(), parseWebResearchSourceCounts(props.job?.sources, 128))));
const statusLabel = computed(() => {
  const job = props.job;
  if (isVideo.value) {
    if (job?.state === 'completed') return 'Análise de vídeo concluída';
    if (job?.state === 'failed') return 'A análise de vídeo foi interrompida.';
    if (job?.state === 'cancelled') return 'Análise de vídeo cancelada.';
    if (job?.phase === 'queued') return 'Análise de vídeo na fila';
    if (job?.phase === 'finishing') return 'Preparando a resposta';
    return 'Analisando vídeo em segundo plano';
  }
  if (isPython.value) {
    if (job?.state === 'completed') return 'Execução em Python concluída';
    if (job?.state === 'failed') return 'A execução em Python foi interrompida.';
    if (job?.state === 'cancelled') return 'Execução em Python cancelada.';
    if (job?.phase === 'queued') return 'Execução em Python na fila';
    if (job?.phase === 'finishing') return 'Preparando a resposta';
    return 'Executando Python em segundo plano';
  }
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
      <span v-if="isPython || isVideo" role="status">{{ statusLabel }}</span>
      <template v-else>
        <WebResearchActivity v-if="job.state !== 'cancelled'" :sites="sites" :label="statusLabel"
          :state="job.state === 'failed' ? 'failed' : job.state === 'completed' ? 'completed' : 'working'" />
        <span v-if="job.state === 'failed'" role="status">A pesquisa foi interrompida.</span>
        <span v-else-if="job.state === 'cancelled'" role="status">Pesquisa cancelada.</span>
      </template>
      <button v-if="isResearchPending(job)" type="button" class="research-job__cancel"
        :disabled="cancelling.has(job.id)"
        :aria-label="cancelLabel"
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
