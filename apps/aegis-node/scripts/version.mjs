import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

export function androidVersionCode(version) {
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:stage|unstable)\.([1-9]\d*))?$/.exec(version);
  if (!match) throw new Error('Use X.Y.Z or X.Y.Z-unstable.N without build metadata (stage.N is legacy).');
  const [major, minor, patch] = match.slice(1, 4).map(Number);
  const preview = match[4] === undefined ? 999 : Number(match[4]);
  if (major > 20 || minor > 99 || patch > 999 || preview < 1 || preview > 999 || (match[4] !== undefined && preview === 999))
    throw new Error('Version exceeds the Android versionCode allocation.');
  return major * 100000000 + minor * 1000000 + patch * 1000 + preview;
}

// Public distribution order is SemVer; installation codes are allocated independently.
export const MAX_ANDROID_VERSION_CODE = 2100000000;
export function parseDistributionVersion(version) {
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(unstable|rollback)\.([1-9]\d*)(?:\.rollback\.([1-9]\d*))?)?$/.exec(version);
  if (!match) throw Error('Use X.Y.Z, X.Y.Z-unstable.N[.rollback.M] or X.Y.Z-rollback.M.');
  const [major, minor, patch] = match.slice(1,4).map(Number);
  if (major > 20 || minor > 99 || patch > 999 || (match[4] === 'rollback' && match[6]))
    throw Error('Distribution version exceeds the native allocation or nests rollback events.');
  for (const number of [match[5],match[6]]) if (number && (!Number.isSafeInteger(Number(number)) || Number(number) > 65535))
    throw Error('Distribution event number exceeds the native range.');
  const prerelease = version.split('-')[1] ?? '';
  return {major, minor, patch, base:`${major}.${minor}.${patch}`, prerelease,
    stable:!prerelease, kind:match[4] === 'rollback' || match[6] ? 'rollback' : 'normal',
    ...(match[4] === 'unstable' ? {preview:Number(match[5])} : {}),
    ...(match[6] || match[4] === 'rollback' ? {rollback:Number(match[6] ?? match[5])} : {})};
}

export function compareVersions(left, right) {
  // This comparator deliberately accepts only the distribution grammar above.
  const a = parseDistributionVersion(left), b = parseDistributionVersion(right);
  for (const key of ['major','minor','patch']) if (a[key] !== b[key]) return a[key] < b[key] ? -1 : 1;
  if (!a.prerelease || !b.prerelease) return a.prerelease === b.prerelease ? 0 : a.prerelease ? -1 : 1;
  const aa = a.prerelease.split('.'), bb = b.prerelease.split('.');
  for (let i=0;i<Math.max(aa.length,bb.length);i++) {
    if (aa[i] === undefined || bb[i] === undefined) return aa[i] === undefined ? -1 : 1;
    if (aa[i] === bb[i]) continue;
    const an = /^\d+$/.test(aa[i]), bn = /^\d+$/.test(bb[i]);
    if (an && bn) return Number(aa[i]) < Number(bb[i]) ? -1 : 1;
    if (an !== bn) return an ? -1 : 1;
    return aa[i] < bb[i] ? -1 : 1;
  }
  return 0;
}

export function rollbackVersion(rollbackOf, eventNumber) {
  const source = parseDistributionVersion(rollbackOf);
  if (!Number.isInteger(eventNumber) || eventNumber < 1 || eventNumber > 65535 || eventNumber !== (source.rollback ?? 0) + 1)
    throw Error('Rollback event must advance the previous recovery number.');
  let base = source.base;
  if (source.stable) {
    let {major,minor,patch} = source;
    if (patch >= 999) throw Error('Stable recovery at the native patch limit requires a reviewed next-line procedure.');
    patch++;
    base=`${major}.${minor}.${patch}`;
  }
  const result = source.preview ? `${base}-unstable.${source.preview}.rollback.${eventNumber}` : `${base}-rollback.${eventNumber}`;
  if (compareVersions(result, rollbackOf) <= 0) throw Error('Rollback must be semantically newer.');
  return result;
}

export function validateVersionCode(code) {
  if (!Number.isInteger(code) || code < 1 || code > MAX_ANDROID_VERSION_CODE) throw Error('Invalid Android installation code.');
  return code;
}

// The caller persists a successful reservation before building. Historical 999
// slots stay available for stable releases so existing clients can still migrate.
export function allocateVersionCode(version, previousCodes = []) {
  const parsed = parseDistributionVersion(version);
  const floor = Math.max(0, ...previousCodes.map(validateVersionCode));
  const stable = androidVersionCode(parsed.base);
  const code = parsed.stable ? stable : Math.max(floor + 1, stable - 998);
  if (code <= floor) throw Error('Installation code must exceed every previous reservation.');
  if (!parsed.stable && code >= stable) throw Error('Preview allocation would consume the reserved stable installation code.');
  return validateVersionCode(code);
}

export function synchronize(write = false, options = {}) {
  const root = new URL('../', import.meta.url);
  const jsonPath = new URL('package.json', root);
  const version = JSON.parse(readFileSync(jsonPath, 'utf8')).version;
  const parsed = parseDistributionVersion(version);
  const configPath = new URL('src-tauri/tauri.conf.json', root);
  const config = JSON.parse(readFileSync(configPath, 'utf8'));
  const explicitCode = options.versionCode ?? (process.env.AEGIS_VERSION_CODE ? Number(process.env.AEGIS_VERSION_CODE) : undefined);
  if (write && parsed.kind === 'rollback' && explicitCode === undefined) throw Error('Rollback requires a reserved installation code.');
  const versionCode = validateVersionCode(explicitCode ?? (write ? androidVersionCode(version) : config.bundle.android.versionCode));
  if (parsed.stable && versionCode !== androidVersionCode(version)) throw Error('Stable installation code must preserve the legacy migration slot.');
  if (!parsed.stable && versionCode >= androidVersionCode(parsed.base)) throw Error('Installation code reaches the reserved stable slot.');

  const cargoPath = new URL('src-tauri/Cargo.toml', root);
  const cargo = readFileSync(cargoPath, 'utf8');
  const nextCargo = cargo.replace(/^(version = )"[^"]+"/m, `$1"${version}"`);
  const cargoLockPath = new URL('src-tauri/Cargo.lock', root);
  const cargoLock = readFileSync(cargoLockPath, 'utf8');
  const nextCargoLock = cargoLock.replace(/(name = "aegis-node"\nversion = )"[^"]+"/, `$1"${version}"`);
  const npmLockPath = new URL('package-lock.json', root);
  const npmLock = JSON.parse(readFileSync(npmLockPath, 'utf8'));
  const companionRoots = ['../../packages/aegis-client/', '../../frontend/aegis-pwa/'].map(path => new URL(path, root));
  const companions = companionRoots.map(directory => {
    const manifestPath = new URL('package.json', directory), lockPath = new URL('package-lock.json', directory);
    return { manifestPath, lockPath, manifest: JSON.parse(readFileSync(manifestPath, 'utf8')), lock: JSON.parse(readFileSync(lockPath, 'utf8')) };
  });
  const linkedLocks = [npmLock, ...companions.map(({ lock }) => lock)].filter(lock => lock.packages['../../packages/aegis-client']);
  const valid = linkedLocks.every(lock => lock.packages['../../packages/aegis-client'].version === version) && config.version === '../package.json' && config.bundle.android.versionCode === versionCode &&
    cargo === nextCargo && cargoLock === nextCargoLock && npmLock.version === version && npmLock.packages[''].version === version &&
    companions.every(({ manifest, lock }) => manifest.version === version && lock.version === version && lock.packages[''].version === version);
  if (write) {
    config.version = '../package.json';
    config.bundle.android = { ...config.bundle.android, versionCode };
    npmLock.version = npmLock.packages[''].version = version;
    for (const lock of linkedLocks) lock.packages['../../packages/aegis-client'].version = version;
    writeFileSync(configPath, JSON.stringify(config, null, 2) + '\n');
    writeFileSync(cargoPath, nextCargo);
    writeFileSync(cargoLockPath, nextCargoLock);
    writeFileSync(npmLockPath, JSON.stringify(npmLock, null, 2) + '\n');
    for (const { manifestPath, lockPath, manifest, lock } of companions) {
      manifest.version = lock.version = lock.packages[''].version = version;
      writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
      writeFileSync(lockPath, JSON.stringify(lock, null, 2) + '\n');
    }
  } else if (!valid) throw new Error('Version drift: run npm run version:sync.');
  return { version, versionCode };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const index = process.argv.indexOf('--version-code');
  const result = synchronize(process.argv.includes('--write'), index < 0 ? {} : {versionCode:Number(process.argv[index+1])});
  console.log(`Aegis ${result.version}; Android versionCode ${result.versionCode}`);
}
