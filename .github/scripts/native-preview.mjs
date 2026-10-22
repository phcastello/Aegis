// Trusted control plane: run this file from the workflow's main checkout only.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFileSync, mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync, appendFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { androidVersionCode } from '../../apps/aegis-node/scripts/version.mjs';

export const signingEnvironment = 'aegis-native-signing';
export function parameters({ sha, branch, number, platform }) {
  assert.match(sha, /^[0-9a-f]{40}$/, 'Supply a full lowercase commit SHA from this repository.');
  assert.match(branch, /^[A-Za-z0-9][A-Za-z0-9_./-]*$/, 'Supply a branch name, not a repository or URL.');
  execFileSync('git', ['check-ref-format', '--branch', branch], { stdio: 'pipe' });
  assert.match(String(number), /^[1-9]\d{0,2}$/, 'Preview number must be 1..998.');
  assert.ok(Number(number) < 999, '999 is reserved for the stable Android version.');
  assert.ok(['android', 'windows', 'both'].includes(platform), 'Unknown platform.');
  return { sha, branch, number: Number(number), platform };
}

export function assertTrustedDispatch(context) {
  assert.equal(context.eventName, 'workflow_dispatch', 'Preview requires explicit manual dispatch.');
  assert.equal(context.ref, 'refs/heads/main', 'Run the trusted workflow from main.');
  assert.equal(context.workflowRef, `${context.repository}/.github/workflows/aegis-native-preview.yml@refs/heads/main`, 'Untrusted workflow reference.');
  assert.equal(Number(context.attempt ?? 1), 1, 'Start a new run with a new preview number; reruns cannot replace a signed version.');
}

export function assertEnvironment(environment, policies) {
  assert.equal(environment.name, signingEnvironment);
  const reviewer = environment.protection_rules?.find(rule => rule.type === 'required_reviewers');
  assert.ok(reviewer?.reviewers?.length, 'Configure required reviewers on aegis-native-signing before use.');
  assert.equal(environment.deployment_branch_policy?.custom_branch_policies, true, 'Restrict signing to main and release tags.');
  assert.ok(policies.branch_policies?.some(policy => policy.name === 'main' && (policy.type ?? 'branch') === 'branch'), 'Signing must permit the trusted main branch.');
  assert.ok(policies.branch_policies.every(policy =>
    ((policy.type ?? 'branch') === 'branch' && policy.name === 'main') || (policy.type === 'tag' && policy.name === 'node-v*')),
  'Signing may allow only main and existing node-v* release tags.');
}

export function assertIncreasing(version, previous) {
  const code = androidVersionCode(version);
  for (const item of previous) {
    const name = /^node-v(.+)$/.exec(item.tag_name ?? '')?.[1] ??
      /^Aegis Native Preview · (\d+\.\d+\.\d+-unstable\.\d+) ·/.exec(item.display_title ?? '')?.[1];
    if (name) assert.ok(code > androidVersionCode(name), `Preview must be newer than reserved/published version ${name}.`);
  }
  return code;
}

async function api(path) {
  const result = await fetch(`https://api.github.com/repos/${process.env.GITHUB_REPOSITORY}/${path}`, {
    headers: { authorization: `Bearer ${process.env.GH_TOKEN}`, accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' }
  });
  assert.equal(result.status, 200, `Cannot verify preview prerequisite: ${path.split('?')[0]} (${result.status}).`);
  return result.json();
}
async function pages(path, field) {
  const result = [];
  for (let page = 1; ; page++) {
    const response = await api(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`);
    const values = field ? response[field] : response;
    assert.ok(Array.isArray(values), 'Invalid GitHub response.');
    result.push(...values);
    if (values.length < 100) return result;
  }
}
const json = path => JSON.parse(readFileSync(path, 'utf8'));
const writeJson = (path, value) => writeFileSync(path, JSON.stringify(value, null, 2) + '\n');
const digest = path => createHash('sha256').update(readFileSync(path)).digest('hex');
function output(name, value) { appendFileSync(process.env.GITHUB_OUTPUT, `${name}=${value}\n`); }

export function prepareCandidate(candidate, trusted, version) {
  const node = join(candidate, 'apps/aegis-node');
  const baseline = json(join(trusted, 'apps/aegis-node/src-tauri/tauri.conf.json'));
  const config = json(join(node, 'src-tauri/tauri.conf.json'));
  for (const key of ['productName', 'identifier']) assert.equal(config[key], baseline[key], `Preview changes ${key}.`);
  assert.deepEqual(config.plugins.updater, baseline.plugins.updater, 'Preview must retain the installed updater identity, key and endpoint.');
  assert.deepEqual(config.bundle.windows, baseline.bundle.windows, 'Preview must retain Windows installation settings.');
  assert.equal(config.app.windows[0].label, baseline.app.windows[0].label);
  const packagePath = join(node, 'package.json');
  const manifest = json(packagePath);
  assert.equal(manifest.version.split('-')[0], version.split('-')[0], 'Preview version must use the candidate release line.');
  manifest.version = version;
  writeJson(packagePath, manifest);
  // The existing sync implementation is reused, but never imported from candidate code.
  copyFileSync(join(trusted, 'apps/aegis-node/scripts/version.mjs'), join(node, 'scripts/version.mjs'));
  execFileSync(process.execPath, ['scripts/version.mjs', '--write'], { cwd: node, stdio: 'inherit' });
  execFileSync(process.execPath, ['scripts/version.mjs'], { cwd: node, stdio: 'inherit' });
}

export async function resolvePreview() {
  assertTrustedDispatch({ eventName: process.env.GITHUB_EVENT_NAME, ref: process.env.GITHUB_REF,
    workflowRef: process.env.GITHUB_WORKFLOW_REF, repository: process.env.GITHUB_REPOSITORY, attempt: process.env.GITHUB_RUN_ATTEMPT });
  const requested = /^(\d+\.\d+\.\d+)-unstable\.([1-9]\d*)$/.exec(process.env.PREVIEW_VERSION ?? '');
  assert.ok(requested, 'Use the complete X.Y.Z-unstable.N preview version.');
  const args = parameters({ sha: process.env.PREVIEW_SHA, branch: process.env.PREVIEW_BRANCH,
    number: requested[2], platform: process.env.PREVIEW_PLATFORM });
  const [environment, branchPolicies] = await Promise.all([
    api(`environments/${signingEnvironment}`), pages(`environments/${signingEnvironment}/deployment-branch-policies`, 'branch_policies')
  ]);
  assertEnvironment(environment, { branch_policies: branchPolicies });
  const comparison = await api(`compare/${args.sha}...${encodeURIComponent(args.branch)}`);
  assert.ok(['ahead', 'identical'].includes(comparison.status), 'SHA must be reachable from the selected repository branch.');
  assert.equal(comparison.base_commit.sha, args.sha);
  const content = await api(`contents/apps/aegis-node/package.json?ref=${args.sha}`);
  const base = JSON.parse(Buffer.from(content.content, 'base64')).version.split('-')[0];
  assert.equal(requested[1], base, 'Preview must match the source release line.');
  const version = `${base}-unstable.${args.number}`;
  const [releases, runs] = await Promise.all([pages('releases'), pages('actions/workflows/aegis-native-preview.yml/runs', 'workflow_runs')]);
  const previous = [];
  for (const run of runs.filter(run => String(run.id) !== process.env.GITHUB_RUN_ID && run.head_branch === 'main' &&
    run.event === 'workflow_dispatch' && /^Aegis Native Preview · /.test(run.display_title ?? ''))) {
    // Lower versions cannot block this allocation; avoid one API request per
    // historical preview while retaining fail-closed checks for possible floors.
    let couldBlock = false;
    try { assertIncreasing(version, [run]); } catch { couldBlock = true; }
    if (!couldBlock) continue;
    // Only admitted plans can have reached build/signing. Failed input validation
    // must not reserve a typo forever; a later build/sign failure still reserves N.
    const jobs = await pages(`actions/runs/${run.id}/jobs?filter=all`, 'jobs');
    if (jobs.some(job => job.name === 'plan' && job.status === 'completed' && job.conclusion === 'success')) previous.push(run);
  }
  const versionCode = assertIncreasing(version, [...releases, ...previous]);
  const plan = { schema: 1, ...args, version, versionCode, repository: process.env.GITHUB_REPOSITORY,
    runId: process.env.GITHUB_RUN_ID, controlSha: process.env.GITHUB_SHA,
    limits: 'Development preview; no stable release, feed update, physical acceptance or backend deployment.' };
  mkdirSync('preview-plan', { recursive: true });
  writeJson('preview-plan/preview.json', plan);
  for (const name of ['sha', 'version', 'versionCode']) output(name, plan[name]);
  output('matrix', JSON.stringify({ include: [
    ...(args.platform !== 'windows' ? [{ platform: 'Android-arm64', android: 'true', os: 'ubuntu-24.04' }] : []),
    ...(args.platform !== 'android' ? [{ platform: 'Windows-x86_64', android: 'false', os: 'windows-2022' }] : [])
  ] }));
}

export function manifest(directory, plan, platform, signed = false) {
  const filenames = platform === 'Android-arm64' ? ['Aegis-Android-arm64.apk'] :
    ['Aegis-Windows-x86_64-Setup.exe', ...(signed ? ['Aegis-Windows-x86_64-Setup.exe.sig'] : [])];
  for (const name of filenames) assert.equal(realpathSync(join(directory, name)), resolve(directory, name), 'Artifacts cannot be symlinks.');
  const hashes = Object.fromEntries(filenames.map(name => [name, digest(join(directory, name))]));
  writeJson(join(directory, 'preview.json'), { ...plan, platform, signatures: signed ? 'official' : 'not-for-installation', sha256: hashes });
  writeFileSync(join(directory, 'SHA256SUMS'), Object.entries(hashes).map(([name, hash]) => `${hash}  ${name}\n`).join(''));
  return hashes;
}

export function verifyInput(directory, plan, platform) {
  const received = json(join(directory, 'preview.json'));
  for (const field of ['sha', 'version', 'versionCode', 'number', 'repository', 'runId', 'controlSha']) assert.equal(received[field], plan[field], `Artifact ${field} mismatch.`);
  assert.equal(received.platform, platform);
  const file = platform === 'Android-arm64' ? 'Aegis-Android-arm64.apk' : 'Aegis-Windows-x86_64-Setup.exe';
  assert.deepEqual(readdirSync(directory).sort(), [file, 'preview.json', 'SHA256SUMS', ...(platform === 'Android-arm64' ? ['BuildConfig.java'] : [])].sort(), 'Unexpected build artifact.');
  assert.equal(realpathSync(join(directory, file)), resolve(directory, file));
  assert.equal(digest(join(directory, file)), received.sha256[file], 'Candidate artifact digest mismatch.');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'resolve') await resolvePreview();
  else if (command === 'prepare') prepareCandidate(resolve(args[0]), resolve(args[1]), args[2]);
  else if (command === 'manifest') manifest(resolve(args[0]), json(args[1]), args[2], args[3] === 'signed');
  else if (command === 'verify-input') verifyInput(resolve(args[0]), json(args[1]), args[2]);
  else throw Error('Unknown preview control command.');
}
