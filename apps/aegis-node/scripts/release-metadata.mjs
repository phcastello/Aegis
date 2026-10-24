import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { androidVersionCode, parseDistributionVersion, compareVersions, rollbackVersion, validateVersionCode, synchronize } from './version.mjs';

export function metadata(version, signature, date, distribution) {
  const versionCode = distribution ? validateVersionCode(distribution.versionCode) : androidVersionCode(version);
  if (!signature.trim()) throw new Error('Missing Windows updater signature.');
  const decoded = Buffer.from(signature.trim(), 'base64').toString('utf8');
  const trusted = decoded.split('\n').find(line => line.startsWith('trusted comment: '));
  const signedVersion = trusted?.split('\t').find(field => field.startsWith('version:'))?.slice(8);
  if (signedVersion !== version) throw new Error('Updater signature must bind this exact version; use the current Tauri CLI.');
  // The official updater performs cryptographic verification against the committed public key.

  if (Number.isNaN(Date.parse(date))) throw new Error('Invalid release date.');
  const base = `https://github.com/phcastello/Aegis/releases/download/node-v${version}/`;
  const result = {
    version, notes: version === '0.7.0'
      ? `Aegis v0.7.0 — It's Not Stalking. It's a Feature.\nhttps://github.com/phcastello/Aegis/releases/tag/v0.7.0`
      : `Aegis ${version}`, pub_date: new Date(date).toISOString(),
    platforms: {
      'windows-x86_64': { url: base + 'Aegis-Windows-x86_64-Setup.exe', signature: signature.trim() }
    },
    // Custom Android link metadata is outside platforms: desktop updater requires signatures
    // on every platforms entry and does not support Android. Android verifies APK signing.
    android: {
      aarch64: { url: base + 'Aegis-Android-arm64.apk', versionCode }
    }
  };
  if (distribution) {
    const parsed = parseDistributionVersion(version);
    const {sourceCommit, sourceVersion, kind, rollbackOf, restoresVersion, compatibility, sha256} = distribution;
    if (!/^[a-f0-9]{40}$/.test(sourceCommit ?? '')) throw Error('Distribution requires the exact source commit.');
    parseDistributionVersion(sourceVersion);
    if (kind !== parsed.kind) throw Error('Distribution kind differs from its version.');
    if (JSON.stringify(compatibility) !== JSON.stringify({api:1,nodeData:1})) throw Error('Unknown API or Node persistence compatibility contract.');
    if (kind === 'rollback') {
      if (rollbackVersion(rollbackOf, parsed.rollback) !== version || compareVersions(restoresVersion, rollbackOf) >= 0)
        throw Error('Rollback event must identify an earlier functional distribution and advance the defective distribution.');
    } else if (rollbackOf !== undefined || restoresVersion !== undefined) throw Error('Normal distributions cannot claim rollback provenance.');
    for (const name of ['Aegis-Windows-x86_64-Setup.exe','Aegis-Android-arm64.apk'])
      if (!/^[a-f0-9]{64}$/.test(sha256?.[name] ?? '')) throw Error('Missing distribution artifact SHA-256.');
    Object.assign(result, {schema:2, versionCode, kind, sourceCommit, sourceVersion,
      publishedAt:result.pub_date, compatibility,
      ...(kind === 'rollback' ? {rollbackOf,restoresVersion} : {})});
    result.platforms['windows-x86_64'].sha256 = sha256['Aegis-Windows-x86_64-Setup.exe'];
    result.android.aarch64.sha256 = sha256['Aegis-Android-arm64.apk'];
    if (kind === 'rollback') result.notes = `Distribuição de recuperação ${version}: restaura ${restoresVersion} após ${rollbackOf}.`;
  }
  return result;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const directory = resolve(process.argv[2]);
  const { version } = synchronize();
  if (process.argv[3] !== `node-v${version}`) throw new Error('Release tag does not match package.json.');
  const signature = readFileSync(resolve(directory, 'Aegis-Windows-x86_64-Setup.exe.sig'), 'utf8');
  const result = metadata(version, signature, process.env.RELEASE_DATE);
  writeFileSync(resolve(directory, 'latest.json'), JSON.stringify(result, null, 2) + '\n');
}
