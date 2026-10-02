import { cpSync, mkdirSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';

const target = process.argv[2];
const output = resolve(process.argv[3] ?? 'release-assets');
mkdirSync(output, { recursive: true });
if (target === 'windows') {
  const directory = 'src-tauri/target/x86_64-pc-windows-msvc/release/bundle/nsis';
  const installers = readdirSync(directory).filter(name => name.endsWith('.exe'));
  if (installers.length !== 1) throw new Error('Expected exactly one Windows NSIS installer.');
  cpSync(resolve(directory, installers[0]), resolve(output, 'Aegis-Windows-x86_64-Setup.exe'));
  if (process.argv.includes('--signed')) cpSync(resolve(directory, installers[0] + '.sig'), resolve(output, 'Aegis-Windows-x86_64-Setup.exe.sig'));
} else if (target === 'android') {
  cpSync('src-tauri/gen/android/app/build/outputs/apk/universal/release/app-universal-release.apk', resolve(output, 'Aegis-Android-arm64.apk'));
} else throw new Error('Expected windows or android.');
