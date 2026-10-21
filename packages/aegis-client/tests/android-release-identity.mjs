// Real Android WebView APK smoke on an isolated emulator; no native IPC simulation.
// This uses an x86_64 development APK, not the signed ARM64 owner installation.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { _android } from 'playwright-core';
import { assertNativeReleaseIdentity, routeOfflineIdentityApi } from './native-release-identity.mjs';

const repository = fileURLToPath(new URL('../../../', import.meta.url));
const [apk, outputArgument] = process.argv.slice(2);
assert.ok(apk && outputArgument, 'Usage: android-release-identity.mjs APP.apk OUTPUT');
const output = resolve(outputArgument);
const adb = process.env.ANDROID_HOME ? join(process.env.ANDROID_HOME, 'platform-tools', 'adb') : 'adb';
const serial = process.env.ANDROID_SERIAL;
const serialArguments = serial ? ['-s', serial] : [];
const runAdb = (...args) => {
  const result = spawnSync(adb, [...serialArguments, ...args], { encoding: 'utf8', timeout: 60000 });
  assert.equal(result.status, 0, `adb ${args[0]} must succeed: ${result.stderr}`);
  return result.stdout.trim();
};
assert.equal(runAdb('shell', 'getprop', 'ro.kernel.qemu'), '1', 'Only an isolated emulator is supported; never use an owner device.');
assert.equal(runAdb('shell', 'getprop', 'ro.product.cpu.abi'), 'x86_64');
const version = JSON.parse(readFileSync(join(repository, 'apps/aegis-node/package.json'), 'utf8')).version;
runAdb('install', '-r', resolve(apk));
runAdb('shell', 'am', 'force-stop', 'com.aegis.node');
runAdb('shell', 'am', 'start', '-W', '-n', 'com.aegis.node/.MainActivity');
let device;
try {
  // Android WebViews do not support desktop Browser.setDownloadBehavior. The
  // Android-specific connector attaches without changing downloads or native IPC.
  const devices = await _android.devices({ omitDriverInstall: true });
  device = serial ? devices.find(candidate => candidate.serial() === serial) : devices.length === 1 ? devices[0] : undefined;
  assert.ok(device, 'The selected isolated emulator must be unambiguous.');
  const webview = await device.webView({ pkg: 'com.aegis.node' }, { timeout: 45000 });
  const page = await webview.page();
  await routeOfflineIdentityApi(page);
  await page.reload({ waitUntil: 'domcontentloaded' });
  const report = await assertNativeReleaseIdentity(page, { version, platform: 'android', output });
  mkdirSync(output, { recursive: true });
  const screenshot = spawnSync(adb, [...serialArguments, 'exec-out', 'screencap', '-p'], { timeout: 15000 });
  assert.equal(screenshot.status, 0);
  writeFileSync(join(output, 'android-emulator-screen.png'), screenshot.stdout);
  writeFileSync(join(output, 'android-identity.json'), JSON.stringify({ ...report,
    api: runAdb('shell', 'getprop', 'ro.build.version.sdk'), abi: 'x86_64',
    limits: 'Real installed development APK and Android WebView on an isolated x86_64 emulator with original native IPC. Disposable HTTP API responses; no production account paired. This is not a physical ARM64 or signed APK upgrade test. Browser layout regression covers the additional CSS viewport/font matrix.'
  }, null, 2) + '\n');
  console.log('Real Android APK/WebView sidebar and About release identity PASS.');
} finally {
  await device?.close().catch(() => {});
  runAdb('shell', 'am', 'force-stop', 'com.aegis.node');
}
