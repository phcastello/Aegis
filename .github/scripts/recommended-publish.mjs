// Executed only from reviewed main, after protected approval and signature checks.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { metadata } from '../../apps/aegis-node/scripts/release-metadata.mjs';
import { advanceLegacyStable } from '../../apps/aegis-node/scripts/publish-release.mjs';
import { DistributionState, repository } from './distribution-state.mjs';

export const releaseFiles = ['Aegis-Windows-x86_64-Setup.exe', 'Aegis-Windows-x86_64-Setup.exe.sig',
  'Aegis-Android-arm64.apk', 'latest.json', 'SHA256SUMS'];
const json = value => JSON.stringify(value, null, 2) + '\n';
const digest = file => createHash('sha256').update(readFileSync(file)).digest('hex');
const readJson = file => JSON.parse(readFileSync(file, 'utf8'));
const execute = args => spawnSync('gh', args[0] === 'api' ? args : [...args, '--repo', repository], { encoding: 'utf8' });

export function verifyFinal(directory, plan, platform) {
  const received = readJson(join(directory, 'preview.json'));
  const { artifactPlatform: actual, signatures, sha256, ...identity } = received;
  assert.deepEqual(identity, plan, 'Signed artifact does not match the exact reservation.');
  assert.equal(actual, platform); assert.equal(signatures, 'official');
  const names = platform === 'Android-arm64' ? ['Aegis-Android-arm64.apk'] :
    ['Aegis-Windows-x86_64-Setup.exe', 'Aegis-Windows-x86_64-Setup.exe.sig'];
  assert.deepEqual(readdirSync(directory).sort(), [...names, 'preview.json', 'SHA256SUMS'].sort(), 'Unexpected signed artifact.');
  assert.deepEqual(Object.keys(sha256).sort(), [...names].sort());
  for (const name of names) {
    assert.equal(realpathSync(join(directory, name)), resolve(directory, name), 'Signed artifacts cannot be symlinks.');
    assert.ok(readFileSync(join(directory, name)).length, 'Empty signed artifact.');
    assert.equal(digest(join(directory, name)), sha256[name], 'Signed artifact hash mismatch.');
  }
  assert.equal(readFileSync(join(directory, 'SHA256SUMS'), 'utf8'),
    Object.entries(sha256).map(([name, hash]) => `${hash}  ${name}\n`).join(''), 'Signed checksums mismatch.');
  return sha256;
}

export function assemble(directory, android, windows, plan) {
  assert.equal(plan.schema, 2); assert.equal(plan.platform, 'both', 'A recommendation needs both supported platforms.');
  assert.match(plan.sha, /^[a-f0-9]{40}$/); assert.equal(plan.sourceCommit, plan.sha);
  const sha256 = { ...verifyFinal(android, plan, 'Android-arm64'), ...verifyFinal(windows, plan, 'Windows-x86_64') };
  mkdirSync(directory, { recursive: true });
  for (const name of releaseFiles.slice(0, 3)) copyFileSync(join(name.endsWith('.apk') ? android : windows, name), join(directory, name));
  const signature = readFileSync(join(directory, releaseFiles[1]), 'utf8');
  const manifest = metadata(plan.version, signature, plan.publishedAt, { ...plan, sha256 });
  writeFileSync(join(directory, 'latest.json'), json(manifest));
  writeFileSync(join(directory, 'SHA256SUMS'), releaseFiles.filter(name => name !== 'SHA256SUMS')
    .map(name => `${digest(join(directory, name))}  ${name}\n`).join(''));
  return manifest;
}

export async function publishRecommended({ directory, plan, runGh = execute, state = new DistributionState() }) {
  assert.equal(plan.schema, 2); assert.equal(plan.platform, 'both');
  directory = resolve(directory);
  const manifest = readJson(join(directory, 'latest.json'));
  const signature = readFileSync(join(directory, releaseFiles[1]), 'utf8');
  const sha256 = Object.fromEntries(releaseFiles.slice(0, 3).map(name => [name, digest(join(directory, name))]));
  assert.deepEqual(manifest, metadata(plan.version, signature, plan.publishedAt, { ...plan, sha256 }), 'Manifest differs from reserved source or verified bytes.');
  const snapshot = await state.read();
  assert.deepEqual(snapshot.ledger.reservations.find(item => item.version === plan.version), plan, 'Missing durable reservation.');
  const tag = `node-v${plan.version}`;
  function gh(args) {
    const result = runGh(args);
    if (result.status !== 0) throw Error(result.stderr || result.stdout || 'Distribution publication failed.');
    return result.stdout;
  }
  function get(path, absent = false) {
    const result = runGh(['api', `repos/${repository}/${path}`]);
    if (result.status === 0) return JSON.parse(result.stdout);
    let response; try { response = JSON.parse(result.stdout); } catch { /* fail closed */ }
    if (absent && String(response?.status) === '404' && response.message === 'Not Found') return null;
    throw Error(result.stderr || 'Cannot inspect distribution state.');
  }
  function assertTag(required) {
    let ref = get(`git/ref/tags/${tag}`, !required);
    if (!ref) return;
    let object = ref.object;
    for (let depth = 0; object.type === 'tag' && depth < 8; depth++) object = get(`git/tags/${object.sha}`).object;
    assert.equal(object.type, 'commit'); assert.equal(object.sha, plan.sha, 'Published tag points to a different source.');
  }
  function release() {
    const current = get(`releases/tags/${tag}`, true);
    if (current) return current;
    const pages = JSON.parse(gh(['api', `repos/${repository}/releases?per_page=100`, '--paginate', '--slurp']));
    assert.ok(Array.isArray(pages) && pages.every(Array.isArray));
    const matches = pages.flat().filter(item => item.tag_name === tag);
    assert.ok(matches.length <= 1, 'Duplicate distribution releases.');
    return matches[0] ?? null;
  }
  function verifyAssets(current, complete = true) {
    assert.equal(current.tag_name, tag); assert.equal(current.prerelease, plan.version.includes('-'));
    const names = current.assets.map(asset => asset.name);
    assert.equal(new Set(names).size, names.length);
    assert.ok(names.every(name => releaseFiles.includes(name)), 'Unexpected immutable asset.');
    const temp = mkdtempSync(join(tmpdir(), 'aegis-publication-'));
    try {
      if (names.length) gh(['release', 'download', tag, ...names.flatMap(name => ['--pattern', name]), '--dir', temp]);
      for (const name of names) assert.ok(readFileSync(join(temp, name)).equals(readFileSync(join(directory, name))), `Immutable asset differs: ${name}`);
    } finally { rmSync(temp, { recursive: true, force: true }); }
    const missing = releaseFiles.filter(name => !names.includes(name));
    if (complete) assert.equal(missing.length, 0, 'Published distribution is incomplete.');
    return missing;
  }
  assertTag(false);
  let current = release();
  if (!current) {
    gh(['release', 'create', tag, '--target', plan.sha, '--draft', '--latest=false', ...(plan.version.includes('-') ? ['--prerelease'] : []),
      '--title', `Aegis Node ${plan.version}`, '--notes', plan.kind === 'rollback'
        ? `Recovery of ${plan.rollbackOf}; restores code from ${plan.restoresVersion} (${plan.sourceCommit}). No database downgrade is performed.`
        : `Recommended Aegis native distribution ${plan.version}. Source: ${plan.sourceCommit}.`,
      ...releaseFiles.map(name => join(directory, name))]);
    current = release();
  } else {
    const missing = verifyAssets(current, !current.draft);
    if (missing.length) gh(['release', 'upload', tag, ...missing.map(name => join(directory, name))]);
    current = release();
  }
  verifyAssets(current);
  if (current.draft) gh(['release', 'edit', tag, '--draft=false', `--prerelease=${plan.version.includes('-')}`, '--latest=false']);
  const published = release();
  assert.ok(published && !published.draft, 'Distribution must be public before recommendation.');
  verifyAssets(published); assertTag(true);
  // No destructive asset replacement. Git atomically advances latest.json only
  // after every public installer, signature and checksum was downloaded/compared.
  const pointer = await state.recommend(plan, manifest);
  if (!plan.version.includes('-')) advanceLegacyStable({ manifest, commit: plan.sha, runGh });
  return { tag, version: plan.version, versionCode: plan.versionCode, pointer, compatibilityFeed: plan.version.includes('-') ? 'unchanged' : 'stable' };
}

export async function verifyPromotion(runId, { state = new DistributionState() } = {}) {
  assert.match(String(runId), /^[1-9]\d*$/);
  const run = await state.api(`actions/runs/${runId}`);
  assert.equal(run.event, 'workflow_dispatch'); assert.equal(run.head_branch, 'main'); assert.equal(run.conclusion, 'success');
  assert.equal(run.path, '.github/workflows/aegis-native-preview.yml');
  assert.equal(run.head_repository.full_name, repository);
  const snapshot = await state.read();
  const plan = snapshot.ledger.reservations.find(item => item.runId === String(runId));
  assert.ok(plan, 'Only a durably reserved v2 run can be promoted.');
  assert.equal(plan.controlSha, run.head_sha); assert.equal(plan.platform, 'both');
  return plan;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'plan') {
    const plan = await verifyPromotion(args[0]);
    mkdirSync('promotion-plan', { recursive: true }); writeFileSync('promotion-plan/preview.json', json(plan));
    const { appendFileSync } = await import('node:fs');
    for (const name of ['sha', 'version', 'versionCode']) appendFileSync(process.env.GITHUB_OUTPUT, `${name}=${plan[name]}\n`);
  } else if (command === 'assemble') assemble(resolve(args[0]), resolve(args[1]), resolve(args[2]), readJson(args[3]));
  else if (command === 'publish') console.log(await publishRecommended({ directory: args[0], plan: readJson(args[1]) }));
  else throw Error('Unknown recommended publication command.');
}
