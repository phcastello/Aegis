import {test} from 'node:test';
import assert from 'node:assert/strict';
import {safeAndroidCrashTypes} from './android-safe-crash.mjs';

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
