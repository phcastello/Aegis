import assert from 'node:assert/strict';
import test from 'node:test';
import { GitHubError, historicalFloor } from './distribution-state.mjs';

import { githubFixture } from './distribution-state.fixture.mjs';
export const planFor = (version, runId = '2') => ({ schema: 2, version, runId, sha: 'a'.repeat(40), sourceCommit: 'a'.repeat(40), sourceVersion: '0.7.2',
  kind: 'normal', publishedAt: '2026-10-23T00:00:00.000Z', compatibility: { api: 1, nodeData: 1 }, platform: 'both' });

test('durable allocation never derives installation code from public preview number', async () => {
  const f = githubFixture();
  const first = await f.state.reserve(planFor('0.7.2-unstable.4'));
  const second = await f.state.reserve(planFor('0.7.2-unstable.5', '3'));
  const rollback = await f.state.reserve({ ...planFor('0.7.2-unstable.5.rollback.1', '4'), kind: 'rollback', rollbackOf: second.version, restoresVersion: first.version });
  const next = await f.state.reserve(planFor('0.7.2-unstable.6', '5'));
  const stable = await f.state.reserve(planFor('0.7.2', '6'));
  assert.deepEqual([first, second, rollback, next, stable].map(item => item.versionCode), [7002002, 7002003, 7002004, 7002005, 7002999]);
  assert.equal((await f.state.read()).ledger.reservations.length, 5);
});
test('concurrent reservation commits cannot reuse a code or version', async () => {
  const f = githubFixture();
  const results = await Promise.allSettled([f.state.reserve(planFor('0.7.2-unstable.2')), f.state.reserve(planFor('0.7.2-unstable.2', '3'))]);
  assert.equal(results.filter(item => item.status === 'fulfilled').length, 1);
  assert.equal((await f.state.read()).ledger.highWaterMark, historicalFloor + 1);
  const reserved = results.find(item => item.status === 'fulfilled').value;
  const { versionCode, ...identity } = reserved;
  assert.deepEqual(await f.state.reserve(identity), reserved, 'same reservation identity is idempotent');
  await assert.rejects(f.state.reserve({ ...identity, sha: 'b'.repeat(40) }), /already reserved/);
});
test('failure after reservation consumes version/code and a new event increases both', async () => {
  const f = githubFixture();
  const failedBuild = await f.state.reserve(planFor('0.7.2-unstable.2'));
  await assert.rejects(f.state.reserve(planFor('0.7.2-unstable.2', '3')));
  const next = await f.state.reserve(planFor('0.7.2-unstable.3', '4'));
  assert.ok(next.versionCode > failedBuild.versionCode);
  await assert.rejects(f.state.reserve(planFor('0.7.2-unstable.1', '5')), /must follow/);
});
test('atomic recommendation keeps previous latest through a failed ref update', async () => {
  const f = githubFixture(), one = await f.state.reserve(planFor('0.7.2-unstable.2'));
  const manifest = { version: one.version, versionCode: one.versionCode, bytes: 'verified' };
  await f.state.recommend(one, manifest);
  const before = f.files()['latest.json'];
  const two = await f.state.reserve(planFor('0.7.2-unstable.3', '3'));
  f.setFailure((path, method) => method === 'PATCH');
  await assert.rejects(f.state.recommend(two, { ...manifest, version: two.version, versionCode: two.versionCode }), GitHubError);
  assert.equal(f.files()['latest.json'], before);
  f.setFailure();
  await f.state.recommend(two, { ...manifest, version: two.version, versionCode: two.versionCode });
  assert.equal(JSON.parse(f.files()['latest.json']).version, two.version);
  await assert.rejects(f.state.recommend(one, manifest), /cannot regress/);
});
test('reservation preserves current manifest, pointer retries are idempotent and metadata immutable', async () => {
  const f = githubFixture(), one = await f.state.reserve(planFor('0.7.2-unstable.2'));
  const manifest = { version: one.version, versionCode: one.versionCode };
  const head = await f.state.recommend(one, manifest);
  assert.equal(await f.state.recommend(one, manifest), head);
  await assert.rejects(f.state.recommend(one, { ...manifest, tampered: true }), /cannot change/);
  await f.state.reserve(planFor('0.7.2-unstable.3', '3'));
  assert.deepEqual(JSON.parse(f.files()['latest.json']), manifest);
  assert.equal(f.calls.some(call => call.body?.force === true), false);
});
