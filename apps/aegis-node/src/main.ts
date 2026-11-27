import { invoke } from '@tauri-apps/api/core';
import { createApp } from 'vue';
import { openUrl } from '@tauri-apps/plugin-opener';
import { configureClient } from '@aegis/client/services/clientEnvironment';
import { diagnosticServices } from './services/runtime';
import App from './App.vue';
import { acquireNativeAudio, nativeSpeech } from './services/handsFree';
import '@aegis/client/styles.css';
import './styles.css';

async function start(): Promise<void> {
  // The classic bootstrap exposes a visible update notice before the bundle
  // loads. Even if this older engine parses the module, leave that notice intact.
  if (document.getElementById('app')?.getAttribute('data-aegis-webview-unsupported') === 'true') return;
  const runtime = await diagnosticServices.runtimeInfo();
  if (!runtime.backendUrl) throw new Error('URL do backend inválida. Verifique AEGIS_NODE_API_BASE_URL.');
  configureClient({ apiBaseUrl: runtime.backendUrl, externalLinks: 'system', openExternalUrl: openUrl, processSessionId: runtime.processSessionId, dismissKeyboard: () => invoke<void>('node_hide_keyboard'),
    voiceMeterProfile: runtime.platform === 'android' ? 'mobile' : 'desktop',
    isForeground: runtime.platform === 'android' ? () => invoke<boolean>('node_read_visibility') : undefined,
    takeSharedAttachments: runtime.platform === 'android' ? () => invoke('node_take_shared_attachments') : undefined,
    nativeSpeech: runtime.platform === 'android' ? nativeSpeech : undefined,
    acquireAudio: ['android', 'windows'].includes(runtime.platform) ? acquireNativeAudio : undefined });
  createApp(App, { runtime }).mount('#app');
}
void start().catch((error: unknown) => {
  const root = document.querySelector('#app');
  if (root) root.textContent = error instanceof Error ? error.message : 'Não foi possível iniciar a Aegis.';
});
