<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';
import { nodeServices, transportServices, type TransportStatus, type TargetResult } from '../services/nodes';
import { useNodes } from '../composables/useNodes';
import type { Platform } from '../services/runtime';
const props = defineProps<{ platform: Platform }>();
defineEmits<{ close: [] }>();
const { identity, nodes, pairingCode, busy, error, refresh, pair, rename, setEnabled, setPriority, revoke, createCode, isCurrent } = useNodes(nodeServices);
const name = ref(props.platform === 'android' ? 'Meu celular' : 'Meu PC');
const target = ref<TargetResult | null>(null);
async function resolveAudio() { try { target.value = await nodeServices.resolve({ requiredCapabilities: [{ name: "audio.output", minimumVersion: 1 }] }); } catch { target.value = null; error.value = "Não foi possível resolver o alvo."; } }
const priorityId = ref<string | null>(null); const priorityValue = ref(0);
const code = ref(''); const editingId = ref<string | null>(null); const editedName = ref('');
const confirmation = ref<{ id: string; action: 'disable' | 'revoke'; name: string } | null>(null);
const transport = ref<TransportStatus>({ transportState: 'offline', lastError: null });
const labels = { online: 'Online', offline: 'Offline', connecting: 'Conectando', reconnecting: 'Reconectando' };
let timer: ReturnType<typeof setInterval>;
async function updateTransport() { try { transport.value = await transportServices.status(); } catch { /* Last public state. */ } }
onMounted(() => { void refresh(); void updateTransport(); timer = setInterval(() => { void updateTransport(); void refresh(); }, 10000); });
onUnmounted(() => clearInterval(timer));
function lastSeen(value?: string | null) {
  if (!value) return 'Ainda não visto pelo transporte';
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 60) return 'Visto há menos de 1 min';
  const minutes = Math.floor(seconds / 60);
  return minutes < 60 ? `Visto há ${minutes} min` : `Visto em ${new Date(value).toLocaleString()}`;
}
async function submitPair() { await pair(name.value, code.value); code.value = ''; }
async function confirmAction() {
  const pending = confirmation.value; confirmation.value = null;
  if (!pending) return;
  if (pending.action === 'revoke') await revoke(pending.id); else await setEnabled(pending.id, false);
}
</script>
<template>
  <div class="nodes-overlay" @click.self="$emit('close')">
    <section class="nodes-panel" role="dialog" aria-modal="true" aria-labelledby="nodes-title">
      <header><h2 id="nodes-title">Dispositivos / Nodes</h2><button @click="$emit('close')">Fechar</button></header>
      <p>Uma identidade persistente para esta instalação. O chat continua disponível sem pareamento.</p>
      <p v-if="error" role="alert">{{ error }}</p>
      <p v-if="identity.state === 'disabled'">Este dispositivo está desativado. Outro Node ativo ou o administrador do servidor pode reativá-lo.</p>
      <p v-if="identity.state === 'revoked'">A identidade anterior foi revogada ou deixou de ser válida. Pareie novamente.</p>
      <form v-if="!['paired', 'disabled'].includes(identity.state)" @submit.prevent="submitPair">
        <p>Este dispositivo ainda não está pareado como Node. Obtenha um código em outro Node ativo ou com o administrador do servidor.</p>
        <label>Nome do dispositivo<input v-model="name" maxlength="100" required autocomplete="off" :disabled="busy" /></label>
        <label>Código de pareamento<input v-model="code" required autocomplete="off" autocapitalize="characters" :spellcheck="false" :disabled="busy" placeholder="XXXX-XXXX-XXXX-XXXX-XXXX-XXXX" /></label>
        <button type="submit" :disabled="busy">{{ busy ? 'Pareando…' : 'Parear dispositivo' }}</button>
      </form>
      <p v-if="identity.node">Transporte deste Node: {{ labels[transport.transportState] }}</p>
      <p v-if="transport.lastError" role="status">{{ transport.lastError }}</p>
      <small v-if="transport.lastDiagnostic">Diagnóstico: {{ transport.lastDiagnostic.phase }} / {{ transport.lastDiagnostic.reason }}<span v-if="transport.lastDiagnostic.httpStatus"> · HTTP {{ transport.lastDiagnostic.httpStatus }}</span><span v-if="transport.lastDiagnostic.closeCode"> · Close {{ transport.lastDiagnostic.closeCode }}</span></small>
      <button :disabled="busy" @click="transportServices.reconnect(); refresh()">{{ busy ? 'Aguarde…' : 'Atualizar / Tentar novamente' }}</button>
      <button v-if="identity.state === 'paired'" :disabled="busy" @click="createCode">Adicionar dispositivo</button>
      <div v-if="pairingCode" class="pairing-code"><p>Digite este código no novo dispositivo:</p><code>{{ pairingCode.code }}</code><p>Expira em {{ new Date(pairingCode.expiresAt).toLocaleTimeString() }}. Uso único.</p></div>
      <button v-if="identity.state === 'paired'" :disabled="busy" @click="resolveAudio">Diagnóstico: resolver audio.output@1</button>
      <p v-if="target" role="status">Alvo: {{ target.node?.name ?? 'Nenhum Node elegível (no_eligible_node)' }} · {{ target.onlineNodes }} Online / {{ target.capabilityCompatibleNodes }} compatíveis. Seleção neste momento; nenhuma ação executada.</p>
      <ul>
        <li v-for="node in nodes" :key="node.id" :data-node-id="node.id">
          <strong>{{ node.name }}</strong><span v-if="isCurrent(node)"> · Este dispositivo</span>
          <p>{{ node.platform === 'android' ? 'Android' : 'Windows' }} · {{ node.revokedAt ? 'Revogado' : node.enabled ? 'Habilitado' : 'Desativado' }}</p>
          <p>Disponibilidade: {{ node.availability === 'online' ? '● Online' : 'Offline' }}</p>
          <p>Capabilities: {{ node.capabilities?.length ? node.capabilities.map(c => `${c.name}@${c.version}`).join(', ') : 'Nenhuma anunciada' }}</p>
          <p>Prioridade: {{ node.targetPriority ?? 0 }}</p>
          <form v-if="priorityId === node.id" @submit.prevent="setPriority(node.id, priorityValue); priorityId = null"><label>Prioridade do alvo<input v-model.number="priorityValue" type="number" min="-1000" max="1000" step="1" required /></label><button :disabled="busy">Salvar prioridade</button></form>
          <small>{{ node.availability === 'online' ? 'Acessível agora' : lastSeen(node.lastSeenAt) }}</small>
          <small>App {{ node.appVersion }} · Protocolo {{ node.protocolVersion }} · Pareado {{ new Date(node.pairedAt).toLocaleDateString() }}</small>
          <form v-if="editingId === node.id" @submit.prevent="rename(node.id, editedName); editingId = null"><input v-model="editedName" aria-label="Novo nome" maxlength="100" required /><button :disabled="busy">Salvar</button></form>
          <div v-if="!node.revokedAt">
            <button :disabled="busy" @click="priorityId = node.id; priorityValue = node.targetPriority ?? 0">Alterar prioridade</button>
            <button :disabled="busy" @click="editingId = node.id; editedName = node.name">Renomear</button>
            <button v-if="node.enabled" :disabled="busy" @click="confirmation = { id: node.id, action: 'disable', name: node.name }">Desativar</button>
            <button v-else :disabled="busy" @click="setEnabled(node.id, true)">Reativar</button>
            <button :disabled="busy" @click="confirmation = { id: node.id, action: 'revoke', name: node.name }">Revogar</button>
          </div>
        </li>
      </ul>
      <div v-if="confirmation" role="alertdialog" aria-label="Confirmar alteração de identidade">
        <p>{{ confirmation.action === 'revoke' ? 'Revogar permanentemente' : 'Desativar' }} {{ confirmation.name }}?</p>
        <p>Se este for o último Node ativo, a recuperação exige acesso administrativo local ao servidor. Revogação exige novo pareamento.</p>
        <button :disabled="busy" @click="confirmAction">Confirmar</button><button @click="confirmation = null">Cancelar</button>
      </div>
    </section>
  </div>
</template>
<style scoped>
.nodes-overlay { position: fixed; inset: 0; z-index: 100; background: #0009; display: flex; justify-content: center; align-items: center; padding: 1rem; }
.nodes-panel { width: 38rem; max-width: 100%; max-height: 90dvh; overflow: auto; background: #171717; color: #e5e5e5; border: 1px solid #444; border-radius: 1rem; padding: 1.2rem; }
header { display: flex; justify-content: space-between; align-items: center; gap: 1rem; } h2 { font-size: 1.1rem; font-weight: 600; }
p { margin: .7rem 0; } label { display: block; margin: .8rem 0; } input { display: block; width: 100%; background: #262626; padding: .65rem; border: 1px solid #555; border-radius: .5rem; margin-top: .3rem; }
button { background: #303030; border: 1px solid #555; border-radius: .5rem; padding: .5rem .7rem; margin: .35rem .35rem .35rem 0; } button:disabled { opacity: .5; } small { display: block; color: #aaa; overflow-wrap: anywhere; }
li, .pairing-code { margin-top: 1rem; padding-top: 1rem; border-top: 1px solid #444; } code { overflow-wrap: anywhere; user-select: all; } [role=alert] { color: #fca5a5; }
</style>
