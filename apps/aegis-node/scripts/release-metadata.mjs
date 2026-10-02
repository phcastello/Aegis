import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { androidVersionCode, synchronize } from './version.mjs';

export function metadata(version, signature, date) {
  const versionCode = androidVersionCode(version);
  if (!signature.trim()) throw new Error('Missing Windows updater signature.');
  if (Number.isNaN(Date.parse(date))) throw new Error('Invalid release date.');
  const base = `https://github.com/phcastello/Aegis/releases/download/node-v${version}/`;
  return {
    version, notes: `Aegis ${version}`, pub_date: new Date(date).toISOString(),
    platforms: {
      'windows-x86_64': { url: base + 'Aegis-Windows-x86_64-Setup.exe', signature: signature.trim() },
      // Additional platform consumed only by our Android notification/link adapter.
      'android-aarch64': { url: base + 'Aegis-Android-arm64.apk', versionCode }
    }
  };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const directory = resolve(process.argv[2]);
  const { version } = synchronize();
  if (process.argv[3] !== `node-v${version}`) throw new Error('Release tag does not match package.json.');
  const signature = readFileSync(resolve(directory, 'Aegis-Windows-x86_64-Setup.exe.sig'), 'utf8');
  const result = metadata(version, signature, process.env.RELEASE_DATE);
  writeFileSync(resolve(directory, 'latest.json'), JSON.stringify(result, null, 2) + '\n');
}
