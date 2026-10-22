import { appendFileSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const flags = ['backend', 'ui', 'windows', 'android', 'framework', 'idle', 'identity'];
const subsystems = ['backend', 'ui', 'windows', 'android', 'distribution', 'geolocation', 'notifications', 'audio', 'energy', 'identity', 'dependencies', 'unknown'];
const truthy = value => value === true || value === 'true';
const isSha = value => /^[a-f0-9]{40}$/i.test(value || '') && !/^0+$/.test(value);
const focusedInput = inputs => ['ui_only', 'windows_only', 'android_distribution_only', 'android_framework_only'].find(name => truthy(inputs[name]));

// Events decide maturity; file selection decides scope. A draft/push cannot opt itself into release acceptance.
export function validationStage({ event = 'pull_request', inputs = {}, payload = {} } = {}) {
  if (event === 'pull_request') return payload.pull_request?.draft === true ? 'development' : 'integration';
  if (event === 'merge_group') return 'integration';
  if (event === 'push' && !inputs.ref) {
    if (payload.ref?.startsWith('refs/tags/')) return 'release';
    const main = payload.repository?.default_branch || 'main';
    return payload.ref === `refs/heads/${main}` ? 'integration' : 'development';
  }
  if (event === 'workflow_dispatch' || event === 'workflow_call' || inputs.ref) {
    const stage = inputs.stage || (truthy(inputs.full_matrix) || inputs.ref || event === 'workflow_call' ? 'release' : 'integration');
    if (!['development', 'preview', 'integration', 'release'].includes(stage)) throw Error(`Invalid validation stage: ${stage}`);
    return truthy(inputs.full_matrix) && stage !== 'preview' && stage !== 'development' ? 'release' : stage;
  }
  throw Error(`Unsupported CI event: ${event}`);
}

export function affectedSubsystems(paths, { unknown = false } = {}) {
  const affected = Object.fromEntries(subsystems.map(name => [name, false]));
  const enable = (...names) => names.forEach(name => { affected[name] = true; });
  const all = () => enable(...subsystems.filter(name => name !== 'unknown'));
  if (unknown) { all(); enable('unknown'); }
  for (const path of paths) {
    if (/^backend\/src\/.*\/Prompts\/.*\.md$/.test(path)) { enable('backend'); continue; }
    if (path === 'scripts/check-compose.mjs') { enable('backend'); continue; }
    if (path === 'scripts/check-docs.mjs' || /\.md$/.test(path) || /^(?:docs\/|Aegis_.*\.zip$)/.test(path)) continue;
    if (/^(?:\.github\/|Dockerfile(?:\.[^/]+)?$|\.dockerignore$|Makefile$|CMakeLists\.txt$)/.test(path)) { all(); continue; }
    if (/^deploy\/sql\//.test(path)) { enable('backend'); continue; }
    if (/^deploy\//.test(path)) { all(); continue; }
    if (/^(?:backend\/|docker-compose.*|\.env\.example)/.test(path)) enable('backend');
    if (/^(?:packages\/aegis-client\/|frontend\/aegis-pwa\/|apps\/aegis-node\/(?:src\/|tests\/))/.test(path)) enable('ui');
    if (/^(?:apps\/aegis-node|packages\/aegis-client|frontend\/aegis-pwa)\/.*(?:package(?:-lock)?\.json|(?:vite|tsconfig|postcss|tailwind)[^/]*|Cargo\.(?:toml|lock)|build\.rs|tauri\.conf\.json)$/.test(path)) {
      enable('ui', 'windows', 'android', 'distribution', 'dependencies', 'identity');
    }
    if (/^apps\/aegis-node\/src-tauri\//.test(path)) {
      if (/\/gen\/android\//.test(path)) enable('android');
      else enable('windows', 'android'); // Shared Rust contracts also compile into Android.
    }
    if (/^(?:apps\/aegis-node\/scripts\/|scripts\/node-transport-probe\/)/.test(path)) enable('backend', 'ui', 'windows', 'android', 'distribution');
    if (/^(?:backend\/.*(?:Nodes|Reminders|Notifications|Transport)|packages\/aegis-client\/tests\/(?:windows|android))/.test(path)) enable('windows', 'android');
    if (/^apps\/aegis-node\/src\/(?:services\/|composables\/|(?:App\.vue|main\.ts)$|components\/ReminderActionSync\.vue$)/.test(path)) enable('windows', 'android');
    if (/^packages\/aegis-client\/src\/.*(?:Transcription|Voice|voice|[Aa]udio|[Mm]icrophone|clientEnvironment|aegisApi)/.test(path)) enable('windows', 'android');
    if (/(?:geofenc|[Ll]ocation|[Pp]lace|[Rr]egion|ReminderReliability|ReminderActivation)/i.test(path)) enable('geolocation');
    if (/(?:notification|[Tt]oast|[Rr]eminder|[Tt]ransport)/i.test(path)) enable('notifications');
    if (/(?:transcription|voice|audio|microphone|capture)/i.test(path)) enable('audio');
    if (/(?:geofenc|location|background|idle|efficiency|lifecycle|runtime|node_transport|battery|power|AndroidManifest|ReminderReliability|ReminderActivation)/i.test(path)) enable('energy');
    if (/(?:releaseIdentity|ReleaseIdentity|AboutPanel|Sidebar|NodeDrawer|\/App\.vue|\/version\.mjs|native_capabilities|node_capabilities)/i.test(path)) enable('identity');
    if (!/^(?:backend\/|deploy\/|docker-compose|\.env\.example$|packages\/aegis-client\/|frontend\/aegis-pwa\/|apps\/aegis-node\/(?:src\/|src-tauri\/|scripts\/|tests\/)|scripts\/node-transport-probe\/)/.test(path)) {
      all(); enable('unknown');
    }
  }
  if (affected.backend && (affected.geolocation || affected.notifications)) enable('windows', 'android');
  return affected;
}

export function selectJobs(paths, options = {}) {
  const { event = 'pull_request', inputs = {}, unknown = false, payload = {}, sha = '', checkoutSha = sha } = options;
  const stage = validationStage({ event, inputs, payload });
  const affected = affectedSubsystems(paths, { unknown });
  const selection = Object.fromEntries(flags.map(name => [name, false]));
  const enable = (...names) => names.forEach(name => { selection[name] = true; });
  const focus = focusedInput(inputs);
  const full = stage === 'release' && truthy(inputs.full_matrix);
  if (stage !== 'development') {
    if (stage === 'preview') {
      const platform = inputs.platform || 'both';
      if (!['android', 'windows', 'both'].includes(platform)) throw Error(`Invalid preview platform: ${platform}`);
      enable('ui', 'identity');
      if (platform !== 'android') enable('windows');
      if (platform !== 'windows') enable('android');
    } else if (full) enable(...flags);
    else if (event === 'workflow_dispatch' && focus) {
      if (focus === 'ui_only') enable('ui');
      if (focus === 'windows_only') enable('windows');
      if (focus === 'android_distribution_only') enable('android');
      if (focus === 'android_framework_only') enable('framework');
    } else {
      for (const flag of ['backend', 'ui', 'windows', 'android', 'identity']) if (affected[flag]) enable(flag);
      if (affected.android) enable('framework');
      if (stage === 'release' && (affected.geolocation || affected.energy || affected.audio || affected.dependencies || affected.unknown)) {
        if (affected.windows || affected.android) enable('idle');
      }
    }
  }
  if (selection.identity) enable('ui');
  const matrix = [];
  if (selection.windows) matrix.push({ os: 'windows-2022', android: 'false', platform: 'Windows-x86_64' });
  if (selection.android) matrix.push({ os: 'ubuntu-24.04', android: 'true', platform: 'Android-arm64' });
  const source_sha = stage === 'release' && inputs.ref ? checkoutSha : payload.pull_request?.head?.sha || payload.merge_group?.head_sha || checkoutSha;
  const evidence_sha = inputs.ref ? checkoutSha : sha || checkoutSha;
  return { ...selection, native: matrix.length > 0, matrix, full, stage, integration: stage === 'integration' && !focus, partial: !!focus && !full, source_sha, evidence_sha, affected };
}

export function validateResults(selection, results, jobs) {
  for (const [job, flag] of Object.entries(jobs)) {
    if (typeof selection[flag] !== 'boolean') throw new Error(`Missing selection for ${job}: ${flag}`);
    const result = results[job]?.result;
    if (selection[flag] && result !== 'success') throw new Error(`Selected job ${job} did not pass: ${result ?? 'missing'}`);
    if (!selection[flag] && !['success', 'skipped'].includes(result)) throw new Error(`Unexpected result for ${job}: ${result ?? 'missing'}`);
  }
}

// A deliberately omitted draft or a preview must never satisfy the integration gate.
export function validateIntegrationContext(selection, { event, payload = {}, sha, checkoutSha } = {}) {
  if (!['integration', 'release'].includes(selection.stage)) throw Error(`Integration acceptance unavailable in ${selection.stage || 'unknown'} stage; mark the PR ready to request independent validation.`);
  if (selection.partial === true || (selection.stage === 'integration' && selection.integration !== true)) throw Error('Focused validation is not complete integration acceptance.');
  if (['pull_request', 'merge_group'].includes(event) && selection.stage !== 'integration') throw Error('Integration event requires integration evidence.');
  if (event === 'pull_request' && payload.pull_request?.draft === true) throw Error('A draft PR is not integration acceptance.');
  const expectedSource = payload.pull_request?.head?.sha || payload.merge_group?.head_sha || checkoutSha;
  const expectedEvidence = selection.stage === 'release' ? checkoutSha : sha;
  for (const [label, value] of Object.entries({ source: selection.source_sha, evidence: selection.evidence_sha, expectedSource, expectedEvidence, checkoutSha })) {
    if (!isSha(value)) throw Error(`Missing or invalid ${label} SHA in integration evidence.`);
  }
  if (selection.source_sha !== expectedSource || selection.evidence_sha !== expectedEvidence || selection.evidence_sha !== checkoutSha) throw Error('Integration evidence does not identify the current candidate commit.');
}

export function changedPaths(payload, event, { inputs = {}, runGit = args => execFileSync('git', args, { encoding: 'utf8' }) } = {}) {
  const stage = validationStage({ event, inputs, payload });
  if ((stage === 'release' && truthy(inputs.full_matrix)) || (event === 'workflow_dispatch' && focusedInput(inputs))) return { paths: [] };
  // A caller-chosen baseline cannot shrink the integration gate for a ready PR.
  // Manual integration without the PR payload therefore uses conservative scope.
  if (event === 'workflow_dispatch' && stage !== 'release') return { paths: [], unknown: true };
  const base = payload.pull_request?.base?.sha || payload.merge_group?.base_sha || inputs.base_ref || payload.before;
  const head = payload.pull_request?.head?.sha || payload.merge_group?.head_sha || inputs.ref || payload.after || 'HEAD';
  if (!isSha(base)) return { paths: [], unknown: true };
  if (head !== 'HEAD' && !isSha(head)) return { paths: [], unknown: true };
  try {
    // PR changes are the complete branch diff, not base-tip..head (which includes unrelated base changes).
    const diffBase = payload.pull_request ? runGit(['merge-base', base, head]).trim() : base;
    if (!isSha(diffBase)) return { paths: [], unknown: true };
    return { paths: runGit(['diff', '--name-only', '-z', diffBase, head]).split('\0').filter(Boolean) };
  } catch { return { paths: [], unknown: true }; }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const payload = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
  const event = process.env.GITHUB_EVENT_NAME;
  const inputs = JSON.parse(process.env.CI_INPUTS || '{}');
  const changed = changedPaths(payload, event, { inputs });
  const checkoutSha = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  const selected = selectJobs(changed.paths, { event, inputs, payload, unknown: changed.unknown, sha: process.env.GITHUB_SHA, checkoutSha });
  for (const [key, value] of Object.entries(selected)) appendFileSync(process.env.GITHUB_OUTPUT, `${key}=${typeof value === 'string' ? value : JSON.stringify(value)}\n`);
  const message = selected.stage === 'development'
    ? 'Development/draft: application jobs are intentionally omitted. This is not integration acceptance.'
    : `Stage: ${selected.stage}. Selected jobs must succeed for the current candidate; intentional omissions are not native evidence.`;
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${message}\n\nSource: \`${selected.source_sha}\`; evidence: \`${selected.evidence_sha}\`.\n\nCI selection: \`${JSON.stringify(selected)}\`\n\nChanged paths: ${changed.paths.length}; unknown history: ${!!changed.unknown}.\n`);
}
