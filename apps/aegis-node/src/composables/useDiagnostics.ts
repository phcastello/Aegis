import { computed, ref } from 'vue';
import { diagnosticServices, type BackendStatus, type DiagnosticServices, type RuntimeInfo } from '../services/runtime.js';

export function useDiagnostics(services: DiagnosticServices = diagnosticServices) {
  const runtime = ref<RuntimeInfo | null>(null);
  const backend = ref<BackendStatus>('unavailable');
  const checking = ref(false);
  const message = ref<string | null>(null);

  const platformLabel = computed(() => {
    switch (runtime.value?.platform) {
      case 'android': return 'Android';
      case 'windows': return 'Windows';
      case 'unsupported': return 'Unsupported';
      default: return 'Unknown';
    }
  });
  const backendLabel = computed(() => backend.value === 'connected' ? 'Connected' : 'Unavailable');

  async function retry(): Promise<void> {
    if (checking.value) return;
    checking.value = true;
    // Clear the previous result while rechecking; do not display stale Connected.
    backend.value = 'unavailable';
    message.value = null;
    try {
      runtime.value = await services.runtimeInfo();
      const result = await services.checkBackend();
      backend.value = result.status;
      message.value = result.message;
    } catch {
      message.value = 'Native diagnostics are unavailable. Open the installed Tauri app and retry.';
    } finally {
      checking.value = false;
    }
  }

  return { runtime, backend, checking, message, platformLabel, backendLabel, retry };
}
