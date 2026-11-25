import { copyFile, mkdir, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const root = new URL('../', import.meta.url);
const output = new URL('dist/', root);
// The output is the fixed, repo-local generated directory, never user data.
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
for (const file of ['index.html', 'landing.css', 'landing.js']) {
  await copyFile(new URL(file, root), new URL(file, output));
}
await copyFile(new URL('public/favicon.svg', root), new URL('favicon.svg', output));
await build({
  entryPoints: [fileURLToPath(new URL('src/sw.ts', root))],
  outfile: fileURLToPath(new URL('sw.js', output)),
  bundle: true, minify: true, format: 'iife', target: 'es2022',
  define: { 'process.env.NODE_ENV': '"production"' }
});
await copyFile(new URL('sw.js', output), new URL('dev-sw.js', output));
