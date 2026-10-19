<script setup lang="ts">
import { defineAsyncComponent, onMounted, onUnmounted, ref, watch } from 'vue';
import { invoke } from '@tauri-apps/api/core';
import { listen, type UnlistenFn } from '@tauri-apps/api/event';
import ChatView from '@aegis/client/views/ChatView.vue';
import SettingsShell, { type SettingsSection } from '@aegis/client/components/SettingsShell.vue';
import SettingsTrigger from '@aegis/client/components/SettingsTrigger.vue';
import AegisMark from '@aegis/client/components/AegisMark.vue';
import UpdateNotice from './components/UpdateNotice.vue';
import ReminderActionSync from './components/ReminderActionSync.vue';
import { pendingActionLabel } from './services/reminderActions';
import { nodeServices, notificationServices, transportServices, type IdentityStatus, type NotificationSettings } from './services/nodes';
import NodesPanel from './components/NodesPanel.vue';
import DiagnosticsPanel from './components/DiagnosticsPanel.vue';
import { useUpdates } from './composables/useUpdates';
import { checkUpdate } from './services/updates';
import type { RuntimeInfo } from './services/runtime';
const RemindersPanel = defineAsyncComponent(() => import('./components/RemindersPanel.vue'));
const PlacesPanel = defineAsyncComponent(() => import('./components/PlacesPanel.vue'));
const props = defineProps<{ runtime: RuntimeInfo }>();
const section = ref<SettingsSection | null>(null); const dirty = ref(false); const activity = ref(false);
const shell = ref<InstanceType<typeof SettingsShell> | null>(null); const focusedReminderId = ref<string | null>(null);
const remindersPanel = ref<InstanceType<typeof RemindersPanel> | null>(null); const placesPanel = ref<InstanceType<typeof PlacesPanel> | null>(null);
const pendingActions = ref(0); const actionSettings = ref<NotificationSettings | null>(null);
const identity = ref<IdentityStatus | null>(null); const updates = useUpdates(() => checkUpdate(props.runtime.platform));
let stopReminderListener: UnlistenFn | null = null; let disposed = false;
let lastIdentityCheck = 0; let foregroundTimer: ReturnType<typeof setTimeout> | null = null;
async function refreshIdentity() { try { identity.value = await nodeServices.status(); lastIdentityCheck = Date.now(); } catch { identity.value = { state: 'pairingError', node: null, error: 'Armazenamento seguro indisponível. Abra Dispositivos para verificar.' }; } }
async function refreshPendingActions() { try {actionSettings.value = await notificationServices.settings(); pendingActions.value = actionSettings.value.pendingActions ?? 0;} catch { /* Keep the last local outbox count. */ } }
function navigate(value: SettingsSection) { dirty.value = false; section.value = value; }
function close() { section.value = null; dirty.value = false; void refreshIdentity(); void refreshPendingActions(); }
function foreground() {
  if (document.visibilityState !== 'visible' || foregroundTimer !== null) return;
  foregroundTimer = setTimeout(async () => { foregroundTimer = null; if (document.visibilityState !== 'visible') return; void updates.start(); try { const current = await transportServices.status(); if (current.transportState === 'offline') await transportServices.reconnect(); } catch { /* Native lifecycle also reconciles independently. */ } if (Date.now() - lastIdentityCheck > 30000) {void refreshIdentity();void refreshPendingActions();} }, 250);
}
function nativeBack() { if (shell.value?.dismissPending()) return; if (section.value === 'reminders' && remindersPanel.value?.requestBack()) return; if (section.value === 'places' && placesPanel.value?.requestBack()) return; if (section.value) shell.value?.requestClose(); }
function showReminder(id: unknown) { if (typeof id !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)) return; focusedReminderId.value = id; if (section.value) shell.value?.navigate('reminders'); else navigate('reminders'); }
function openReminder(event: Event) { showReminder((event as CustomEvent<{reminderId?:unknown}>).detail?.reminderId); }
async function subscribeReminderOpen() { try { const unlisten = await listen<{reminderId:string}>('aegis-reminder-open', event => showReminder(event.payload.reminderId)); if (disposed) unlisten(); else stopReminderListener = unlisten; } catch { /* Android DOM bridge remains available. */ } try { const pending = await invoke<{reminderId:string|null}>('node_consume_reminder_open'); if (!disposed) showReminder(pending.reminderId); } catch { /* Older runtimes have no stored deep link. */ } }
watch(section, value => { void invoke('node_settings_open', { open: value !== null }).catch(() => undefined); if (value === 'updates') void updates.start(); });
onMounted(() => { void subscribeReminderOpen(); void refreshIdentity(); void refreshPendingActions(); void updates.start(); document.addEventListener('visibilitychange', foreground); window.addEventListener('aegis-reminder-actions-changed', refreshPendingActions); window.addEventListener('aegis-back', nativeBack); window.addEventListener('aegis-reminder-open', openReminder); });
onUnmounted(() => { disposed = true; stopReminderListener?.(); document.removeEventListener('visibilitychange', foreground); window.removeEventListener('aegis-reminder-actions-changed', refreshPendingActions); window.removeEventListener('aegis-back', nativeBack); window.removeEventListener('aegis-reminder-open', openReminder); if (foregroundTimer !== null) clearTimeout(foregroundTimer); void invoke('node_settings_open', { open: false }).catch(() => undefined); });
</script>
<template>
  <ChatView :app-version="runtime.version" @activity-changed="activity = $event">
    <template #global-navigation><SettingsTrigger @click="navigate('devices')" /></template>
    <template #notice><div v-if="pendingActions" class="native-node-notice" role="status">{{ pendingActionLabel(pendingActions) }}. Adiamentos pendentes não têm horário offline garantido.<button @click="navigate('reminders')">Ver lembretes</button></div><div v-if="updates.visible.value && section !== 'updates'" class="native-node-notice" role="status"><span>Atualização da Aegis{{ updates.update.value ? ` ${updates.update.value.version}` : '' }}. Acompanhe em Atualizações.</span><button @click="navigate('updates')">Ver Atualizações</button></div><div v-if="identity && identity.state !== 'paired'" class="native-node-notice">{{ identity.state === 'disabled' ? 'Este dispositivo está desativado.' : identity.state === 'revoked' ? 'A identidade deixou de ser válida. Pareie este dispositivo novamente.' : 'Pareie este dispositivo para receber lembretes nativos.' }} <button @click="navigate('devices')">Abrir dispositivos</button></div></template>
  </ChatView>
  <SettingsShell ref="shell" :section="section" :dirty="dirty" @navigate="navigate" @close="close" @discard="dirty = false">
    <NodesPanel v-if="section === 'devices'" :platform="runtime.platform" @dirty="dirty = $event" />
    <RemindersPanel v-else-if="section === 'reminders'" ref="remindersPanel" :platform="runtime.platform" :focused-reminder-id="focusedReminderId" @dirty="dirty = $event" />
    <PlacesPanel v-else-if="section === 'places'" ref="placesPanel" :platform="runtime.platform" @dirty="dirty = $event" />
    <section v-else-if="section === 'updates'"><h2>Atualizações</h2><p class="settings-muted">Versão instalada: {{ runtime.version }}. O download e a instalação precisam de sua ação.</p><UpdateNotice :updates="updates" :work-active="activity" /><div class="settings-actions"><button :disabled="updates.state.value === 'checking' || updates.state.value === 'installing'" @click="updates.start(true)">{{ updates.state.value === 'checking' ? 'Verificando…' : 'Verificar atualizações' }}</button></div><p v-if="updates.checkedMessage.value" role="status">{{ updates.checkedMessage.value }}</p><p v-if="updates.checkedAt.value" class="settings-muted">Última consulta: {{ new Date(updates.checkedAt.value).toLocaleString('pt-BR') }}<span v-if="updates.diagnostic.value"> · Código: {{ updates.diagnostic.value.code }}</span></p><details v-if="runtime.platform === 'android'"><summary>Downloads antigos do navegador</summary><p>APKs baixados anteriormente pelo navegador continuam na pasta Downloads. Você pode removê-los uma vez pelo gerenciador de arquivos do Android. A Aegis não procura nem apaga arquivos pessoais nessa pasta.</p></details></section>
    <section v-else-if="section === 'about'">
      <div class="about-brand"><div><AegisMark /></div><div><h2>Sobre a Aegis</h2><span class="settings-badge">{{ runtime.version }}</span></div></div>
      <p class="settings-muted">Sua assistente pessoal, com modelos acessados por API.</p>
      <ReminderActionSync :settings="actionSettings" />
      <dl><div><dt>Aplicativo</dt><dd>{{ runtime.platform === 'android' ? 'Android' : runtime.platform === 'windows' ? 'Windows' : 'Plataforma não suportada' }}</dd></div><div><dt>Identidade do dispositivo</dt><dd>{{ identity?.state === 'paired' ? 'Pareada' : 'Verificar em Dispositivos' }}</dd></div></dl>
      <div class="settings-actions"><button @click="navigate('updates')">Abrir Atualizações</button></div>
      <details><summary>Diagnóstico técnico</summary><DiagnosticsPanel /></details>
    </section>
  </SettingsShell>
</template>
<style scoped>.about-brand { display: flex; gap: 16px; align-items: center; }.about-brand > div:first-child { width: 52px; height: 52px; }.about-brand h2 { margin-bottom: 8px; }</style>
