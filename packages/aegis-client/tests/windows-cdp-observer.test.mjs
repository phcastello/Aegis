import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { installWindowsIpcObserver } from './windows-cdp-observer.mjs';
function environment(result, fallback = false) {
  const callbacks = new Map();
  const internals = {};
  Object.defineProperty(internals, 'invoke', { value: () => 'native untouched' });
  Object.defineProperty(internals, 'callbacks', { value: callbacks });
  const messages = [];
  const window = { __TAURI_INTERNALS__: internals, chrome:{webview:{postMessage:raw=>messages.push(raw)}},
    fetch:async()=>{if(fallback)throw Error('Private network error must not be recorded');return {headers:{get:()=> 'ok'},clone:()=>({json:async()=>result})};} };
  const context = vm.createContext({window,URL,performance,document:{addEventListener(){}}});
  vm.runInContext(`(${installWindowsIpcObserver.toString()})({diagnosticCodes:['windows_notification_post_failed']})`, context);
  return {window,callbacks,messages};
}
test('observes real IPC transport while preserving read-only Tauri invoke and excluding request secrets', async()=>{
  const env=environment({status:'failed',transport:'live_websocket',diagnosticCode:'windows_notification_post_failed',credential:'must-not-appear'});
  await env.window.fetch('https://ipc.localhost/node_test_notification',{headers:{'Tauri-Invoke-Key':'private-key'},body:'private-payload'});
  assert.equal(env.window.__TAURI_INTERNALS__.invoke(),'native untouched');
  assert.equal(env.window.__aegisIpc.counts.node_test_notification,1);
  assert.equal(env.window.__aegisIpc.lastNotification.diagnosticCode,'windows_notification_post_failed');
  assert.doesNotMatch(JSON.stringify(env.window.__aegisIpc),/credential|private-key|private-payload|must-not-appear/);
});
test('unknown diagnostics and commands are redacted; ordinary network traffic is not counted as IPC', async()=>{
  const env=environment({status:'invalid-private-status',diagnosticCode:'private-prose'});
  await env.window.fetch('https://example.test/node_list');
  await env.window.fetch('https://ipc.localhost/private-secret-command');
  await env.window.fetch('https://ipc.localhost/node_test_notification');
  assert.equal(env.window.__aegisIpc.counts.node_list,undefined);
  assert.equal(env.window.__aegisIpc.counts.other,1);
  assert.equal(env.window.__aegisIpc.lastNotification.status,'failed');
  assert.equal(env.window.__aegisIpc.lastNotification.diagnosticCode,'unknown');
  assert.doesNotMatch(JSON.stringify(env.window.__aegisIpc),/private-prose|private-secret-command|invalid-private-status/);
});
test('postMessage fallback counts commands and observes only the selected native result callback',async()=>{
  const env=environment(null,true);let returned;
  env.callbacks.set(4,value=>{returned=value.status;});env.callbacks.set(5,()=>{});
  const raw=JSON.stringify({cmd:'node_test_notification',callback:4,error:5,payload:{token:'not-recorded'},__TAURI_INVOKE_KEY__:'private'});
  env.window.chrome.webview.postMessage(raw);
  env.callbacks.get(4)({status:'success',transport:'live_websocket',token:'not-recorded'});
  assert.equal(returned,'success');assert.equal(env.messages[0],raw);
  assert.equal(env.window.__aegisIpc.counts.node_test_notification,1);
  assert.equal(env.window.__aegisIpc.lastNotification.status,'success');
  assert.equal(env.window.__aegisIpc.bridgeWrapped,true);
  assert.doesNotMatch(JSON.stringify(env.window.__aegisIpc),/not-recorded|private|payload|token/);
});
