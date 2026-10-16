import { createApp } from 'vue';
import { openUrl } from '@tauri-apps/plugin-opener';
import { configureClient } from '@aegis/client/services/clientEnvironment';
import { diagnosticServices } from './services/runtime';
import App from './App.vue';
import '@aegis/client/styles.css';
import './styles.css';

async function start(): Promise<void> {
  const runtime = await diagnosticServices.runtimeInfo();
  if (!runtime.backendUrl) throw new Error('URL do backend inválida. Verifique AEGIS_NODE_API_BASE_URL.');
  configureClient({ apiBaseUrl: runtime.backendUrl, externalLinks: 'system', openExternalUrl: openUrl });
  createApp(App, { runtime }).mount('#app');
}
void start().catch((error: unknown) => {
  const root = document.querySelector('#app');
  if (root) root.textContent = error instanceof Error ? error.message : 'Não foi possível iniciar a Aegis.';
});
