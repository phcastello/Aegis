<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onMounted, ref, watch } from 'vue';
import { placesApi, locationApi, dateLabel, type Place, type Circle, type MapConfig, type SearchResult, type LocationPermissions, type LocationObservation } from '../services/domains';
import type { Platform } from '../services/runtime';
import { useLocationPermissionRefresh } from '../composables/useLocationPermissionRefresh';
const PlaceMap = defineAsyncComponent(() => import('./PlaceMap.vue'));
const props = defineProps<{ platform: Platform }>();
const emit = defineEmits<{ dirty: [value: boolean] }>();
const places = ref<Place[]>([]); const busy = ref(false); const error = ref<string | null>(null); const notice = ref<string | null>(null); const loaded = ref(false);
const editing = ref(false); const target = ref<Place | null>(null); const name = ref(''); const type = ref<Place['type']>('property'); const parentId = ref<string | null>(null); const aliases = ref(''); const circles = ref<Circle[]>([]);
const mapConfig = ref<MapConfig>({ tileUrl: null, attribution: '', geocodingConfigured: false }); const query = ref(''); const searchResults = ref<SearchResult[]>([]); const searchBusy = ref(false); const searchMessage = ref<string | null>(null); const searchError = ref(false);
const mapCenter = ref<{ latitude: number; longitude: number } | null>(null); const areaConfirmed = ref(false); const deleteTarget = ref<Place | null>(null); const permissions = ref<LocationPermissions | null>(null);
const currentPosition = ref<LocationObservation | null>(null); const locationBusy = ref(false); const locationMessage = ref<string | null>(null); const locationError = ref(false);
const panel = ref<HTMLElement | null>(null); const editor = ref<HTMLFormElement | null>(null); const nameInput = ref<HTMLInputElement | null>(null); const discard = ref(false);
const baseline = ref(''); const resultPlaceId = ref<string | null>(null); let originPlaceId: string | null = null;
const mapDraftDirty = ref(false);
useLocationPermissionRefresh(props.platform, permissions);
const typeLabels: Record<Place['type'], string> = { property: 'Propriedade', room: 'Cômodo', outdoor: 'Área externa', other: 'Outro lugar' };
function snapshot() { return JSON.stringify({ name: name.value.trim(), type: type.value, parentId: parentId.value, aliases: [...new Set(aliases.value.split(',').map(alias => alias.trim()).filter(Boolean))], circles: circles.value }); }
const dirty = computed(() => editing.value && (mapDraftDirty.value || snapshot() !== baseline.value));
watch(dirty, value => emit('dirty', value));
watch(circles, () => { areaConfirmed.value = false; }, { deep: true, flush: 'sync' });
const orderedPlaces = computed(() => {
  const result: { place: Place; depth: number }[] = []; const visited = new Set<string>();
  function add(parent: string | null, depth: number) { places.value.filter(place => place.parentId === parent).sort((a,b) => a.name.localeCompare(b.name)).forEach(place => { if (visited.has(place.id)) return; visited.add(place.id); result.push({ place, depth }); add(place.id, depth + 1); }); }
  add(null, 0); places.value.filter(place => !visited.has(place.id)).forEach(place => result.push({place, depth: 0})); return result;
});
const parentOptions = computed(() => {
  const excluded = new Set<string>(target.value ? [target.value.id] : []); let changed = true;
  while (changed) { changed = false; places.value.forEach(place => { if (place.parentId && excluded.has(place.parentId) && !excluded.has(place.id)) { excluded.add(place.id); changed = true; } }); }
  return orderedPlaces.value.filter(item => !excluded.has(item.place.id));
});
async function load() { const result = await placesApi<Place[] | { places: Place[] }>('list'); places.value = Array.isArray(result) ? result : result.places; loaded.value = true; }
async function showError(message: string) { error.value = message; await nextTick(); if (editing.value) editor.value?.querySelector('[data-place-error]')?.scrollIntoView({ block: 'nearest' }); }
async function operation(action: () => Promise<unknown>) { if (busy.value) return; busy.value = true; error.value = null; notice.value = null; try { await action(); } catch { await showError('Não foi possível concluir. Confira a conexão e tente novamente. Se o lugar mudou em outro dispositivo, reabra sua versão atual antes de salvar. Suas mudanças continuam neste formulário.'); } finally { busy.value = false; } }
onMounted(() => { void operation(async () => { await load(); try { mapConfig.value = await placesApi<MapConfig>('config'); } catch { /* Saved places remain available without maps. */ } if (props.platform === 'android') { try { permissions.value = await locationApi<LocationPermissions>('permissions'); } catch { /* Optional native location. */ } } }); });
async function open(place?: Place) {
  originPlaceId = place?.id ?? null; target.value = place ?? null; name.value = place?.name ?? ''; type.value = place?.type ?? 'property'; parentId.value = place?.parentId ?? null; aliases.value = place?.aliases.join(', ') ?? ''; circles.value = place ? place.circles.map(circle => ({ ...circle })) : [];
  mapCenter.value = null; currentPosition.value = null; areaConfirmed.value = !!place?.circles.length; query.value = ''; searchResults.value = []; searchMessage.value = null; locationMessage.value = null; error.value = null; notice.value = null; resultPlaceId.value = null; discard.value = false;
  mapDraftDirty.value = false; baseline.value = snapshot(); editing.value = true;
  await nextTick(); editor.value?.scrollIntoView({ block: 'start' }); nameInput.value?.focus({ preventScroll: true });
}
async function returnToList(id = originPlaceId) {
  editing.value = false; discard.value = false; await nextTick();
  const button = id ? Array.from(panel.value?.querySelectorAll<HTMLButtonElement>('[data-edit-place]') ?? []).find(item => item.dataset.editPlace === id) : panel.value?.querySelector<HTMLButtonElement>('[data-new-place]');
  button?.scrollIntoView({ block: 'nearest' });
  if (notice.value && resultPlaceId.value === id) button?.closest('article')?.querySelector('[role="status"]')?.scrollIntoView({ block: 'nearest' });
  button?.focus({ preventScroll: true });
}
async function cancel() {
  if (busy.value) return;
  if (!dirty.value) { await returnToList(); return; }
  discard.value = true; await nextTick(); const confirmation = editor.value?.querySelector<HTMLElement>('[data-place-discard]'); confirmation?.scrollIntoView({ block: 'nearest' }); confirmation?.querySelector<HTMLButtonElement>('button')?.focus({ preventScroll: true });
}
function requestBack() { if (!editing.value) return false; if (busy.value) return true; if (discard.value) discard.value = false; else void cancel(); return true; }
defineExpose({ requestBack });
async function save() {
  if (!name.value.trim()) return;
  if (circles.value.length && !areaConfirmed.value) { await showError('Confirme a área escolhida no mapa antes de salvar.'); return; }
  let savedSuccessfully = false;
  await operation(async () => {
    const payload = { ...JSON.parse(snapshot()), ...(target.value ? { expectedRevision: target.value.revision } : {}) };
    const saved = await placesApi<Place>(target.value ? 'update' : 'create', payload, target.value?.id);
    resultPlaceId.value = saved?.id ?? target.value?.id ?? null; baseline.value = snapshot();
    savedSuccessfully = true;
    try { await load(); notice.value = 'Lugar salvo.'; }
    catch {
      // The write was accepted. Keep its returned place and close the editor so a
      // failed list refresh cannot turn a retry into a duplicate create.
      if (saved?.id) {
        const accepted = { ...target.value, ...payload, ...saved } as Place;
        places.value = [...places.value.filter(place => place.id !== saved.id), accepted];
      }
      notice.value = 'Lugar salvo; não foi possível atualizar a lista. Atualize quando a conexão voltar.';
    }
  });
  if (savedSuccessfully) await returnToList(resultPlaceId.value);
}
async function search() {
  if (searchBusy.value || !query.value.trim()) return; searchBusy.value = true; searchMessage.value = null; locationMessage.value = null; searchResults.value = []; searchError.value = false;
  try { const result = await placesApi<{ configured: boolean; results: SearchResult[] }>('search', { query: query.value.trim() }); searchResults.value = result.results;
    if (!result.configured) { searchError.value = true; searchMessage.value = 'Busca de endereços não configurada no servidor.'; }
    else searchMessage.value = result.results.length ? 'Escolha um endereço para vê-lo no mapa. A área permanece igual.' : 'Nenhum resultado. Tente um endereço mais completo.';
  } catch { searchError.value = true; searchMessage.value = 'A busca não está disponível agora. Tente novamente.'; }
  finally { searchBusy.value = false; }
}
function choosePoint(point: SearchResult) { mapCenter.value = { latitude: point.latitude, longitude: point.longitude }; searchResults.value = []; searchError.value = false; searchMessage.value = 'Endereço mostrado no mapa; a área foi preservada.'; }
async function current() {
  if (locationBusy.value) return; locationBusy.value = true; locationMessage.value = null; searchMessage.value = null; searchResults.value = []; locationError.value = false;
  try {
    const response = await locationApi<LocationObservation | { observation: LocationObservation } | { latest: LocationObservation | null; status?: string }>('current');
    const point = 'observation' in response ? response.observation : 'latest' in response ? response.latest : response;
    if (!point || !Number.isFinite(point.latitude) || !Number.isFinite(point.longitude)) { currentPosition.value = null; locationError.value = true; locationMessage.value = 'Localização indisponível. Confira os serviços e as permissões do celular e tente novamente.'; return; }
    currentPosition.value = { ...point }; mapCenter.value = { latitude: point.latitude, longitude: point.longitude };
    const ageSeconds = point.ageSeconds ?? Math.max(0, (Date.now() - new Date(point.capturedAt).getTime()) / 1000);
    const previous = !Number.isFinite(ageSeconds) || ageSeconds > 120 || ('status' in response && ['pending', 'source_unreachable'].includes(response.status ?? ''));
    locationError.value = previous;
    locationMessage.value = previous ? `Última posição disponível${Number.isFinite(ageSeconds) ? `, de ${Math.ceil(ageSeconds / 60)} min atrás` : ''}. Sem leitura nova; a área foi preservada.` : 'Posição em verde; a área foi preservada.';
  } catch { currentPosition.value = null; locationError.value = true; locationMessage.value = 'Localização indisponível. Confira os serviços e as permissões do celular e tente novamente.'; }
  finally { locationBusy.value = false; }
}
async function permission() { await operation(async () => { await locationApi('requestPermission', { background: false, precise: true }); permissions.value = await locationApi<LocationPermissions>('permissions'); }); }
async function remove() { if (!deleteTarget.value) return; const item = deleteTarget.value; await operation(async () => { await placesApi('remove', { expectedRevision: item.revision }, item.id); deleteTarget.value = null; resultPlaceId.value = null; await load(); notice.value = 'Lugar removido.'; }); }
</script>
<template>
  <section ref="panel" aria-labelledby="places-title"><h2 id="places-title">Lugares</h2>
    <template v-if="!editing">
      <div class="settings-actions"><button data-new-place class="settings-primary" :disabled="busy" @click="open()">Novo lugar</button><button :disabled="busy" @click="operation(load)">{{ busy ? 'Atualizando…' : 'Atualizar' }}</button></div>
      <p v-if="error" class="settings-error" role="alert">{{ error }}</p><p v-if="notice && !resultPlaceId" class="settings-status" role="status">{{ notice }}</p>
    </template>
    <form v-if="editing" ref="editor" :inert="busy" class="settings-card settings-form place-editor" @submit.prevent="save">
      <h3>{{ target ? 'Editar lugar' : 'Novo lugar' }}</h3>
      <fieldset class="place-group"><legend>Identificação</legend>
        <label>Nome<input ref="nameInput" v-model="name" maxlength="100" required autocomplete="off" placeholder="Ex.: Casa" /></label>
        <div class="settings-grid"><label>Tipo<select v-model="type"><option v-for="(label,key) in typeLabels" :key="key" :value="key">{{ label }}</option></select></label><label>Faz parte de<select v-model="parentId"><option :value="null">Lugar independente</option><option v-for="item in parentOptions" :key="item.place.id" :value="item.place.id">{{ '— '.repeat(item.depth) }}{{ item.place.name }}</option></select></label></div>
        <label>Apelidos, separados por vírgula<input v-model="aliases" placeholder="Ex.: minha casa, residência" /></label>
      </fieldset>
      <fieldset v-if="type !== 'room' || circles.length" class="place-group place-location"><legend>Localização e área</legend>
        <div class="place-map-tools">
          <label>Buscar endereço<input v-model="query" :disabled="!mapConfig.geocodingConfigured || searchBusy" placeholder="Rua, número e cidade" @keydown.enter.prevent="search" /></label>
          <div class="settings-actions"><button type="button" :disabled="!mapConfig.geocodingConfigured || searchBusy || !query.trim()" @click="search">{{ searchBusy ? 'Buscando…' : 'Buscar endereço' }}</button><button v-if="platform === 'android' && !permissions?.foreground" type="button" :disabled="busy" @click="permission">Permitir localização</button><button type="button" :disabled="locationBusy || (platform === 'android' && !permissions?.foreground)" @click="current">{{ locationBusy ? 'Localizando…' : 'Minha localização' }}</button></div>
          <small v-if="!mapConfig.geocodingConfigured">Busca indisponível. Você pode ajustar a área pelo mapa.</small>
          <p v-if="searchMessage" :class="searchError ? 'settings-error' : 'settings-status'" :role="searchError ? 'alert' : 'status'">{{ searchMessage }}</p>
          <ul v-if="searchResults.length" class="place-search-results" aria-label="Sugestões de endereço"><li v-for="(result,index) in searchResults" :key="index"><button type="button" @click="choosePoint(result)"><strong>{{ result.name }}</strong><small>Ver no mapa · mantém a área atual</small></button></li></ul>
          <p v-if="locationMessage" :class="locationError ? 'settings-error' : 'settings-status'" :role="locationError ? 'alert' : 'status'">{{ locationMessage }}</p>
          <small v-if="currentPosition">Precisão estimada: {{ Math.round(currentPosition.accuracyMeters) }} m · {{ dateLabel(currentPosition.capturedAt) }}</small>
        </div>
        <PlaceMap v-model="circles" :config="mapConfig" :center="mapCenter" :current-position="currentPosition" @draft-dirty="mapDraftDirty = $event" />
        <label v-if="circles.length" class="area-confirm"><input v-model="areaConfirmed" type="checkbox" />Conferi a área que representa este lugar.</label>
      </fieldset>
      <small v-else>Este cômodo usa a área do lugar pai. GPS não identifica cômodos.</small>
      <details><summary>Saiba mais sobre lugares e precisão</summary><p>Círculos sobrepostos formam uma única área. A posição do celular, marcada em verde, é uma observação separada: buscar um endereço ou usar “Minha localização” apenas move o mapa.</p><p>A precisão é uma estimativa. Com economia de bateria, uma área de 65 m pode continuar incerta se a precisão for de 100 m. Confira a presença em Lembretes antes de sair.</p><p>Os círculos salvos definem o lugar e são registrados no Android com o mesmo raio. Se escolher ampliar a área para monitoramento, essa área passa a incluir o entorno e pode atrasar o aviso de saída. Não há ampliação automática nem garantia de precisão.</p><p>Cômodos podem usar a área do lugar pai; apelidos ajudam a reconhecer os nomes usados por você.</p><p v-if="mapConfig.geocodingConfigured">Ao tocar em Buscar endereço, o texto digitado é enviado a {{ mapConfig.geocoderProvider === 'photon' ? 'Photon/Komoot, com dados do OpenStreetMap' : mapConfig.geocoderProvider || 'seu provedor de busca configurado' }}.</p></details>
      <div class="settings-actions"><button class="settings-primary" :disabled="busy" type="submit">{{ busy ? 'Salvando…' : 'Salvar lugar' }}</button><button type="button" :disabled="busy" @click="cancel">Cancelar</button></div>
      <p v-if="error" data-place-error class="settings-error" role="alert">{{ error }}</p>
      <div v-if="discard" data-place-discard class="settings-error" role="alertdialog" aria-label="Descartar alterações do lugar?"><p>Descartar alterações do lugar?</p><div class="settings-actions"><button type="button" @click="discard = false">Continuar editando</button><button type="button" class="settings-danger" @click="returnToList()">Descartar alterações</button></div></div>
    </form>
    <p v-if="!loaded && busy" role="status">Carregando lugares…</p><div v-if="loaded && !places.length && !editing" class="settings-empty">Nenhum lugar cadastrado.</div>
    <template v-if="!editing"><article v-for="item in orderedPlaces" :key="item.place.id" class="settings-card" :class="{'place-card-child': item.depth > 0}"><div class="settings-card-header"><div><h3>{{ item.place.name }}</h3><small>{{ typeLabels[item.place.type] }}<span v-if="item.place.parentId"> · {{ places.find(place => place.id === item.place.parentId)?.name }}</span></small></div><span class="settings-badge">{{ item.place.circles.length ? `${item.place.circles.length} círculo${item.place.circles.length > 1 ? 's' : ''}` : item.place.parentId ? 'Área herdada' : 'Sem área' }}</span></div><p v-if="item.place.aliases.length" class="settings-muted">Apelidos: {{ item.place.aliases.join(', ') }}</p><div class="settings-actions"><button :data-edit-place="item.place.id" :disabled="busy" @click="open(item.place)">Editar</button><button class="settings-danger" :disabled="busy" @click="deleteTarget = item.place">Excluir</button></div><p v-if="notice && resultPlaceId === item.place.id" class="settings-status" role="status">{{ notice }}</p><div v-if="deleteTarget?.id === item.place.id" class="settings-error" role="alertdialog" aria-label="Excluir lugar"><p>Excluir {{ item.place.name }}?</p><small>Lugares usados por regras ou com filhos precisam ser reorganizados antes da exclusão.</small><div class="settings-actions"><button class="settings-danger" :disabled="busy" @click="remove">Excluir lugar</button><button :disabled="busy" @click="deleteTarget = null">Cancelar</button></div></div></article></template>
  </section>
</template>
<style scoped>
.place-group { display: grid; gap: 14px; min-width: 0; margin: 0; padding: 0; border: 0; }
.place-group legend { margin-bottom: 14px; color: var(--text); font-weight: 600; }
.place-editor .settings-grid { grid-template-columns: repeat(auto-fit, minmax(min(100%, 180px), 1fr)); }
.place-editor :is(input, select) { min-width: 0; max-width: 100%; }
.place-map-tools { display: grid; gap: 8px; min-width: 0; }
.place-map-tools .settings-actions { margin-top: 0; }
.place-map-tools p { margin: 0; }
.place-card-child { margin-left: 12px; }
.place-editor .area-confirm { display: flex; align-items: start; gap: 10px; }
.area-confirm input { flex-shrink: 0; }
.place-search-results { list-style: none; margin: 0; padding: 0; }
.place-search-results li + li { margin-top: 8px; }
.place-search-results button { width: 100%; text-align: left; overflow-wrap: anywhere; }
</style>
