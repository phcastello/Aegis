import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8').replace(/\r\n/g, '\n');
const setup = read('../actions/setup-aegis-node/action.yml');
const ci = read('../workflows/aegis-node-ci.yml');
const identity = read('../workflows/aegis-release-identity-ci.yml');
const preview = read('../workflows/aegis-native-preview.yml');
const step = name => {
  const marker = `name: ${name}\n`;
  const start = setup.indexOf(marker);
  assert.notEqual(start, -1);
  const end = setup.indexOf('\n    - ', start);
  return setup.slice(start, end < 0 ? undefined : end);
};

test('Gradle cache uses deterministic workload, wrapper and complete dependency fingerprint', () => {
  assert.doesNotMatch(setup, /^\s+cache: gradle$/m, 'Do not restore the former immutable shared cache via setup-java');
  const keys = step('Identify Gradle dependency and legacy seed caches');
  assert.match(keys, /aegis-gradle-modules-v1-\$\{RUNNER_OS\}-\$\{arch\}-java17-\$\{VARIANT\}-\$\{WRAPPER_HASH\}-/);
  assert.match(keys, /key=\$\{prefix\}\$\{DEPENDENCIES_HASH\}/);
  for (const path of ['gradle.properties', 'gradle-wrapper.properties', 'gradle-wrapper.jar', 'buildSrc/src', 'versions.toml', 'gradle.lockfile', 'verification-metadata.xml', 'Cargo.lock', 'package-lock.json', 'aegis-node-ci.yml', 'aegis-native-preview.yml']) {
    assert.ok(keys.match(/DEPENDENCIES_HASH: (.*)/)?.[1].includes(path), `Fingerprint must include ${path}`);
  }
  const deps = step('Cache resolved Gradle dependencies for this workload');
  assert.match(deps, /actions\/cache@caa296126883cff596d87d8935842f9db880ef25/);
  for (const dir of ['files-*', 'metadata-*', 'resources-*']) assert.ok(deps.includes(`/caches/modules-2/${dir}`));
  assert.doesNotMatch(deps, /save-always|cache-on-failure|run_id|run_attempt/);
  assert.match(deps, /restore-keys: \|\n +\$\{\{ steps.gradle-cache.outputs.prefix \}\}\n +\$\{\{ steps.gradle-cache.outputs.seed-prefix \}\}/);
});

test('Legacy Gradle common cache is an optional, read-only seed, with a separate wrapper', () => {
  assert.match(setup, /if: inputs.android == 'true' && steps.gradle-dependencies.outputs.cache-hit == ''\n +name: Seed empty Gradle dependency cache/);
  const seed = step('Seed empty Gradle dependency cache from previous setup-java cache');
  assert.match(seed, /actions\/cache\/restore@caa296126883cff596d87d8935842f9db880ef25/);
  assert.match(seed, /path: \$\{\{ steps.gradle-cache.outputs.home \}\}\/caches/);
  assert.match(seed, /key: \$\{\{ steps.gradle-cache.outputs.legacy \}\}/);
  assert.match(setup, /legacy=setup-java-\$\{RUNNER_OS\}-\$\{arch\}-gradle-\$\{LEGACY_HASH\}/);
  assert.match(setup, /wrapper=setup-java-\$\{RUNNER_OS\}-\$\{arch\}-gradle-wrapper-\$\{WRAPPER_HASH\}/);
  assert.match(step('Cache Gradle wrapper distribution'), /path: \$\{\{ steps.gradle-cache.outputs.home \}\}\/wrapper/);
});

test('Full ARM64, focused distribution, identity, framework APIs, preview and stable build cannot overwrite each other', () => {
  assert.match(ci, /gradle-cache-variant:.*full != 'true'.*android_distribution_only.*'arm64-distribution-only'.*'arm64-full'/);
  assert.match(ci, /gradle-cache-seed-variant:.*full != 'true'.*android_distribution_only.*'arm64-full'.*''/);
  assert.match(ci, /gradle-cache-variant:.*framework-x86_64-api\{0\}-\{1\}.*matrix.api.*idle.*'idle-acceptance'.*'contracts'/);
  assert.match(ci, /gradle-cache-seed-variant:.*matrix.api == 35.*'framework-x86_64-api35-contracts'.*'framework-x86_64-api26-contracts'.*'framework-x86_64-api35-contracts'/);
  assert.match(identity, /gradle-cache-variant: release-identity-x86_64/);
  assert.match(preview, /gradle-cache-variant:.*needs.plan.outputs.stable == 'true'.*'stable-build-arm64'.*'preview-arm64'/);
  assert.match(preview, /gradle-cache-seed-variant:.*needs.plan.outputs.stable == 'true'.*'arm64-full'.*''/);
  assert.doesNotMatch(preview.split('\n  sign:')[1], /gradle-cache-variant|setup-aegis-node/);
});
