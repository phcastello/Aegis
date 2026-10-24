<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, onUnmounted, ref, watch } from 'vue';
import { invoke } from '@tauri-apps/api/core';
import { listen, type UnlistenFn } from '@tauri-apps/api/event';
import { dismissKeyboard, hasNavigationLayer } from '@aegis/client/services/navigation';
import ChatView from '@aegis/client/views/ChatView.vue';
import SettingsShell, { type SettingsSection } from '@aegis/client/components/SettingsShell.vue';
import SettingsTrigger from '@aegis/client/components/SettingsTrigger.vue';
import AegisMark from '@aegis/client/components/AegisMark.vue';
import { getReleaseIdentity } from '@aegis/client/services/releaseIdentity';
import { openClientLink } from '@aegis/client/services/clientEnvironment';
import UpdateNotice from './components/UpdateNotice.vue';
import ReminderActionSync from './components/ReminderActionSync.vue';
import { actionDiagnosticsUnavailable, mergeActionSettings, pendingActionLabel } from './services/reminderActions';
import { nodeServices, notificationServices, transportServices, type IdentityStatus, type NotificationSettings } from './services/nodes';
import NodesPanel from './components/NodesPanel.vue';
import DiagnosticsPanel from './components/DiagnosticsPanel.vue';
import { useUpdates } from './composables/useUpdates';
import { checkUpdate } from './services/updates';
import type { RuntimeInfo } from './services/runtime';
const RemindersPanel = defineAsyncComponent(() => import('./components/RemindersPanel.vue'));
const PlacesPanel = defineAsyncComponent(() => import('./components/PlacesPanel.vue'));
const props = defineProps<{ runtime: RuntimeInfo }>();
const releaseIdentity = computed(() => getReleaseIdentity(props.runtime.version));
const section = ref<SettingsSection | null>(null); const dirty = ref(false); const activity = ref(false);
const chat = ref<InstanceType<typeof ChatView> | null>(null);
const shell = ref<InstanceType<typeof SettingsShell> | null>(null); const focusedReminderId = ref<string | null>(null);
const remindersPanel = ref<InstanceType<typeof RemindersPanel> | null>(null); const placesPanel = ref<InstanceType<typeof PlacesPanel> | null>(null);
const actionSettings = ref<NotificationSettings | null>(null);
const pendingActions = computed(() => actionSettings.value?.pendingActions ?? 0);
const actionStatusUnavailable = computed(() => actionDiagnosticsUnavailable(actionSettings.value));
const identity = ref<IdentityStatus | null>(null); const updates = useUpdates(() => checkUpdate(props.runtime));
const recoveryLinkError = ref<string | null>(null);
async function openRecoveryPublication() {
  recoveryLinkError.value = null;
  try { await openClientLink('https://github.com/phcastello/Aegis/actions/workflows/aegis-native-preview.yml'); }
  catch { recoveryLinkError.value = 'Não foi possível abrir a página de publicação. Tente novamente.'; }
}
let stopReminderListener: UnlistenFn | null = null; let disposed = false;
let lastIdentityCheck = 0; let foregroundTimer: ReturnType<typeof setTimeout> | null = null;
async function refreshIdentity() { try { identity.value = await nodeServices.status(); lastIdentityCheck = Date.now(); } catch { identity.value = { state: 'pairingError', node: null, error: 'Armazenamento seguro indisponível. Abra Dispositivos para verificar.' }; } }
async function refreshPendingActions() { try { actionSettings.value = mergeActionSettings(actionSettings.value, await notificationServices.settings()); } catch { actionSettings.value = mergeActionSettings(actionSettings.value); } }
function navigate(value: SettingsSection) { dismissKeyboard(); dirty.value = false; section.value = value; }
function close() { section.value = null; dirty.value = false; void refreshIdentity(); void refreshPendingActions(); }
function foreground() {
  if (document.visibilityState !== 'visible' || foregroundTimer !== null) return;
  foregroundTimer = setTimeout(async () => { foregroundTimer = null; if (document.visibilityState !== 'visible') return; void updates.start(); try { const current = await transportServices.status(); if (current.transportState === 'offline') await transportServices.reconnect(); } catch { /* Native lifecycle also reconciles independently. */ } if (Date.now() - lastIdentityCheck > 30000) {void refreshIdentity();void refreshPendingActions();} }, 250);
}
function nativeBack() { if (shell.value?.dismissPending()) return; if (section.value === 'reminders' && remindersPanel.value?.requestBack()) return; if (section.value === 'places' && placesPanel.value?.requestBack()) return; if (section.value) shell.value?.requestClose(); }
function consumeNativeDestination() { void invoke('node_consume_reminder_open').catch(() => undefined); }
const remindersNavigation = ref(0);
const pendingDestinations = new Set<string>();
let lastDestination: { key: string; at: number } | null = null;
async function showDestination(kind: 'conversation' | 'reminder', id: unknown) {
  if (typeof id !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)) return;
  const key = `${kind}:${id.toLowerCase()}`;
  if (pendingDestinations.has(key) || lastDestination?.key === key && Date.now() - lastDestination.at < 1000) return;
  consumeNativeDestination();
  pendingDestinations.add(key);
  const hadUnsavedChanges = dirty.value;
  const open = () => {
    if (disposed) return;
    if (kind === 'conversation') { close(); void chat.value?.openConversation(id); }
    else {
      // Discarding within the same section must also dispose of its editor.
      if (section.value === 'reminders' && hadUnsavedChanges) remindersNavigation.value++;
      focusedReminderId.value = id; navigate('reminders');
    }
  };
  try {
    if (section.value && shell.value) await shell.value.requestNavigation(open);
    else open();
  } finally {
    pendingDestinations.delete(key);
    lastDestination = { key, at: Date.now() };
  }
}
function showReminder(id: unknown) { void showDestination('reminder', id); }
function showConversation(id: unknown) { void showDestination('conversation', id); }
function openConversation(event: Event) { showConversation((event as CustomEvent<{conversationId?:unknown}>).detail?.conversationId); }
function openReminder(event: Event) { showReminder((event as CustomEvent<{reminderId?:unknown}>).detail?.reminderId); }
let stopConversationListener: (() => void) | undefined;
async function subscribeReminderOpen() { try { const stop = await listen<{conversationId:string}>('aegis-conversation-open', event => showConversation(event.payload.conversationId)); if (disposed) stop(); else stopConversationListener = stop; } catch {} try { const unlisten = await listen<{reminderId:string}>('aegis-reminder-open', event => showReminder(event.payload.reminderId)); if (disposed) unlisten(); else stopReminderListener = unlisten; } catch { /* Android DOM bridge remains available. */ } try { const pending = await invoke<{reminderId:string|null;conversationId?:string|null}>('node_consume_reminder_open'); if (!disposed) { showReminder(pending.reminderId); showConversation(pending.conversationId); } } catch { /* Older runtimes have no stored deep link. */ } }
function syncNavigation() { void invoke('node_settings_open', { open: section.value !== null || hasNavigationLayer() }).catch(() => undefined); }
watch(section, value => { syncNavigation(); if (value === 'updates') void updates.start(); });
onMounted(() => { window.addEventListener('aegis-navigation-layer', syncNavigation); void subscribeReminderOpen(); void refreshIdentity(); void refreshPendingActions(); void updates.start(); document.addEventListener('visibilitychange', foreground); window.addEventListener('aegis-reminder-actions-changed', refreshPendingActions); window.addEventListener('aegis-back', nativeBack); window.addEventListener('aegis-reminder-open', openReminder); window.addEventListener('aegis-conversation-open', openConversation); });
onUnmounted(() => { window.removeEventListener('aegis-navigation-layer', syncNavigation); disposed = true; stopReminderListener?.(); stopConversationListener?.(); document.removeEventListener('visibilitychange', foreground); window.removeEventListener('aegis-reminder-actions-changed', refreshPendingActions); window.removeEventListener('aegis-back', nativeBack); window.removeEventListener('aegis-reminder-open', openReminder); window.removeEventListener('aegis-conversation-open', openConversation); if (foregroundTimer !== null) clearTimeout(foregroundTimer); void invoke('node_settings_open', { open: false }).catch(() => undefined); });
</script>
<template>
  <ChatView ref="chat" :app-version="runtime.version" @activity-changed="activity = $event">
    <template #global-navigation><SettingsTrigger @click="navigate('devices')" /></template>
    <template #notice><div v-if="pendingActions || actionStatusUnavailable" class="native-node-notice" role="status"><template v-if="actionStatusUnavailable">Consulta das ações locais temporariamente indisponível. Não foi possível confirmar as pendências.<template v-if="pendingActions"> Última leitura: {{ pendingActionLabel(pendingActions) }}.</template></template><template v-else>{{ pendingActionLabel(pendingActions) }}. Adiamentos pendentes não têm horário offline garantido.</template><button @click="navigate('reminders')">Ver lembretes</button></div><div v-if="updates.visible.value && section !== 'updates'" class="native-node-notice" role="status"><span>{{ updates.update.value?.distributionKind === 'rollback' ? 'Atualização de recuperação da Aegis' : 'Atualização da Aegis' }}{{ updates.update.value ? ` ${updates.update.value.version}` : '' }}. Acompanhe em Atualizações.</span><button @click="navigate('updates')">Ver Atualizações</button></div><div v-if="identity && identity.state !== 'paired'" class="native-node-notice">{{ identity.state === 'disabled' ? 'Este dispositivo está desativado.' : identity.state === 'revoked' ? 'A identidade deixou de ser válida. Pareie este dispositivo novamente.' : 'Pareie este dispositivo para receber lembretes nativos.' }} <button @click="navigate('devices')">Abrir dispositivos</button></div></template>
  </ChatView>
  <SettingsShell ref="shell" :section="section" :dirty="dirty" @navigate="navigate" @close="close" @discard="dirty = false">
    <NodesPanel v-if="section === 'devices'" :platform="runtime.platform" @dirty="dirty = $event" />
    <RemindersPanel v-else-if="section === 'reminders'" ref="remindersPanel" :key="remindersNavigation" :platform="runtime.platform" :focused-reminder-id="focusedReminderId" @dirty="dirty = $event" />
    <PlacesPanel v-else-if="section === 'places'" ref="placesPanel" :platform="runtime.platform" @dirty="dirty = $event" />
    <section v-else-if="section === 'updates'"><h2>Atualizações</h2><p class="settings-muted">Versão instalada: {{ runtime.version }}. O download e a instalação precisam de sua ação.</p><UpdateNotice :updates="updates" :work-active="activity" /><div class="settings-actions"><button :disabled="updates.state.value === 'checking' || updates.state.value === 'installing'" @click="updates.start(true)">{{ updates.state.value === 'checking' ? 'Verificando…' : 'Verificar atualizações' }}</button></div><p v-if="updates.checkedMessage.value" role="status">{{ updates.checkedMessage.value }}</p><p v-if="updates.checkedAt.value" class="settings-muted">Última consulta: {{ new Date(updates.checkedAt.value).toLocaleString('pt-BR') }}<span v-if="updates.diagnostic.value"> · Código: {{ updates.diagnostic.value.code }}</span></p><details class="update-recovery"><summary>Recuperar este Node</summary><p>Uma atualização de recuperação publicada aparece acima e pode ser instalada pelo mesmo fluxo, preservando os dados deste Node.</p><button :disabled="updates.state.value === 'checking' || updates.state.value === 'installing'" @click="updates.start(true)">Buscar atualização de recuperação</button><p>Para restaurar uma versão histórica, o proprietário precisa reconstruir e aprovar uma nova distribuição de recuperação. Não instale um pacote antigo por cima da versão atual.</p><button @click="openRecoveryPublication">Abrir publicação de recuperação</button><p v-if="recoveryLinkError" role="alert">{{ recoveryLinkError }}</p><p class="settings-muted">Se a Aegis não iniciar, instale por cima o instalador oficial da recuperação publicada, sem desinstalar ou apagar os dados. Consulte o procedimento de recuperação na documentação de distribuição.</p></details><details v-if="runtime.platform === 'android'"><summary>Downloads antigos do navegador</summary><p>APKs baixados anteriormente pelo navegador continuam na pasta Downloads. Você pode removê-los uma vez pelo gerenciador de arquivos do Android. A Aegis não procura nem apaga arquivos pessoais nessa pasta.</p></details></section>
    <section v-else-if="section === 'about'">
      <div class="about-brand"><div><AegisMark /></div><div><h2>Sobre a Aegis</h2><span class="settings-badge">{{ releaseIdentity.versionLabel }}</span></div></div>
      <p class="about-release-codename">{{ releaseIdentity.codename }}</p>
      <p v-if="releaseIdentity.tagline" class="about-release-tagline settings-muted">{{ releaseIdentity.tagline }}</p>
      <p class="settings-muted">Aegis é sua assistente pessoal baseada em IA, com conversa por texto e voz, memória persistente e integrações. Usa modelos acessados por API e recursos contextuais dos dispositivos pareados para ajudar nas tarefas do dia a dia.</p>
      <dl><div><dt>Aplicativo</dt><dd>{{ runtime.platform === 'android' ? 'Android' : runtime.platform === 'windows' ? 'Windows' : 'Plataforma não suportada' }}</dd></div><div><dt>Identidade do dispositivo</dt><dd>{{ identity?.state === 'paired' ? 'Pareada' : 'Verificar em Dispositivos' }}</dd></div></dl>
      <details><summary>Diagnóstico técnico</summary><ReminderActionSync :settings="actionSettings" /><DiagnosticsPanel /></details>
    </section>
  </SettingsShell>
</template>
<style scoped>.about-brand { display: flex; gap: 16px; align-items: center; }.about-brand > div:first-child { width: 52px; height: 52px; flex-shrink: 0; }.about-brand > div { min-width: 0; }.about-brand h2 { margin-bottom: 8px; }</style>
