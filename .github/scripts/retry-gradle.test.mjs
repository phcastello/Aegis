import assert from 'node:assert/strict';
import test from 'node:test';
import { mavenRateLimited, retryGradle, runGradleCommand } from './retry-gradle.mjs';

const maven429 = "Could not GET 'https://repo.maven.apache.org/maven2/org/jetbrains/kotlin/example.pom'. Received status code 429 from server: Too Many Requests";

test('retry classification requires actual Maven Central HTTP 429 dependency failure', () => {
  assert.equal(mavenRateLimited(maven429), true);
  assert.equal(mavenRateLimited(maven429.replace('GET', 'HEAD').replace('repo.maven.apache.org', 'repo1.maven.org')), true);
  assert.equal(mavenRateLimited(`\u001b[31m${maven429}\u001b[0m`), true);
  for (const other of [
    maven429.replace('429', '503'), maven429.replace('repo.maven.apache.org', 'other.example'),
    maven429.replace('repo.maven.apache.org', 'repo.maven.apache.org.other.example'),
    'Compilation error: line 429', 'FAILURE: Build failed with an exception.', 'AssertionError: tests failed',
    'Received status code 429 from server: Too Many Requests'
  ]) assert.equal(mavenRateLimited(other), false, other);
});

async function simulated(outcomes) {
  const calls = [], delays = [], warnings = [];
  const code = await retryGradle('npm', ['run', 'build:android'], {
    run: async (command, args) => { calls.push([command, args]); return outcomes[calls.length - 1]; },
    sleep: async (delay) => { delays.push(delay); }, jitter: () => 0,
    warn: (warning) => warnings.push(warning)
  });
  return { code, calls, delays, warnings };
}
test('first transient failure retries identical command and succeeds without timing sleeps', async () => {
  const result = await simulated([{ code: 1, rateLimited: true }, { code: 0, rateLimited: false }]);
  assert.equal(result.code, 0);
  assert.deepEqual(result.calls, [['npm', ['run', 'build:android']], ['npm', ['run', 'build:android']]]);
  assert.deepEqual(result.delays, [30000]);
  assert.equal(result.warnings.length, 1);
});
test('permanent compile and test failures are not retried; success never retries', async () => {
  for (const outcome of [{ code: 7, rateLimited: false }, { code: 0, rateLimited: true }]) {
    const result = await simulated([outcome]);
    assert.equal(result.code, outcome.code);
    assert.equal(result.calls.length, 1);
    assert.deepEqual(result.delays, []);
  }
  const result = await simulated([{ code: 1, rateLimited: true }, { code: 5, rateLimited: false }]);
  assert.equal(result.code, 5);
  assert.equal(result.calls.length, 2);
});
test('repeated rate limiting has strictly bounded attempts and returns failed exit code', async () => {
  const result = await simulated(Array(4).fill({ code: 2, rateLimited: true }));
  assert.equal(result.code, 2);
  assert.equal(result.calls.length, 4);
  assert.deepEqual(result.delays, [30000, 60000, 120000]);
});
test('child process output classifies a Gradle failure split across stream chunks', async () => {
  const program = 'process.stderr.write(process.argv[1].slice(0, 60)); setImmediate(() => { process.stderr.write(process.argv[1].slice(60)); process.exitCode = 13; });';
  assert.deepEqual(await runGradleCommand(process.execPath, ['-e', program, maven429]), { code: 13, rateLimited: true });
  assert.deepEqual(await runGradleCommand(process.execPath, ['-e', 'process.exitCode = 7']), { code: 7, rateLimited: false });
});
