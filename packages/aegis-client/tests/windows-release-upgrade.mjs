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
import { signedUpgradeBaseline } from './windows-upgrade-baseline.mjs';

const repository = fileURLToPath(new URL('../../../', import.meta.url));
const version = JSON.parse(readFileSync(join(repository, 'apps/aegis-node/package.json'), 'utf8')).version;
const baseline = signedUpgradeBaseline(version);
assert.ok(process.argv[2], 'Expected the exact signed NSIS installer path.');
const candidate = resolve(process.argv[2]);
const digest = data => createHash('sha256').update(data).digest('hex');
const work = mkdtempSync(join(tmpdir(), 'aegis-signed-upgrade-'));
const previousUrl = baseline.url;
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
const output = join(repository, 'artifacts/release-identity');
mkdirSync(output, { recursive: true });
await windowsReleaseIdentity(executable, output);
writeFileSync(join(output, 'windows-release-upgrade.json'), JSON.stringify({
  from: before.version, to: after.version,
  previousUrl, previousSha256: previousDigest, publishedInstallerSha256: digest(readFileSync(candidate)),
  sameInstallationPath: true, actualApplicationProfilePreserved: true,
  before, after,
  limits: 'Actual signed NSIS installation and WebView storage check on an isolated Windows runner. Apps are offline and unpaired. Native paired identity/OS vault is tested separately. This does not claim owner-machine or signed-updater UI acceptance.'
}, null, 2) + '\n');
console.log(`Signed NSIS ${baseline.version} -> ${version} upgrade, embedded runtime version and application storage PASS.`);
