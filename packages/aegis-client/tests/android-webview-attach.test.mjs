import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { attachAndroidReleaseWebView, isWebViewAttachTimeout } from './android-webview-attach.mjs';

const timeout = () => Object.assign(Error('androidDevice.webView: Timeout 45000ms exceeded while waiting for event "webview"'), { name: 'TimeoutError' });
const view = {};
function fixture(outcomes, pids) {
  const calls = [];
  const warnings = [];
  let launchCount = 0, pidIndex = 0;
  const hooks = {
    launch: async () => { launchCount++; calls.push('launch'); },
    processId: async () => pids[pidIndex++],
    openDevice: async () => {
      const attempt = launchCount;
      calls.push(`open:${attempt}`);
      return {
        webView: async (selector, options) => {
          calls.push(`attach:${attempt}`);
          assert.deepEqual(selector, { pkg: 'com.aegis.node' });
          assert.deepEqual(options, { timeout: 45000 });
          const outcome = outcomes[attempt-1];
          if (outcome instanceof Error) throw outcome;
          return outcome;
        },
        close: async () => { calls.push(`close:${attempt}`); }
      };
    },
    socketCount: async pid => { assert.equal(pid, 100); return 1; },
    warn: line => warnings.push(line)
  };
  return { hooks, calls, warnings };
}

test('first successful attach returns the original device and real webview without retry', async () => {
  const f = fixture([view], [100,100]);
  assert.equal((await attachAndroidReleaseWebView(f.hooks)).webview, view);
  assert.deepEqual(f.calls, ['launch','open:1','attach:1']);
  assert.deepEqual(f.warnings, []);
});
test('only an explicit WebView event timeout on the same live PID permits one cold attach retry', async () => {
  const f = fixture([timeout(),view], [100,100,200,200]);
  assert.equal((await attachAndroidReleaseWebView(f.hooks)).webview, view);
  assert.deepEqual(f.calls, ['launch','open:1','attach:1','close:1','launch','open:2','attach:2']);
  assert.deepEqual(f.warnings, ['Android WebView attach timeout attempt=1 sameProcessAlive=true devtoolsSockets=1']);
});
test('crash or PID replacement makes a discovery timeout terminal', async () => {
  for (const pid of [null,200]) {
    const failure = timeout();
    const f = fixture([failure], [100,pid]);
    await assert.rejects(attachAndroidReleaseWebView(f.hooks), error => error === failure);
    assert.deepEqual(f.calls, ['launch','open:1','attach:1','close:1']);
    assert.deepEqual(f.warnings, ['Android WebView attach timeout attempt=1 sameProcessAlive=false devtoolsSockets=unavailable']);
  }
});
test('other failures including DOM timeouts never retry', async () => {
  for (const failure of [Error('app crashed'), Object.assign(Error('locator.click: Timeout 45000ms exceeded'), {name:'TimeoutError'}), new assert.AssertionError({message:'identity mismatch'})]) {
    assert.equal(isWebViewAttachTimeout(failure),false);
    const f = fixture([failure], [100]);
    await assert.rejects(attachAndroidReleaseWebView(f.hooks), error => error === failure);
    assert.deepEqual(f.calls, ['launch','open:1','attach:1','close:1']);
  }
});
test('two timeouts are the strict limit; second failure is preserved', async () => {
  const failure = timeout();
  const f = fixture([timeout(),failure], [100,100,100,100]);
  await assert.rejects(attachAndroidReleaseWebView(f.hooks), error => error === failure);
  assert.deepEqual(f.calls, ['launch','open:1','attach:1','close:1','launch','open:2','attach:2','close:2']);
});
test('missing or changed process is a failed identity smoke without retry', async () => {
  let f = fixture([view], [null]);
  await assert.rejects(attachAndroidReleaseWebView(f.hooks), /not alive/);
  assert.deepEqual(f.calls,['launch']);
  f = fixture([view], [100,200]);
  await assert.rejects(attachAndroidReleaseWebView(f.hooks), /process changed/);
  assert.deepEqual(f.calls,['launch','open:1','attach:1','close:1']);
});

test('a failed disconnect prevents retry and preserves original discovery failure', async () => {
  const failure = timeout();
  const f = fixture([failure], [100,100]);
  const openDevice = f.hooks.openDevice;
  f.hooks.openDevice = async () => {
    const device = await openDevice();
    const close = device.close;
    device.close = async () => { await close(); throw Error('disconnect failed'); };
    return device;
  };
  await assert.rejects(attachAndroidReleaseWebView(f.hooks), error => error === failure);
  assert.deepEqual(f.calls, ['launch','open:1','attach:1','close:1']);
});

test('installed identity smoke still selects the isolated emulator and runs the original assertions once after attach', () => {
  const smoke = readFileSync(new URL('./android-release-identity.mjs', import.meta.url), 'utf8');
  assert.match(smoke, /getprop', 'ro\.kernel\.qemu'\), '1'/);
  assert.match(smoke, /getprop', 'ro\.product\.cpu\.abi'\), 'x86_64'/);
  assert.match(smoke, /assert\.ok\(selected, 'The selected isolated emulator must be unambiguous\.'/);
  assert.equal([...smoke.matchAll(/await assertNativeReleaseIdentity\(/g)].length, 1);
  assert.match(smoke, /await attachAndroidReleaseWebView\([\s\S]+await webview\.page\(\)[\s\S]+await routeOfflineIdentityApi\(page\)[\s\S]+await page\.reload\([\s\S]+await assertNativeReleaseIdentity\(page, \{ version, platform: 'android', output \}\)/);
});
