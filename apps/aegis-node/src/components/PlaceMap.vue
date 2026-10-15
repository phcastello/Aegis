<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { sanitizeMapAttribution } from '@aegis/client/services/mapAttribution';
import { openClientLink } from '@aegis/client/services/clientEnvironment';
import type * as Leaflet from 'leaflet';
import type { Circle, MapConfig } from '../services/domains';
const props = defineProps<{ modelValue: Circle[]; config: MapConfig; center?: { latitude: number; longitude: number } | null }>();
const emit = defineEmits<{ 'update:modelValue': [circles: Circle[]] }>();
const container = ref<HTMLDivElement | null>(null); const adding = ref(false); const error = ref<string | null>(null); const loading = ref(true); const selected = ref<string | null>(null);
let library: typeof Leaflet | null = null; let map: Leaflet.Map | null = null; let layers: Leaflet.LayerGroup | null = null; let resize: ResizeObserver | null = null; let disposed = false;
function openAttribution(event: MouseEvent) { const link = event.target instanceof Element ? event.target.closest('a') : null; if (!link) return; event.preventDefault(); void openClientLink(link.href).catch(() => { error.value = 'Não foi possível abrir o link de atribuição.'; }); }
function addAtCenter() { const center = map?.getCenter(); if (center) emit('update:modelValue', [...props.modelValue, {id:crypto.randomUUID(),latitude:center.lat,longitude:center.lng,radiusMeters:150}]); if (center && map) map.setView(center,Math.max(16,map.getZoom())); }
function moveToCenter(id: string) { const center = map?.getCenter(); if (center) updateCircle(id,{latitude:center.lat,longitude:center.lng}); }
function updateCircle(id: string, change: Partial<Circle>) { emit('update:modelValue', props.modelValue.map(circle => circle.id === id ? { ...circle, ...change } : { ...circle })); }
function validTileUrl(value: string | null) { if (!value) return false; try { return new URL(value.replace('{z}', '1').replace('{x}', '1').replace('{y}', '1')).protocol === 'https:' && ['{z}', '{x}', '{y}'].every(token => value.includes(token)); } catch { return false; } }
function render() {
  if (!library || !map || !layers) return;
  layers.clearLayers();
  const L = library; const activeMap = map;
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
    map = library.map(container.value, { zoomAnimation: !reducedMotion, fadeAnimation: !reducedMotion, markerZoomAnimation: !reducedMotion, zoomControl: true, scrollWheelZoom: false, attributionControl: true });
    const first = props.modelValue[0]; const center = props.center ?? first;
    map.setView(center ? [center.latitude, center.longitude] : [0, 0], center ? 16 : 2);
    if (validTileUrl(props.config.tileUrl)) {
      library.tileLayer(props.config.tileUrl!, { attribution: sanitizeMapAttribution(props.config.attribution), maxZoom: 19, referrerPolicy: 'strict-origin-when-cross-origin' }).on('tileerror', () => { error.value = 'O mapa de fundo não carregou. Os círculos e os lugares salvos continuam preservados.'; }).addTo(map);
    } else { error.value = 'Mapa de fundo indisponível. Configure um provedor de tiles HTTPS no servidor.'; }
    layers = library.layerGroup().addTo(map);
    map.on('click', event => { if (!adding.value) return; emit('update:modelValue', [...props.modelValue, { id: crypto.randomUUID(), latitude: event.latlng.lat, longitude: event.latlng.lng, radiusMeters: 150 }]); map?.setView(event.latlng,Math.max(16,map.getZoom())); adding.value = false; });
    render(); if (props.modelValue.length) map.fitBounds(library.featureGroup(props.modelValue.map(circle => library!.circle([circle.latitude, circle.longitude], { radius: circle.radiusMeters }))).getBounds(), { padding: [24,24], maxZoom: 17 });
    resize = new ResizeObserver(() => map?.invalidateSize({ animate: false })); resize.observe(container.value);
  } catch { error.value = 'Não foi possível abrir o mapa. Seus lugares salvos continuam disponíveis.'; }
  finally { loading.value = false; }
});
watch(() => props.modelValue, render, { deep: true });
watch(selected, render);
watch(() => props.center, point => { if (point) map?.setView([point.latitude, point.longitude], 16); });
onBeforeUnmount(() => { disposed = true; resize?.disconnect(); map?.remove(); });
</script>
<template>
  <section class="place-map-editor" aria-label="Área do lugar">
    <p class="settings-muted">A área é a união dos círculos. Arraste o centro para mover e o ponto na borda para redimensionar. Confira a cobertura antes de salvar. Pelo teclado, use as setas no mapa e as ações de adicionar ou mover para o centro.</p>
    <p v-if="error" class="settings-error" role="status">{{ error }}</p><p v-if="loading" role="status">Abrindo mapa…</p>
    <div ref="container" class="place-map" @click="openAttribution" aria-label="Mapa interativo para ajustar a área" />
    <div class="settings-actions"><button type="button" :aria-pressed="adding" :disabled="loading" @click="adding = !adding">{{ adding ? 'Clique no mapa para adicionar' : 'Adicionar círculo no mapa' }}</button><button type="button" :disabled="loading" @click="addAtCenter">Adicionar no centro do mapa</button><small v-if="adding">Para cancelar, pressione o botão novamente.</small></div>
    <div v-for="(circle,index) in modelValue" :key="circle.id" class="map-circle-row"><strong>Círculo {{ index + 1 }}</strong><label :for="`radius-${circle.id}`">Raio em metros</label><input :id="`radius-${circle.id}`" type="number" min="50" max="5000" :value="circle.radiusMeters" @input="updateCircle(circle.id,{radiusMeters:Math.max(50,Math.min(5000, Number(($event.target as HTMLInputElement).value)))})" /><button type="button" @click="selected=circle.id; map?.setView([circle.latitude,circle.longitude],16)">Ver no mapa</button><button type="button" @click="moveToCenter(circle.id)">Mover para o centro do mapa</button><button type="button" class="settings-danger" @click="emit('update:modelValue', modelValue.filter(item => item.id !== circle.id))">Remover círculo</button></div>
    <p v-if="!modelValue.length" class="settings-muted">Nenhuma área definida. Use a localização atual, uma busca por endereço ou adicione um círculo no mapa.</p>
  </section>
</template>
<style>
.place-map { height: 340px; min-height: 260px; border-radius: var(--radius-sm); background: var(--canvas); border: 1px solid var(--line-strong); z-index: 0; }
.place-map .leaflet-tile-pane { filter: brightness(.74) saturate(.7); }
.place-map-center { display: grid; place-items: center; border: 2px solid #172132; border-radius: 50%; color: #172132; background: #bdcbf6; font: bold 13px/1 system-ui; cursor: move; }
.place-map-radius { display: grid; place-items: center; background: transparent; cursor: ew-resize; }.place-map-radius::before { content: ''; width: 18px; height: 18px; border: 2px solid #172132; border-radius: 50%; background: #91a9ee; }
.map-circle-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-top: 12px; }
.map-circle-row input { width: 100px; }
.map-circle-row label { font-size: .82rem; color: var(--muted); }
.place-map .leaflet-control-attribution { color: #26344a; }
@media (max-width: 620px) { .place-map { height: 300px; } }
</style>
