// Only a disposable emulator/test APK is supported. This never operates on a physical Node.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import { resolve } from 'node:path';
import {safeAndroidCrashTypes} from './android-safe-crash.mjs';
const [buildType = 'debug', bootstrap] = process.argv.slice(2);
assert(['debug', 'release'].includes(buildType));
const serial = process.env.AEGIS_TEST_EMULATOR_SERIAL ?? 'emulator-5554';
assert(/^emulator-\d{4}$/.test(serial), 'Only a disposable emulator is allowed');
const adb = process.env.ANDROID_HOME ? resolve(process.env.ANDROID_HOME, 'platform-tools/adb') : 'adb';
const run = args => {
  const result = spawnSync(adb, ['-s', serial, ...args], {encoding:'utf8', timeout:180000});
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
  const output = run(['shell','am','instrument','-w','-r','-e','class',`com.aegis.node.${testClass}`,...extra,'com.aegis.node.test/androidx.test.runner.AndroidJUnitRunner']);
  // Test assertions contain only fixed phases/codes. Production logcat is never dumped.
  process.stdout.write(output);
  assert.match(output, /OK \(\d+ tests?\)/, 'Real Android framework test failed');
  assert.doesNotMatch(output, /FAILURES!!!|INSTRUMENTATION_FAILED/);
};
try { run(['logcat', '-c']); } catch { console.log('phase=logcat_clear reason=unavailable'); }
try {
  permission(true);
  instrumentation('NodeNotificationFrameworkTest');
  permission(false);
  instrumentation('NodeNotificationPermissionDeniedTest');
  // API 26 additionally covers the real renderer/permission. The complete live UI/JNI
  // gate uses API 35; the stock legacy emulator's Activity/WebView fixture crashes.
  const live = Boolean(bootstrap) && api >= 33;
  if (live) {
    if (api < 33) permission(true);
    run(['reverse', 'tcp:18104', 'tcp:18104']);
    // MonitoringInstrumentation otherwise destroys the last Tauri Activity before
    // publishing the JUnit result, causing Tao to exit the instrumented process.
    instrumentation('NodeNotificationLiveTest', ['-e','waitForActivitiesToComplete','false','-e','pairingCode',readFileSync(bootstrap,'utf8').trim()]);
  }
} finally {
  permission(true);
  let logs = '';
  try { logs = run(['logcat','-d','-s','AegisNodeNotification:E','*:S']); } catch { console.log('phase=logcat_read reason=unavailable'); }
  for (const line of logs.split('\n')) {
    const safe = line.match(/phase=[a-z_]{1,32} errorType=[A-Za-z0-9_]{1,64}$/);
    if (safe) console.log(`AegisNodeNotification ${safe[0]}`);
  }
  let crash = '';
  try { crash = run(['logcat','-d','-b','all','-s','AndroidRuntime:E','libc:F','art:F','DEBUG:F','RustStdoutStderr:I','AegisNodeNativeTest:I','*:S']); } catch { console.log('phase=crash_type_read reason=unavailable'); }
  for (const errorType of safeAndroidCrashTypes(crash)) console.log(`AndroidNativeTest phase=activity_runtime errorType=${errorType}`);
  for (const line of crash.split('\n')) {
    const checkpoint = line.includes('AegisNodeNativeTest') && line.match(/phase=(live_activity_launch|live_activity_ready|live_webview_ready|live_runtime_ready|live_paired|live_online|live_permission_denied|live_permission_granted|live_notification_active|live_cleanup_started|live_notification_cancelled|live_node_revoke_started|live_node_revoked|live_cleanup_completed|tao_activity_jni|runtime_state|native_bridge|notification_plugin_init|wry_webview|tauri_runtime) errorType=(Checkpoint|JavaException|NativePanic)$/);
    if (checkpoint) console.log(`AegisNodeNativeTest ${checkpoint[0]}`);
  }
  // External cleanup happens after instrumentation has published success/failure.
  run(['shell','am','force-stop','com.aegis.node']);
}
console.log(`Android API ${api} ${buildType}: renderer, active notification, permission and bounded diagnostics PASS${bootstrap && api >= 33?' including native live WebSocket/JNI':''}`);
