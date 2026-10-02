import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

export function androidVersionCode(version) {
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-stage\.([1-9]\d*))?$/.exec(version);
  if (!match) throw new Error('Use X.Y.Z or X.Y.Z-stage.N without build metadata.');
  const [major, minor, patch] = match.slice(1, 4).map(Number);
  const stage = match[4] === undefined ? 999 : Number(match[4]);
  if (major > 20 || minor > 99 || patch > 999 || stage < 1 || stage > 999 || (match[4] !== undefined && stage === 999))
    throw new Error('Version exceeds the Android versionCode allocation.');
  return major * 100000000 + minor * 1000000 + patch * 1000 + stage;
}

export function synchronize(write = false) {
  const root = new URL('../', import.meta.url);
  const jsonPath = new URL('package.json', root);
  const version = JSON.parse(readFileSync(jsonPath, 'utf8')).version;
  const versionCode = androidVersionCode(version);
  const configPath = new URL('src-tauri/tauri.conf.json', root);
  const config = JSON.parse(readFileSync(configPath, 'utf8'));
  const cargoPath = new URL('src-tauri/Cargo.toml', root);
  const cargo = readFileSync(cargoPath, 'utf8');
  const nextCargo = cargo.replace(/^(version = )"[^"]+"/m, `$1"${version}"`);
  const cargoLockPath = new URL('src-tauri/Cargo.lock', root);
  const cargoLock = readFileSync(cargoLockPath, 'utf8');
  const nextCargoLock = cargoLock.replace(/(name = "aegis-node"\nversion = )"[^"]+"/, `$1"${version}"`);
  const npmLockPath = new URL('package-lock.json', root);
  const npmLock = JSON.parse(readFileSync(npmLockPath, 'utf8'));
  const valid = config.version === '../package.json' && config.bundle.android.versionCode === versionCode &&
    cargo === nextCargo && cargoLock === nextCargoLock && npmLock.version === version && npmLock.packages[''].version === version;
  if (write) {
    config.version = '../package.json';
    config.bundle.android = { ...config.bundle.android, versionCode };
    npmLock.version = npmLock.packages[''].version = version;
    writeFileSync(configPath, JSON.stringify(config, null, 2) + '\n');
    writeFileSync(cargoPath, nextCargo);
    writeFileSync(cargoLockPath, nextCargoLock);
    writeFileSync(npmLockPath, JSON.stringify(npmLock, null, 2) + '\n');
  } else if (!valid) throw new Error('Version drift: run npm run version:sync.');
  return { version, versionCode };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const result = synchronize(process.argv.includes('--write'));
  console.log(`Aegis ${result.version}; Android versionCode ${result.versionCode}`);
}
