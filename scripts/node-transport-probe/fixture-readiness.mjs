import { existsSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';

// A bootstrap file is written before Kestrel starts. It is necessary, but
// not sufficient: wait for the actual disposable API, not a marker alone.
export async function waitForFixture(api, bootstrap, {
  origin = 'http://127.0.0.1:18104', timeoutMs = 60_000, pollMs = 100
} = {}) {
  const deadline = performance.now() + timeoutMs;
  function assertRunning() {
    if (api.pid === undefined) throw Error('fixture_start_failed');
    if (api.exitCode !== null || api.signalCode !== null)
      throw Error(`fixture_exited (code=${api.exitCode}, signal=${api.signalCode})`);
  }
  while (performance.now() < deadline) {
    assertRunning();
    if (existsSync(bootstrap)) {
      const remaining = Math.max(1, Math.ceil(deadline - performance.now()));
      let healthy = false;
      try {
        const health = await fetch(`${origin}/api/health`, {
          signal: AbortSignal.timeout(Math.min(1000, remaining))
        });
        healthy = health.ok && (await health.json()).status === 'ok';
      } catch { /* The fixture can still be starting. */ }
      assertRunning();
      if (healthy && performance.now() < deadline) return;
    }
    const remaining = deadline - performance.now();
    if (remaining > 0) await delay(Math.min(pollMs, remaining));
  }
  assertRunning();
  throw Error('fixture_readiness_timeout');
}
