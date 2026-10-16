import { cpSync, mkdirSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export function packageArtifacts(target, { root = '.', output = 'release-assets', signed = false, debug = false } = {}) {
  const directory = resolve(root, output);
  mkdirSync(directory, { recursive: true });
  if (target === 'windows') {
    const source = resolve(root, 'src-tauri/target/x86_64-pc-windows-msvc/release/bundle/nsis');
    const installers = readdirSync(source).filter(name => name.endsWith('.exe'));
    if (installers.length !== 1) throw new Error('Expected exactly one Windows NSIS installer.');
    cpSync(resolve(source, installers[0]), resolve(directory, 'Aegis-Windows-x86_64-Setup.exe'));
    if (signed) cpSync(resolve(source, installers[0] + '.sig'), resolve(directory, 'Aegis-Windows-x86_64-Setup.exe.sig'));
  } else if (target === 'android') {
    const variant = debug ? 'debug' : 'release';
    cpSync(resolve(root, `src-tauri/gen/android/app/build/outputs/apk/universal/${variant}/app-universal-${variant}.apk`), resolve(directory, debug ? 'Aegis-Android-arm64-Debug.apk' : 'Aegis-Android-arm64.apk'));
  } else throw new Error('Expected windows or android.');
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [target, output] = process.argv.slice(2).filter(value => !value.startsWith('--'));
  packageArtifacts(target, { output, signed: process.argv.includes('--signed'), debug: process.argv.includes('--debug') });
}
