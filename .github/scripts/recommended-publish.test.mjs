import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { tmpdir } from 'node:os';
import { manifest as artifactManifest } from './native-preview.mjs';
import { assemble, publishRecommended, releaseFiles, verifyFinal, verifyPromotion } from './recommended-publish.mjs';

const signature = version => Buffer.from(`untrusted comment: fixture\nsignature\ntrusted comment: timestamp:1\tfile:Aegis.exe\tversion:${version}\nglobal-signature`).toString('base64');
const json = value => JSON.stringify(value, null, 2) + '\n';
function fixture(t, changes = {}) {
  const work = mkdtempSync(join(tmpdir(), 'aegis-recommend-'));
  t.after(() => rmSync(work, { recursive: true, force: true }));
  const android = join(work, 'android'), windows = join(work, 'windows'), directory = join(work, 'release');
  mkdirSync(android); mkdirSync(windows);
  const plan = { schema: 2, version: '0.7.2-unstable.2', versionCode: 7002002, kind: 'normal', sha: 'a'.repeat(40), sourceCommit: 'a'.repeat(40),
    sourceVersion: '0.7.2', compatibility: { api: 1, nodeData: 1 }, publishedAt: '2026-10-23T00:00:00.000Z',
    platform: 'both', branch: 'fix/test', number: 2, repository: 'phcastello/Aegis', runId: '123', controlSha: 'b'.repeat(40), ...changes };
  writeFileSync(join(android, 'Aegis-Android-arm64.apk'), 'signed APK bytes');
  writeFileSync(join(windows, 'Aegis-Windows-x86_64-Setup.exe'), 'signed updater bytes');
  writeFileSync(join(windows, 'Aegis-Windows-x86_64-Setup.exe.sig'), signature(plan.version));
  artifactManifest(android, plan, 'Android-arm64', true); artifactManifest(windows, plan, 'Windows-x86_64', true);
  const result = assemble(directory, android, windows, plan);
  const releases = new Map(), tags = new Map(), calls = [], recommendations = [];
  let failure;
  const view = value => ({ tag_name: value.tag_name, draft: value.draft, prerelease: value.prerelease, assets: [...value.files.keys()].map(name => ({ name })) });
  function runGh(args) {
    calls.push(args);
    if (failure) { const failed = failure(args); if (failed) return { status: 1, stdout: '', stderr: 'simulated publication interruption' }; }
    const ok = value => ({ status: 0, stdout: value === undefined ? '' : JSON.stringify(value), stderr: '' });
    const absent = () => ({ status: 1, stdout: JSON.stringify({ status: '404', message: 'Not Found' }), stderr: 'HTTP 404' });
    if (args[0] === 'api') {
      if (args[1].endsWith('/releases?per_page=100')) return ok([[...releases.values()].map(view)]);
      if (args[1].includes('/git/ref/tags/')) return tags.has(args[1].split('/').at(-1)) ? ok({ object: { type: 'commit', sha: tags.get(args[1].split('/').at(-1)) } }) : absent();
      const item = releases.get(args[1].split('/').at(-1));
      return item && !item.draft ? ok(view(item)) : absent();
    }
    const [group, command, tag] = args; assert.equal(group, 'release');
    const item = releases.get(tag), files = args.slice(3).filter(path => existsSync(path) && !['--dir'].includes(path));
    if (command === 'create') {
      assert.ok(!item);
      if(tag==='node-preview') assert.ok(recommendations.length, 'Legacy feed advances after complete recommendation.');
      releases.set(tag, { tag_name: tag, draft: args.includes('--draft'), prerelease: args.includes('--prerelease'),
        files: new Map(files.map(path => [basename(path), readFileSync(path)])) });
      tags.set(tag, args[args.indexOf('--target') + 1]);
    } else if (command === 'download') {
      const target = args[args.indexOf('--dir') + 1];
      for (let index = 0; index < args.length; index++) if (args[index] === '--pattern') {
        const name = args[++index]; if (!item.files.has(name)) return absent();
        writeFileSync(join(target, name), item.files.get(name));
      }
    } else if (command === 'upload') {
      assert.ok(!args.includes('--clobber'));
      for (const path of files) { const name = basename(path); assert.ok(!item.files.has(name)); item.files.set(name, readFileSync(path)); }
    } else if (command === 'edit') { item.draft = false; item.prerelease = args.includes('--prerelease=true'); }
    else throw Error(`Unexpected command ${args}`);
    return ok();
  }
  const state = { read: async () => ({ ledger: { reservations: [plan] } }), recommend: async (received, candidate) => {
    assert.deepEqual(received, plan); const published = releases.get(`node-v${plan.version}`);
    assert.ok(published && !published.draft); assert.equal(published.files.size, releaseFiles.length);
    for (const name of releaseFiles) assert.ok(published.files.get(name).equals(readFileSync(join(directory, name))));
    recommendations.push(candidate); return 'c'.repeat(40);
  } };
  const publish = () => publishRecommended({ directory, plan, state, runGh });
  return { work, android, windows, directory, plan, result, releases, tags, calls, recommendations, state, publish,
    setFailure: fn => { failure = fn; } };
}

test('real both-platform plan retains distribution platform and independently validates artifact platform', t => {
  const f = fixture(t);
  const artifact = JSON.parse(readFileSync(join(f.android, 'preview.json')));
  assert.equal(artifact.platform, 'both'); assert.equal(artifact.artifactPlatform, 'Android-arm64');
  verifyFinal(f.android, f.plan, 'Android-arm64');
  assert.equal(f.result.schema, 2); assert.equal(f.result.sourceCommit, f.plan.sha);
  assert.equal(f.result.android.aarch64.versionCode, f.plan.versionCode);
  assert.match(f.result.android.aarch64.sha256, /^[a-f0-9]{64}$/);
  assert.throws(() => verifyFinal(f.android, { ...f.plan, sourceCommit: 'd'.repeat(40) }, 'Android-arm64'), /reservation/);
  writeFileSync(join(f.android, 'Aegis-Android-arm64.apk'), 'tampered');
  assert.throws(() => verifyFinal(f.android, f.plan, 'Android-arm64'), /hash mismatch/);
});
test('publication makes all immutable bytes public and verifies downloads before atomic recommendation', async t => {
  const f = fixture(t); const result = await f.publish();
  assert.equal(result.compatibilityFeed, 'unchanged'); assert.equal(f.recommendations.length, 1);
  assert.ok(!f.calls.some(args => args.includes('--clobber') || args.some(value => value.includes('node-preview'))));
  const before = f.calls.length; await f.publish();
  assert.ok(!f.calls.slice(before).some(args => ['create', 'upload', 'edit'].includes(args[1])), 'same bytes retry is publication no-op');
});
test('stable promotion retains its historical code and advances both recommended and legacy stable feed', async t => {
  const f=fixture(t,{version:'0.7.2',versionCode:7002999});
  assert.equal((await f.publish()).compatibilityFeed,'stable');
  assert.equal(f.recommendations.length,1);
  assert.deepEqual(JSON.parse(f.releases.get('node-preview').files.get('latest.json')),f.result);
  const before=f.calls.length;await f.publish();
  assert.ok(!f.calls.slice(before).some(args=>['create','upload','edit'].includes(args[1])));
});
test('partial upload leaves the previous recommendation and resumes only missing immutable assets', async t => {
  const f = fixture(t), tag = `node-v${f.plan.version}`;
  f.releases.set(tag, { tag_name: tag, draft: true, prerelease: true, files: new Map([[releaseFiles[0], readFileSync(join(f.directory, releaseFiles[0]))]]) });
  f.tags.set(tag, f.plan.sha);
  f.setFailure(args => args[1] === 'upload');
  await assert.rejects(f.publish(), /interruption/);
  assert.equal(f.recommendations.length, 0); assert.equal(f.releases.get(tag).draft, true);
  f.setFailure(); await f.publish();
  assert.equal(f.recommendations.length, 1);
  const upload = f.calls.filter(args => args[1] === 'upload').at(-1);
  assert.ok(!upload.some(path => path.endsWith(releaseFiles[0])));
});
test('different published bytes, wrong source tag, absent assets and inconsistent metadata cannot advance pointer', async t => {
  for (const mode of ['bytes', 'tag', 'missing', 'metadata']) {
    const f = fixture(t); await f.publish(); f.recommendations.length = 0;
    const current = f.releases.get(`node-v${f.plan.version}`);
    if (mode === 'bytes') current.files.set(releaseFiles[0], Buffer.from('other bytes'));
    if (mode === 'tag') f.tags.set(`node-v${f.plan.version}`, 'd'.repeat(40));
    if (mode === 'missing') current.files.delete(releaseFiles[2]);
    if (mode === 'metadata') { const manifest = JSON.parse(readFileSync(join(f.directory, 'latest.json'))); manifest.sourceCommit = 'd'.repeat(40); writeFileSync(join(f.directory, 'latest.json'), json(manifest)); }
    await assert.rejects(f.publish()); assert.equal(f.recommendations.length, 0);
  }
});
test('rollback is rebuilt under a new version/code with exact restored source recorded', async t => {
  const f = fixture(t, { version: '0.7.2-unstable.5.rollback.1', versionCode: 7002006, kind: 'rollback', rollbackOf: '0.7.2-unstable.5', restoresVersion: '0.7.2-unstable.4' });
  await f.publish();
  assert.equal(f.result.rollbackOf, '0.7.2-unstable.5'); assert.equal(f.result.restoresVersion, '0.7.2-unstable.4');
  assert.equal(f.result.sourceCommit, f.plan.sha); assert.equal(f.result.versionCode, 7002006);
});
test('promotion admits only successful main manual v2 runs with both complete platforms', async () => {
  const plan = { schema: 2, runId: '123', controlSha: 'b'.repeat(40), platform: 'both' };
  const run = { event: 'workflow_dispatch', head_branch: 'main', conclusion: 'success', path: '.github/workflows/aegis-native-preview.yml',
    head_repository: { full_name: 'phcastello/Aegis' }, head_sha: plan.controlSha };
  const state = { api: async () => run, read: async () => ({ ledger: { reservations: [plan] } }) };
  assert.deepEqual(await verifyPromotion('123', { state }), plan);
  for (const [field, value] of [['head_branch', 'fix/untrusted'], ['event', 'push'], ['conclusion', 'failure'], ['head_sha', 'c'.repeat(40)]]) {
    const previous = run[field]; run[field] = value; await assert.rejects(verifyPromotion('123', { state })); run[field] = previous;
  }
  plan.platform = 'android'; await assert.rejects(verifyPromotion('123', { state }));
});
