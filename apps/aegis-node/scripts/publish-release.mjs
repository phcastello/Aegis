import { spawnSync } from 'node:child_process';
import { readFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { androidVersionCode } from './version.mjs';

const repo = 'phcastello/Aegis';
const tag = process.env.RELEASE_TAG;
const next = JSON.parse(readFileSync('release-assets/latest.json', 'utf8'));
if (tag !== `node-v${next.version}`) throw new Error('Release tag/version mismatch.');
function gh(args, allowFailure = false) {
  const result = spawnSync('gh', [...args, '--repo', repo], { encoding: 'utf8' });
  if (result.status !== 0 && !allowFailure) throw new Error(result.stderr || 'GitHub release command failed.');
  return result;
}
// Published version releases are immutable; reruns must not silently replace installers.
if (gh(['release', 'view', tag], true).status === 0) throw new Error('Release already exists: ' + tag);
const feedExists = gh(['release', 'view', 'node-preview'], true).status === 0;
if (feedExists) {
  const temp = mkdtempSync(join(tmpdir(), 'aegis-feed-'));
  try {
    gh(['release', 'download', 'node-preview', '--pattern', 'latest.json', '--dir', temp]);
    const previous = JSON.parse(readFileSync(join(temp, 'latest.json'), 'utf8'));
    if (androidVersionCode(next.version) <= androidVersionCode(previous.version))
      throw new Error('Preview feed would downgrade or replace the current version.');
  } finally { rmSync(temp, { recursive: true, force: true }); }
}
gh(['release', 'create', tag, '--verify-tag', '--prerelease', '--title', `Aegis ${next.version}`,
  '--notes', 'Preview v0.7.0. Windows: Setup.exe. Android: signed ARM64 APK. Android updates open the APK in your browser for system installation.',
  'release-assets/Aegis-Windows-x86_64-Setup.exe', 'release-assets/Aegis-Windows-x86_64-Setup.exe.sig',
  'release-assets/Aegis-Android-arm64.apk', 'release-assets/latest.json']);
if (!feedExists) {
  gh(['release', 'create', 'node-preview', '--target', process.env.RELEASE_COMMIT, '--prerelease',
    '--title', 'Aegis native preview update feed', '--notes', 'Update metadata only. Download installers from the versioned node-v releases.',
    'release-assets/latest.json']);
} else {
  gh(['release', 'upload', 'node-preview', 'release-assets/latest.json', '--clobber']);
}
