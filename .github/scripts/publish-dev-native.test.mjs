import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { privateReady, retryPublication } from './publish-dev-native.mjs';

const now = Date.now();
const plan = { sha: 'a'.repeat(40), version: '0.7.2-unstable.3', kind: 'normal', platform: 'both', compatibility: { api: 1, nodeData: 1 } };
const ready = { id: 1, context: 'Aegis private deployment', state: 'success', creator: { login: 'phcastello' },
  created_at: new Date(now).toISOString(), description: `api=1;nodeData=1;backend=${'b'.repeat(40)};pwa=${'c'.repeat(40)}` };
test('native gate accepts fresh owner attestation on exact selected commit', async () => {
  const state = { api: async path => { assert.equal(path, `commits/${plan.sha}/statuses?per_page=100`); return [ready]; } };
  assert.equal(await privateReady(plan, { state, now }), 1);
});
test('missing, failed, stale, incompatible, candidate-created attestations block recommendation', async () => {
  for (const statuses of [[], [{ ...ready, state: 'failure' }, ready], [{ ...ready, created_at: new Date(now - 120_001).toISOString() }],
    [{ ...ready, creator: { login: 'github-actions[bot]' } }], [{ ...ready, description: 'api=2' }]]) {
    await assert.rejects(privateReady(plan, { state: { api: async () => statuses }, now }));
  }
  await assert.rejects(privateReady({ ...plan, platform: 'android' }, { state: {}, now }));
  await assert.rejects(privateReady({ ...plan, kind: 'rollback' }, { state: {}, now }));
});
test('one protected job verifies both signatures and publishes without extra workflow', () => {
  const workflow = readFileSync(new URL('../workflows/aegis-native-preview.yml', import.meta.url), 'utf8');
  const signer = workflow.split('  sign:\n')[1];
  assert.equal((signer.match(/environment: aegis-native-signing/g) ?? []).length, 1);
  assert.doesNotMatch(signer, /strategy:|matrix:|ref: \$\{\{ needs.plan.outputs.sha/);
  assert.match(signer, /native-preview-android.py sign candidate-android signed-android/);
  assert.match(signer, /preview-updater-verifier\/Cargo.toml/);
  assert.match(signer, /if: inputs.publish_dev/);
  assert.match(signer, /publish-dev-native.mjs publish/);
  assert.doesNotMatch(signer, /workflow run|actions\/workflows\/.*dispatches/);
  assert.doesNotMatch(workflow, /\n  push:|pull_request_target/);
});
test('publication retries inside its approved job reuse the same bytes without signing or allocation', async () => {
  let calls = 0, healthChecks = 0;
  const publish = async () => { if (++calls < 3) throw Error('transient upload failure'); return 'same immutable distribution'; };
  assert.equal(await retryPublication(publish, async () => { healthChecks++; }, async () => {}), 'same immutable distribution');
  assert.equal(calls, 3);
  assert.equal(healthChecks, 5);
});
test('lost private health blocks retry and persistent publication failure stays a failure', async () => {
  let calls = 0;
  await assert.rejects(retryPublication(async () => { calls++; throw Error('upload interrupted'); },
    async () => { if (calls) throw Error('private deployment failed'); }, async () => {}), /private deployment failed/);
  assert.equal(calls, 1);
  calls = 0;
  await assert.rejects(retryPublication(async () => { calls++; throw Error('persistent'); }, async () => {}, async () => {}), /persistent/);
  assert.equal(calls, 3);
});
