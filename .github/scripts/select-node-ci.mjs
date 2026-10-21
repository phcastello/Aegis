import { appendFileSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const flags = ['backend', 'ui', 'windows', 'android', 'framework', 'idle', 'identity'];
const truthy = value => value === true || value === 'true';
export function selectJobs(paths, { event = 'pull_request', inputs = {}, unknown = false } = {}) {
  const selection = Object.fromEntries(flags.map(name => [name, false]));
  const enable = (...names) => names.forEach(name => { selection[name] = true; });
  const focus = ['ui_only', 'windows_only', 'android_distribution_only', 'android_framework_only'].find(name => truthy(inputs[name]));
  const full = truthy(inputs.full_matrix) || unknown || (event === 'workflow_dispatch' && !focus);
  if (full) enable(...flags);
  else if (event === 'workflow_dispatch') {
    if (focus === 'ui_only') enable('ui');
    if (focus === 'windows_only') enable('windows');
    if (focus === 'android_distribution_only') enable('android');
    if (focus === 'android_framework_only') enable('framework');
  } else for (const path of paths) {
    if (/^backend\/src\/.*\/Prompts\/.*\.md$/.test(path)) { enable('backend'); continue; }
    if (path === 'scripts/check-compose.mjs') { enable('backend'); continue; }
    if (path === 'scripts/check-docs.mjs') continue; // The always-running selection job validates documentation.
    if (/^(?:docs\/|README\.md$|Aegis_.*\.(?:md|zip)$)/.test(path) || /\.md$/.test(path)) continue;
    if (/^(?:\.github\/|(?:apps\/aegis-node|packages\/aegis-client|frontend\/aegis-pwa)\/.*(?:package(?:-lock)?\.json|(?:vite|tsconfig|postcss|tailwind).*|Cargo\.(?:toml|lock)|build\.rs|tauri\.conf\.json)$)/.test(path)) {
      enable(...flags); continue;
    }
    if (/^(?:Dockerfile(?:\.[^/]+)?|\.dockerignore|Makefile|CMakeLists\.txt)$/.test(path)) { enable(...flags); continue; }
    if (/^deploy\/sql\//.test(path)) { enable('backend'); continue; }
    // Proxy/runtime/signing deployment configuration can change native transport contracts.
    if (/^deploy\//.test(path)) { enable(...flags); continue; }
    if (/^(?:backend\/|docker-compose.*|\.env\.example)/.test(path)) enable('backend');
    if (/^(?:packages\/aegis-client\/|frontend\/aegis-pwa\/|apps\/aegis-node\/(?:src\/|tests\/))/.test(path)) enable('ui');
    if (/^apps\/aegis-node\/src-tauri\//.test(path)) {
      if (/\/gen\/android\//.test(path)) enable('android', 'framework');
      else enable('windows', 'android', 'framework');
      // Any runtime/permission/storage change keeps equivalent idle/consumption evidence.
      enable('idle');
    }
    if (/^(?:apps\/aegis-node\/scripts\/|scripts\/node-transport-probe\/)/.test(path)) enable('backend', 'ui', 'windows', 'android', 'framework', 'idle');
    if (/^(?:backend\/.*(?:Nodes|Reminders|Notifications|Transport)|packages\/aegis-client\/tests\/(?:windows|android))/.test(path)) enable('windows', 'android', 'framework', 'idle');
    if (/^apps\/aegis-node\/src\/(?:services\/|composables\/|(?:App\.vue|main\.ts)$|components\/ReminderActionSync\.vue$)/.test(path)) enable('windows', 'android', 'framework', 'idle');
    if (/^packages\/aegis-client\/src\/.*(?:Transcription|Voice|voice|[Aa]udio|[Mm]icrophone|clientEnvironment|aegisApi)/.test(path)) enable('windows', 'android', 'framework', 'idle');
    if (!/^(?:backend\/|deploy\/|docker-compose|\.env\.example$|packages\/aegis-client\/|frontend\/aegis-pwa\/|apps\/aegis-node\/(?:src\/|src-tauri\/|scripts\/|tests\/)|scripts\/node-transport-probe\/)/.test(path)) enable(...flags);
    if (/(?:releaseIdentity|ReleaseIdentity|AboutPanel|Sidebar|NodeDrawer|\/App\.vue|\/version\.mjs)/i.test(path)) enable('identity');
  }
  if (selection.identity) enable('ui');
  const matrix = [];
  if (selection.windows) matrix.push({ os: 'windows-2022', android: 'false', platform: 'Windows-x86_64' });
  if (selection.android) matrix.push({ os: 'ubuntu-24.04', android: 'true', platform: 'Android-arm64' });
  return { ...selection, native: matrix.length > 0, matrix, full };
}

export function validateResults(selection, results, jobs) {
  for (const [job, flag] of Object.entries(jobs)) {
    const result = results[job]?.result;
    if (selection[flag] && result !== 'success') throw new Error(`Selected job ${job} did not pass: ${result ?? 'missing'}`);
    if (!selection[flag] && !['success', 'skipped'].includes(result)) throw new Error(`Unexpected result for ${job}: ${result ?? 'missing'}`);
  }
}

function changedPaths(event, name) {
  if (name === 'workflow_dispatch' || truthy(JSON.parse(process.env.CI_INPUTS || '{}').full_matrix)) return { paths: [] };
  const base = event.pull_request?.base?.sha || event.before;
  const head = event.pull_request?.head?.sha || event.after;
  if (!/^[a-f0-9]{40}$/.test(base || '') || /^0+$/.test(base) || !/^[a-f0-9]{40}$/.test(head || '')) return { paths: [], unknown: true };
  // Missing history fails conservatively to the full matrix, never to zero tests.
  try { return { paths: execFileSync('git', ['diff', '--name-only', '-z', base, head], { encoding: 'utf8' }).split('\0').filter(Boolean) }; }
  catch { return { paths: [], unknown: true }; }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
  const name = process.env.GITHUB_EVENT_NAME;
  const changed = changedPaths(event, name);
  const selected = selectJobs(changed.paths, { event: name, inputs: JSON.parse(process.env.CI_INPUTS || '{}'), unknown: changed.unknown });
  for (const [key, value] of Object.entries(selected)) appendFileSync(process.env.GITHUB_OUTPUT, `${key}=${JSON.stringify(value)}\n`);
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, `CI selection: \`${JSON.stringify(selected)}\`\n\nChanged paths: ${changed.paths.length}; unknown history: ${!!changed.unknown}. Skips are selection decisions, not runtime acceptance.\n`);
}
