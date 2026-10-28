import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtempSync, cpSync, mkdirSync, readFileSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { parameters, assertTrustedDispatch, assertEnvironment, assertIncreasing, prepareCandidate, manifest, verifyInput, resolvePreview, output } from './native-preview.mjs';
import { githubFixture } from './distribution-state.fixture.mjs';
import { recommendedFeed } from './distribution-state.mjs';
import { metadata } from '../../apps/aegis-node/scripts/release-metadata.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));
const sha = 'a'.repeat(40);
const input = { sha, branch: 'fix/v0.7.2-ux-proactivity', number: '21', platform: 'both' };
const context = { eventName: 'workflow_dispatch', ref: 'refs/heads/main', repository: 'phcastello/Aegis',
  workflowRef: 'phcastello/Aegis/.github/workflows/aegis-native-preview.yml@refs/heads/main' };
const environment = { name: 'aegis-native-signing', protection_rules: [],
  deployment_branch_policy: { custom_branch_policies: true } };
const policies = { branch_policies: [{ name: 'main', type: 'branch' }, { name: 'node-v*', type: 'tag' }] };
const plan = { schema: 2, platform: 'both', sha, sourceCommit: sha, sourceVersion: '0.7.2', kind: 'normal', compatibility: {api:1,nodeData:1}, publishedAt:'2026-10-23T00:00:00.000Z',
  version: '0.7.2-unstable.21', versionCode: 7002021, number: 21, repository: 'phcastello/Aegis', runId: '123', controlSha: 'b'.repeat(40) };

test('references accept only a full SHA and repository branch, with allocated preview number', () => {
  assert.equal(parameters(input).number, 21);
  for (const fields of [{ sha: 'main' }, { sha: 'abcdef0' }, { branch: 'https://github.com/else/repo' }, { branch: 'fix/../bad' },
    { branch: '--upload-pack=bad' }, { number: 65536 }, { number: 0 }, { number: '01' }, { platform: 'linux' }]) {
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
test('signing accepts noninteractive authorization while unbounded environment policies fail closed', () => {
  assertEnvironment(environment, policies);
  assertEnvironment(environment, { branch_policies: [{ name: 'main' }] }); // Legacy API branch response.
  assertEnvironment({ ...environment, protection_rules: [] }, policies);
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
test('workflow outputs reject multiline values independently of metadata validation', () => {
  const work = mkdtempSync(join(tmpdir(), 'aegis-preview-output-'));
  const previous = process.env.GITHUB_OUTPUT;
  try {
    process.env.GITHUB_OUTPUT = join(work, 'output');
    output('sha', sha);
    const original = readFileSync(process.env.GITHUB_OUTPUT, 'utf8');
    for (const value of [`0.7.2\nsha=${'c'.repeat(40)}`, '0.7.2\rversionCode=1'])
      assert.throws(() => output('sourceVersion', value), /single-line/);
    assert.throws(() => output('sha\nversion', sha), /output name/);
    assert.equal(readFileSync(process.env.GITHUB_OUTPUT, 'utf8'), original);
  } finally {
    if (previous === undefined) delete process.env.GITHUB_OUTPUT; else process.env.GITHUB_OUTPUT = previous;
    rmSync(work, { recursive: true, force: true });
  }
});
test('temporary synchronization updates all consumers without changing branch files', () => {
  const work = mkdtempSync(join(tmpdir(), 'aegis-preview-version-'));
  const files = ['apps/aegis-node/package.json', 'apps/aegis-node/package-lock.json', 'apps/aegis-node/src-tauri/tauri.conf.json',
    'apps/aegis-node/src-tauri/Cargo.toml', 'apps/aegis-node/src-tauri/Cargo.lock', 'apps/aegis-node/scripts/version.mjs', 'apps/aegis-node/scripts/version.test.mjs',
    'packages/aegis-client/package.json', 'packages/aegis-client/package-lock.json', 'frontend/aegis-pwa/package.json', 'frontend/aegis-pwa/package-lock.json'];
  const original = readFileSync(join(root, files[0]), 'utf8');
  try {
    for (const file of files) { mkdirSync(dirname(join(work, file)), { recursive: true }); cpSync(join(root, file), join(work, file)); }
    const candidateManifest = JSON.parse(readFileSync(join(work, files[0])));
    candidateManifest.version = plan.version.split('-')[0];
    writeFileSync(join(work, files[0]), JSON.stringify(candidateManifest));
    // The trusted infrastructure can be bootstrapped while main still ships the
    // legacy updater. This fixture represents the candidate opting into v2.
    const configPath = join(work, files[2]);
    const candidateConfig = JSON.parse(readFileSync(configPath));
    candidateConfig.plugins.updater.endpoints = [recommendedFeed];
    writeFileSync(configPath, JSON.stringify(candidateConfig));
    const independentCode = plan.versionCode + 1;
    prepareCandidate(work, root, plan.version, independentCode);
    assert.equal(JSON.parse(readFileSync(join(work, files[0]))).version, plan.version);
    assert.equal(JSON.parse(readFileSync(join(work, files[2]))).bundle.android.versionCode, independentCode);
    assert.equal(JSON.parse(readFileSync(join(work, 'packages/aegis-client/package.json'))).version, plan.version);
    assert.equal(readFileSync(join(root, files[0]), 'utf8'), original);
    const checkSnapshot = () => execFileSync(process.execPath, ['--test', 'scripts/version.test.mjs'], { cwd: join(work, 'apps/aegis-node'), stdio: 'pipe' });
    checkSnapshot();
    prepareCandidate(work, root, `${plan.version}.rollback.1`, independentCode + 1);
    checkSnapshot();
    prepareCandidate(work, root, '0.7.2-unstable.22', independentCode + 2);
    checkSnapshot();
    assert.throws(() => prepareCandidate(work, root, plan.version), /Command failed/, 'A new prerelease must never silently derive its installation code from SemVer.');
    const config = JSON.parse(readFileSync(configPath));
    config.plugins.updater.endpoints = ['https://github.com/phcastello/Aegis/releases/download/node-preview/latest.json'];
    writeFileSync(configPath, JSON.stringify(config));
    assert.throws(() => prepareCandidate(work, root, plan.version, plan.versionCode), /exact recommended endpoint/);
    config.plugins.updater.endpoints = [recommendedFeed];
    config.plugins.updater.pubkey = 'other-key'; writeFileSync(configPath, JSON.stringify(config));
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
  const git = githubFixture();
  const v2Releases = [], v2Runs = [];
  let fail = false, ancestry = 'ahead', hiddenPermissivePolicy = false, sourceVersion = '0.7.2', permission = 'admin';
  const prepared = { sourceCommit: sha, backend: 'c'.repeat(40), pwa: 'd'.repeat(40), compatibility: {api:1,nodeData:1} };
  let feedCompatibility = prepared.compatibility;
  const fake = async (url, options) => {
    const parsed = new URL(url); calls.push(parsed.pathname + parsed.search);
    assert.match(parsed.pathname, /^\/repos\/phcastello\/Aegis\//);
    if (fail) return { status: 403 };
    if (parsed.pathname.includes('/git/') || parsed.pathname.endsWith('/contents/ledger.json')) return git.fetcher(url, options);
    let body;
    if (parsed.pathname.endsWith('deployment-branch-policies')) body = !hiddenPermissivePolicy ? policies : { branch_policies:
      parsed.searchParams.get('page') === '1' ? Array.from({ length: 100 }, () => ({ name: 'main', type: 'branch' })) : [{ name: '*', type: 'branch' }] };
    else if (parsed.pathname.endsWith('aegis-native-signing')) body = environment;
    else if (parsed.pathname.includes('/collaborators/')) body = { permission };
    else if (parsed.pathname.includes('/commits/')) body = { sha: parsed.pathname.split('/').at(-1) };
    else if (parsed.pathname.endsWith('/contents/latest.json')) body = { content: Buffer.from(JSON.stringify({compatibility:feedCompatibility})).toString('base64') };
    else if (parsed.pathname.includes('/compare/')) body = { status: ancestry, base_commit: { sha } };
    else if (parsed.pathname.includes('/contents/')) body = { content: Buffer.from(JSON.stringify({ version: sourceVersion })).toString('base64') };
    else if (parsed.pathname.endsWith('/releases')) body = parsed.searchParams.get('page') === '1' ? Array.from({ length: 100 }, () => ({ tag_name: 'v0.7.1' })) : [{ tag_name: 'node-v0.7.1' }, ...v2Releases];
    else if (parsed.pathname.endsWith('/runs')) body = { workflow_runs: [
      { id: 1, head_branch: 'main', event: 'workflow_dispatch', display_title: `Aegis Native Preview · 0.7.2-unstable.20 · android · ${sha}`, conclusion: 'cancelled' },
      { id: 2, head_branch: 'main', event: 'workflow_dispatch', display_title: `Aegis Native Preview · 20.99.999-unstable.998 · android · ${sha}`, conclusion: 'failure' },
      { id: 3, head_branch: 'main', event: 'workflow_dispatch', display_title: `Aegis Native Preview · 0.7.2-unstable.999 · android · ${sha}`, conclusion: 'failure' },
      { id: 4, head_branch: 'fix/untrusted', event: 'workflow_dispatch', display_title: `Aegis Native Preview · 20.99.999-unstable.998 · android · ${sha}`, conclusion: 'success' }, ...v2Runs
    ] };
    else if (parsed.pathname.endsWith('/jobs')) body = { jobs: [{ name: 'plan', status: 'completed',
      conclusion: parsed.pathname.includes('/runs/1/') ? 'success' : 'failure' }] };
    else throw Error(`Unexpected endpoint ${url}`);
    return { status: 200, ok: true, json: async () => body };
  };
  try {
    Object.assign(process.env, { GITHUB_EVENT_NAME: context.eventName, GITHUB_REF: context.ref, GITHUB_WORKFLOW_REF: context.workflowRef,
      GITHUB_REPOSITORY: context.repository, GITHUB_ACTOR: 'phcastello', PREVIEW_SHA: sha, PREVIEW_BRANCH: input.branch, PREVIEW_VERSION: plan.version,
      PREVIEW_PLATFORM: input.platform, GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '1', GITHUB_SHA: plan.controlSha, GITHUB_OUTPUT: join(work, 'output') });
    globalThis.fetch = fake; process.chdir(work);
    for (const invalid of [`0.7.2-unstable.1\nsha=${'c'.repeat(40)}`, '0.7.2-unstable.1\rversionCode=1', '0.7.2-beta.1', '0.7.2+build', 702]) {
      sourceVersion = invalid;
      await assert.rejects(resolvePreview(), /Use X\.Y\.Z|SemVer string/);
      assert.equal(existsSync(process.env.GITHUB_OUTPUT), false, 'Invalid source metadata must not write workflow outputs.');
      assert.equal(git.calls.some(call => call.method !== 'GET'), false, 'Invalid source metadata must not create a reservation.');
      assert.equal(existsSync(join(work, 'preview-plan')), false);
    }
    sourceVersion = '0.7.2';
    permission = 'read';
    await assert.rejects(resolvePreview(), /write permission/);
    assert.equal(git.calls.some(call => call.method !== 'GET'), false, 'Unauthorized dispatch cannot reserve a distribution.');
    permission = 'admin';
    await resolvePreview();
    const received = JSON.parse(readFileSync(join(work, 'preview-plan/preview.json')));
    assert.equal(received.sha, sha); assert.equal(received.versionCode, plan.versionCode);
    assert.ok(calls.some(call => call.includes('/releases?per_page=100&page=2')));
    assert.ok(calls.some(call => call.includes('/runs/2/jobs?filter=all')));
    assert.equal(calls.some(call => call.includes('/runs/4/jobs?filter=all')), false);
    process.env.PREVIEW_VERSION = '0.7.2-unstable.20';
    await assert.rejects(resolvePreview(), /already reserved|must follow/);
    process.env.PREVIEW_VERSION = plan.version;
    hiddenPermissivePolicy = true; await assert.rejects(resolvePreview(), /Signing may allow only/);
    hiddenPermissivePolicy = false;
    ancestry = 'diverged'; await assert.rejects(resolvePreview(), /reachable/);
    ancestry = 'ahead'; fail = true; await assert.rejects(resolvePreview(), /Cannot verify preview prerequisite/);
    fail = false;
    const {versionCode, ...identity}=received;
    const reserved=await git.state.reserve({...identity,version:'0.7.2-unstable.500',number:500,runId:'500'});
    assert.equal(reserved.versionCode,7002022);
    v2Releases.push({tag_name:`node-v${reserved.version}`,draft:false});
    v2Runs.push({id:500,head_branch:'main',event:'workflow_dispatch',display_title:`Aegis Native Preview · ${reserved.version} · both · ${sha}`,conclusion:'success'});
    Object.assign(process.env,{PREVIEW_VERSION:'0.7.2-unstable.501',GITHUB_RUN_ID:'501'});
    await resolvePreview();
    assert.equal(JSON.parse(readFileSync(join(work,'preview-plan/preview.json'))).versionCode,7002023,'v2 release/run names must not inflate the independent ledger counter');
    sourceVersion = '0.7.2-stage.10';
    Object.assign(process.env,{PREVIEW_VERSION:'0.7.2-unstable.502',GITHUB_RUN_ID:'502'});
    await resolvePreview();
    assert.equal(JSON.parse(readFileSync(join(work,'preview-plan/preview.json'))).sourceVersion,sourceVersion,'Valid historical code provenance is preserved without changing its spelling.');
    Object.assign(process.env, {PUBLISH_DEV:'true', DEPLOYMENT:JSON.stringify(prepared), PREVIEW_VERSION:'0.7.2-unstable.503', GITHUB_RUN_ID:'503'});
    feedCompatibility = {api:2,nodeData:1};
    const callsBefore = git.calls.filter(call => call.method !== 'GET').length;
    await assert.rejects(resolvePreview(), /existing Node contracts/);
    assert.equal(git.calls.filter(call => call.method !== 'GET').length, callsBefore, 'Incompatible deployment cannot consume a reservation.');
    feedCompatibility = prepared.compatibility;
    await resolvePreview();
    const preparedPlan = JSON.parse(readFileSync(join(work,'preview-plan/preview.json')));
    assert.deepEqual(preparedPlan.deployment, prepared);
    assert.deepEqual((await git.state.read()).ledger.reservations.at(-1).deployment, prepared, 'Prepared contracts and deployed sources are durable with the reservation.');
    assert.equal(calls.some(call => call.includes('/statuses')), false, 'The workflow never polls private deployment status.');
  } finally {
    process.chdir(cwd); globalThis.fetch = previousFetch;
    for (const key of Object.keys(process.env)) if (!(key in previousEnv)) delete process.env[key];
    Object.assign(process.env, previousEnv); rmSync(work, { recursive: true, force: true });
  }
});

test('build workflow requires manual dispatch, durable reservation and isolated signing; promotion is separately explicit', () => {
  const workflow = readFileSync(join(root, '.github/workflows/aegis-native-preview.yml'), 'utf8');
  const build = workflow.split('  build:\n')[1].split('  sign:\n')[0];
  const signer = workflow.split('  sign:\n')[1];
  assert.doesNotMatch(workflow, /pull_request_target|publish-release|node-preview\/latest\.json|gh release|git tag/);
  assert.match(workflow.split('  plan:\n')[1].split('  acceptance:\n')[0], /contents: write/);
  assert.doesNotMatch(build, /secrets\./);
  assert.match(signer, /environment: aegis-native-signing/);
  assert.match(signer,/if: \$\{\{ !cancelled\(\) && needs.plan.result == 'success' && needs.build.result == 'success' \}\}/);
  assert.doesNotMatch(signer, /windows-release-upgrade|npm run build|\.\/candidate\/|ref: \$\{\{ needs\.plan\.outputs\.sha/);
  assert.match(signer, /tauri signer sign --app-version/);
  assert.match(signer, /preview-updater-verifier\/Cargo.toml/);
  assert.match(workflow, /cancel-in-progress: false/);
  assert.match(workflow, /group: aegis-native-distribution/);
  assert.match(workflow, /if: needs.plan.outputs.stable == 'true'/);
  assert.match(workflow, /stage: release\n      full_matrix: true/);
  const promotion=readFileSync(join(root,'.github/workflows/aegis-native-promote.yml'),'utf8');
  assert.match(promotion,/if: inputs.publish/); assert.match(promotion,/default: false/);
  assert.match(promotion,/environment: aegis-native-signing/); assert.doesNotMatch(promotion,/secrets\.|npm run build/);
  assert.match(promotion,/native-preview-android.py verify/); assert.match(promotion,/preview-updater-verifier\/Cargo.toml/);
  assert.doesNotMatch(promotion,/\n  push:/);
});

test('rollback resolver requires published compatible provenance, exact restored SHA and explicit confirmation', async () => {
  const work=mkdtempSync(join(tmpdir(),'aegis-rollback-plan-')), previousEnv={...process.env}, oldFetch=globalThis.fetch, cwd=process.cwd();
  const git=githubFixture(), signatures=version=>Buffer.from(`trusted comment: timestamp:1\tversion:${version}`).toString('base64');
  const hashes={'Aegis-Android-arm64.apk':'a'.repeat(64),'Aegis-Windows-x86_64-Setup.exe':'b'.repeat(64)};
  const versioned=(version,sourceCommit,versionCode)=>metadata(version,signatures(version),'2026-10-23T00:00:00Z',{
    versionCode,sourceCommit,sourceVersion:'0.7.2',kind:'normal',compatibility:{api:1,nodeData:1},sha256:hashes});
  let restored=versioned('0.7.2-unstable.4',sha,7002002), faulty=versioned('0.7.2-unstable.5','b'.repeat(40),7002003);
  await git.state.reserve({...plan,version:restored.version,runId:'44',versionCode:undefined});
  await git.state.reserve({...plan,version:faulty.version,runId:'55',versionCode:undefined});
  const releaseFor=version=>({tag_name:`node-v${version}`,draft:false,assets:[{name:'latest.json',browser_download_url:`https://github.com/phcastello/Aegis/releases/download/node-v${version}/latest.json`}]});
  const fake=async(url,options)=>{
    const parsed=new URL(url),path=parsed.pathname;
    const ok=value=>({status:200,ok:true,json:async()=>value});
    if(parsed.hostname==='github.com') return ok(path.includes(restored.version)?restored:faulty);
    if(path.includes('/git/')||path.endsWith('/contents/ledger.json')) return git.fetcher(url,options);
    if(path.endsWith('/aegis-native-signing')) return ok(environment);
    if(path.includes('/collaborators/')) return ok({permission:'admin'});
    if(path.endsWith('/deployment-branch-policies')) return ok(policies);
    if(path.includes('/compare/')) return ok({status:'ahead',base_commit:{sha}});
    if(path.includes('/contents/apps/')) return ok({content:Buffer.from('{"version":"0.7.2"}').toString('base64')});
    if(path.includes('/releases/tags/')) return ok(releaseFor(path.split('node-v')[1]));
    if(path.endsWith('/releases')) return ok([releaseFor(restored.version),releaseFor(faulty.version)]);
    if(path.endsWith('/runs')) return ok({workflow_runs:[]});
    throw Error(`Unexpected rollback endpoint ${url}`);
  };
  try {
    Object.assign(process.env,{GITHUB_EVENT_NAME:context.eventName,GITHUB_REF:context.ref,GITHUB_WORKFLOW_REF:context.workflowRef,GITHUB_REPOSITORY:context.repository,GITHUB_ACTOR:'phcastello',
      PREVIEW_SHA:sha,PREVIEW_BRANCH:input.branch,PREVIEW_VERSION:'0.7.2-unstable.5.rollback.1',PREVIEW_PLATFORM:'both',DISTRIBUTION_KIND:'rollback',
      ROLLBACK_OF:faulty.version,RESTORES_VERSION:restored.version,COMPATIBILITY_CONFIRMED:'false',GITHUB_RUN_ID:'66',GITHUB_RUN_ATTEMPT:'1',GITHUB_SHA:plan.controlSha,GITHUB_OUTPUT:join(work,'output')});
    globalThis.fetch=fake;process.chdir(work);
    await assert.rejects(resolvePreview(),/explicit API/);
    process.env.COMPATIBILITY_CONFIRMED='true';
    const good=restored;
    restored=versioned(restored.version,'c'.repeat(40),7002002);
    await assert.rejects(resolvePreview(),/Restored SHA/);
    restored={...good,schema:1};await assert.rejects(resolvePreview(),/Legacy distributions/);
    restored=good;faulty={...faulty,compatibility:{api:2,nodeData:1}};await assert.rejects(resolvePreview(),/compatibility/);
    faulty=versioned(faulty.version,'b'.repeat(40),7002003);
    process.env.PREVIEW_VERSION='0.7.2-unstable.4.rollback.1';await assert.rejects(resolvePreview(),/Invalid recovery event/);
    process.env.PREVIEW_VERSION='0.7.2-unstable.5.rollback.1';await resolvePreview();
    const result=JSON.parse(readFileSync(join(work,'preview-plan/preview.json')));
    assert.equal(result.sourceCommit,sha);assert.equal(result.versionCode,7002004);assert.equal(result.restoresVersion,restored.version);assert.equal(result.rollbackOf,faulty.version);
  } finally {
    globalThis.fetch=oldFetch;process.chdir(cwd);
    for(const key of Object.keys(process.env)) if(!(key in previousEnv)) delete process.env[key];
    Object.assign(process.env,previousEnv);rmSync(work,{recursive:true,force:true});
  }
});
