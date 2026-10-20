import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { androidVersionCode, synchronize } from './version.mjs';
import { metadata } from './release-metadata.mjs';

const repo = 'phcastello/Aegis';
export const releaseFiles = [
  'Aegis-Windows-x86_64-Setup.exe', 'Aegis-Windows-x86_64-Setup.exe.sig',
  'Aegis-Android-arm64.apk', 'latest.json', 'SHA256SUMS',
];

export function releaseKind(version) {
  androidVersionCode(version);
  if (/^\d+\.\d+\.\d+$/.test(version)) return 'stable';
  if (/^\d+\.\d+\.\d+-unstable\.[1-9]\d*$/.test(version)) return 'preview';
  throw new Error('New native releases require X.Y.Z or X.Y.Z-unstable.N.');
}

export function checksumManifest(directory) {
  return releaseFiles.filter(name => name !== 'SHA256SUMS').map(name =>
    `${createHash('sha256').update(readFileSync(join(directory, name))).digest('hex')}  ${name}\n`).join('');
}

function ghCommand(args) {
  return spawnSync('gh', args[0] === 'api' ? args : [...args, '--repo', repo], { encoding: 'utf8' });
}

// Versioned assets are immutable, including on retry. Only node-preview/latest.json
// is mutable, and it is advanced after the public version's bytes are verified.
export function publishRelease({ tag, commit, version, directory = 'release-assets', runGh = ghCommand }) {
  const kind = releaseKind(version);
  if (tag !== `node-v${version}`) throw new Error('Release tag/package version mismatch.');
  if (!/^[a-f0-9]{40}$/.test(commit ?? '')) throw new Error('Missing exact release commit.');
  directory = resolve(directory);
  const manifestBytes = readFileSync(join(directory, 'latest.json'));
  const next = JSON.parse(manifestBytes.toString('utf8'));
  const signature = readFileSync(join(directory, 'Aegis-Windows-x86_64-Setup.exe.sig'), 'utf8');
  const expected = metadata(version, signature, next.pub_date);
  if (JSON.stringify(next) !== JSON.stringify(expected)) throw new Error('Manifest must exactly match package, installer signature, versionCode and versioned URLs.');
  // Both binaries must exist and be nonempty before any GitHub mutation.
  for (const name of releaseFiles.slice(0, 3)) {
    if (!readFileSync(join(directory, name)).length) throw new Error('Empty distribution asset: ' + name);
  }
  writeFileSync(join(directory, 'SHA256SUMS'), checksumManifest(directory));

  function gh(args) {
    const result = runGh(args);
    if (result.status !== 0) throw new Error(result.stderr || result.stdout || 'GitHub release command failed.');
    return result.stdout;
  }
  function release(name) {
    const result = runGh(['api', `repos/${repo}/releases/tags/${name}`]);
    let value;
    try { value = JSON.parse(result.stdout); } catch { /* Authentication/network failures must not mean absent. */ }
    if (result.status === 0) {
      if (!value || value.tag_name !== name || !Array.isArray(value.assets)) throw new Error('GitHub returned invalid release metadata.');
      return value;
    }
    if (String(value?.status) === '404' && value?.message === 'Not Found') {
      // The tag endpoint only returns published releases. Authenticated listing
      // includes drafts, so an interrupted draft remains discoverable on retry.
      const matches = listReleases().filter(item => item.tag_name === name);
      if (matches.length > 1) throw new Error('Multiple releases use the requested tag: ' + name);
      if (!matches.length) return null;
      if (!Array.isArray(matches[0].assets)) throw new Error('GitHub returned invalid draft release metadata.');
      return matches[0];
    }
    throw new Error(result.stderr || result.stdout || 'Unable to inspect GitHub release.');
  }
  function listReleases() {
    const pages = JSON.parse(gh(['api', `repos/${repo}/releases?per_page=100`, '--paginate', '--slurp']));
    if (!Array.isArray(pages) || pages.some(page => !Array.isArray(page))) throw new Error('GitHub returned invalid release listing.');
    return pages.flat();
  }
  function rejectNewerDistribution() {
    for (const item of listReleases()) {
      if (item.draft || !item.tag_name?.startsWith('node-v')) continue;
      const previous = item.tag_name.slice(6);
      if (previous === version) continue;
      let code;
      try { code = androidVersionCode(previous); } catch { continue; }
      if (code >= androidVersionCode(version)) throw new Error('A newer or conflicting native distribution is already published: ' + item.tag_name);
    }
  }
  function download(name, patterns, action) {
    const temp = mkdtempSync(join(tmpdir(), 'aegis-release-'));
    try {
      gh(['release', 'download', name, ...patterns.flatMap(pattern => ['--pattern', pattern]), '--dir', temp]);
      return action(temp);
    } finally { rmSync(temp, { recursive: true, force: true }); }
  }
  function verifyAssets(current, requireComplete) {
    const names = current.assets.map(asset => asset.name);
    if (new Set(names).size !== names.length || names.some(name => !releaseFiles.includes(name)))
      throw new Error('Existing versioned release has unexpected or duplicate assets.');
    if (names.length) download(tag, names, temp => {
      for (const name of names) {
        if (!readFileSync(join(temp, name)).equals(readFileSync(join(directory, name))))
          throw new Error('Immutable release asset differs: ' + name);
      }
    });
    const missing = releaseFiles.filter(name => !names.includes(name));
    if (requireComplete && missing.length) throw new Error('Published versioned release is incomplete: ' + missing.join(', '));
    return missing;
  }
  function feedState() {
    const current = release('node-preview');
    if (!current) return { exists: false, equal: false };
    if (current.draft) throw new Error('Compatibility feed must be public.');
    return download('node-preview', ['latest.json'], temp => {
      const previousBytes = readFileSync(join(temp, 'latest.json'));
      const previous = JSON.parse(previousBytes.toString('utf8'));
      const difference = androidVersionCode(version) - androidVersionCode(previous.version);
      if (difference < 0) throw new Error('Compatibility feed would downgrade the current version.');
      if (difference === 0 && !previousBytes.equals(manifestBytes))
        throw new Error('Compatibility feed would replace an existing version with different metadata.');
      return { exists: true, equal: difference === 0 };
    });
  }

  rejectNewerDistribution();
  feedState(); // Fail before creating a release when the current feed is newer or unavailable.
  let current = release(tag);
  if (current && (current.tag_name !== tag || current.prerelease !== (kind === 'preview')))
    throw new Error('Existing release tag/channel differs from the requested distribution.');
  const notes = kind === 'stable'
    ? `Stable native distribution for [Aegis v${version}](https://github.com/${repo}/releases/tag/v${version}). See the official release for the complete changelog, upgrade instructions and known limitations. Android: signed ARM64 APK. Windows: x86_64 NSIS installer with the existing updater signature. SHA256SUMS audits the exact published bytes. Technical evidence: [release completion](https://github.com/${repo}/blob/${tag}/docs/v0.7.0-completion.md).`
    : `Aegis ${version} preview. Android: signed ARM64 APK. Windows: x86_64 NSIS installer and signed updater. See [completion evidence](https://github.com/${repo}/blob/${tag}/docs/v0.7.0-completion.md) for scope, validation and remaining acceptance. SHA256SUMS audits the exact published bytes.`;
  if (!current) {
    gh(['release', 'create', tag, '--verify-tag', '--draft', '--latest=false',
      ...(kind === 'preview' ? ['--prerelease'] : []), '--title', `Aegis Node ${version} — native distribution`, '--notes', notes,
      ...releaseFiles.map(name => join(directory, name))]);
    current = release(tag);
    if (!current) throw new Error('Created release cannot be inspected.');
  } else {
    const missing = verifyAssets(current, !current.draft);
    // Recover a partially uploaded draft without changing any existing asset.
    if (missing.length) gh(['release', 'upload', tag, ...missing.map(name => join(directory, name))]);
    current = release(tag);
  }
  verifyAssets(current, true);
  if (current.draft) gh(['release', 'edit', tag, '--draft=false', `--prerelease=${kind === 'preview'}`, '--latest=false']);
  const published = release(tag);
  if (!published || published.draft || published.prerelease !== (kind === 'preview')) throw new Error('Distribution release is not public in the expected channel.');

  // Recheck after publication: another release may have advanced the feed while
  // assets were uploading. Workflow concurrency also serializes official runs.
  rejectNewerDistribution();
  const feed = feedState();
  if (!feed.exists) {
    gh(['release', 'create', 'node-preview', '--target', commit, '--prerelease', '--latest=false',
      '--title', 'Aegis native update compatibility feed',
      '--notes', 'Compatibility update metadata for existing Android and Windows installations. Installers are immutable assets in the versioned node-v releases.',
      join(directory, 'latest.json')]);
  } else if (!feed.equal) {
    gh(['release', 'upload', 'node-preview', join(directory, 'latest.json'), '--clobber']);
  }
  if (!feedState().equal) throw new Error('Published compatibility feed does not match the verified distribution.');
  return { tag, version, kind, commit, assets: releaseFiles };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const { version } = synchronize();
  const result = publishRelease({ tag: process.env.RELEASE_TAG, commit: process.env.RELEASE_COMMIT, version });
  console.log(`Published and verified ${result.tag}; compatibility feed is current.`);
}
