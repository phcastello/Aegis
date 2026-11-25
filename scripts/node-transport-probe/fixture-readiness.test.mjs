import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { waitForFixture } from './fixture-readiness.mjs';

test('bootstrap marker alone or a non-healthy HTTP response does not release the proxy probe', async t => {
  const dir = await mkdtemp(join(tmpdir(), 'aegis-fixture-ready-test-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const marker = join(dir, 'bootstrap');
  const api = { pid: 1, exitCode: null, signalCode: null };
  let status = 503, requests = 0;
  const server = createServer((req, res) => {
    assert.equal(req.url, '/api/health');
    requests++;
    res.writeHead(status, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ status: status === 200 ? 'ok' : 'starting' }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const options = { origin, timeoutMs: 150, pollMs: 10 };
  await assert.rejects(waitForFixture(api, marker, options), /fixture_readiness_timeout/);
  assert.equal(requests, 0);
  await writeFile(marker, 'synthetic marker');
  await assert.rejects(waitForFixture(api, marker, options), /fixture_readiness_timeout/);
  assert.ok(requests > 0);
  status = 200;
  await waitForFixture(api, marker, { ...options, timeoutMs: 500 });
});

test('a fixture that cannot start or exits is not accepted', async () => {
  const marker = join(tmpdir(), 'not-created-aegis-fixture');
  await assert.rejects(waitForFixture({ pid: undefined, exitCode: null, signalCode: null }, marker), /fixture_start_failed/);
  await assert.rejects(waitForFixture({ pid: 1, exitCode: 2, signalCode: null }, marker), /fixture_exited \(code=2, signal=null\)/);
  await assert.rejects(waitForFixture({ pid: 1, exitCode: null, signalCode: 'SIGTERM' }, marker), /fixture_exited/);
});
