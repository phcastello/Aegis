// Trusted main only. Candidate bytes are data; no candidate code runs here.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { DistributionState, repository } from './distribution-state.mjs';
import { assemble, publishRecommended } from './recommended-publish.mjs';
import { assertEnvironment, signingEnvironment } from './native-preview.mjs';

export async function privateReady(plan, { state = new DistributionState(), now = Date.now() } = {}) {
  assert.equal(plan.kind, 'normal');
  assert.match(plan.version, /^\d+\.\d+\.\d+-unstable\.[1-9]\d*$/);
  assert.equal(plan.platform, 'both');
  assert.deepEqual(plan.compatibility, { api: 1, nodeData: 1 });
  // Candidate build tokens have no statuses:write permission. Only the owner
  // running the private-host orchestrator can attest this private deployment.
  const statuses = await state.api(`commits/${plan.sha}/statuses?per_page=100`);
  const latest = statuses.find(s => s.context === 'Aegis private deployment');
  assert.equal(latest?.state, 'success', 'Private deployment is not healthy.');
  assert.equal(latest.creator.login, repository.split('/')[0], 'Private deployment must be attested by the owner.');
  const age = now - Date.parse(latest.created_at);
  assert.ok(age >= -30_000 && age < 120_000, 'Private deployment heartbeat expired; keep publish-dev running.');
  assert.match(latest.description, /^api=1;nodeData=1;backend=[a-f0-9]{40};pwa=[a-f0-9]{40}$/);
  return latest.id;
}

export async function integratedPublish({ directory, plan, android, windows, state = new DistributionState() }) {
  const policies = [];
  for (let page = 1; ; page++) {
    const response = await state.api(`environments/${signingEnvironment}/deployment-branch-policies?per_page=100&page=${page}`);
    policies.push(...response.branch_policies);
    if (response.branch_policies.length < 100) break;
  }
  assertEnvironment(await state.api(`environments/${signingEnvironment}`), { branch_policies: policies });
  await privateReady(plan, { state });
  assemble(directory, android, windows, plan);
  // Both APK and updater signatures were verified in this SAME approved job.
  // Recheck immediately before releasing bytes/advancing the existing pointer.
  await privateReady(plan, { state });
  return publishRecommended({ directory, plan, state, beforeRecommend: () => privateReady(plan, { state }) });
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [command, directory, planFile, android, windows] = process.argv.slice(2);
  assert.equal(process.env.GITHUB_REF, 'refs/heads/main');
  assert.equal(process.env.GITHUB_WORKFLOW_REF, `${repository}/.github/workflows/aegis-native-preview.yml@refs/heads/main`);
  assert.equal(process.env.GITHUB_EVENT_NAME, 'workflow_dispatch');
  const plan = JSON.parse(readFileSync(planFile));
  assert.equal(plan.controlSha, process.env.GITHUB_SHA);
  if (command === 'check') await privateReady(plan);
  else {
    assert.equal(command, 'publish');
    console.log(await integratedPublish({ directory, plan, android, windows }));
  }
}
