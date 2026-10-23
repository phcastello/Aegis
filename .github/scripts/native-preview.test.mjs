import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtempSync, cpSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parameters, assertTrustedDispatch, assertEnvironment, assertIncreasing, prepareCandidate, manifest, verifyInput, resolvePreview } from './native-preview.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));
const sha = 'a'.repeat(40);
const input = { sha, branch: 'fix/v0.7.2-ux-proactivity', number: '21', platform: 'both' };
const context = { eventName: 'workflow_dispatch', ref: 'refs/heads/main', repository: 'phcastello/Aegis',
  workflowRef: 'phcastello/Aegis/.github/workflows/aegis-native-preview.yml@refs/heads/main' };
const environment = { name: 'aegis-native-signing', protection_rules: [{ type: 'required_reviewers', reviewers: [{ type: 'User', reviewer: { login: 'owner' } }] }],
  deployment_branch_policy: { custom_branch_policies: true } };
const policies = { branch_policies: [{ name: 'main', type: 'branch' }, { name: 'node-v*', type: 'tag' }] };
const plan = { sha, version: '0.7.2-unstable.21', versionCode: 7002021, number: 21, repository: 'phcastello/Aegis', runId: '123', controlSha: 'b'.repeat(40) };

test('references accept only a full SHA and repository branch, with allocated preview number', () => {
  assert.equal(parameters(input).number, 21);
  for (const fields of [{ sha: 'main' }, { sha: 'abcdef0' }, { branch: 'https://github.com/else/repo' }, { branch: 'fix/../bad' },
    { branch: '--upload-pack=bad' }, { number: 999 }, { number: 0 }, { number: '01' }, { platform: 'linux' }]) {
    assert.throws(() => parameters({ ...input, ...fields }));
  }
});
test('signing control must be manually dispatched from the registered main workflow', () => {
  assertTrustedDispatch(context);
  for (const fields of [{ eventName: 'pull_request_target' }, { eventName: 'push' }, { ref: 'refs/heads/fix/test' },
    { workflowRef: 'phcastello/Aegis/.github/workflows/aegis-native-preview.yml@refs/heads/fix/test' }, { attempt: '2' }]) {
    assert.throws(() => assertTrustedDispatch({ ...context, ...fields }));
  }
});
test('missing review approval or unbounded environment branch policy fails closed', () => {
  assertEnvironment(environment, policies);
  assertEnvironment(environment, { branch_policies: [{ name: 'main' }] }); // Legacy API branch response.
  assert.throws(() => assertEnvironment({ ...environment, protection_rules: [] }, policies));
  assert.throws(() => assertEnvironment({ ...environment, deployment_branch_policy: null }, policies));
  assert.throws(() => assertEnvironment(environment, { branch_policies: [{ name: '*', type: 'branch' }] }));
  assert.throws(() => assertEnvironment(environment, { branch_policies: [...policies.branch_policies, { name: '*', type: 'tag' }] }));
});
test('global Android allocation advances stable and all attempted preview reservations', () => {
  const title = (n, conclusion) => ({ display_title: `Aegis Native Preview · 0.7.2-unstable.${n} · android · ${sha}`, conclusion });
  assert.equal(assertIncreasing(plan.version, [{ tag_name: 'node-v0.7.1' }, title(19, 'failure'), title(20, 'cancelled')]), 7002021);
  assert.throws(() => assertIncreasing(plan.version, [title(21, 'failure')]));
  assert.throws(() => assertIncreasing(plan.version, [title(22, 'cancelled')]));
  assert.throws(() => assertIncreasing(plan.version, [{ tag_name: 'node-v0.7.2' }]));
  assert.throws(() => assertIncreasing(plan.version, [{ tag_name: 'node-v0.7.3-unstable.1' }]));
});
test('temporary synchronization updates all consumers without changing branch files', () => {
  const work = mkdtempSync(join(tmpdir(), 'aegis-preview-version-'));
  const files = ['apps/aegis-node/package.json', 'apps/aegis-node/package-lock.json', 'apps/aegis-node/src-tauri/tauri.conf.json',
    'apps/aegis-node/src-tauri/Cargo.toml', 'apps/aegis-node/src-tauri/Cargo.lock', 'apps/aegis-node/scripts/version.mjs',
    'packages/aegis-client/package.json', 'packages/aegis-client/package-lock.json', 'frontend/aegis-pwa/package.json', 'frontend/aegis-pwa/package-lock.json'];
  const original = readFileSync(join(root, files[0]), 'utf8');
  try {
    for (const file of files) { mkdirSync(dirname(join(work, file)), { recursive: true }); cpSync(join(root, file), join(work, file)); }
    const candidateManifest = JSON.parse(readFileSync(join(work, files[0])));
    candidateManifest.version = plan.version.split('-')[0];
    writeFileSync(join(work, files[0]), JSON.stringify(candidateManifest));
    prepareCandidate(work, root, plan.version);
    assert.equal(JSON.parse(readFileSync(join(work, files[0]))).version, plan.version);
    assert.equal(JSON.parse(readFileSync(join(work, files[2]))).bundle.android.versionCode, plan.versionCode);
    assert.equal(JSON.parse(readFileSync(join(work, 'packages/aegis-client/package.json'))).version, plan.version);
    assert.equal(readFileSync(join(root, files[0]), 'utf8'), original);
    const configPath = join(work, files[2]);
    const config = JSON.parse(readFileSync(configPath)); config.plugins.updater.pubkey = 'other-key'; writeFileSync(configPath, JSON.stringify(config));
    assert.throws(() => prepareCandidate(work, root, plan.version), /updater identity/);
  } finally { rmSync(work, { recursive: true, force: true }); }
});
test('artifact provenance rejects modified bytes, unexpected files and other source/version', () => {
  const work = mkdtempSync(join(tmpdir(), 'aegis-preview-artifact-'));
  try {
    writeFileSync(join(work, 'Aegis-Windows-x86_64-Setup.exe'), 'installer bytes');
    manifest(work, plan, 'Windows-x86_64');
    verifyInput(work, plan, 'Windows-x86_64');
    assert.throws(() => verifyInput(work, { ...plan, sha: 'c'.repeat(40) }, 'Windows-x86_64'));
    assert.throws(() => verifyInput(work, { ...plan, version: '0.7.2-unstable.22' }, 'Windows-x86_64'));
    writeFileSync(join(work, 'injected-script.js'), 'forbidden');
    assert.throws(() => verifyInput(work, plan, 'Windows-x86_64'), /Unexpected build artifact/);
    rmSync(join(work, 'injected-script.js'));
    writeFileSync(join(work, 'Aegis-Windows-x86_64-Setup.exe'), 'modified bytes');
    assert.throws(() => verifyInput(work, plan, 'Windows-x86_64'), /digest mismatch/);
  } finally { rmSync(work, { recursive: true, force: true }); }
});

test('resolver follows pagination, same-repository ancestry and fails closed on API error', async () => {
  const work = mkdtempSync(join(tmpdir(), 'aegis-preview-plan-'));
  const previousEnv = { ...process.env }, previousFetch = globalThis.fetch, cwd = process.cwd();
  const calls = [];
  let fail = false, ancestry = 'ahead', hiddenPermissivePolicy = false;
  const fake = async url => {
    const parsed = new URL(url); calls.push(parsed.pathname + parsed.search);
    assert.match(parsed.pathname, /^\/repos\/phcastello\/Aegis\//);
    if (fail) return { status: 403 };
    let body;
    if (parsed.pathname.endsWith('deployment-branch-policies')) body = !hiddenPermissivePolicy ? policies : { branch_policies:
      parsed.searchParams.get('page') === '1' ? Array.from({ length: 100 }, () => ({ name: 'main', type: 'branch' })) : [{ name: '*', type: 'branch' }] };
    else if (parsed.pathname.endsWith('aegis-native-signing')) body = environment;
    else if (parsed.pathname.includes('/compare/')) body = { status: ancestry, base_commit: { sha } };
    else if (parsed.pathname.includes('/contents/')) body = { content: Buffer.from('{"version":"0.7.2"}').toString('base64') };
    else if (parsed.pathname.endsWith('/releases')) body = parsed.searchParams.get('page') === '1' ? Array.from({ length: 100 }, () => ({ tag_name: 'v0.7.1' })) : [{ tag_name: 'node-v0.7.1' }];
    else if (parsed.pathname.endsWith('/runs')) body = { workflow_runs: [
      { id: 1, head_branch: 'main', event: 'workflow_dispatch', display_title: `Aegis Native Preview · 0.7.2-unstable.20 · android · ${sha}`, conclusion: 'cancelled' },
      { id: 2, head_branch: 'main', event: 'workflow_dispatch', display_title: `Aegis Native Preview · 20.99.999-unstable.998 · android · ${sha}`, conclusion: 'failure' },
      { id: 3, head_branch: 'main', event: 'workflow_dispatch', display_title: `Aegis Native Preview · 0.7.2-unstable.999 · android · ${sha}`, conclusion: 'failure' },
      { id: 4, head_branch: 'fix/untrusted', event: 'workflow_dispatch', display_title: `Aegis Native Preview · 20.99.999-unstable.998 · android · ${sha}`, conclusion: 'success' }
    ] };
    else if (parsed.pathname.endsWith('/jobs')) body = { jobs: [{ name: 'plan', status: 'completed',
      conclusion: parsed.pathname.includes('/runs/1/') ? 'success' : 'failure' }] };
    else throw Error(`Unexpected endpoint ${url}`);
    return { status: 200, json: async () => body };
  };
  try {
    Object.assign(process.env, { GITHUB_EVENT_NAME: context.eventName, GITHUB_REF: context.ref, GITHUB_WORKFLOW_REF: context.workflowRef,
      GITHUB_REPOSITORY: context.repository, PREVIEW_SHA: sha, PREVIEW_BRANCH: input.branch, PREVIEW_VERSION: plan.version,
      PREVIEW_PLATFORM: input.platform, GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '1', GITHUB_SHA: plan.controlSha, GITHUB_OUTPUT: join(work, 'output') });
    globalThis.fetch = fake; process.chdir(work);
    await resolvePreview();
    const received = JSON.parse(readFileSync(join(work, 'preview-plan/preview.json')));
    assert.equal(received.sha, sha); assert.equal(received.versionCode, plan.versionCode);
    assert.ok(calls.some(call => call.includes('/releases?per_page=100&page=2')));
    assert.ok(calls.some(call => call.includes('/runs/2/jobs?filter=all')));
    assert.equal(calls.some(call => call.includes('/runs/4/jobs?filter=all')), false);
    process.env.PREVIEW_VERSION = '0.7.2-unstable.20';
    await assert.rejects(resolvePreview(), /reserved\/published version 0.7.2-unstable.20/);
    process.env.PREVIEW_VERSION = plan.version;
    hiddenPermissivePolicy = true; await assert.rejects(resolvePreview(), /Signing may allow only/);
    hiddenPermissivePolicy = false;
    ancestry = 'diverged'; await assert.rejects(resolvePreview(), /reachable/);
    ancestry = 'ahead'; fail = true; await assert.rejects(resolvePreview(), /Cannot verify preview prerequisite/);
  } finally {
    process.chdir(cwd); globalThis.fetch = previousFetch;
    for (const key of Object.keys(process.env)) if (!(key in previousEnv)) delete process.env[key];
    Object.assign(process.env, previousEnv); rmSync(work, { recursive: true, force: true });
  }
});

test('workflow publishes artifacts only and keeps signing isolated from candidate execution', () => {
  const workflow = readFileSync(join(root, '.github/workflows/aegis-native-preview.yml'), 'utf8');
  const build = workflow.split('  build:\n')[1].split('  sign:\n')[0];
  const signer = workflow.split('  sign:\n')[1];
  assert.doesNotMatch(workflow, /contents: write|pull_request_target|publish-release|node-preview\/latest\.json|gh release|git tag/);
  assert.doesNotMatch(build, /secrets\./);
  assert.match(signer, /environment: aegis-native-signing/);
  assert.doesNotMatch(signer, /windows-release-upgrade|npm run build|\.\/candidate\/|ref: \$\{\{ needs\.plan\.outputs\.sha/);
  assert.match(signer, /tauri signer sign --app-version/);
  assert.match(signer, /preview-updater-verifier\/Cargo.toml/);
  assert.match(workflow, /cancel-in-progress: false/);
});
