import { onBeforeUnmount, onMounted, type Ref } from 'vue';
import { locationApi, type LocationPermissions } from '../services/domains.js';
import type { Platform } from '../services/runtime.js';
// Android's background-location Settings screen returns independently of the
// WebView. Recheck native permission state on return while its consumer exists.
export function useLocationPermissionRefresh(platform: Platform, permissions: Ref<LocationPermissions | null>) {
  let refreshing = false; let lastStarted = 0;
  async function refresh() {
    if (platform !== 'android' || document.visibilityState !== 'visible' || refreshing || Date.now() - lastStarted < 750) return;
    refreshing = true; lastStarted = Date.now();
    try { permissions.value = await locationApi<LocationPermissions>('permissions'); }
    catch { /* Preserve the last known state; an explicit action exposes errors. */ }
    finally { refreshing = false; }
  }
  onMounted(() => { if (platform === 'android') { document.addEventListener('visibilitychange', refresh); window.addEventListener('focus', refresh); } });
  onBeforeUnmount(() => { document.removeEventListener('visibilitychange', refresh); window.removeEventListener('focus', refresh); });
}
