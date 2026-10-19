// Only a disposable emulator/test APK is supported. This never operates on a physical Node.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import { resolve } from 'node:path';
import {safeAndroidCrashTypes} from './android-safe-crash.mjs';
import {captureLegacyLocation,enableLegacyLocation,restoreLegacyLocation,safeLegacyLocation} from './android-legacy-location.mjs';
const [buildType = 'debug', bootstrap] = process.argv.slice(2);
assert(['debug', 'release'].includes(buildType));
const serial = process.env.AEGIS_TEST_EMULATOR_SERIAL ?? 'emulator-5554';
assert(/^emulator-\d{4}$/.test(serial), 'Only a disposable emulator is allowed');
const adb = process.env.ANDROID_HOME ? resolve(process.env.ANDROID_HOME, 'platform-tools/adb') : 'adb';
const run = (args, timeout=180000) => {
  const result = spawnSync(adb, ['-s', serial, ...args], {encoding:'utf8', timeout});
  assert.equal(result.status, 0, 'adb operation failed');
  return result.stdout;
};
const end = Date.now() + 900000;
while (true) {
  if (process.env.AEGIS_TEST_EMULATOR_PID) {
    try { process.kill(Number(process.env.AEGIS_TEST_EMULATOR_PID), 0); } catch { throw Error('emulator_process_exited_before_boot'); }
  }
  try { if (run(['shell', 'getprop', 'sys.boot_completed']).trim() === '1') break; } catch {}
  assert(Date.now() < end, 'Emulator boot timeout');
  await new Promise(r => setTimeout(r, 1000));
}
const root = resolve(import.meta.dirname, '../src-tauri/gen/android/app/build/outputs/apk');
const app = resolve(root, `universal/${buildType}/app-universal-${buildType}.apk`);
const test = buildType === 'release'
  ? resolve(import.meta.dirname, '../src-tauri/gen/android/notificationFrameworkTest/build/outputs/apk/release/notificationFrameworkTest-release.apk')
  : resolve(root, `androidTest/universal/${buildType}/app-universal-${buildType}-androidTest.apk`);
assert(existsSync(app) && existsSync(test), 'Build target and instrumentation APKs first');
run(['install', '-r', app]); run(['install', '-r', test]);
const api = Number(run(['shell','getprop','ro.build.version.sdk']).trim());
assert([26,35].includes(api), 'Use a supported disposable framework test emulator');
let appUid;
if (api === 26) {
  run(['root']); run(['wait-for-device']);
  const installed = run(['shell','dumpsys','package','com.aegis.node']);
  appUid = installed.match(/\buserId=(\d+)\b/)?.[1];
  assert(appUid, 'Fixture package UID missing');
}
const permission = granted => {
  if (api >= 33) run(['shell','pm',granted?'grant':'revoke','com.aegis.node','android.permission.POST_NOTIFICATIONS']);
  else {
    // API 26 INotificationManager transaction 9 updates the actual package notification
    // ranking permission. AppOps alone does not change areNotificationsEnabled().
    // Root is available only on this disposable google_apis userdebug emulator.
    const reply = run(['shell','service','call','notification','9','s16','com.aegis.node','i32',appUid,'i32',granted?'1':'0']);
    assert(!/Exception|ffffffff/.test(reply), 'Framework permission update failed');
  }
};
const instrumentation = (testClass, extra = []) => {
  // The live gate now includes four real GMS registrations/acquisitions plus
  // authenticated failure/recovery receipts. Keep its host watchdog explicit.
  const timeout=testClass==='NodeNotificationLiveTest'?480000:testClass==='NodeGeofenceReceiverFrameworkTest'?420000:180000;
  const output = run(['shell','am','instrument','-w','-r','-e','class',`com.aegis.node.${testClass}`,...extra,'com.aegis.node.test/androidx.test.runner.AndroidJUnitRunner'],timeout);
  // Test assertions contain only fixed phases/codes. Production logcat is never dumped.
  process.stdout.write(output);
  assert.match(output, /OK \(\d+ tests?\)/, 'Real Android framework test failed');
  assert.doesNotMatch(output, /FAILURES!!!|INSTRUMENTATION_FAILED/);
};
let livePassed=false;
let receiverPassed=false;
let interruptionPassed=false;
let bootPassed=false;
const preservedCheckpoints=new Set();
const advancedHybridPhases=['hybrid_shared_session_two_arrivals_inner_exit_50_200','hybrid_occurrence_outbox_partial_commit_recovery','hybrid_cancellation_during_active_session_removed_receipt','hybrid_inflight_acquisition_cancelled_process_alive','hybrid_service_lifecycle_bounded_denied_arrival','hybrid_foreground_promoted','hybrid_start_denied_before_service'];
const legacyLocationPhases=['hybrid_legacy_nlp_resolution_opened','hybrid_legacy_nlp_consent_accepted','hybrid_legacy_nlp_settings_ready'];
const hybridStartupPhases=['hybrid_scenario_outside_start','hybrid_scenario_denied_start','hybrid_scenario_start_denied_start','hybrid_scenario_provider_error_start','hybrid_scenario_inside_start'];
const liveSyncPhases=['outbox_foreground_temporary_lost_response_permanent_old_action','cancellation_local_receipt_and_manifest_failure_reconciliation','outbox_geo_dependency_foreground_worker_deferred'];
// API 26's fixture uses high accuracy for real GMS registration and acquisition.
// Change only this disposable fixture and restore even a missing original value.
const originalLocationState=bootstrap && api===26?captureLegacyLocation(run):undefined;
const restoreCodes={
  restoreSignal:new Set(['boot_completed','package_replaced']),
  restoreWorkerState:new Set(['started','retry_pending','completed','failed','retry_exhausted']),
  restoreResult:new Set(['started','unbound','identity_unavailable','scope_changed','completed']),
  status:new Set(['pending_sync','active','installed','recoverable_error','unavailable','disabled','waiting_baseline','ready']),
  error:new Set(['location_provider_unavailable','location_permission_required','precise_location_required','background_location_required','location_services_disabled','native_registration_failed'])
};
function safeRestoreState(xml) {
  const state={};
  for(const name of ['restoreSignalBootCount','restoreWorkerBootCount','lastRestoreCompletedBootCount','restoreSignalAt','restoreWorkerStartedAt','lastRestoreCompletedAt']) {
    const match=xml.match(new RegExp(`<(?:int|long) name="${name}" value="(-?\\d+)"\\s*/>`));
    state[name]=match && Number.isSafeInteger(Number(match[1]))?Number(match[1]):-1;
  }
  for(const [name,allowed] of Object.entries(restoreCodes)) {
    const value=xml.match(new RegExp(`<string name="${name}">([^<]*)</string>`))?.[1];
    state[name]=allowed.has(value)?value:'unknown';
  }
  return state;
}
async function waitForActualBootRestore(bootCount) {
  // Starting am instrument kills any running target process, including its boot
  // worker. Observe durable completion through root before launching verification.
  // Raw preferences can contain credentials/geometries and must never be printed.
  const deadline=Date.now()+115000;
  let state=safeRestoreState('');
  while(true) {
    try {state=safeRestoreState(run(['shell','cat','/data/user/0/com.aegis.node/shared_prefs/aegis-geofences-v1.xml'],5000));}catch{}
    if(state.restoreSignal==='boot_completed' && state.restoreSignalBootCount===bootCount && state.restoreWorkerBootCount===bootCount && state.lastRestoreCompletedBootCount===bootCount && state.restoreWorkerStartedAt>=state.restoreSignalAt && state.lastRestoreCompletedAt>=state.restoreWorkerStartedAt && ['completed','retry_pending'].includes(state.restoreWorkerState))return;
    if(Date.now()>=deadline)throw Error(`actual_boot_restore_timeout ${JSON.stringify(state)}`);
    await new Promise(resolve=>setTimeout(resolve,1000));
  }
}
async function refreshBootstrap(kind) {
  const response=await fetch('http://127.0.0.1:18104/fixture/refresh-bootstrap', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({kind}),signal:AbortSignal.timeout(10000)});
  assert(response.ok,'Disposable bootstrap refresh failed');
}
try { run(['logcat', '-c']); } catch { console.log('phase=logcat_clear reason=unavailable'); }
try {
  if(originalLocationState!==undefined) {
    console.log(`AndroidLegacyLocation before=${JSON.stringify(safeLegacyLocation(run))}`);
    enableLegacyLocation(run,originalLocationState);
    console.log(`AndroidLegacyLocation configured=${JSON.stringify(safeLegacyLocation(run))}`);
  }
  permission(true);
  instrumentation('NodeNotificationFrameworkTest');
  permission(false);
  instrumentation('NodeNotificationPermissionDeniedTest');
  if (bootstrap) {
    permission(true);
    run(['reverse', 'tcp:18104', 'tcp:18104']);
    instrumentation('NodeGeofenceReceiverFrameworkTest', ['-e','geofencePairingCode',readFileSync(bootstrap+'.geofence','utf8').trim()]);
    receiverPassed=true;
    // Revocation kills a target with instrumentation attached. Exercise actual OS
    // permissions before the first interruption-test process is launched.
    run(['shell','am','force-stop','com.aegis.node']);
    if(api>=29)run(['shell','pm','revoke','com.aegis.node','android.permission.ACCESS_BACKGROUND_LOCATION']);
    run(['shell','pm','revoke','com.aegis.node','android.permission.ACCESS_FINE_LOCATION']);
    run(['shell','pm','revoke','com.aegis.node','android.permission.ACCESS_COARSE_LOCATION']);
    instrumentation('NodeHybridProcessInterruptionFrameworkTest#prepareSessionBeforeProcessDeath', ['-e','processPairingCode',readFileSync(bootstrap+'.interruption','utf8').trim()]);
    run(['shell','am','force-stop','com.aegis.node']);
    instrumentation('NodeHybridProcessInterruptionFrameworkTest#verifyNoRestartAfterProcessDeath');
    interruptionPassed=true;
    await refreshBootstrap('boot');
    instrumentation('NodeHybridBootRecoveryFrameworkTest#prepareBeforeBoot', ['-e','bootPairingCode',readFileSync(bootstrap+'.boot','utf8').trim()]);
    // Reboot clears logcat. Preserve only closed checkpoint names, never raw logs.
    const beforeBoot=run(['logcat','-d','-v','raw','-s','AegisNodeNativeTest:I','*:S']);
    for(const phase of ['geofence_receiver_native_transition_idempotent_saver_reconcile','hybrid_actual_permission_denial','hybrid_process_interruption_recovered',...advancedHybridPhases,...legacyLocationPhases])if(beforeBoot.split('\n').includes(`phase=${phase} errorType=Checkpoint`))preservedCheckpoints.add(phase);
    const previousBoot=Number(run(['shell','settings','get','global','boot_count']).trim());
    const userState=run(['shell','dumpsys','package','com.aegis.node']).match(/User 0:([^\r\n]*)/)?.[1];
    assert(userState && /\bstopped=false\b/.test(userState),'Prepared fixture must be eligible for the actual boot broadcast');
    run(['reboot']);
    const bootDeadline=Date.now()+240000;
    while(true) {
      try {
        if(run(['shell','getprop','sys.boot_completed']).trim()==='1' && Number(run(['shell','settings','get','global','boot_count']).trim())>previousBoot)break;
      }catch{}
      assert(Date.now()<bootDeadline,'Disposable emulator reboot timeout');
      await new Promise(resolve=>setTimeout(resolve,1000));
    }
    run(['root']);run(['wait-for-device']);
    run(['reverse','tcp:18104','tcp:18104']);
    run(['shell','input','keyevent','82']);
    permission(true);
    const currentBoot=Number(run(['shell','settings','get','global','boot_count']).trim());
    await waitForActualBootRestore(currentBoot);
    instrumentation('NodeHybridBootRecoveryFrameworkTest#verifyAfterBoot');
    bootPassed=true;
  }
  // API 26 additionally covers the real renderer/permission. The complete live UI/JNI
  // gate uses API 35; the stock legacy emulator's Activity/WebView fixture crashes.
  const live = Boolean(bootstrap) && api >= 33;
  if (live) {
    // Earlier native fixtures grant location and post real notifications. Revoke
    // outside instrumentation so the live permission lifecycle starts denied.
    run(['shell','am','force-stop','com.aegis.node']);
    if(api>=29)run(['shell','pm','revoke','com.aegis.node','android.permission.ACCESS_BACKGROUND_LOCATION']);
    run(['shell','pm','revoke','com.aegis.node','android.permission.ACCESS_FINE_LOCATION']);
    run(['shell','pm','revoke','com.aegis.node','android.permission.ACCESS_COARSE_LOCATION']);
    permission(false);
    if (api < 33) permission(true);
    run(['reverse', 'tcp:18104', 'tcp:18104']);
    await refreshBootstrap('live');
    // MonitoringInstrumentation otherwise destroys the last Tauri Activity before
    // publishing the JUnit result, causing Tao to exit the instrumented process.
    instrumentation('NodeNotificationLiveTest', ['-e','waitForActivitiesToComplete','false','-e','pairingCode',readFileSync(bootstrap,'utf8').trim(),'-e','replacementCode',readFileSync(bootstrap+'.replacement','utf8').trim()]);
    livePassed=true;
  }
} finally {
  if(originalLocationState!==undefined)restoreLegacyLocation(run,originalLocationState);
  permission(true);
  let logs = '';
  try { logs = run(['logcat','-d','-s','AegisNodeNotification:E','*:S']); } catch { console.log('phase=logcat_read reason=unavailable'); }
  for (const line of logs.split('\n')) {
    const safe = line.match(/phase=[a-z_]{1,32} errorType=[A-Za-z0-9_]{1,64}$/);
    if (safe) console.log(`AegisNodeNotification ${safe[0]}`);
  }
  let crash = '';
  try { crash = run(['logcat','-d','-b','all','-s','AndroidRuntime:E','libc:F','art:F','DEBUG:F','RustStdoutStderr:I','AegisNodeNativeTest:I','*:S']); } catch { console.log('phase=crash_type_read reason=unavailable'); }
  crash+='\n'+[...preservedCheckpoints].map(phase=>`AegisNodeNativeTest phase=${phase} errorType=Checkpoint`).join('\n');
  for (const errorType of safeAndroidCrashTypes(crash)) console.log(`AndroidNativeTest phase=activity_runtime errorType=${errorType}`);
  const phases=new Set();
  for(const phase of [...advancedHybridPhases,...legacyLocationPhases,...liveSyncPhases,...hybridStartupPhases])if(crash.includes(`phase=${phase} errorType=Checkpoint`)) {
    console.log(`AegisNodeNativeTest phase=${phase} errorType=Checkpoint`);
    phases.add(phase);
  }
  for(const phase of ['hybrid_actual_permission_denial','hybrid_process_interruption_recovered','hybrid_reboot_preserves_identity_no_transition'])if(crash.includes(`phase=${phase} errorType=Checkpoint`)) {
    console.log(`AegisNodeNativeTest phase=${phase} errorType=Checkpoint`);
    phases.add(phase);
  }
  if (/AegisNodeNativeTest.*phase=geofence_receiver_native_transition_idempotent_saver_reconcile errorType=Checkpoint/.test(crash)) {
    console.log('AegisNodeNativeTest phase=geofence_receiver_native_transition_idempotent_saver_reconcile errorType=Checkpoint');
    phases.add('geofence_receiver_native_transition_idempotent_saver_reconcile');
  }
  for (const line of crash.split('\n')) {
    const checkpoint = line.includes('AegisNodeNativeTest') && line.match(/phase=(live_activity_launch|live_activity_ready|live_webview_ready|live_runtime_ready|live_paired|live_online|live_permission_denied|live_permission_granted|live_notification_active|live_cleanup_started|live_notification_cancelled|live_node_revoke_started|live_node_revoked|live_cleanup_completed|location_bridge_framework|update_archive_source_inspection|notification_live_authenticated_clock|geofence_authenticated_native_http|geofence_google_registration_task_succeeded|geofence_google_registration_unavailable|geofence_pre_activation_cache_ignored|geofence_union_offline_outbox_repeat_persisted|geofence_restore_no_duplicate|geofence_receipt_503_install|geofence_receipt_disconnect_install|geofence_receipt_503_restore|geofence_receipt_disconnect_restore|withdrawal_receipt_fcm_offline_replay_lost_response|native_identity_reenabled|native_identity_scope_reset|tao_activity_jni|runtime_state|native_bridge|notification_plugin_init|wry_webview|tauri_runtime) errorType=(Checkpoint|JavaException|NativePanic)$/);
    if (checkpoint) console.log(`AegisNodeNativeTest ${checkpoint[0]}`);
    if(checkpoint?.[2]==='Checkpoint')phases.add(checkpoint[1]);
  }
  // Retain only the fixed residential adapter checkpoint, never Location data.
  if (/AegisNodeNativeTest.*phase=residential65_adapter_fresh_accuracy_timeout_saver errorType=Checkpoint/.test(crash)) {
    console.log('AegisNodeNativeTest phase=residential65_adapter_fresh_accuracy_timeout_saver errorType=Checkpoint');
    phases.add('residential65_adapter_fresh_accuracy_timeout_saver');
  }
  if(livePassed && phases.has('geofence_google_registration_task_succeeded'))assert(phases.has('residential65_adapter_fresh_accuracy_timeout_saver'),'Residential native adapter validation required');
  if (/AegisNodeNativeTest.*phase=reminder_webview_scroll_edit_keyboard_native_back errorType=Checkpoint/.test(crash)) {
    console.log('AegisNodeNativeTest phase=reminder_webview_scroll_edit_keyboard_native_back errorType=Checkpoint');
    phases.add('reminder_webview_scroll_edit_keyboard_native_back');
  }
  if(livePassed && phases.has('geofence_google_registration_task_succeeded'))assert(phases.has('reminder_webview_scroll_edit_keyboard_native_back'),'Real WebView editor, keyboard and native Back validation required');
  // External cleanup happens after instrumentation has published success/failure.
  run(['shell','am','force-stop','com.aegis.node']);
  if(receiverPassed)assert(phases.has('geofence_receiver_native_transition_idempotent_saver_reconcile'),'Native receiver, saver, persisted occurrence and authenticated reconciliation checkpoint required');
  if(receiverPassed)for(const phase of advancedHybridPhases)assert(phases.has(phase),'Shared session, real-boundary exit, partial occurrence outbox and active cancellation checkpoints required');
  if(receiverPassed && api===26)assert(phases.has('hybrid_legacy_nlp_settings_ready'),'Real Google location settings and stable usable network provider required on API 26');
  if(interruptionPassed)for(const phase of ['hybrid_actual_permission_denial','hybrid_process_interruption_recovered'])assert(phases.has(phase),'Actual OS permission denial and process interruption with no automatic monitoring restart required');
  if(bootPassed)assert(phases.has('hybrid_reboot_preserves_identity_no_transition'),'Actual BOOT_COMPLETED restore must preserve identity and avoid retroactive occurrences or session resurrection');
  if(livePassed){
    for(const phase of liveSyncPhases)assert(phases.has(phase),'Authenticated local cancellation, temporal outbox and foreground occurrence dependency recovery checkpoints required');
    assert(phases.has('withdrawal_receipt_fcm_offline_replay_lost_response'),'Native withdrawal receipt regression checkpoint required');
    if(phases.has('geofence_google_registration_task_succeeded'))for(const mode of ['503','disconnect'])for(const path of ['install','restore'])assert(phases.has(`geofence_receipt_${mode}_${path}`),'Installed geofence must survive backend receipt failure');
  }
}
console.log(`Android API ${api} ${buildType}: renderer, active notification, permission and bounded diagnostics PASS${bootstrap?' including native geofence receiver fixture':''}${bootstrap && api >= 33?' and native live WebSocket/JNI':''}`);
