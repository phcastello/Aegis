import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { affectedSubsystems, changedPaths, selectJobs, validateIntegrationContext, validateResults, validationStage } from './select-node-ci.mjs';

const head = 'a'.repeat(40), merge = 'b'.repeat(40), base = 'c'.repeat(40), ancestor = 'd'.repeat(40);
const kotlin = 'apps/aegis-node/src-tauri/gen/android/app/src/main/java/com/aegis/node/NodePlugin.kt';
const rust = 'apps/aegis-node/src-tauri/src/node_capabilities.rs';
const ui = 'packages/aegis-client/src/views/ChatView.vue';
const pr = (draft = false, action = 'synchronize') => ({ event: 'pull_request', payload: { action, pull_request: { draft, head: { sha: head }, base: { sha: base } } }, sha: merge, checkoutSha: merge });
const release = inputs => ({ event: 'workflow_dispatch', inputs: { stage: 'release', ...inputs } });
const assertNoBuilds = selected => {
  for (const flag of ['backend', 'ui', 'windows', 'android', 'framework', 'idle', 'identity', 'native', 'full']) assert.equal(selected[flag], false, flag);
  assert.deepEqual(selected.matrix, []);
};

test('draft PR with native changes retains affected scope but runs no builds or prolonged tests', () => {
  const selected = selectJobs([kotlin, rust], pr(true, 'opened'));
  assert.equal(selected.stage, 'development');
  assert.equal(selected.affected.android, true);
  assert.equal(selected.affected.windows, true);
  assertNoBuilds(selected);
  assert.throws(() => validateIntegrationContext(selected, pr(true)), /not integration|unavailable/);
});

test('additional draft commit cannot request a heavy matrix through inputs or changed files', () => {
  assertNoBuilds(selectJobs([kotlin, '.github/workflows/aegis-node-ci.yml'], { ...pr(true), inputs: { full_matrix: true, stage: 'release' }, unknown: true }));
  for (const branch of ['fix/v0.7.2-ux-proactivity', 'feat/example', 'experiment/example']) {
    const selected = selectJobs([kotlin], { event: 'push', payload: { ref: `refs/heads/${branch}` }, unknown: true });
    assert.equal(selected.stage, 'development');
    assertNoBuilds(selected);
  }
});

test('ready-for-review, ready synchronize, reopened and edited PRs request integration', () => {
  for (const action of ['ready_for_review', 'synchronize', 'reopened', 'edited', 'opened']) {
    const options = pr(false, action), selected = selectJobs([kotlin], options);
    assert.equal(selected.stage, 'integration', action);
    assert.equal(selected.android, true, action);
    assert.equal(selected.framework, true, action);
    assert.equal(selected.idle, false, action);
    validateIntegrationContext(selected, options);
  }
});

test('a new ready-PR head invalidates previous acceptance until current evidence passes', () => {
  const options = pr(), selected = selectJobs([rust], options);
  validateIntegrationContext(selected, options);
  assert.throws(() => validateIntegrationContext(selected, { ...options, payload: { pull_request: { draft: false, head: { sha: base } } } }), /current candidate/);
  assert.throws(() => validateIntegrationContext(selected, { ...options, sha: base, checkoutSha: base }), /current candidate/);
  assert.throws(() => validateIntegrationContext({ ...selected, stage: 'preview' }, options), /unavailable/);
  assert.throws(() => validateIntegrationContext({ ...selected, stage: 'release' }, options), /requires integration/);
  assert.throws(() => validateIntegrationContext({ ...selected, evidence_sha: '' }, options), /invalid/);
});

test('docs-only integration runs the selector/documentation checks without application builds', () => {
  const selected = selectJobs(['AGENTS.md', 'README.md', 'docs/operations.md', 'apps/aegis-node/README.md', 'scripts/check-docs.mjs']);
  assert.equal(selected.stage, 'integration');
  assertNoBuilds(selected);
});

test('UI-only integration keeps interface builds and contracts without native/energy jobs', () => {
  for (const path of [ui, 'apps/aegis-node/src/components/PlacesPanel.vue', 'frontend/aegis-pwa/src/main.ts']) {
    const selected = selectJobs([path]);
    assert.equal(selected.ui, true); assert.equal(selected.native, false); assert.equal(selected.backend, false); assert.equal(selected.framework, false); assert.equal(selected.idle, false);
  }
});

test('backend, embedded prompts, Compose and SQL select affected backend checks', () => {
  for (const path of ['backend/src/Aegis.Api/Program.cs', 'backend/src/Aegis.Api/Prompts/aegis_identity.md', 'scripts/check-compose.mjs', 'deploy/sql/v070-retire-verified-probes.sql']) {
    const selected = selectJobs([path]);
    assert.equal(selected.backend, true, path); assert.equal(selected.native, false, path); assert.equal(selected.idle, false, path);
  }
  const selected = selectJobs(['backend/src/Aegis.Api/Controllers/NodesController.cs']);
  assert.equal(selected.native, true); assert.equal(selected.framework, true); assert.equal(selected.idle, false);
});

test('Android Kotlin, manifest and permission integration retain framework and release-distribution checks without long windows', () => {
  for (const path of [kotlin, 'apps/aegis-node/src-tauri/gen/android/app/src/main/AndroidManifest.xml']) {
    const selected = selectJobs([path]);
    assert.equal(selected.android, true); assert.equal(selected.framework, true); assert.equal(selected.windows, false); assert.equal(selected.idle, false);
    assert.deepEqual(selected.matrix, [{ os: 'ubuntu-24.04', android: 'true', platform: 'Android-arm64' }]);
  }
});

test('shared Rust selects Windows and Android integration, including capability identity contracts', () => {
  const selected = selectJobs([rust]);
  for (const flag of ['windows', 'android', 'framework', 'identity', 'ui']) assert.equal(selected[flag], true, flag);
  assert.equal(selected.idle, false);
  assert.equal(selected.matrix.length, 2);
});

test('dependencies, lockfiles, build config and workflows do not promote integration to prolonged acceptance', () => {
  for (const path of ['apps/aegis-node/package-lock.json', 'frontend/aegis-pwa/package.json', 'packages/aegis-client/tsconfig.json', 'apps/aegis-node/src-tauri/Cargo.lock', 'apps/aegis-node/src-tauri/build.rs', '.github/scripts/select-node-ci.mjs', '.github/workflows/aegis-node-ci.yml']) {
    const selected = selectJobs([path]);
    for (const flag of ['ui', 'windows', 'android', 'framework', 'identity']) assert.equal(selected[flag], true, `${path}: ${flag}`);
    assert.equal(selected.stage, 'integration'); assert.equal(selected.idle, false, path);
  }
});

test('geofencing, background and consumption receive long windows only in release acceptance', () => {
  for (const path of ['apps/aegis-node/src-tauri/gen/android/app/src/main/java/com/aegis/node/GeofencingService.kt', 'apps/aegis-node/src/composables/useLocationPermissionRefresh.ts', 'backend/src/Aegis.Infrastructure/Services/PlaceService.cs', 'scripts/node-transport-probe/android-hybrid-efficiency.mjs']) {
    const integration = selectJobs([path]), candidate = selectJobs([path], release());
    assert.equal(integration.idle, false, path);
    assert.equal(candidate.native, true, path); assert.equal(candidate.idle, true, path);
  }
  for (const path of [ui, 'packages/aegis-client/src/components/AboutPanel.vue', 'README.md']) assert.equal(selectJobs([path], release()).idle, false, path);
});

test('manual preview limits scope to requested builds and never requests framework/baseline/idle acceptance', () => {
  for (const platform of ['android', 'windows', 'both']) {
    const selected = selectJobs([kotlin], { event: 'workflow_dispatch', inputs: { stage: 'preview', platform, full_matrix: true }, unknown: true });
    assert.equal(selected.stage, 'preview'); assert.equal(selected.full, false); assert.equal(selected.framework, false); assert.equal(selected.idle, false);
    assert.equal(selected.android, platform !== 'windows'); assert.equal(selected.windows, platform !== 'android');
    assert.throws(() => validateIntegrationContext(selected, { sha: head, checkoutSha: head }), /unavailable/);
  }
  assert.throws(() => selectJobs([], { event: 'workflow_dispatch', inputs: { stage: 'preview', platform: 'external' } }), /Invalid preview/);
});

test('stable-release reuse and explicit full acceptance preserve every release gate', () => {
  for (const options of [release({ full_matrix: true }), { event: 'push', inputs: { ref: 'node-v0.7.2', full_matrix: true }, payload: { ref: 'refs/tags/node-v0.7.2' } }, { event: 'workflow_call', inputs: { ref: head, full_matrix: true } }]) {
    const selected = selectJobs([], options);
    assert.equal(selected.stage, 'release');
    for (const flag of ['backend', 'ui', 'windows', 'android', 'framework', 'idle', 'identity', 'full']) assert.equal(selected[flag], true, flag);
  }
  const options = { event: 'push', inputs: { ref: head, full_matrix: true }, sha: base, checkoutSha: head };
  validateIntegrationContext(selectJobs([], options), options);
});

test('unknown files and failed diff remain conservative for integration and release, never for draft depth', () => {
  for (const options of [{ unknown: true }, {}, release()]) {
    const selected = selectJobs(['new-build-system.config.mjs'], options);
    for (const flag of ['backend', 'ui', 'windows', 'android', 'framework', 'identity']) assert.equal(selected[flag], true, flag);
    assert.equal(selected.idle, options.inputs?.stage === 'release');
  }
  assert.equal(selectJobs([], { ...release(), unknown: true }).idle, true);
  assertNoBuilds(selectJobs([], { ...pr(true), unknown: true }));
});

test('workflow concurrency cancels superseded branch/PR runs without using a per-commit group', () => {
  const workflow = readFileSync(new URL('../workflows/aegis-node-ci.yml', import.meta.url), 'utf8');
  const concurrency = workflow.match(/^concurrency:\n([\s\S]*?)(?=^[^\s])/m)?.[1];
  assert.ok(concurrency, 'workflow must define concurrency');
  assert.match(concurrency, /cancel-in-progress:\s*true/);
  assert.match(concurrency, /github\.(?:head_ref|event\.pull_request\.number)/);
  assert.doesNotMatch(concurrency, /github\.sha|head\.sha|run_id|run_number/);
});

test('focused manual filters are retained; default manual is integration and only full requests release', () => {
  const selected = selectJobs([], { event: 'workflow_dispatch', inputs: { ui_only: true } });
  assert.equal(selected.stage, 'integration'); assert.equal(selected.ui, true); assert.equal(selected.native, false); assert.equal(selected.idle, false);
  assert.equal(selected.integration, false); assert.equal(selected.partial, true);
  assert.throws(() => validateIntegrationContext(selected, { sha: head, checkoutSha: head }), /Focused validation/);
  const partialRelease = selectJobs([], release({ ui_only: true }));
  assert.throws(() => validateIntegrationContext(partialRelease, { sha: head, checkoutSha: head }), /Focused validation/);
  assert.equal(selectJobs([], { event: 'workflow_dispatch', inputs: { ui_only: true, full_matrix: true } }).full, true);
  assert.equal(validationStage({ event: 'workflow_dispatch' }), 'integration');
  assert.throws(() => validationStage({ event: 'workflow_dispatch', inputs: { stage: 'surprise' } }), /Invalid/);
});

test('reusable identity keeps caller event semantics and the same PR merge evidence', () => {
  const options = { ...pr(), inputs: { ref: merge, stage: 'integration', full_matrix: false } };
  const selected = selectJobs([rust], options);
  assert.equal(selected.stage, 'integration'); assert.equal(selected.source_sha, head); assert.equal(selected.evidence_sha, merge);
  validateIntegrationContext(selected, options);
  const draftOptions = { ...pr(true), inputs: options.inputs };
  assertNoBuilds(selectJobs([rust], draftOptions));
  assert.throws(() => validateIntegrationContext(selected, { ...options, payload: pr(true).payload }), /draft/);
});

test('merge queue and main pushes validate current integration evidence', () => {
  const options = { event: 'merge_group', payload: { merge_group: { head_sha: merge, base_sha: base } }, sha: merge, checkoutSha: merge };
  const selected = selectJobs([rust], options);
  assert.equal(selected.stage, 'integration'); assert.equal(selected.idle, false); validateIntegrationContext(selected, options);
  assert.equal(selectJobs([rust], { event: 'push', payload: { ref: 'refs/heads/main' } }).stage, 'integration');
});

test('PR changes use merge-base and the complete current branch diff, preserving source SHA', () => {
  const calls = [];
  const changed = changedPaths(pr().payload, 'pull_request', { runGit: args => { calls.push(args); return args[0] === 'merge-base' ? `${ancestor}\n` : `${kotlin}\0${ui}\0`; } });
  assert.deepEqual(calls, [['merge-base', base, head], ['diff', '--name-only', '-z', ancestor, head]]);
  assert.deepEqual(changed.paths, [kotlin, ui]);
  const selected = selectJobs(changed.paths, pr());
  assert.equal(selected.source_sha, head); assert.equal(selected.evidence_sha, merge);
  assert.equal(changedPaths(pr().payload, 'pull_request', { runGit: () => { throw Error('missing history'); } }).unknown, true);
  assert.equal(changedPaths({ before: '0'.repeat(40), after: head }, 'push').unknown, true);
  assert.equal(changedPaths({}, 'workflow_dispatch').unknown, true);
  assert.deepEqual(changedPaths({}, 'workflow_dispatch', { inputs: { full_matrix: true } }), { paths: [] });
  assert.equal(changedPaths({}, 'workflow_dispatch', { inputs: { stage: 'integration', base_ref: head, ref: head } }).unknown, true, 'manual baseline cannot suppress integration checks');
  assert.equal(changedPaths(pr().payload, 'pull_request', { inputs: { full_matrix: true }, runGit: () => { throw Error('diff required'); } }).unknown, true, 'PR cannot bypass changed files with release inputs');
  assert.deepEqual(changedPaths({}, 'workflow_dispatch', { inputs: { stage: 'release', base_ref: base, ref: head }, runGit: () => `${ui}\0` }), { paths: [ui] });
});

test('aggregate fails closed on selected failure/cancelled/missing/skipped and accepts intentional omissions', () => {
  const selection = { backend: true, ui: false }, jobs = { regression: 'backend', ui: 'ui' };
  validateResults(selection, { regression: { result: 'success' }, ui: { result: 'skipped' } }, jobs);
  for (const result of ['failure', 'cancelled', 'skipped', undefined]) assert.throws(() => validateResults(selection, { regression: { result }, ui: { result: 'skipped' } }, jobs));
  for (const result of ['failure', 'cancelled', undefined]) assert.throws(() => validateResults(selection, { regression: { result: 'success' }, ui: { result } }, jobs));
  assert.throws(() => validateResults({}, { regression: { result: 'success' } }, { regression: 'backend' }), /Missing selection/);
});

test('subsystem scope is independent from depth for native audio and distribution changes', () => {
  for (const path of ['packages/aegis-client/src/composables/useAegisTranscription.ts', 'packages/aegis-client/src/services/voiceInput.ts', 'apps/aegis-node/src/services/runtime.ts', 'apps/aegis-node/src/services/updateMetadata.ts']) {
    const integration = selectJobs([path]);
    assert.equal(integration.ui, true); assert.equal(integration.windows, true); assert.equal(integration.android, true); assert.equal(integration.framework, true); assert.equal(integration.idle, false);
  }
  assert.equal(affectedSubsystems(['packages/aegis-client/src/services/voiceInput.ts']).audio, true);
  for (const path of ['Dockerfile', 'Dockerfile.release', '.dockerignore', 'Makefile', 'CMakeLists.txt', 'deploy/signing/config.json']) {
    assert.equal(selectJobs([path]).backend, true, path);
    assert.equal(selectJobs([path]).idle, false, path);
    assert.equal(selectJobs([path], release()).idle, true, path);
  }
});
