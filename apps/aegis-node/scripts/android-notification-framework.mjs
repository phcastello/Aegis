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
const test = resolve(root, `androidTest/universal/${buildType}/app-universal-${buildType}-androidTest.apk`);
assert(existsSync(app) && existsSync(test), 'Build target and instrumentation APKs first');
run(['install', '-r', app]); run(['install', '-r', test]);
const api = Number(run(['shell','getprop','ro.build.version.sdk']).trim());
const permission = granted => {
  if (api >= 33) run(['shell','pm',granted?'grant':'revoke','com.aegis.node','android.permission.POST_NOTIFICATIONS']);
  else run(['shell','cmd','appops','set','com.aegis.node','POST_NOTIFICATION',granted?'allow':'deny']);
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
  if (bootstrap) {
    run(['reverse', 'tcp:18104', 'tcp:18104']);
    instrumentation('NodeNotificationLiveTest', ['-e','pairingCode',readFileSync(bootstrap,'utf8').trim()]);
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
  try { crash = run(['logcat','-d','-s','AndroidRuntime:E','libc:F','*:S']); } catch { console.log('phase=crash_type_read reason=unavailable'); }
  for (const errorType of safeAndroidCrashTypes(crash)) console.log(`AndroidNativeTest phase=activity_runtime errorType=${errorType}`);
}
console.log(`Android API ${api} ${buildType}: renderer, active notification, permission and bounded diagnostics PASS${bootstrap?' including native live WebSocket/JNI':''}`);
