import { createApp } from 'vue';
import { registerSW } from 'virtual:pwa-register';
import App from './App.vue';
import '@aegis/client/styles.css';
import { configureClient } from '@aegis/client/services/clientEnvironment';

configureClient({ apiBaseUrl: import.meta.env.VITE_AEGIS_API_BASE_URL ?? '', externalLinks: 'browser' });

registerSW({
  immediate: true,
  onRegisteredSW(_url, registration) {
    // Check once on opening, including when an older worker serves cached assets.
    // An offline check must not interrupt the app or queued notification actions.
    if (registration && !registration.installing && navigator.onLine)
      void registration.update().catch(() => undefined);
  }
});

createApp(App).mount('#app');
