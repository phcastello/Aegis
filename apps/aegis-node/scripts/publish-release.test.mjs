import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import test from 'node:test';
import { metadata } from './release-metadata.mjs';
import { checksumManifest, publishRelease, releaseFiles, releaseKind } from './publish-release.mjs';

const commit = 'a'.repeat(40);
const signature = version => Buffer.from(`untrusted comment: test fixture\nsignature\ntrusted comment: timestamp:1\tfile:Aegis.exe\tversion:${version}\nglobal-signature`).toString('base64');
const json = value => Buffer.from(JSON.stringify(value, null, 2) + '\n');
function fixture(t, version = '0.7.0') {
  const directory = mkdtempSync(join(tmpdir(), 'aegis-publisher-test-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  writeFileSync(join(directory, releaseFiles[0]), 'installer bytes');
  writeFileSync(join(directory, releaseFiles[1]), signature(version));
  writeFileSync(join(directory, releaseFiles[2]), 'apk bytes');
  writeFileSync(join(directory, 'latest.json'), json(metadata(version, signature(version), '2026-10-20T00:00:00Z')));
  const releases = new Map();
  const calls = [];
  let fail = null;
  function addRelease(tag, { version: previousVersion = '0.7.0-unstable.19', files, draft = false, prerelease = true } = {}) {
    releases.set(tag, { tag_name: tag, draft, prerelease, files: files ?? new Map([['latest.json', json(metadata(previousVersion, signature(previousVersion), '2026-10-19T00:00:00Z'))]]) });
  }
  addRelease('node-preview');
  function runGh(args) {
    calls.push(args);
    if (fail) { const failure = fail(args); if (failure) return failure; }
    const ok = value => ({ status: 0, stdout: value === undefined ? '' : JSON.stringify(value), stderr: '' });
    const view = item => ({ ...item, files: undefined, assets: [...item.files.keys()].map(name => ({ name })) });
    if (args[0] === 'api') {
      if (args[1].endsWith('/releases?per_page=100')) return ok([[...releases.values()].map(view)]);
      const item = releases.get(args[1].split('/').at(-1));
      // REST's tag lookup excludes drafts; only the authenticated list exposes them.
      return item && !item.draft ? ok(view(item)) : { status: 1, stdout: JSON.stringify({ message: 'Not Found', status: '404' }), stderr: 'HTTP 404' };
    }
    const [group, command, tag] = args;
    assert.equal(group, 'release');
    const item = releases.get(tag);
    const files = args.slice(3).filter(value => existsSync(value));
    if (command === 'create') {
      assert.ok(!item, 'release must not be recreated');
      addRelease(tag, { files: new Map(files.map(path => [basename(path), readFileSync(path)])), draft: args.includes('--draft'), prerelease: args.includes('--prerelease') });
    } else if (command === 'download') {
      assert.ok(item, 'release download requires existing release');
      const target = args[args.indexOf('--dir') + 1];
      for (let i = 0; i < args.length; i++) if (args[i] === '--pattern') {
        const name = args[++i];
        if (!item.files.has(name)) return { status: 1, stdout: '', stderr: 'asset not found' };
        writeFileSync(join(target, name), item.files.get(name));
      }
    } else if (command === 'upload') {
      for (const path of files) {
        const name = basename(path);
        assert.ok(!item.files.has(name) || args.includes('--clobber'), 'existing asset requires explicit clobber');
        item.files.set(name, readFileSync(path));
      }
    } else if (command === 'edit') {
      item.draft = !args.includes('--draft=false');
      item.prerelease = args.includes('--prerelease=true');
    } else throw new Error('Unhandled gh command: ' + args.join(' '));
    return ok();
  }
  const publish = () => publishRelease({ tag: `node-v${version}`, commit, version, directory, runGh });
  return { directory, releases, calls, addRelease, publish, runGh, setFail: value => { fail = value; } };
}

for (const version of ['0.7.0-unstable.20', '0.7.0', '0.7.1']) {
  test(`${version}: publishes verified immutable assets before advancing feed, without becoming Latest`, t => {
    const f = fixture(t, version);
    if (version === '0.7.1') f.addRelease('node-preview', { version: '0.7.0' });
    const result = f.publish();
    const current = f.releases.get(`node-v${version}`);
    assert.equal(result.kind, version.includes('-') ? 'preview' : 'stable');
    assert.equal(current.draft, false);
    assert.equal(current.prerelease, version.includes('-'));
    assert.deepEqual([...current.files.keys()], releaseFiles);
    assert.deepEqual(f.releases.get('node-preview').files.get('latest.json'), current.files.get('latest.json'));
    const create = f.calls.find(args => args[1] === 'create');
    assert.ok(create.includes('--draft'));
    assert.ok(create.includes('--latest=false'));
    assert.equal(create.includes('--prerelease'), version.includes('-'));
    assert.ok(!create.join(' ').includes('ainda não está autorizada'));
    const publication = f.calls.findIndex(args => args[1] === 'edit');
    const feedUpload = f.calls.findIndex(args => args[1] === 'upload' && args[2] === 'node-preview');
    assert.ok(publication < feedUpload);
    assert.equal(f.calls.filter(args => args.includes('--clobber')).length, 1);
    for (const args of f.calls.filter(args => args.includes('--clobber'))) assert.equal(args[2], 'node-preview');
    assert.equal(readFileSync(join(f.directory, 'SHA256SUMS'), 'utf8'), checksumManifest(f.directory));
    const installerHash = createHash('sha256').update('installer bytes').digest('hex');
    assert.ok(current.files.get('SHA256SUMS').toString().includes(installerHash));
  });
}

test('0.7.1 advances the stable Android allocation and immutable distribution URLs', t => {
  const f = fixture(t, '0.7.1');
  const manifest = JSON.parse(readFileSync(join(f.directory, 'latest.json')));
  assert.equal(manifest.version, '0.7.1');
  assert.equal(manifest.android.aarch64.versionCode, 7001999);
  assert.ok(manifest.android.aarch64.versionCode > 7000999);
  assert.match(manifest.android.aarch64.url, /\/node-v0\.7\.1\/Aegis-Android-arm64\.apk$/);
  assert.match(manifest.platforms['windows-x86_64'].url, /\/node-v0\.7\.1\/Aegis-Windows-x86_64-Setup\.exe$/);
});

test('stable manifest binds exact version, updater signature, APK code and immutable URLs', t => {
  const f = fixture(t);
  const manifest = JSON.parse(readFileSync(join(f.directory, 'latest.json')));
  assert.equal(manifest.android.aarch64.versionCode, 7000999);
  assert.match(manifest.notes, /It's Not Stalking\. It's a Feature\./);
  manifest.android.aarch64.versionCode = 7000019;
  writeFileSync(join(f.directory, 'latest.json'), json(manifest));
  assert.throws(f.publish, /Manifest must exactly match/);
  assert.equal(f.calls.length, 0);
});

test('rejects tag/package mismatch, unsupported legacy channel and absent commit before GitHub calls', t => {
  const f = fixture(t);
  assert.throws(() => publishRelease({ tag: 'node-v0.7.0-unstable.19', commit, version: '0.7.0', directory: f.directory, runGh: f.runGh }), /tag\/package/);
  assert.throws(() => publishRelease({ tag: 'node-v0.7.0', version: '0.7.0', directory: f.directory, runGh: f.runGh }), /exact release commit/);
  assert.throws(() => releaseKind('0.7.0-stage.19'), /New native releases/);
  assert.equal(f.calls.length, 0);
});

test('rejects preview after stable without creating or replacing assets', t => {
  const f = fixture(t, '0.7.0-unstable.20');
  f.addRelease('node-preview', { version: '0.7.0' });
  assert.throws(f.publish, /downgrade/);
  assert.ok(!f.calls.some(args => ['create', 'upload', 'edit'].includes(args[1])));
});

test('rejects a newer published distribution even if feed is stale', t => {
  const f = fixture(t);
  f.addRelease('node-v0.7.1', { version: '0.7.1', prerelease: false });
  assert.throws(f.publish, /newer or conflicting/);
  assert.ok(!f.calls.some(args => ['create', 'upload', 'edit'].includes(args[1])));
});

test('retry of an identical published release is a verified no-op', t => {
  const f = fixture(t);
  f.publish();
  f.calls.length = 0;
  f.publish();
  assert.ok(!f.calls.some(args => ['create', 'upload', 'edit'].includes(args[1])));
});

test('retry repairs missing draft assets without replacing existing bytes', t => {
  const f = fixture(t);
  f.addRelease('node-v0.7.0', { draft: true, prerelease: false, files: new Map([[releaseFiles[0], readFileSync(join(f.directory, releaseFiles[0]))]]) });
  f.publish();
  const upload = f.calls.find(args => args[1] === 'upload' && args[2] === 'node-v0.7.0');
  assert.ok(!upload.includes('--clobber'));
  assert.ok(!upload.some(arg => arg.endsWith(releaseFiles[0])));
  assert.equal(f.releases.get('node-v0.7.0').draft, false);
});

test('retry rejects different immutable bytes and leaves existing release/feed untouched', t => {
  const f = fixture(t);
  f.publish();
  writeFileSync(join(f.directory, releaseFiles[0]), 'different installer');
  f.calls.length = 0;
  assert.throws(f.publish, /Immutable release asset differs/);
  assert.ok(!f.calls.some(args => ['create', 'upload', 'edit'].includes(args[1])));
});

test('same version feed metadata cannot be silently replaced', t => {
  const f = fixture(t);
  f.addRelease('node-preview', { version: '0.7.0' }); // Different date from the deterministic local metadata.
  assert.throws(f.publish, /replace an existing version/);
  assert.ok(!f.calls.some(args => ['create', 'upload', 'edit'].includes(args[1])));
});

test('missing compatibility feed is created only after public distribution is verified', t => {
  const f = fixture(t);
  f.releases.delete('node-preview');
  f.publish();
  const creates = f.calls.filter(args => args[1] === 'create');
  assert.deepEqual(creates.map(args => args[2]), ['node-v0.7.0', 'node-preview']);
  assert.ok(creates[1].includes('--latest=false'));
});

test('network failure inspecting a release blocks publication instead of meaning no update', t => {
  const f = fixture(t);
  f.setFail(args => args[0] === 'api' && args[1].endsWith('/tags/node-preview') ? { status: 1, stdout: '', stderr: 'network unreachable' } : null);
  assert.throws(f.publish, /network unreachable/);
  assert.ok(!f.calls.some(args => ['create', 'upload', 'edit'].includes(args[1])));
});

test('failed upload leaves recoverable draft and never advances feed', t => {
  const f = fixture(t);
  f.addRelease('node-v0.7.0', { draft: true, prerelease: false, files: new Map() });
  f.setFail(args => args[1] === 'upload' ? { status: 1, stdout: '', stderr: 'upload interrupted' } : null);
  assert.throws(f.publish, /upload interrupted/);
  assert.equal(f.releases.get('node-v0.7.0').draft, true);
  assert.ok(!f.calls.some(args => args[1] === 'edit' || args[1] === 'upload' && args[2] === 'node-preview'));
  f.setFail(null);
  f.publish();
  assert.equal(f.releases.get('node-v0.7.0').draft, false);
});


test('published-only tag lookup discovers newly created and partial drafts through authenticated listing', t => {
  const f = fixture(t);
  f.publish();
  const creation = f.calls.findIndex(args => args[1] === 'create' && args[2] === 'node-v0.7.0');
  const publication = f.calls.findIndex(args => args[1] === 'edit');
  const beforePublication = f.calls.slice(creation + 1, publication);
  assert.ok(beforePublication.some(args => args[0] === 'api' && args[1].endsWith('/tags/node-v0.7.0')));
  assert.ok(beforePublication.some(args => args[0] === 'api' && args[1].endsWith('/releases?per_page=100')));
  assert.ok(beforePublication.some(args => args[1] === 'download' && args[2] === 'node-v0.7.0'));
  assert.equal(f.calls.filter(args => args[1] === 'create' && args[2] === 'node-v0.7.0').length, 1);
});
