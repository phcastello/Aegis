import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

test('chat Markdown completes pathological unclosed references without blocking the renderer', () => {
  // GHSA-gfr2-rv44-r23c: old versions take over a minute for this title.
  // Run separately so an accidental vulnerable downgrade cannot hang the test suite.
  const script = `
    import MarkdownIt from 'markdown-it';
    const markdown = new MarkdownIt({ html: false, linkify: true, breaks: true });
    for (const input of ['[a]: /u "' + 'x\\n'.repeat(400000), '[' + 'a\\n'.repeat(20000)]) {
      if (!markdown.render(input).length) throw Error('Expected literal text output');
    }
    process.stdout.write('completed');
  `;
  const result = spawnSync(process.execPath, ['--input-type=module', '-e', script], {
    cwd: fileURLToPath(new URL('../', import.meta.url)), encoding: 'utf8', timeout: 15000,
  });
  assert.equal(result.error, undefined, 'Malicious reference input must finish within a bounded worker.');
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, 'completed');
});
