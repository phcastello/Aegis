<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { sanitizeMapAttribution } from '@aegis/client/services/mapAttribution';
import { openClientLink } from '@aegis/client/services/clientEnvironment';
import type * as Leaflet from 'leaflet';
import type { Circle, LocationObservation, MapConfig } from '../services/domains';
import { placeAccuracyGuidance } from '../services/placeMapGuidance';
const props = defineProps<{ modelValue: Circle[]; config: MapConfig; center?: { latitude: number; longitude: number } | null; currentPosition?: LocationObservation | null; monitoringCircles?: Circle[]; inheritedCircles?: Circle[] }>();
const emit = defineEmits<{ 'update:modelValue': [circles: Circle[]]; 'draft-dirty': [value: boolean] }>();
const container = ref<HTMLDivElement | null>(null); const adding = ref(false); const error = ref<string | null>(null); const loading = ref(true); const selected = ref<string | null>(null);
const accuracyGuidance = computed(() => placeAccuracyGuidance(props.currentPosition?.accuracyMeters));
const insufficientArea = computed(() => accuracyGuidance.value && props.modelValue.some(circle => circle.radiusMeters <= accuracyGuidance.value!.marginMeters));
const radiusDrafts = ref<Record<string, string>>({});
const draftDirty = computed(() => Object.entries(radiusDrafts.value).some(([id, value]) => {
  const circle = props.modelValue.find(item => item.id === id); return !!circle && value !== String(circle.radiusMeters);
}));
watch(draftDirty, value => emit('draft-dirty', value), { immediate: true });
let library: typeof Leaflet | null = null; let map: Leaflet.Map | null = null; let layers: Leaflet.LayerGroup | null = null; let positionLayers: Leaflet.LayerGroup | null = null; let monitoringLayers: Leaflet.LayerGroup | null = null; let resize: ResizeObserver | null = null; let disposed = false;
function openAttribution(event: MouseEvent) { const link = event.target instanceof Element ? event.target.closest('a') : null; if (!link) return; event.preventDefault(); void openClientLink(link.href).catch(() => { error.value = 'Não foi possível abrir o link de atribuição.'; }); }
function addAtCenter() { const center = map?.getCenter(); if (center) emit('update:modelValue', [...props.modelValue, {id:crypto.randomUUID(),latitude:center.lat,longitude:center.lng,radiusMeters:50}]); if (center && map) map.setView(center,Math.max(16,map.getZoom())); }
function moveToCenter(id: string) { const center = map?.getCenter(); if (center) updateCircle(id,{latitude:center.lat,longitude:center.lng}); }
function updateCircle(id: string, change: Partial<Circle>) { if (change.radiusMeters !== undefined) delete radiusDrafts.value[id]; emit('update:modelValue', props.modelValue.map(circle => circle.id === id ? { ...circle, ...change } : { ...circle })); }
function setRadiusInput(id: string, value: string) {
  radiusDrafts.value[id] = value; const radiusMeters = Number(value);
  if (Number.isFinite(radiusMeters) && radiusMeters >= 50 && radiusMeters <= 5000) updateCircle(id, { radiusMeters });
}
function validTileUrl(value: string | null) { if (!value) return false; try { return new URL(value.replace('{z}', '1').replace('{x}', '1').replace('{y}', '1')).protocol === 'https:' && ['{z}', '{x}', '{y}'].every(token => value.includes(token)); } catch { return false; } }
function renderPosition() {
  if (!library || !positionLayers) return;
  positionLayers.clearLayers(); const point = props.currentPosition; if (!point) return;
  if (Number.isFinite(point.accuracyMeters) && point.accuracyMeters > 0) library.circle([point.latitude, point.longitude], { radius: point.accuracyMeters, color: '#6de3b2', weight: 1, dashArray: '5 5', fillOpacity: .05, interactive: false }).addTo(positionLayers);
  const marker = library.circleMarker([point.latitude, point.longitude], { radius: 7, pane: 'place-current-position', color: '#10271f', weight: 2, fillColor: '#6de3b2', fillOpacity: 1 }).addTo(positionLayers);
  marker.bindTooltip('Posição do celular', { direction: 'top' });
  marker.getElement()?.setAttribute('aria-label', 'Posição do celular'); marker.getElement()?.setAttribute('role', 'img');
}
function renderMonitoring() {
  if (!library || !monitoringLayers) return;
  monitoringLayers.clearLayers();
  for (const circle of props.monitoringCircles ?? []) library.circle([circle.latitude, circle.longitude], {
    radius: circle.radiusMeters, color: '#e6c578', weight: 2, dashArray: '8 6', fillOpacity: .025, interactive: false,
  }).addTo(monitoringLayers);
}
function fitArea() {
  if (!library || !map || !layers) return;
  const L = library; const bounds = L.latLngBounds([]);
  // Circle bounds require the map projection, so use the attached rendered layers.
  monitoringLayers?.eachLayer(layer => { if (layer instanceof L.Circle) bounds.extend(layer.getBounds()); });
  layers.eachLayer(layer => { if (layer instanceof L.Circle) bounds.extend(layer.getBounds()); });
  if (bounds.isValid()) map.fitBounds(bounds, { padding: [24, 24], maxZoom: 17 });
}
function render() {
  if (!library || !map || !layers) return;
  layers.clearLayers();
  const L = library; const activeMap = map;
  for (const circle of props.inheritedCircles ?? []) L.circle([circle.latitude, circle.longitude], { radius: circle.radiusMeters, color: '#91a9ee', weight: 2, fillOpacity: .2, interactive: false }).addTo(layers);
  props.modelValue.forEach((item, index) => {
    const circle = L.circle([item.latitude, item.longitude], { radius: item.radiusMeters, color: '#91a9ee', weight: selected.value === item.id ? 3 : 2, fillOpacity: .2 }).addTo(layers!);
    circle.on('click', () => { selected.value = item.id; });
    const marker = L.marker([item.latitude, item.longitude], { draggable: true, icon: L.divIcon({ className: 'place-map-center', html: String(index + 1), iconSize: [44, 44], iconAnchor: [22, 22] }), title: `Mover círculo ${index + 1}`, keyboard: true }).addTo(layers!);
    marker.on('drag', () => circle.setLatLng(marker.getLatLng()));
    marker.on('dragend', () => { const position = marker.getLatLng(); updateCircle(item.id, { latitude: position.lat, longitude: position.lng }); });
    const longitudeOffset = item.radiusMeters / (111320 * Math.max(.01, Math.cos(item.latitude * Math.PI / 180)));
    const radiusMarker = L.marker([item.latitude, item.longitude + longitudeOffset], { draggable: true, icon: L.divIcon({ className: 'place-map-radius', html: '', iconSize: [44, 44], iconAnchor: [22, 22] }), title: `Redimensionar círculo ${index + 1}`, keyboard: true }).addTo(layers!);
    radiusMarker.on('drag', () => circle.setRadius(Math.max(50, Math.min(5000, activeMap.distance(circle.getLatLng(), radiusMarker.getLatLng())))));
    radiusMarker.on('dragend', () => updateCircle(item.id, { radiusMeters: Math.round(circle.getRadius()) }));
  });
}
onMounted(async () => {
  try {
    library = await import('leaflet'); await import('leaflet/dist/leaflet.css');
    if (disposed || !container.value) return;
    const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
    // Leaflet 1.9's zoom completion timer can outlive an editor being closed.
    // Instant zoom keeps save/back safe while preserving touch and keyboard zoom.
    map = library.map(container.value, { zoomAnimation: false, fadeAnimation: !reducedMotion, markerZoomAnimation: false, zoomControl: true, scrollWheelZoom: false, attributionControl: true });
    // Keep the observation visible even when it coincides with a circle centre.
    const positionPane = map.createPane('place-current-position'); positionPane.style.zIndex = '650'; positionPane.style.pointerEvents = 'none';
    const first = props.modelValue[0] ?? props.inheritedCircles?.[0]; const center = props.center ?? first;
    map.setView(center ? [center.latitude, center.longitude] : [0, 0], center ? 16 : 2);
    if (validTileUrl(props.config.tileUrl)) {
      library.tileLayer(props.config.tileUrl!, { attribution: sanitizeMapAttribution(props.config.attribution), maxZoom: 19, referrerPolicy: 'strict-origin-when-cross-origin' }).on('tileerror', () => { error.value = 'O mapa de fundo não carregou. Os círculos e os lugares salvos continuam preservados.'; }).addTo(map);
    } else { error.value = 'Mapa de fundo indisponível. Você ainda pode ajustar os círculos e seus raios.'; }
    monitoringLayers = library.layerGroup().addTo(map); layers = library.layerGroup().addTo(map); positionLayers = library.layerGroup().addTo(map);
    map.on('click', event => { if (!adding.value) return; emit('update:modelValue', [...props.modelValue, { id: crypto.randomUUID(), latitude: event.latlng.lat, longitude: event.latlng.lng, radiusMeters: 50 }]); map?.setView(event.latlng,Math.max(16,map.getZoom())); adding.value = false; });
    renderMonitoring(); render(); renderPosition(); if ((props.modelValue.length || props.inheritedCircles?.length) && !props.center) fitArea();
    resize = new ResizeObserver(() => map?.invalidateSize({ animate: false })); resize.observe(container.value);
  } catch { error.value = 'Não foi possível abrir o mapa. Seus lugares salvos continuam disponíveis.'; }
  finally { loading.value = false; }
});
watch(() => props.modelValue, render, { deep: true });
watch(selected, render);
watch(() => props.inheritedCircles, render, { deep: true });
watch(() => props.monitoringCircles, renderMonitoring, { deep: true });
watch(() => props.currentPosition, renderPosition, { deep: true });
watch(() => props.center, point => { if (point) map?.setView([point.latitude, point.longitude], 16); });
onBeforeUnmount(() => { disposed = true; resize?.disconnect(); map?.remove(); });
</script>
<template>
  <section class="place-map-editor" aria-label="Área do lugar">
    <p v-if="error" class="settings-error" role="status">{{ error }}</p><p v-if="loading" role="status">Abrindo mapa…</p>
    <div ref="container" class="place-map" @click="openAttribution" aria-label="Mapa interativo para ajustar a área" />
    <p class="map-legend"><span>● Área real</span><span v-if="monitoringCircles?.length" class="map-monitoring-legend">┄ Proximidade: inicia a verificação</span><span v-if="currentPosition" class="map-position-legend">● Verde: celular e precisão estimada</span></p>
    <div class="settings-actions"><button type="button" :aria-pressed="adding" :disabled="loading" @click="adding = !adding">{{ adding ? 'Cancelar adição de círculo' : 'Adicionar círculo no mapa' }}</button><button type="button" :disabled="loading" @click="addAtCenter">Adicionar no centro do mapa</button></div>
    <p v-if="adding" class="settings-status" role="status">Toque no mapa para criar um círculo de 50 m. Depois ajuste o raio.</p>
    <p v-if="!modelValue.length && !inheritedCircles?.length" class="settings-muted">Nenhuma área definida. Adicione um círculo no ponto escolhido.</p>
    <div v-for="(circle,index) in modelValue" :key="circle.id" class="map-circle-row" role="group" :aria-label="`Círculo ${index + 1}`"><strong>Círculo {{ index + 1 }}</strong><label :for="`radius-${circle.id}`">Raio em metros</label><input :id="`radius-${circle.id}`" type="number" min="50" max="5000" :value="radiusDrafts[circle.id] ?? circle.radiusMeters" required @input="setRadiusInput(circle.id, ($event.target as HTMLInputElement).value)" /><button type="button" @click="selected=circle.id; map?.setView([circle.latitude,circle.longitude],16)">Ver no mapa</button><button type="button" @click="moveToCenter(circle.id)">Mover para o centro do mapa</button><button type="button" class="settings-danger" @click="emit('update:modelValue', modelValue.filter(item => item.id !== circle.id))">Remover círculo</button></div>
    <p v-if="accuracyGuidance?.exceedsNativeAccuracyLimit" class="settings-status" role="status">A leitura atual está imprecisa demais para confirmar uma transição. A área real continua preservada.</p>
    <p v-else-if="insufficientArea" class="settings-status" role="status">A precisão atual pode dificultar a confirmação de presença. Eventos do Android e o histórico também participam da decisão.</p>
    <details><summary>Como ajustar a área</summary><p>Arraste os centros numerados para mover e os pontos na borda para ajustar o raio. Os círculos juntos formam a área real do lugar, inclusive suas interseções.</p><p>O contorno amarelo tracejado inicia a detecção de proximidade. O lembrete usa a área real. Ajuste a proximidade no campo abaixo, sem alterar o tamanho da casa.</p><p>A posição verde mostra uma leitura e sua precisão estimada. Essa estimativa não é uma fronteira física nem uma probabilidade de confiança comprovada.</p></details>
  </section>
</template>
<style>
.place-map { height: 340px; min-height: 260px; border-radius: var(--radius-sm); background: var(--canvas); border: 1px solid var(--line-strong); z-index: 0; }
.place-map .leaflet-tile-pane { filter: brightness(.74) saturate(.7); }
.place-map .place-map-center { display: grid; place-items: center; border: 2px solid #172132; border-radius: 50%; color: #172132; background: #bdcbf6; font: bold 13px/1 system-ui; cursor: move; }
.place-map .place-map-radius { display: grid; place-items: center; background: transparent; cursor: ew-resize; }.place-map .place-map-radius::before { display: block; content: ''; width: 18px; height: 18px; border: 2px solid #172132; border-radius: 50%; background: #91a9ee; }
.map-circle-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-top: 12px; }
.map-circle-row input { width: 100px; min-width: 0; max-width: 100%; }
.place-map-editor { min-width: 0; }
.map-circle-row > strong { flex-basis: 100%; }
.map-circle-row button { max-width: 100%; overflow-wrap: anywhere; }
.map-legend { display: flex; flex-wrap: wrap; gap: 6px 12px; color: #bdcbf6; font-size: .8rem; }
.map-monitoring-legend { color: #e6c578; }
.map-position-legend { color: #6de3b2; }
.map-larger-area { margin-top: 12px; }
.map-circle-row label { font-size: .82rem; color: var(--muted); }
.place-map .leaflet-control-attribution { color: #26344a; }
@media (max-width: 620px) { .place-map { height: 300px; } }
</style>
