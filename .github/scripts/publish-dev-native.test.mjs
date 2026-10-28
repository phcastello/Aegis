import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { retryPublication } from './publish-dev-native.mjs';
import { assertAuthorizedActor, deploymentSnapshot } from './native-preview.mjs';

const sha = 'a'.repeat(40);
const deployment = { sourceCommit: sha, backend: 'b'.repeat(40), pwa: 'c'.repeat(40), compatibility: { api: 1, nodeData: 1 } };

test('explicit publication accepts authorized repository identities and refuses read-only identities', async () => {
  for (const permission of ['write', 'maintain', 'admin']) {
    await assertAuthorizedActor('owner', async path => {
      assert.equal(path, 'collaborators/owner/permission'); return { permission };
    });
  }
  for (const permission of ['read', 'triage', 'none'])
    await assert.rejects(assertAuthorizedActor('reader', async () => ({ permission })), /write permission/);
  for (const actor of [undefined, '', '../owner', 'owner\nextra'])
    await assert.rejects(assertAuthorizedActor(actor, async () => ({ permission: 'admin' })));
});
test('prepared immutable deployment records exact source and supported contracts without expiration', () => {
  assert.deepEqual(deploymentSnapshot(JSON.stringify(deployment), sha), deployment);
  for (const change of [{ sourceCommit: 'd'.repeat(40) }, { backend: 'main' }, { pwa: undefined },
    { compatibility: { api: 2, nodeData: 1 } }, { compatibility: { api: 1, nodeData: 2 } },
    { created_at: '2020-01-01T00:00:00Z' }])
    assert.throws(() => deploymentSnapshot({ ...deployment, ...change }, sha));
});
test('one trusted job verifies both signatures and publishes without interactive or private-host coordination', () => {
  const workflow = readFileSync(new URL('../workflows/aegis-native-preview.yml', import.meta.url), 'utf8');
  const build = workflow.split('  build:\n')[1].split('  sign:\n')[0];
  const signer = workflow.split('  sign:\n')[1];
  assert.equal((signer.match(/environment: aegis-native-signing/g) ?? []).length, 1);
  assert.doesNotMatch(build, /secrets\.|environment:/);
  assert.doesNotMatch(signer, /strategy:|matrix:|ref: \$\{\{ needs.plan.outputs.sha|npm run build/);
  assert.match(signer, /ref: \$\{\{ github.sha \}\}/);
  assert.match(signer, /native-preview-android.py sign candidate-android signed-android/);
  assert.match(signer, /preview-updater-verifier\/Cargo.toml/);
  assert.match(signer, /if: inputs.publish_dev/);
  assert.match(signer, /publish-dev-native.mjs publish/);
  assert.doesNotMatch(signer, /workflow run|actions\/workflows\/.*dispatches|publish-dev-native.mjs check|statuses:/);
  assert.doesNotMatch(workflow, /\n  push:|pull_request_target|heartbeat/);
  assert.match(signer, /needs.build.result == 'success'/, 'A failed build cannot sign or publish.');
  for (const name of ['publish-dev-native', 'native-preview', 'recommended-publish']) {
    const source = readFileSync(new URL(`./${name}.mjs`, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /commits\/.*\/statuses|beforeRecommend|privateReady|120_000/);
  }
});
test('publication retries reuse the same bytes without external health checks or re-signing', async () => {
  let calls = 0, waits = 0;
  const publish = async () => { if (++calls < 3) throw Error('transient upload failure'); return 'same immutable distribution'; };
  assert.equal(await retryPublication(publish, async () => { waits++; }), 'same immutable distribution');
  assert.equal(calls, 3); assert.equal(waits, 2);
});
test('persistent publication failure stays a failure after three bounded attempts', async () => {
  let calls = 0;
  await assert.rejects(retryPublication(async () => { calls++; throw Error('persistent'); }, async () => {}), /persistent/);
  assert.equal(calls, 3);
});
