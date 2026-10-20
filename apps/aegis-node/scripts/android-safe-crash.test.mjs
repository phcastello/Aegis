import {test} from 'node:test';
import assert from 'node:assert/strict';
import {safeAndroidCrashTypes,safeAndroidCrashSummaries} from './android-safe-crash.mjs';

test('crash evidence retains only bounded class/signal types, never arbitrary content', () => {
  const secret = 'token=private notification title /private/device/path';
  const evidence = safeAndroidCrashTypes(`
10-04 20:00:00 123 456 E AndroidRuntime: java.lang.RuntimeException: ${secret}
10-04 20:00:00 123 456 E AndroidRuntime: Caused by: java.lang.UnsatisfiedLinkError: ${secret}
10-04 20:00:00 123 456 E AndroidRuntime:     at com.aegis.node.MainActivity.onCreate(MainActivity.kt:17)
10-04 20:00:00 123 456 F libc: Fatal signal 6 (SIGABRT), code -1 ${secret}
10-04 20:00:00 123 456 E AndroidRuntime: ${secret}
10-04 20:00:00 123 456 E AndroidRuntime: java.lang.RuntimeException: ${secret}
10-04 20:00:00 123 456 F art: JNI DETECTED ERROR IN APPLICATION: ${secret}
10-04 20:00:00 123 456 F art: Pending exception java.lang.IllegalArgumentException: ${secret}
`);
  assert.deepEqual(evidence, ['RuntimeException', 'UnsatisfiedLinkError', 'SIGABRT', 'JniAbort', 'IllegalArgumentException']);
  assert(!JSON.stringify(evidence).includes(secret));
});

test('GMS and target exceptions retain closed origins across interleaved process logs',()=>{
  const privateMessage='token=private /private/device/path';
  const evidence=safeAndroidCrashSummaries(`
10-04 20:00:00 11223 4 E AndroidRuntime: Process: com.google.android.gms.persistent, PID: 11223
10-04 20:00:00 44556 7 E AndroidRuntime: Process: com.aegis.node:worker, PID: 44556
10-04 20:00:00 11223 4 E AndroidRuntime: java.lang.NullPointerException: ${privateMessage}
10-04 20:00:00 44556 7 E AndroidRuntime: java.lang.NullPointerException: ${privateMessage}
`);
  assert.deepEqual(evidence,[{processCode:'gms',errorType:'NullPointerException'},{processCode:'target',errorType:'NullPointerException'}]);
  for(const privateValue of [privateMessage,'com.google.android.gms','com.aegis.node','11223','44556'])assert(!JSON.stringify(evidence).includes(privateValue));
});

test('private unrelated processes and system origins never expose process names or identifiers',()=>{
  const evidence=safeAndroidCrashSummaries(`
10-04 20:00:00 10101 2 E AndroidRuntime: Process: private.customer.application:private-session, PID: 10101
10-04 20:00:00 10101 2 E AndroidRuntime: java.lang.NullPointerException: private title
10-04 20:00:00 20202 3 E AndroidRuntime: Process: system_server, PID: 20202
10-04 20:00:00 20202 3 E AndroidRuntime: java.lang.IllegalStateException: private system detail
10-04 20:00:00 30303 4 E AndroidRuntime: java.lang.RuntimeException: unknown process
`);
  assert.deepEqual(evidence,[{processCode:'other',errorType:'NullPointerException'},{processCode:'system',errorType:'IllegalStateException'},{processCode:'unknown',errorType:'RuntimeException'}]);
  for(const privateValue of ['private','customer','application','system_server','10101','20202','30303'])assert(!JSON.stringify(evidence).includes(privateValue));
  assert.deepEqual(safeAndroidCrashSummaries('AndroidRuntime: Process: android, PID: 33\nAndroidRuntime: java.lang.RuntimeException: private\nAndroidRuntime: FATAL EXCEPTION: main\nAndroidRuntime: java.lang.NullPointerException: private'),[{processCode:'system',errorType:'RuntimeException'},{processCode:'unknown',errorType:'NullPointerException'}]);
});

test('legacy emulator UI and Google provider crashes retain only recognized origin enums',()=>{
  const processes=[['com.android.settings','settings'],['com.android.systemui','systemui'],['com.android.launcher3','launcher'],['com.google.android.apps.nexuslauncher','launcher'],['com.android.packageinstaller','permissioncontroller'],['com.google.android.permissioncontroller','permissioncontroller'],['com.google.process.gapps','googleframework'],['com.google.android.gsf','googleframework'],['com.google.android.location','googlelocation'],['com.android.settings.private','other']];
  for(const [process,code] of processes) {
    const evidence=safeAndroidCrashSummaries(`AndroidRuntime: Process: ${process}:fixture, PID: 1234\nAndroidRuntime: java.lang.NullPointerException: private`);
    assert.deepEqual(evidence,[{processCode:code,errorType:'NullPointerException'}]);
    for(const privateValue of [process,'fixture','1234','private'])assert(!JSON.stringify(evidence).includes(privateValue));
  }
});
