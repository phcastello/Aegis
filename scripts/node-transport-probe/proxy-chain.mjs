// Linux CI: real Kestrel + two real Nginx hops; no production data or credentials.
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
const root = resolve(import.meta.dirname, '../..');
const work = mkdtempSync(join(tmpdir(), 'aegis-ws-proxy-'));
const bootstrap = join(work, 'code.txt'), container = 'aegis-ws-proxy-' + process.pid;
const api = spawn('dotnet', ['run', '--project', 'backend/tests/Aegis.NodeRuntimeFixture', '--', bootstrap], { cwd: root, stdio: ['ignore', 'ignore', 'ignore'] });
async function run(command, args, env) { const child = spawn(command, args, { cwd: root, env: { ...process.env, ...env }, stdio: 'inherit' });
  await new Promise((resolve, reject) => { child.on('error', () => reject(Error('fixture_process_failed'))); child.on('exit', code => code === 0 ? resolve() : reject(Error('fixture_process_failed'))); }); }
try {
  const start = Date.now(); while (!existsSync(bootstrap)) { if (api.exitCode !== null || Date.now() - start > 60000) throw Error('fixture_readiness_timeout'); await new Promise(r => setTimeout(r, 100)); }
  const internal = readFileSync(join(root, 'frontend/aegis-pwa/nginx.conf'), 'utf8').replace('listen 80;', 'listen 18105;').replace('aegis-api:8090', '127.0.0.1:18104');
  const external = `server { listen 18106; location / { include /etc/nginx/transport.inc; proxy_pass http://127.0.0.1:18105; } }
server { listen 18107; location / { proxy_pass http://127.0.0.1:18105; } }`;
  writeFileSync(join(work, 'nginx.conf'), `events {}\nhttp { ${internal}\n${external}\n}`);
  writeFileSync(join(work, 'transport.inc'), readFileSync(join(root, 'deploy/nginx/node-transport-proxy.inc.conf')));
  await run('docker', ['run', '-d', '--name', container, '--network', 'host', '-v', join(work, 'nginx.conf') + ':/etc/nginx/nginx.conf:ro', '-v', join(work, 'transport.inc') + ':/etc/nginx/transport.inc:ro', 'nginx:1.27-alpine']);
  await run('docker', ['exec', container, 'nginx', '-t']);
  await run('node', ['scripts/node-transport-probe/probe.mjs'], { AEGIS_PROBE_ORIGIN: 'http://127.0.0.1:18106', AEGIS_PROBE_REJECT_ORIGIN: 'http://127.0.0.1:18107', AEGIS_PROBE_CLEANUP_ORIGIN: 'http://127.0.0.1:18104', AEGIS_PROBE_CODE_FILE: bootstrap, AEGIS_PROBE_CYCLES: '4' });
  console.log('PASS two-proxy production regression: stripped upgrade rejected; HTTP101, hello, two Nodes, four heartbeat cycles and availability');
} finally {
  spawnSync('docker', ['rm', '-f', container], { stdio: 'ignore' }); api.kill(); await new Promise(r => api.exitCode !== null ? r() : api.once('exit', r));
  rmSync(work, { recursive: true, force: true });
}
