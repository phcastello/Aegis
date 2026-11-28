<script setup lang="ts">
import { invoke } from '@tauri-apps/api/core';
import { computed, onBeforeUnmount, onMounted, ref, useId } from 'vue';
import { nodeServices } from '../services/nodes';

interface Sample {
  nodeId: string;
  eventId: string;
  activatedAt: string;
  disposition: string;
  commandDisposition: string | null;
  hasPostAudio: boolean;
  humanLabel: string | null;
  commandLabel: string | null;
}
interface NodeStorage { nodeId: string; bytes: number; samples: number; quotaBlocked: boolean }
interface Page {
  samples: Sample[];
  total: number;
  nextOffset: number | null;
  bytes: number;
  maximumBytes: number;
  quotaBlocked: boolean;
  nodes: NodeStorage[];
}

const filters = [
  { value: '', label: 'Todas' },
  { value: 'unreviewed', label: 'Sem rótulos' },
  { value: 'ignored', label: 'Ignoradas pela Aegis' },
  { value: 'election_lost', label: 'Atendidas em outro dispositivo' },
  { value: 'verifier_rejected', label: 'Rejeitadas pelo detector' },
  { value: 'false_positive', label: 'Marcadas como falso positivo' },
  { value: 'correct', label: 'Marcadas como corretas' },
  { value: 'not_directed', label: 'Marcadas como fala alheia' }
];
const dispositionLabels: Record<string, string> = {
  activated: 'Ativada', election_lost: 'Outro dispositivo venceu', verifier_rejected: 'Detector rejeitou',
  unavailable: 'Não atendida', candidate: 'Candidata', accepted: 'Pedido aceito', ignored: 'Fala ignorada',
  no_speech: 'Sem fala', cancelled: 'Interação cancelada', unknown: 'Comando sem classificação'
};
const page = ref<Page | null>(null);
const filter = ref('');
const nodeId = ref('');
const offset = ref(0);
const busy = ref(false);
const error = ref('');
const notice = ref('');
const selected = ref<Sample | null>(null);
const humanLabel = ref('');
const commandLabel = ref('');
const confirmDelete = ref(false);
const nodeNames = ref<Record<string, string>>({});
const audio = ref<{ url: string; label: string } | null>(null);
const nodeFilterId = useId();
const labelFilterId = useId();
const humanLabelId = useId();
const commandLabelId = useId();
const unchanged = computed(() => Boolean(selected.value &&
  (selected.value.humanLabel ?? '') === humanLabel.value && (selected.value.commandLabel ?? '') === commandLabel.value));
let alive = true;

function size(bytes: number) {
  if (bytes === 0) return '0 MiB';
  if (bytes < 1024 * 1024) return (bytes / 1024).toLocaleString('pt-BR', { maximumFractionDigits: 1 }) + ' KiB';
  return (bytes / 1024 / 1024).toLocaleString('pt-BR', { maximumFractionDigits: 1 }) + ' MiB';
}
function name(id: string) { return nodeNames.value[id] || 'Dispositivo ' + id.slice(0, 8); }
function when(activatedAt: string) { return new Date(activatedAt).toLocaleString('pt-BR'); }
function closeAudio() {
  if (audio.value) URL.revokeObjectURL(audio.value.url);
  audio.value = null;
}
function choose(sample: Sample | null) {
  closeAudio();
  selected.value = sample;
  humanLabel.value = sample?.humanLabel ?? '';
  commandLabel.value = sample?.commandLabel ?? '';
  confirmDelete.value = false;
}
async function load(at: number) {
  const next = await invoke<Page>('node_wake_dataset', {
    request: { operation: 'list', nodeId: nodeId.value || null, filter: filter.value || null, offset: at }
  });
  if (!next.samples.length && at > 0 && next.total > 0) {
    await load(0);
    return;
  }
  page.value = next;
  offset.value = at;
  const current = selected.value;
  choose(current ? next.samples.find(item => item.nodeId === current.nodeId && item.eventId === current.eventId) ?? null : null);
}
async function perform(action: () => Promise<void>) {
  if (busy.value) return;
  busy.value = true;
  error.value = '';
  notice.value = '';
  try { await action(); }
  catch (reason) { error.value = typeof reason === 'string' ? reason : 'Não foi possível consultar as amostras.'; }
  finally { busy.value = false; }
}
function refresh(at = 0) { return perform(() => load(at)); }
function play(command: boolean) {
  const sample = selected.value;
  if (!sample) return;
  return perform(async () => {
    closeAudio();
    const { wav } = await invoke<{ wav: string }>('node_wake_dataset', {
      request: { operation: 'audio', nodeId: sample.nodeId, eventId: sample.eventId, command }
    });
    if (!alive) return;
    const raw = atob(wav);
    const data = Uint8Array.from(raw, character => character.charCodeAt(0));
    audio.value = {
      url: URL.createObjectURL(new Blob([data], { type: 'audio/wav' })),
      label: command ? 'Áudio do comando' : 'Áudio da ativação'
    };
  });
}
function save() {
  const sample = selected.value;
  if (!sample) return;
  return perform(async () => {
    const updated = await invoke<Sample>('node_wake_dataset', {
      request: { operation: 'label', nodeId: sample.nodeId, eventId: sample.eventId,
        humanLabel: humanLabel.value || null, commandLabel: commandLabel.value || null }
    });
    choose(updated);
    notice.value = 'Rótulos salvos no servidor.';
    await load(offset.value);
  });
}
function remove() {
  const sample = selected.value;
  if (!sample || !confirmDelete.value) return;
  return perform(async () => {
    await invoke('node_wake_dataset', { request: { operation: 'delete', nodeId: sample.nodeId, eventId: sample.eventId } });
    choose(null);
    notice.value = 'Amostra excluída do servidor.';
    await load(offset.value);
  });
}
onMounted(() => {
  void refresh();
  void nodeServices.list().then(nodes => {
    nodeNames.value = Object.fromEntries(nodes.map(node => [node.id, node.name]));
  }).catch(() => { /* The corpus can still be reviewed with device IDs. */ });
});
onBeforeUnmount(() => { alive = false; closeAudio(); });
</script>

<template>
  <section class="wake-review" aria-label="Acervo de amostras no servidor">
    <div v-if="page" class="wake-review-usage">
      <p><strong>{{ size(page.bytes) }}</strong> de {{ size(page.maximumBytes) }} usados no servidor</p>
      <progress v-if="page.maximumBytes > 0" aria-label="Espaço ocupado no servidor"
        :max="page.maximumBytes" :value="page.bytes"></progress>
      <p v-if="page.quotaBlocked || page.bytes >= page.maximumBytes" role="status">
        A cota do servidor bloqueou novos envios. Os dispositivos tentarão enviar as amostras quando houver espaço.
      </p>
      <ul v-if="page.nodes.length">
        <li v-for="node in page.nodes" :key="node.nodeId">
          {{ name(node.nodeId) }} · {{ node.samples }} {{ node.samples === 1 ? 'amostra' : 'amostras' }} · {{ size(node.bytes) }}
          <span v-if="node.quotaBlocked"> · envio barrado pela cota</span>
        </li>
      </ul>
    </div>
    <div class="wake-review-filters">
      <div class="wake-review-choice">
        <label :for="nodeFilterId">Dispositivo</label>
        <select :id="nodeFilterId" v-model="nodeId" :disabled="busy" @change="refresh()">
          <option value="">Todos</option>
          <option v-for="node in page?.nodes ?? []" :key="node.nodeId" :value="node.nodeId">{{ name(node.nodeId) }}</option>
        </select>
      </div>
      <div class="wake-review-choice">
        <label :for="labelFilterId">Ativações</label>
        <select :id="labelFilterId" v-model="filter" :disabled="busy" @change="refresh()">
          <option v-for="item in filters" :key="item.value" :value="item.value">{{ item.label }}</option>
        </select>
      </div>
      <button type="button" :disabled="busy" @click="refresh()">Atualizar acervo</button>
    </div>
    <p v-if="busy" role="status">Consultando o acervo…</p>
    <p v-else-if="notice" role="status">{{ notice }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="page && !page.samples.length && !busy">Nenhuma amostra para este filtro.</p>
    <ul v-if="page?.samples.length" class="wake-review-list">
      <li v-for="sample in page.samples" :key="sample.nodeId + sample.eventId">
        <button type="button" :aria-pressed="selected?.nodeId === sample.nodeId && selected.eventId === sample.eventId"
          :disabled="busy" @click="choose(sample)">
          <span>{{ name(sample.nodeId) }} · {{ when(sample.activatedAt) }}</span>
          <small>{{ dispositionLabels[sample.disposition] || sample.disposition }}
            <template v-if="sample.commandDisposition"> · {{ dispositionLabels[sample.commandDisposition] || sample.commandDisposition }}</template>
            · {{ sample.humanLabel || sample.commandLabel ? 'Revisada' : 'Sem rótulo' }}
          </small>
        </button>
      </li>
    </ul>
    <div v-if="page && (offset || page.nextOffset !== null)" class="settings-actions wake-review-pages" aria-label="Paginação das amostras">
      <button type="button" :disabled="busy || offset === 0" @click="refresh(Math.max(0, offset - 24))">Anteriores</button>
      <span>{{ offset + 1 }}–{{ offset + page.samples.length }} de {{ page.total }}</span>
      <button type="button" :disabled="busy || page.nextOffset === null" @click="refresh(page?.nextOffset ?? 0)">Próximas</button>
    </div>
    <article v-if="selected" class="wake-review-selected">
      <h4>Amostra de {{ when(selected.activatedAt) }}</h4>
      <div class="settings-actions">
        <button type="button" :disabled="busy" @click="play(false)">Carregar ativação</button>
        <button v-if="selected.hasPostAudio" type="button" :disabled="busy" @click="play(true)">Carregar áudio posterior</button>
      </div>
      <div v-if="audio" class="wake-review-audio">
        <audio :key="audio.url" :src="audio.url" :aria-label="audio.label" controls />
        <button type="button" @click="closeAudio">Fechar áudio</button>
      </div>
      <div class="wake-review-filters">
        <div class="wake-review-choice">
          <label :for="humanLabelId">A palavra “Aegis” foi dita?</label>
          <select :id="humanLabelId" v-model="humanLabel" :disabled="busy">
            <option value="">Sem rótulo</option><option value="correct">Sim</option>
            <option value="false_positive">Não</option><option value="uncertain">Incerto</option>
          </select>
        </div>
        <div class="wake-review-choice">
          <label :for="commandLabelId">A fala era dirigida à Aegis?</label>
          <select :id="commandLabelId" v-model="commandLabel" :disabled="busy">
            <option value="">Sem rótulo</option><option value="directed">Sim</option>
            <option value="not_directed">Não</option><option value="uncertain">Incerto</option>
          </select>
        </div>
        <button type="button" :disabled="busy || unchanged" @click="save">Salvar rótulos</button>
      </div>
      <div class="settings-actions">
        <button v-if="!confirmDelete" type="button" :disabled="busy" @click="confirmDelete = true">Excluir desta coleção…</button>
        <template v-else>
          <p>Excluir o áudio e os rótulos desta amostra do servidor?</p>
          <button type="button" class="settings-danger" :disabled="busy" @click="remove">Confirmar exclusão</button>
          <button type="button" :disabled="busy" @click="confirmDelete = false">Manter amostra</button>
        </template>
      </div>
    </article>
  </section>
</template>

<style scoped>
.wake-review { margin-block-start: 1rem; min-width: 0; }
.wake-review-usage progress { width: 100%; }
.wake-review-usage ul { padding-inline-start: 1.1rem; }
.wake-review-filters, .wake-review-audio, .wake-review-pages { align-items: end; display: flex; flex-wrap: wrap; gap: .6rem; }
.wake-review-choice { display: grid; gap: .25rem; }
.wake-review-filters select { margin-inline-start: 0; max-width: 100%; }
.wake-review-list { list-style: none; padding: 0; }
.wake-review-list button { display: grid; gap: .2rem; text-align: start; width: 100%; margin-block: .3rem; }
.wake-review-list button[aria-pressed="true"] { border-color: var(--accent-color, #767dff); }
.wake-review-list small { opacity: .8; }
.wake-review-selected { border-top: 1px solid var(--border-color, #444); margin-block-start: 1rem; padding-block-start: .5rem; }
.wake-review-selected .wake-review-filters, .wake-review-audio { margin-block: .8rem; }
.wake-review-audio audio { max-width: 100%; }
</style>
