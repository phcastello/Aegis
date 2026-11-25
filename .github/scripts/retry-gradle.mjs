// A fresh CI runner can hit Maven Central rate limits while resolving Gradle
// dependencies. Retry only a confirmed HTTP 429, preserving every other failure.
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout } from 'node:timers/promises';
import { stripVTControlCharacters } from 'node:util';

const backoff = [30_000, 60_000, 120_000];

export function mavenRateLimited(output) {
  return /Could not (?:GET|HEAD) ['"]https:\/\/(?:repo\.maven\.apache\.org|repo1\.maven\.org)\/maven2\/[^\r\n'"]+['"]\. Received status code 429\b/.test(stripVTControlCharacters(output));
}

export function runGradleCommand(command, args) {
  return new Promise((done) => {
    const child = spawn(command, args, { stdio: ['inherit', 'pipe', 'pipe'], shell: false });
    let rateLimited = false;
    for (const [stream, destination] of [[child.stdout, process.stdout], [child.stderr, process.stderr]]) {
      let tail = '';
      stream.setEncoding('utf8');
      stream.on('data', (text) => {
        // Print the original output, and retain only the tail to recognize an
        // error split across chunks. Do not save build logs or credentials.
        destination.write(text);
        const combined = tail + text;
        rateLimited ||= mavenRateLimited(combined);
        tail = combined.slice(-8192);
      });
    }
    child.once('error', (error) => {
      console.error(error.message);
      done({ code: 1, rateLimited: false });
    });
    child.once('close', (code) => done({ code: code ?? 1, rateLimited: code !== null && rateLimited }));
  });
}

export async function retryGradle(command, args, {
  run = runGradleCommand, sleep = setTimeout, jitter = () => Math.floor(Math.random() * 10_000),
  warn = console.warn
} = {}) {
  for (let attempt = 0; ; attempt++) {
    const { code, rateLimited } = await run(command, args);
    if (code === 0 || !rateLimited || attempt >= backoff.length) return code;
    const delay = backoff[attempt] + jitter();
    warn(`Maven Central HTTP 429: retrying Gradle in ${Math.round(delay / 1000)}s (${attempt + 1}/${backoff.length})`);
    await sleep(delay);
  }
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  const [command, ...args] = process.argv.slice(2);
  if (!command) throw new Error('Expected an Android/Gradle command to execute.');
  process.exitCode = await retryGradle(command, args);
}
