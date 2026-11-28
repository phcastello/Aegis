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
    assert.deepEqual(Object.keys(configuration.volumes).sort(), ['data_protection_keys', 'neo4j_data', 'postgres_data', 'qdrant_data', 'script_workspaces', 'wake_dataset']);
    for (const [source, target] of [['script_workspaces', '/var/lib/aegis/script-workspaces'], ['wake_dataset', '/var/lib/aegis/wake-dataset']]) {
      assert.ok(configuration.services['aegis-api'].volumes.some(v => v.type === 'volume' && v.source === source && v.target === target && !v.read_only));
    }
    assert.ok(configuration.services['aegis-api'].tmpfs.includes('/run/aegis-script:rw,nosuid,nodev,noexec,size=1073741824,nr_inodes=16384,mode=0700'));
    assert.equal(configuration.services['aegis-api'].container_name, 'aegis-api');
    assert.ok(configuration.services['aegis-api'].volumes.some(volume => volume.source === 'data_protection_keys'));
    assert.ok(!Object.keys(configuration.services['aegis-api'].environment).some(key => key.includes('WEB_PUSH')));
  }
  assert.deepEqual(development.volumes, privateMode.volumes);
  const modelKeys = ['AEGIS_CHAT_MODEL', 'AEGIS_CHAT_REASONING_EFFORT',
    'AEGIS_TITLE_MODEL', 'AEGIS_TITLE_REASONING_EFFORT',
    'AEGIS_MEMORY_EXTRACTION_MODEL', 'AEGIS_MEMORY_EXTRACTION_REASONING_EFFORT',
    'AEGIS_MEMORY_EMBEDDING_MODEL'];
  const modelDefaults = ['gpt-6.1-terra', 'low', 'gpt-6-luna', 'none',
    'gpt-6-luna', 'low', 'text-embedding-3-large'];
  const defaultsFixture = join(temporary, 'model-defaults.env');
  const withoutModels = example.split('\n').filter(line =>
    !modelKeys.some(key => line.startsWith(key + '='))).join('\n');
  await writeFile(defaultsFixture, withoutModels);
  for (const configuration of [development, privateMode, config(false, defaultsFixture), config(true, defaultsFixture)]) {
    assert.deepEqual(modelKeys.map(key => configuration.services['aegis-api'].environment[key]), modelDefaults);
  }
  const overrideFixture = join(temporary, 'model-override.env');
  await writeFile(overrideFixture, withoutModels + '\nAEGIS_CHAT_MODEL=gpt-6-luna\nAEGIS_CHAT_REASONING_EFFORT=medium\nAEGIS_TITLE_MODEL=gpt-5-nano\nAEGIS_TITLE_REASONING_EFFORT=minimal\n');
  const overrides = config(true, overrideFixture).services['aegis-api'].environment;
  assert.deepEqual(modelKeys.slice(0, 4).map(key => overrides[key]), ['gpt-6-luna', 'medium', 'gpt-5-nano', 'minimal']);
  const searchKeys = ['AEGIS_WEB_SEARCH_ENABLED', 'AEGIS_WEB_SEARCH_MAX_SEARCH_ACTIONS'];
  const withoutSearch = example.split('\n').filter(line =>
    !searchKeys.some(key => line.startsWith(key + '='))).join('\n');
  const searchDefaultsFixture = join(temporary, 'search-defaults.env');
  await writeFile(searchDefaultsFixture, withoutSearch);
  for (const configuration of [development, privateMode, config(false, searchDefaultsFixture), config(true, searchDefaultsFixture)]) {
    assert.deepEqual(searchKeys.map(key => configuration.services['aegis-api'].environment[key]), ['true', '20']);
  }
  const legacySearchFixture = join(temporary, 'search-legacy.env');
  await writeFile(legacySearchFixture, withoutSearch + '\nAEGIS_WEB_SEARCH_MAX_CALLS=8\nAEGIS_WEB_SEARCH_ENABLED=false\n');
  assert.deepEqual(searchKeys.map(key => config(false, legacySearchFixture).services['aegis-api'].environment[key]), ['false', '8']);
  const searchOverrideFixture = join(temporary, 'search-override.env');
  await writeFile(searchOverrideFixture, withoutSearch + '\nAEGIS_WEB_SEARCH_MAX_CALLS=8\nAEGIS_WEB_SEARCH_MAX_SEARCH_ACTIONS=12\n');
  assert.equal(config(true, searchOverrideFixture).services['aegis-api'].environment.AEGIS_WEB_SEARCH_MAX_SEARCH_ACTIONS, '12');
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
