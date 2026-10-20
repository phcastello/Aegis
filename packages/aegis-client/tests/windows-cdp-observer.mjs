// Passed to CDP as a self-contained function. Tauri's invoke/ipc properties are
// read-only; observe the browser transport without replacing native contracts.
export function installWindowsIpcObserver(options = {}) {
  const state = window.__aegisIpc = { counts: {}, lastNotification: null, fetchWrapped: false, bridgeWrapped: false, fetchFailed: false };
  const codes = options.diagnosticCodes ?? [];
  const statuses = ['success','expired','unsupported','permission_denied','failed','duplicate','timeout','busy','unavailable','no_eligible_node','accepted'];
  const allowed = new Set(['runtime_info','check_backend','check_android_update','install_android_update','node_status','node_pair','node_list','node_rename','node_set_enabled','node_set_target_priority','node_revoke','node_remove','node_create_pairing_code','node_resolve_target','node_notification_settings','node_request_notification_permission','node_test_notification','node_set_autostart','node_exit','node_transport_status','node_transport_reconnect','node_set_reminders','node_reminders','node_places','node_location','node_geofences','node_settings_open','node_consume_reminder_open']);
  const command = value => typeof value === 'string' && /^[a-z0-9_:|\-]{1,100}$/i.test(value) ? allowed.has(value)?value:'other' : null;
  function count(cmd) { if (cmd) state.counts[cmd] = (state.counts[cmd] ?? 0) + 1; }
  function record(data, started, success) {
    state.lastNotification = { phase:success?'native_result':'ipc_rejected', status:success&&statuses.includes(data?.status)?data.status:'failed',
      transport:['live_websocket','fcm'].includes(data?.transport)?data.transport:null,
      diagnosticCode:codes.includes(data?.diagnosticCode)?data.diagnosticCode:data?.diagnosticCode?'unknown':null,
      commandId:typeof data?.commandId==='string'&&/^[0-9a-f-]{36}$/i.test(data.commandId)?data.commandId:null,
      durationMs:Math.round(performance.now()-started) };
  }
  const originalFetch = window.fetch.bind(window);
  window.fetch = async (resource, init) => {
    let cmd = null;
    try { const url = new URL(typeof resource === 'string' || resource instanceof URL ? String(resource) : resource.url); if (url.hostname === 'ipc.localhost') cmd = command(decodeURIComponent(url.pathname.slice(1))); } catch {}
    count(cmd); const started = performance.now();
    try {
      const response = await originalFetch(resource, init);
      if (cmd === 'node_test_notification') {
        try { record(await response.clone().json(), started, response.headers.get('Tauri-Response') === 'ok'); }
        catch { record(null, started, false); }
      }
      return response;
    } catch (failure) { if(cmd)state.fetchFailed=true; throw failure; }
  };
  state.fetchWrapped = true;
  function bridge() {
    if (state.bridgeWrapped || !window.chrome?.webview?.postMessage) return;
    const target = window.chrome.webview, original = target.postMessage.bind(target);
    const wrapped = raw => {
      try {
        const message = typeof raw === 'string' ? JSON.parse(raw) : raw;
        const cmd = command(message?.cmd); count(cmd);
        if (cmd === 'node_test_notification') {
          const callbacks = window.__TAURI_INTERNALS__?.callbacks, started = performance.now();
          for (const [id, success] of [[message.callback,true],[message.error,false]]) {
            const callback = callbacks?.get(id);
            if (callback) callbacks.set(id, data => { record(data, started, success); return callback(data); });
          }
        }
      } catch { /* Forward the original transport without logging any payload. */ }
      return original(raw);
    };
    try { target.postMessage = wrapped; state.bridgeWrapped = target.postMessage === wrapped; } catch {}
  }
  bridge(); document.addEventListener('DOMContentLoaded', bridge, { once:true });
}
