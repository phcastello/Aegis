// Verify the exact signed installer that will be published, on an isolated Windows runner.
// Production-configured apps remain offline and unpaired; this checks installation and
// the real application WebView profile. OS vault/NodeId continuity has a separate CI test.
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { install, installedExecutable, installedSmoke } from './windows-upgrade-idle.mjs';

const repository = fileURLToPath(new URL('../../../', import.meta.url));
const version = JSON.parse(readFileSync(join(repository, 'apps/aegis-node/package.json'), 'utf8')).version;
assert.equal(version, '0.7.0', 'This upgrade gate covers the first stable release.');
assert.ok(process.argv[2], 'Expected the exact signed NSIS installer path.');
const candidate = resolve(process.argv[2]);
const digest = data => createHash('sha256').update(data).digest('hex');
const work = mkdtempSync(join(tmpdir(), 'aegis-signed-upgrade-'));
const previousUrl = 'https://github.com/phcastello/Aegis/releases/download/node-v0.7.0-unstable.19/Aegis-Windows-x86_64-Setup.exe';
const previousDigest = '3e62a0359a4865e3bad7b03df8745a33641f5eb6b50919f50e4a55e1503e748b';
const response = await fetch(previousUrl, { signal: AbortSignal.timeout(60000) });
assert.equal(response.status, 200);
const bytes = Buffer.from(await response.arrayBuffer());
assert.equal(digest(bytes), previousDigest, 'Preview19 must match the immutable published installer.');
const previous = join(work, 'preview19.exe');
writeFileSync(previous, bytes);
const destination = join(work, 'installed');
assert.ok(process.env.LOCALAPPDATA);
const profile = join(process.env.LOCALAPPDATA, 'com.aegis.node');
const sentinel = randomUUID();
install(previous, destination);
const executable = installedExecutable(destination);
const before = await installedSmoke(executable, '0.7.0-unstable.19', profile, sentinel, true);
install(candidate, destination);
assert.equal(installedExecutable(destination), executable, 'Installation path must remain the same.');
const after = await installedSmoke(executable, version, profile, sentinel, false);
const output = join(repository, 'artifacts/v0.7.0-completion');
mkdirSync(output, { recursive: true });
writeFileSync(join(output, 'windows-release-upgrade.json'), JSON.stringify({
  from: before.version, to: after.version,
  previousUrl, previousSha256: previousDigest, publishedInstallerSha256: digest(readFileSync(candidate)),
  sameInstallationPath: true, actualApplicationProfilePreserved: true,
  before, after,
  limits: 'Actual signed NSIS installation and WebView storage check on an isolated Windows runner. Apps are offline and unpaired. Native paired identity/OS vault is tested separately. This does not claim owner-machine or signed-updater UI acceptance.'
}, null, 2) + '\n');
console.log('Signed NSIS preview19 -> stable upgrade, embedded runtime version and application storage PASS.');
