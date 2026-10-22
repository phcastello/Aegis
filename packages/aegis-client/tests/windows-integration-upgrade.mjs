// Short integration counterpart of the release idle comparison, on disposable Windows runners.
import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { install, installedExecutable, installedSmoke } from './windows-upgrade-idle.mjs';

const [planArgument, outputArgument] = process.argv.slice(2);
assert.ok(planArgument && outputArgument, 'Expected the prepared upgrade plan and evidence directory.');
const plan = JSON.parse(readFileSync(resolve(planArgument), 'utf8'));
assert.equal(plan.kind, 'aegis-windows-upgrade-ci-v1');
const version = JSON.parse(readFileSync(new URL('../../../apps/aegis-node/package.json', import.meta.url), 'utf8')).version;
try {
  install(plan.candidateInstaller, plan.destination);
  assert.equal(installedExecutable(plan.destination), plan.executable, 'Upgrade must preserve its installation path.');
  const after = await installedSmoke(plan.executable, version, plan.profile, plan.sentinel, false);
  mkdirSync(outputArgument, {recursive:true});
  writeFileSync(join(outputArgument, 'windows-upgrade.json'), JSON.stringify({
    kind:'integration-installed-upgrade', before:plan.beforeInstalled, after,
    sourceSha:process.env.GITHUB_SHA, idleMeasurements:false,
    limits:['Disposable runner, offline API responses; physical devices and signed updater acceptance remain separate.'],
  }, null, 2) + '\n');
} finally {
  rmSync(plan.work, {recursive:true,force:true,maxRetries:5,retryDelay:200});
}
