import { readFileSync } from 'node:fs';
import ts from 'typescript';
const cache = new Map();
export function moduleUrl(path) {
  const url = new URL(path, import.meta.url);
  if (cache.has(url.href)) return cache.get(url.href);
  let code = ts.transpileModule(readFileSync(url, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }
  }).outputText;
  code = code.replace(/from '(\.[^']+)'/g, (_, relative) => `from '${moduleUrl(new URL(relative + '.ts', url).href)}'`);
  const result = `data:text/javascript;base64,${Buffer.from(code).toString('base64')}`;
  cache.set(url.href, result);
  return result;
}
export function loadTypeScript(path) { return import(moduleUrl(path)); }
