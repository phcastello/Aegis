import assert from 'node:assert/strict';
import test from 'node:test';
import { selectJobs, validateResults } from './select-node-ci.mjs';

test('docs-only finishes without application builds', () => {
  const selected = selectJobs(['README.md', 'docs/operations.md', 'apps/aegis-node/README.md', 'scripts/check-docs.mjs']);
  for (const [key, value] of Object.entries(selected)) if (key !== 'matrix') assert.equal(value, false, key);
});
test('shared/UI changes run typecheck, regression and browser parity without native builds', () => {
  for (const path of ['packages/aegis-client/src/views/ChatView.vue', 'apps/aegis-node/src/components/PlacesPanel.vue', 'frontend/aegis-pwa/src/main.ts']) {
    const selected = selectJobs([path]);
    assert.equal(selected.ui, true); assert.equal(selected.native, false); assert.equal(selected.backend, false); assert.equal(selected.framework, false);
  }
});
test('embedded model prompts are backend runtime despite their Markdown extension', () => {
  const selected = selectJobs(['backend/src/Aegis.Api/Prompts/aegis_identity.md']);
  assert.equal(selected.backend, true); assert.equal(selected.ui, false); assert.equal(selected.native, false);
});
test('backend-only changes select backend tests; native contracts keep native integration', () => {
  assert.equal(selectJobs(['backend/src/Aegis.Api/Program.cs']).backend, true);
  assert.equal(selectJobs(['backend/src/Aegis.Api/Program.cs']).native, false);
  const selected = selectJobs(['backend/src/Aegis.Api/Controllers/NodesController.cs']);
  assert.equal(selected.native, true); assert.equal(selected.framework, true);
});
test('Compose validation script runs backend operation and configuration checks', () => {
  const selected = selectJobs(['scripts/check-compose.mjs']);
  assert.equal(selected.backend, true); assert.equal(selected.ui, false); assert.equal(selected.native, false);
});
test('root build files and Docker contexts conservatively retain the complete matrix', () => {
  for (const path of ['Dockerfile', 'Dockerfile.release', '.dockerignore', 'Makefile', 'CMakeLists.txt']) {
    const selected = selectJobs([path]);
    for (const flag of ['backend', 'ui', 'windows', 'android', 'framework', 'idle', 'identity']) assert.equal(selected[flag], true, `${path}: ${flag}`);
  }
});
test('deployment proxy, runtime and signing settings preserve native gates; SQL stays backend', () => {
  for (const path of ['deploy/nginx/node-transport-proxy.inc.conf', 'deploy/signing/config.json', 'deploy/runtime/settings.json']) {
    const selected = selectJobs([path]);
    for (const flag of ['backend', 'ui', 'windows', 'android', 'framework', 'idle', 'identity']) assert.equal(selected[flag], true, `${path}: ${flag}`);
  }
  const sql = selectJobs(['deploy/sql/v070-retire-verified-probes.sql']);
  assert.equal(sql.backend, true); assert.equal(sql.native, false);
});
test('Kotlin and Rust preserve platform, release fixture/JNI/R8 and lifecycle gates', () => {
  const kotlin = selectJobs(['apps/aegis-node/src-tauri/gen/android/app/src/main/AndroidManifest.xml']);
  assert.equal(kotlin.android, true); assert.equal(kotlin.framework, true); assert.equal(kotlin.idle, true); assert.equal(kotlin.windows, false);
  const rust = selectJobs(['apps/aegis-node/src-tauri/src/updates.rs']);
  assert.equal(rust.windows, true); assert.equal(rust.android, true); assert.equal(rust.framework, true); assert.equal(rust.idle, true);
});
test('dependencies, lockfiles, build configuration and workflows conservatively select full coverage', () => {
  for (const path of ['apps/aegis-node/package-lock.json', 'frontend/aegis-pwa/package.json', 'packages/aegis-client/tsconfig.json', 'apps/aegis-node/src-tauri/Cargo.lock', 'apps/aegis-node/src-tauri/build.rs', '.github/scripts/select-node-ci.mjs', '.github/workflows/aegis-node-ci.yml']) {
    const selected = selectJobs([path]);
    for (const flag of ['backend', 'ui', 'windows', 'android', 'framework', 'idle', 'identity']) assert.equal(selected[flag], true, `${path}: ${flag}`);
  }
});
test('capture lifecycle changes preserve Windows and Android runtime smokes and idle gates', () => {
  for (const path of ['packages/aegis-client/src/composables/useAegisTranscription.ts', 'packages/aegis-client/src/services/voiceInput.ts', 'apps/aegis-node/src/services/runtime.ts', 'apps/aegis-node/src/composables/useLocationPermissionRefresh.ts']) {
    const selected = selectJobs([path]);
    for (const flag of ['ui', 'windows', 'android', 'framework', 'idle']) assert.equal(selected[flag], true, `${path}: ${flag}`);
  }
});
test('new Windows update metadata code selects both native validation paths', () => {
  const selected = selectJobs(['apps/aegis-node/src/services/updateMetadata.ts']);
  assert.equal(selected.ui, true); assert.equal(selected.windows, true); assert.equal(selected.android, true); assert.equal(selected.framework, true);
});
test('unclassified build/code paths conservatively retain full coverage', () => {
  const selected = selectJobs(['new-build-system.config.mjs']);
  for (const flag of ['backend', 'ui', 'windows', 'android', 'framework', 'idle', 'identity']) assert.equal(selected[flag], true);
});
test('manual full, release reuse and unknown histories retain every gate', () => {
  for (const options of [{ event: 'workflow_dispatch' }, { inputs: { full_matrix: true } }, { unknown: true }]) {
    const selected = selectJobs([], options);
    for (const flag of ['backend', 'ui', 'windows', 'android', 'framework', 'idle', 'identity', 'full']) assert.equal(selected[flag], true);
  }
});
test('focused manual modes remain explicit and full takes precedence', () => {
  const selected = selectJobs([], { event: 'workflow_dispatch', inputs: { ui_only: true } });
  assert.equal(selected.ui, true); assert.equal(selected.native, false); assert.equal(selected.idle, false);
  assert.equal(selectJobs([], { event: 'workflow_dispatch', inputs: { ui_only: true, full_matrix: true } }).full, true);
});
test('aggregate fails closed for selected failure, cancellation, missing and unexpected skip', () => {
  const selection = { backend: true, ui: false }, jobs = { regression: 'backend', ui: 'ui' };
  validateResults(selection, { regression: { result: 'success' }, ui: { result: 'skipped' } }, jobs);
  for (const result of ['failure', 'cancelled', 'skipped', undefined]) assert.throws(() => validateResults(selection, { regression: { result }, ui: { result: 'skipped' } }, jobs));
});
