<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';
import ChatView from '@aegis/client/views/ChatView.vue';
import UpdateNotice from './components/UpdateNotice.vue';
import { nodeServices, transportServices, type IdentityStatus } from './services/nodes';
import NodesPanel from './components/NodesPanel.vue';
import DiagnosticsPanel from './components/DiagnosticsPanel.vue';
import type { RuntimeInfo } from './services/runtime';
defineProps<{ runtime: RuntimeInfo }>();
const aboutOpen = ref(false);
const nodesOpen = ref(false);
const identity = ref<IdentityStatus | null>(null);
async function refreshIdentity() { try { identity.value = await nodeServices.status(); } catch { identity.value = { state: 'pairingError', node: null, error: 'Armazenamento seguro indisponível. Abra Dispositivos para verificar.' }; } }
function foreground() { if (document.visibilityState === 'visible') { void transportServices.reconnect().catch(() => {}); void refreshIdentity(); } }
onMounted(() => { void refreshIdentity(); document.addEventListener('visibilitychange', foreground); });
onUnmounted(() => document.removeEventListener('visibilitychange', foreground));
function closeNodes() { nodesOpen.value = false; void refreshIdentity(); }
</script>

<template>
  <ChatView>
    <template #client-info><button class="native-about-button" aria-label="Dispositivos / Nodes" @click="nodesOpen = true">Dispositivos</button><button class="native-about-button" aria-label="Sobre a Aegis" @click="aboutOpen = !aboutOpen">ⓘ</button></template>
    <template #notice><UpdateNotice :platform="runtime.platform" /><div v-if="identity && identity.state !== 'paired'" class="native-node-notice">{{ identity.state === 'disabled' ? 'Este Node está desativado.' : identity.state === 'revoked' ? 'Identidade revogada: pareie este dispositivo novamente.' : 'Este dispositivo ainda não está pareado como Node.' }} <button @click="nodesOpen = true">Dispositivos</button></div></template>
  </ChatView>
  <NodesPanel v-if="nodesOpen" :platform="runtime.platform" @close="closeNodes" />
  <aside v-if="aboutOpen" class="native-about" aria-label="Sobre a Aegis">
    <button @click="aboutOpen = false">Fechar</button>
    <p>Aegis {{ runtime.version }}</p>
    <DiagnosticsPanel />
  </aside>
</template>

<style scoped>
.native-node-notice { padding: .45rem .8rem; font-size: .8rem; color: #bbb; background: #1b1b1b; }
.native-node-notice button { margin-left: .5rem; text-decoration: underline; }
</style>
