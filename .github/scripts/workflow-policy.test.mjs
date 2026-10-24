import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const workflow = name => readFileSync(new URL(`../workflows/${name}.yml`, import.meta.url), 'utf8');
const job = (source, name) => {
  const match = source.match(new RegExp(`^  ${name}:\\n([\\s\\S]*?)(?=^  [\\w-]+:\\n|(?![\\s\\S]))`, 'm'));
  assert.ok(match, `Missing job ${name}`); return match[1];
};

test('development pushes do not trigger native workflows; PR lifecycle and merge queue are explicit', () => {
  const source = workflow('aegis-node-ci');
  assert.match(source, /push:\n    branches: \[main\]/);
  for (const event of ['synchronize','ready_for_review','reopened','converted_to_draft','edited']) assert.ok(source.includes(event));
  assert.match(source, /merge_group:\n    types: \[checks_requested\]/);
  assert.doesNotMatch(source, /^\s+paths(?:-ignore)?:/m);
  const identity = workflow('aegis-release-identity-ci').split('permissions:')[0];
  assert.doesNotMatch(identity, /^  (push|pull_request):/m, 'Identity is called by integration, avoiding duplicated builds.');
});

test('new commits cancel previous integration runs using the same PR concurrency key', () => {
  const source = workflow('aegis-node-ci');
  const concurrency = source.match(/^concurrency:\n([\s\S]*?)^jobs:/m)[1];
  assert.match(concurrency, /github.head_ref/);
  assert.doesNotMatch(concurrency, /github.sha|github.run_id/);
  assert.match(concurrency, /cancel-in-progress: true/);
});

test('long Windows baselines and comparisons require the release idle selection', () => {
  const native = job(workflow('aegis-node-ci'), 'native');
  for (const step of native.split(/\n      - /)) {
    if (/name: (?:Preserve the candidate executable|Build the immutable accepted baseline|Real NSIS upgrade, same native identity)/.test(step)) assert.match(step, /needs.selection.outputs.idle == 'true'/);
  }
  assert.match(native, /windows-integration-upgrade\.mjs/);
  const android = job(workflow('aegis-node-ci'), 'android-notification-framework');
  for (const step of android.split(/\n      - /)) if (/android-hybrid-efficiency\.mjs|android-idle\.mjs|aegis-accepted-baseline/.test(step)) assert.match(step, /needs.selection.outputs.idle == 'true'/);
});

test('stable integration gate includes identity, verifies current candidate, and never tolerates failures', () => {
  const source = workflow('aegis-node-ci');
  const gate = job(source, 'required');
  assert.match(gate, /name:.*outputs.stage == 'release'.*'Aegis Release Acceptance result'.*'Aegis Node CI result'/);
  assert.match(gate, /needs: \[selection, regression, ui, native, android-notification-framework, identity\]/);
  assert.match(gate, /if: \$\{\{ always\(\) \}\}/);
  assert.match(gate, /validateIntegrationContext\(selected/);
  assert.match(gate, /validateResults\(selected/);
  assert.match(gate, /current.head.sha !== selected.source_sha/);
  assert.match(gate, /current.base.sha !== payload.pull_request.base.sha/);
  assert.doesNotMatch(source, /continue-on-error/);
  const checkouts = source.matchAll(/ref: (.+)/g);
  for (const [, value] of checkouts) assert.match(value, /inputs.ref \|\| github.sha|needs.selection.outputs.evidence_sha/, 'Builds cannot silently follow mutable refs.');
});

test('stable release still requests full acceptance and uses one resolved SHA for build and publication', () => {
  const source = workflow('aegis-node-release');
  const acceptance = job(source, 'acceptance');
  assert.match(acceptance, /stage: release/);
  assert.match(acceptance, /full_matrix: true/);
  assert.match(acceptance, /ref: \$\{\{ needs.resolve.outputs.sha \}\}/);
  assert.match(job(source, 'resolve'), /assertEnvironment\(environment/);
  assert.match(job(source, 'resolve'), /test \"\$GITHUB_REF\" = refs\/heads\/main/);
  assert.match(job(source, 'resolve'), /merge-base', '--is-ancestor', sha, 'refs\/remotes\/origin\/main'/);
  assert.doesNotMatch(acceptance, /secrets: inherit/);
  assert.match(job(source, 'build'), /environment: aegis-native-signing/);
  assert.match(job(source, 'build'), /verify-android-distribution\.py/);
  assert.match(job(source, 'publish'), /minisign-verify/);
});
