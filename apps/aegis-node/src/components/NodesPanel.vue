<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { nodeServices, notificationDiagnosticCodes, notificationServices, transportServices, type TransportStatus, type NodeView } from '../services/nodes';
import { locationApi, placesApi, type LocationSettings, type Place, type NodeLocation } from '../services/domains';
import { useNodes } from '../composables/useNodes';
import type { Platform } from '../services/runtime';
const props = defineProps<{ platform: Platform }>();
const emit = defineEmits<{ dirty: [value: boolean] }>();
const { identity, nodes, pairingCode, busy, error, refresh, pair, rename, setEnabled, setPriority, remove, setReminders, createCode, isCurrent } = useNodes(nodeServices);
const name = ref(props.platform === 'android' ? 'Meu celular' : 'Meu PC');
const code = ref('');
const editedName = ref('');
const editingId = ref<string | null>(null);
const priorityId = ref<string | null>(null);
const priorityValue = ref(0);
const confirmation = ref<{ id: string; action: 'disable' | 'remove'; name: string } | null>(null);
const notificationNodeId = ref<string | null>(null);
const notificationResult = ref('');
const notificationDiagnostic = ref('');
const notificationSending = ref(false);
const settings = ref({ granted: false, pushConfigured: false, autostart: false, pendingActions: 0 });
const settingsBusy = ref(false);
const transport = ref<TransportStatus>({ transportState: 'offline', lastError: null });
const locations = ref<LocationSettings | null>(null);
const places = ref<Place[]>([]);
const profile = ref<NodeLocation | null>(null);
const profileBusy = ref(false);
const visibleNodes = computed(() => nodes.value.filter(node => !node.revokedAt));
const transportLabels: Record<string, string> = { online: 'Sessão conectada', offline: 'Sem sessão', connecting: 'Conectando', reconnecting: 'Reconectando' };
watch(() => !!editingId.value || !!profile.value || !!code.value || !!priorityId.value || (!['paired','disabled'].includes(identity.value.state) && name.value !== (props.platform === 'android' ? 'Meu celular' : 'Meu PC')), value => emit('dirty', value));
async function loadSettings() { try { const value = await notificationServices.settings(); settings.value = {...value,pendingActions:value.pendingActions ?? 0}; } catch { /* Native settings may be unavailable. */ } }
async function loadLocationSettings() {
  try { locations.value = await locationApi<LocationSettings>('settings'); } catch { locations.value = null; }
  try { const result = await placesApi<Place[] | { places: Place[] }>('list'); places.value = Array.isArray(result) ? result : result.places; } catch { places.value = []; }
}
async function updateTransport() { try { transport.value = await transportServices.status(); } catch { /* Keep the last known state. */ } }
async function reload() { await refresh(); await Promise.all([updateTransport(), loadSettings(), loadLocationSettings()]); }
onMounted(() => { void reload(); });
function reachability(node: NodeView) {
  if (!node.enabled) return 'Desativado';
  return node.availability === 'online' ? 'Sessão conectada' : node.availability === 'backgroundReachable' ? 'Rota de notificação disponível' : 'Sem rota conhecida';
}
function configuredLocation(id: string) {
  const location = locations.value?.nodes.find(node => node.nodeId === id);
  if (location?.mode === 'fixed') return places.value.find(place => place.id === location.fixedPlaceId)?.name ?? 'Lugar fixo indisponível';
  return location?.mode === 'dynamic' ? 'Posição do celular, quando disponível' : 'Sem localização configurada';
}
async function submitPair() { await pair(name.value, code.value); if (!error.value) code.value = ''; }
async function saveName(id: string) { await rename(id, editedName.value); if (!error.value) editingId.value = null; }
async function confirmAction() {
  if (!confirmation.value) return;
  const pending = confirmation.value;
  if (pending.action === 'remove') await remove(pending.id); else await setEnabled(pending.id, false);
  if (!error.value) confirmation.value = null;
}
async function saveProfile() {
  if (!profile.value || profileBusy.value) return;
  profileBusy.value = true; error.value = null;
  try { await locationApi('configureNode', profile.value); profile.value = null; await loadLocationSettings(); }
  catch { error.value = 'Não foi possível salvar a localização deste dispositivo.'; }
  finally { profileBusy.value = false; }
}
async function selectSource(id: string) {
  if (profileBusy.value) return;
  profileBusy.value = true; error.value = null;
  try { await locationApi('selectSource', { sourceNodeId: id }); await loadLocationSettings(); }
  catch { error.value = 'Não foi possível selecionar o celular de localização.'; }
  finally { profileBusy.value = false; }
}
async function testNotification(id: string) {
  if (notificationSending.value) return;
  notificationNodeId.value = id; notificationSending.value = true; notificationResult.value = 'Enviando…'; notificationDiagnostic.value = '';
  try {
    const result = await notificationServices.test(id);
    const labels: Record<string, string> = { success: 'Notificação postada pelo dispositivo.', accepted: 'Pedido aceito pelo serviço de push. A postagem no aparelho ainda não foi confirmada.', duplicate: 'Esta entrega já foi recebida pelo dispositivo.', timeout: 'Sem confirmação no prazo. O resultado da entrega é desconhecido.', busy: 'O dispositivo ainda está processando esta entrega.', permission_denied: 'Permita notificações neste dispositivo.', unavailable: 'Não há uma rota utilizável agora.', no_eligible_node: 'Nenhum dispositivo elegível agora.', expired: 'O pedido expirou.', unsupported: 'Este dispositivo não oferece essa ação.' };
    notificationResult.value = labels[result.status] ?? 'Não foi possível entregar a notificação.';
    if (result.diagnosticCode && notificationDiagnosticCodes.has(result.diagnosticCode)) notificationDiagnostic.value = result.diagnosticCode;
  } catch { notificationResult.value = 'Não foi possível enviar. Confira a conexão e tente novamente.'; }
  finally { notificationSending.value = false; }
}
async function permission() { if (settingsBusy.value) return; settingsBusy.value = true; try { await notificationServices.permission(); await loadSettings(); } catch { error.value = 'Não foi possível solicitar a permissão.'; } finally { settingsBusy.value = false; } }
async function autostart(enabled: boolean) { if (settingsBusy.value) return; settingsBusy.value = true; try { await notificationServices.autostart(enabled); await loadSettings(); } catch { error.value = 'Não foi possível alterar o início automático.'; } finally { settingsBusy.value = false; } }
</script>
<template>
  <section aria-labelledby="devices-title">
    <h2 id="devices-title">Seus dispositivos</h2><p class="settings-muted">Escolha onde receber lembretes e qual celular fornece sua localização. Uma rota disponível não garante que o aparelho esteja acordado.</p>
    <p v-if="settings.pendingActions" class="settings-status" role="status">{{ settings.pendingActions }} ação(ões) aguardam conexão. O reconhecimento ou adiamento será confirmado após sincronizar.</p><p v-if="error" class="settings-error" role="alert">{{ error }}</p>
    <p v-if="identity.state === 'disabled'" class="settings-status">Este dispositivo está desativado. Use outro dispositivo habilitado para reativá-lo.</p>
    <p v-if="identity.state === 'revoked'" class="settings-status">A identidade desta instalação deixou de ser válida. Faça um novo pareamento.</p>
    <form v-if="!['paired', 'disabled'].includes(identity.state)" class="settings-form" @submit.prevent="submitPair">
      <p>Digite o código gerado em outro dispositivo da Aegis. O chat continua disponível durante o pareamento.</p>
      <label>Nome deste dispositivo<input v-model="name" maxlength="100" required :disabled="busy" autocomplete="off" /></label>
      <label>Código de pareamento<input v-model="code" required :disabled="busy" autocomplete="off" autocapitalize="characters" :spellcheck="false" placeholder="XXXX-XXXX-XXXX-XXXX-XXXX-XXXX" /></label>
      <div><button type="submit" class="settings-primary" :disabled="busy">{{ busy ? 'Pareando…' : 'Parear dispositivo' }}</button></div>
    </form>
    <div class="settings-actions"><button :disabled="busy" @click="reload">{{ busy ? 'Atualizando…' : 'Atualizar lista' }}</button><button v-if="identity.state === 'paired'" :disabled="busy" @click="createCode">Adicionar dispositivo</button></div>
    <div v-if="pairingCode" class="settings-card"><h3>Código para o novo dispositivo</h3><p><code class="pairing-code">{{ pairingCode.code }}</code></p><small>Uso único. Expira às {{ new Date(pairingCode.expiresAt).toLocaleTimeString('pt-BR') }}.</small></div>
    <div v-if="platform === 'windows'" class="settings-row"><label for="autostart"><span>Iniciar Aegis com o Windows<small>Permite receber lembretes no tray.</small></span></label><input id="autostart" type="checkbox" :checked="settings.autostart" :disabled="settingsBusy" @change="autostart(($event.target as HTMLInputElement).checked)" /></div>
    <div v-if="platform === 'android' && !settings.granted" class="settings-card"><h3>Permita os lembretes no Android</h3><p>As notificações precisam de sua permissão para aparecer no sistema.</p><button :disabled="settingsBusy" @click="permission">Permitir notificações</button></div>
    <p v-if="identity.state === 'paired' && !visibleNodes.length && !busy" class="settings-empty">Nenhum dispositivo na lista. Atualize para verificar.</p>
    <article v-for="node in visibleNodes" :key="node.id" class="settings-card" :data-node-id="node.id">
      <div class="settings-card-header"><div><h3>{{ node.name }}</h3><small>{{ node.platform === 'android' ? 'Android' : 'Windows' }}<span v-if="isCurrent(node)"> · Este dispositivo</span></small></div><span class="settings-badge">{{ reachability(node) }}</span></div>
      <p class="settings-muted">{{ configuredLocation(node.id) }}</p><small v-if="locations?.latest?.nodeId === node.id && locations?.nodes.find(item => item.nodeId === node.id)?.mode === 'dynamic'">Última posição: {{ places.find(place => place.id === locations?.latest?.placeId)?.name ?? 'Lugar não identificado' }} · {{ locations?.latest?.capturedAt ? new Date(locations.latest.capturedAt).toLocaleString('pt-BR') : 'instante indisponível' }}</small><small v-if="locations?.sourceNodeId === node.id">Celular selecionado para sua localização</small>
      <div class="settings-row"><label :for="`reminders-${node.id}`">Receber lembretes pessoais</label><input :id="`reminders-${node.id}`" type="checkbox" :checked="node.receiveReminders !== false" :disabled="busy" @change="setReminders(node.id, ($event.target as HTMLInputElement).checked)" /></div><small v-if="node.platform === 'android'">Desativar o recebimento também pausa as regras geográficas deste celular. Ao reativar, retome essas regras explicitamente em Lembretes.</small>
      <form v-if="editingId === node.id" class="settings-form" @submit.prevent="saveName(node.id)"><label>Novo nome<input v-model="editedName" maxlength="100" required :disabled="busy" /></label><div class="settings-actions"><button type="submit" :disabled="busy">Salvar nome</button><button type="button" :disabled="busy" @click="editingId = null">Cancelar</button></div></form>
      <form v-if="profile?.nodeId === node.id" class="settings-form" @submit.prevent="saveProfile"><label>Localização<select v-model="profile.mode"><option value="none">Sem localização</option><option value="fixed">Fixo em um lugar</option><option v-if="node.platform === 'android'" value="dynamic">Dinâmica pelo celular</option></select></label><label v-if="profile.mode === 'fixed'">Lugar<select v-model="profile.fixedPlaceId" required><option :value="null" disabled>Escolha um lugar</option><option v-for="place in places" :key="place.id" :value="place.id">{{ place.name }}</option></select></label><div class="settings-actions"><button type="submit" :disabled="profileBusy">Salvar localização</button><button type="button" @click="profile = null">Cancelar</button></div></form>
      <div class="settings-actions"><button :disabled="busy || !!editingId || !!profile || !!priorityId" @click="editingId = node.id; editedName = node.name">Renomear</button><button :disabled="profileBusy || !!editingId || !!profile || !!priorityId" @click="profile = { ...(locations?.nodes.find(item => item.nodeId === node.id) ?? { nodeId: node.id, mode: 'none', fixedPlaceId: null }) }">Localização</button><button v-if="node.platform === 'android' && locations?.sourceNodeId !== node.id" :disabled="profileBusy || !node.enabled" @click="selectSource(node.id)">Usar como meu celular</button><button v-if="node.enabled" :disabled="busy" @click="confirmation = { id: node.id, name: node.name, action: 'disable' }">Desativar</button><button v-else :disabled="busy" @click="setEnabled(node.id, true)">Reativar</button><button class="settings-danger" :disabled="busy" @click="confirmation = { id: node.id, name: node.name, action: 'remove' }">Remover dispositivo</button></div>
      <details><summary>Detalhes técnicos e diagnóstico</summary><dl><div><dt>ID</dt><dd><code>{{ node.id }}</code></dd></div><div><dt>App / protocolo</dt><dd>{{ node.appVersion }} / {{ node.protocolVersion }}</dd></div><div><dt>Prioridade de destino único</dt><dd>{{ node.targetPriority ?? 0 }}</dd></div><div><dt>Última observação do canal</dt><dd>{{ node.lastSeenAt ? new Date(node.lastSeenAt).toLocaleString('pt-BR') : 'Sem informação' }}</dd></div></dl><small>Capacidades implementadas: {{ node.capabilities?.map(item => `${item.name}@${item.version}`).join(', ') || 'Nenhuma informada' }}</small><p v-if="isCurrent(node)">{{ transportLabels[transport.transportState] }}<small v-if="transport.lastDiagnostic">{{ transport.lastDiagnostic.phase }} / {{ transport.lastDiagnostic.reason }}</small></p>
        <form v-if="priorityId === node.id" class="settings-form" @submit.prevent="setPriority(node.id, priorityValue); priorityId = null"><label>Prioridade<input v-model.number="priorityValue" type="number" min="-1000" max="1000" required /></label><div class="settings-actions"><button :disabled="busy">Salvar prioridade</button><button type="button" @click="priorityId = null">Cancelar</button></div></form>
        <div class="settings-actions"><button :disabled="busy || !!editingId || !!profile || !!priorityId" @click="priorityId = node.id; priorityValue = node.targetPriority ?? 0">Alterar prioridade</button><button v-if="node.enabled && node.capabilities?.some(item => item.name === 'notification.show')" :disabled="busy || notificationSending" @click="testNotification(node.id)">{{ notificationSending && notificationNodeId === node.id ? 'Enviando…' : 'Testar notificação' }}</button></div><p v-if="notificationNodeId === node.id && notificationResult" role="status">{{ notificationResult }}<small v-if="notificationDiagnostic">Código: {{ notificationDiagnostic }}</small></p>
      </details>
      <div v-if="confirmation?.id === node.id" class="settings-error" role="alertdialog" aria-label="Confirmar alteração do dispositivo"><p>{{ confirmation.action === 'remove' ? 'Remover' : 'Desativar' }} {{ confirmation.name }}?</p><p>{{ confirmation.action === 'remove' ? 'A identidade será invalidada e será necessário parear novamente. O histórico dos lembretes será preservado. Regras locais de um aparelho offline só podem ser retiradas após sincronização ou expiração.' : 'O dispositivo deixará de receber novas entregas. Você poderá reativá-lo depois.' }}</p><small>Se este for o último dispositivo ativo, a recuperação exige acesso administrativo ao servidor.</small><div class="settings-actions"><button class="settings-danger" :disabled="busy" @click="confirmAction">Confirmar</button><button :disabled="busy" @click="confirmation = null">Cancelar</button></div></div>
    </article>
  </section>
</template>
<style scoped>.pairing-code { user-select: all; letter-spacing: .04em; font-size: 1.05rem; }</style>
