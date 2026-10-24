import { compareVersions } from '../../../apps/aegis-node/scripts/version.mjs';

// Surviving immutable public installers, verified against GitHub asset metadata
// and downloaded bytes. Never substitute a different binary for a removed preview.
const baselines = Object.freeze([
  Object.freeze({ version: '0.7.0', sha256: '34504a0b8e197f24977e7f7e886196ad187a01f0acb9b0e81c23407176faeb5f' }),
  Object.freeze({ version: '0.7.1', sha256: 'f4cda9fc477258ff6c5f3bf420b0430c5ac3fbd2e76c01d145a3466657d494c3' })
]);

export function signedUpgradeBaseline(candidateVersion) {
  const candidate = candidateVersion.replace(/-stage\.([1-9]\d*)$/, '-unstable.$1');
  // Windows selection follows public SemVer; Android installation codes are
  // independent and must never be inferred to select an upgrade baseline.
  const baseline = baselines.findLast(item => compareVersions(item.version, candidate) < 0);
  if (!baseline) throw new Error('No older surviving stable signed-upgrade baseline exists for this candidate; historical preview upgrades are evidence only.');
  return { ...baseline, url: `https://github.com/phcastello/Aegis/releases/download/node-v${baseline.version}/Aegis-Windows-x86_64-Setup.exe` };
}
