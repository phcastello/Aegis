import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

// Configuration only: do not start services or read an installation's .env.
const root = fileURLToPath(new URL('../', import.meta.url));
const temporary = await mkdtemp(join(tmpdir(), 'aegis-maintenance-compose-'));
const environment = Object.fromEntries(Object.entries(process.env).filter(([key]) =>
  !/^(AEGIS_|POSTGRES_|QDRANT_|NEO4J_|GOOGLE_|VITE_|COMPOSE_|OPENAI_API_KEY$)/.test(key)));
try {
  const fixture = join(temporary, 'fixture.env');
  const example = await readFile(new URL('../.env.example', import.meta.url), 'utf8');
  await writeFile(fixture, example);
  function config(privateMode, envFile = fixture) {
    return JSON.parse(execFileSync('docker', ['compose', '--env-file', envFile, '--project-name', 'aegis-maintenance-config',
      '-f', 'docker-compose.yml', ...(privateMode ? ['-f', 'docker-compose.private.yml'] : []), 'config', '--format', 'json'],
    { cwd: root, env: environment, encoding: 'utf8' }));
  }
  const development = config(false), privateMode = config(true);
  assert.equal(development.services['aegis-api'].environment.ASPNETCORE_ENVIRONMENT, 'Development');
  assert.equal(privateMode.services['aegis-api'].environment.ASPNETCORE_ENVIRONMENT, 'Production');
  for (const configuration of [development, privateMode]) {
    for (const service of Object.values(configuration.services)) {
      for (const port of service.ports ?? []) assert.equal(port.host_ip, '127.0.0.1');
    }
    assert.deepEqual(Object.keys(configuration.volumes).sort(), ['data_protection_keys', 'neo4j_data', 'postgres_data', 'qdrant_data']);
    assert.equal(configuration.services['aegis-api'].container_name, 'aegis-api');
    assert.ok(configuration.services['aegis-api'].volumes.some(volume => volume.source === 'data_protection_keys'));
    assert.ok(!Object.keys(configuration.services['aegis-api'].environment).some(key => key.includes('WEB_PUSH')));
  }
  assert.deepEqual(development.volumes, privateMode.volumes);
  const privateFixture = join(temporary, 'private-ip.env');
  await writeFile(privateFixture, example.replace('AEGIS_HTTP_BIND_ADDRESS=127.0.0.1', 'AEGIS_HTTP_BIND_ADDRESS=192.0.2.25'));
  const privateAddress = config(true, privateFixture);
  for (const name of ['aegis-api', 'aegis-pwa']) {
    for (const port of privateAddress.services[name].ports) assert.equal(port.host_ip, '192.0.2.25');
  }
  for (const name of ['postgres', 'qdrant', 'neo4j']) {
    for (const port of privateAddress.services[name].ports) assert.equal(port.host_ip, '127.0.0.1');
  }
  console.log('Compose: Development/Production, explicit private HTTP IP, loopback data bindings and existing volumes validated.');
} finally { await rm(temporary, { recursive: true, force: true }); }
