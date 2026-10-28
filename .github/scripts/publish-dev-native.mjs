// Trusted main only. Candidate bytes are data; no candidate code runs here.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { DistributionState, repository } from './distribution-state.mjs';
import { assemble, publishRecommended } from './recommended-publish.mjs';

export async function integratedPublish({ directory, plan, android, windows, state = new DistributionState() }) {
  // Health and contract preparation completed before dispatch and is part of the
  // durable reservation. Signing/publication need no live private-host process.
  assert.equal(plan.kind, 'normal');
  assert.match(plan.version, /^\d+\.\d+\.\d+-unstable\.[1-9]\d*$/);
  assert.equal(plan.platform, 'both');
  assert.deepEqual(plan.compatibility, { api: 1, nodeData: 1 });
  assert.equal(plan.deployment?.sourceCommit, plan.sha, 'Missing prepared deployment.');
  assemble(directory, android, windows, plan);
  return retryPublication(() => publishRecommended({ directory, plan, state }));
}

// Resume partial immutable uploads/pointer failures with the SAME signed bytes.
// Three bounded attempts add no reservation, signing or external supervision.
export async function retryPublication(publish, wait = () => new Promise(resolve => setTimeout(resolve, 5000))) {
  for (let attempt = 0; ; attempt++) {
    try { return await publish(); }
    catch (error) {
      if (attempt === 2) throw error;
      await wait();
    }
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [command, directory, planFile, android, windows] = process.argv.slice(2);
  assert.equal(process.env.GITHUB_REF, 'refs/heads/main');
  assert.equal(process.env.GITHUB_WORKFLOW_REF, `${repository}/.github/workflows/aegis-native-preview.yml@refs/heads/main`);
  assert.equal(process.env.GITHUB_EVENT_NAME, 'workflow_dispatch');
  assert.equal(command, 'publish');
  const plan = JSON.parse(readFileSync(planFile));
  assert.equal(plan.controlSha, process.env.GITHUB_SHA);
  console.log(await integratedPublish({ directory, plan, android, windows }));
}
