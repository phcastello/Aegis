import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { packageArtifacts } from './package-artifacts.mjs';

test('packaging retains signed Windows payload/signature and clear Android filename', () => {
  const root = mkdtempSync(join(tmpdir(), 'aegis-packaging-'));
  try {
    const windows = join(root, 'src-tauri/target/x86_64-pc-windows-msvc/release/bundle/nsis');
    const android = join(root, 'src-tauri/gen/android/app/build/outputs/apk/universal/release');
    mkdirSync(windows, { recursive: true }); mkdirSync(android, { recursive: true });
    writeFileSync(join(windows, 'Aegis_0.7.0_x64-setup.exe'), 'signed payload');
    writeFileSync(join(windows, 'Aegis_0.7.0_x64-setup.exe.sig'), 'signature');
    writeFileSync(join(android, 'app-universal-release.apk'), 'apk');
    packageArtifacts('windows', { root, signed: true }); packageArtifacts('android', { root });
    assert.equal(readFileSync(join(root, 'release-assets/Aegis-Windows-x86_64-Setup.exe'), 'utf8'), 'signed payload');
    assert.equal(readFileSync(join(root, 'release-assets/Aegis-Windows-x86_64-Setup.exe.sig'), 'utf8'), 'signature');
    assert.equal(readFileSync(join(root, 'release-assets/Aegis-Android-arm64.apk'), 'utf8'), 'apk');
    writeFileSync(join(windows, 'ambiguous.exe'), 'other');
    assert.throws(() => packageArtifacts('windows', { root }), /exactly one/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
