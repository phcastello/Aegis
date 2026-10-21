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
import { windowsReleaseIdentity } from './windows-release-identity.mjs';

const repository = fileURLToPath(new URL('../../../', import.meta.url));
const version = JSON.parse(readFileSync(join(repository, 'apps/aegis-node/package.json'), 'utf8')).version;
const baselines = {
  '0.7.0': { version: '0.7.0-unstable.19', sha256: '3e62a0359a4865e3bad7b03df8745a33641f5eb6b50919f50e4a55e1503e748b' },
  '0.7.1': { version: '0.7.0', sha256: '34504a0b8e197f24977e7f7e886196ad187a01f0acb9b0e81c23407176faeb5f' }
};
const baseline = baselines[version];
assert.ok(baseline, 'An explicit immutable signed-upgrade baseline is required for this release.');
assert.ok(process.argv[2], 'Expected the exact signed NSIS installer path.');
const candidate = resolve(process.argv[2]);
const digest = data => createHash('sha256').update(data).digest('hex');
const work = mkdtempSync(join(tmpdir(), 'aegis-signed-upgrade-'));
const previousUrl = `https://github.com/phcastello/Aegis/releases/download/node-v${baseline.version}/Aegis-Windows-x86_64-Setup.exe`;
const previousDigest = baseline.sha256;
const response = await fetch(previousUrl, { signal: AbortSignal.timeout(60000) });
assert.equal(response.status, 200);
const bytes = Buffer.from(await response.arrayBuffer());
assert.equal(digest(bytes), previousDigest, 'The baseline must match the immutable published installer.');
const previous = join(work, 'previous.exe');
writeFileSync(previous, bytes);
const destination = join(work, 'installed');
assert.ok(process.env.LOCALAPPDATA);
const profile = join(process.env.LOCALAPPDATA, 'com.aegis.node');
const sentinel = randomUUID();
install(previous, destination);
const executable = installedExecutable(destination);
const before = await installedSmoke(executable, baseline.version, profile, sentinel, true);
install(candidate, destination);
assert.equal(installedExecutable(destination), executable, 'Installation path must remain the same.');
const after = await installedSmoke(executable, version, profile, sentinel, false);
const output = join(repository, version === '0.7.0' ? 'artifacts/v0.7.0-completion' : 'artifacts/release-identity');
mkdirSync(output, { recursive: true });
if (version === '0.7.1') await windowsReleaseIdentity(executable, output);
writeFileSync(join(output, 'windows-release-upgrade.json'), JSON.stringify({
  from: before.version, to: after.version,
  previousUrl, previousSha256: previousDigest, publishedInstallerSha256: digest(readFileSync(candidate)),
  sameInstallationPath: true, actualApplicationProfilePreserved: true,
  before, after,
  limits: 'Actual signed NSIS installation and WebView storage check on an isolated Windows runner. Apps are offline and unpaired. Native paired identity/OS vault is tested separately. This does not claim owner-machine or signed-updater UI acceptance.'
}, null, 2) + '\n');
console.log(`Signed NSIS ${baseline.version} -> ${version} upgrade, embedded runtime version and application storage PASS.`);
