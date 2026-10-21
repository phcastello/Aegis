// Lightweight guard for documented commands, runner targets and local Markdown links.
import assert from 'node:assert/strict';
import { existsSync, globSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const packages = ['packages/aegis-client', 'frontend/aegis-pwa', 'apps/aegis-node'];
const manifests = new Map(packages.map(path => [path, JSON.parse(readFileSync(resolve(root, path, 'package.json'), 'utf8'))]));
for (const [path, manifest] of manifests) {
  for (const [name, script] of Object.entries(manifest.scripts)) {
    // Include node/tsc script files and test globs, but exclude package executables.
    for (const match of script.matchAll(/(?:^|\s)([^\s;&|]+\.(?:mjs|cjs|json))(?:\s|$)/g)) {
      const target = match[1];
      assert.ok(globSync(target, { cwd: resolve(root, path) }).length, `${path}:${name}: missing target ${target}`);
    }
  }
}

for (const [doc, context] of [
  ['README.md', null], ['docs/README.md', null], ['docs/testes.md', null],
  ['apps/aegis-node/README.md', 'apps/aegis-node'],
]) {
  const source = readFileSync(resolve(root, doc), 'utf8');
  for (const match of source.matchAll(/npm\s+(?:(?:--prefix\s+([\w./-]+))\s+)?(run\s+([\w:-]+)|test|ci)(?:\s+--prefix\s+([\w./-]+))?/g)) {
    const packagePath = match[1] ?? match[4] ?? context;
    assert.ok(manifests.has(packagePath), `${doc}: npm command requires a documented package directory: ${match[0]}`);
    const script = match[2] === 'test' ? 'test' : match[3];
    if (script) assert.ok(manifests.get(packagePath).scripts[script], `${doc}: nonexistent npm script ${packagePath}:${script}`);
  }
}

function markdownFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = resolve(directory, entry.name);
    return entry.isDirectory() ? markdownFiles(path) : entry.name.endsWith('.md') ? [path] : [];
  });
}
let links = 0;
for (const path of [resolve(root, 'README.md'), resolve(root, 'apps/aegis-node/README.md'), ...markdownFiles(resolve(root, 'docs'))]) {
  const source = readFileSync(path, 'utf8');
  const destinations = [...source.matchAll(/\]\(([^\s)]+)(?:\s+"[^"]*")?\)/g), ...source.matchAll(/^\[[^\]]+\]:\s*(\S+)/gm)];
  for (const [, target] of destinations) {
    if (/^(?:[\w+.-]+:|#|\/)/.test(target)) continue;
    const [file] = target.split('#');
    assert.ok(existsSync(resolve(dirname(path), decodeURIComponent(file))), `${relative(root, path)}: broken local link ${target}`);
    links++;
  }
}
console.log(`Documentation OK: ${packages.length} package runners, documented npm commands, ${links} local links.`);
