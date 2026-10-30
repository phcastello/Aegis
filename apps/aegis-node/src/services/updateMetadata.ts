export interface DistributionDetails {
  version: string;
  versionCode: number;
  distributionKind: 'normal' | 'rollback';
  sourceCommit: string;
  sourceVersion: string;
  publishedAt: string;
  rollbackOf?: string;
  restoresVersion?: string;
}

const numeric = '(0|[1-9]\\d*)';
const positive = '[1-9]\\d*';
const distributionVersion = new RegExp(`^${numeric}\\.${numeric}\\.${numeric}(?:-(unstable\\.${positive}(?:\\.rollback\\.${positive})?|rollback\\.${positive}))?$`);
const historicalVersion = new RegExp(`^${numeric}\\.${numeric}\\.${numeric}(?:-(unstable\\.${positive}(?:\\.rollback\\.${positive})?|rollback\\.${positive}|stage\\.${positive}))?$`);
const hash = /^[a-f0-9]{64}$/;

function invalid(): never { throw new Error('update_metadata_invalid'); }
function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return invalid();
  return value as Record<string, unknown>;
}
function parseVersion(version: unknown, historical = false): string[] {
  if (typeof version !== 'string') return invalid();
  const match = (historical ? historicalVersion : distributionVersion).exec(version);
  if (!match) return invalid();
  const values = match.slice(1, 4);
  if (Number(values[0]) > 20 || Number(values[1]) > 99 || Number(values[2]) > 999) return invalid();
  if (match[4]) values.push(...match[4].split('.'));
  if (values.some(value => /^\d+$/.test(value) && (!Number.isSafeInteger(Number(value)) || Number(value) > 4294967295))) return invalid();
  if (values.slice(3).some(value => /^\d+$/.test(value) && Number(value) > 65535)) return invalid();
  return values;
}

// These are the same restricted SemVer forms accepted by the native comparator.
export function compareUpdateVersions(left: string, right: string): number {
  const a = parseVersion(left, true), b = parseVersion(right, true);
  for (let index = 0; index < 3; index++) {
    if (Number(a[index]) !== Number(b[index])) return Number(a[index]) < Number(b[index]) ? -1 : 1;
  }
  if (a.length === 3 || b.length === 3) return a.length === b.length ? 0 : a.length === 3 ? 1 : -1;
  for (let index = 3; index < Math.max(a.length, b.length); index++) {
    const x = a[index], y = b[index];
    if (x === y) continue;
    if (x === undefined || y === undefined) return x === undefined ? -1 : 1;
    const xn = /^\d+$/.test(x), yn = /^\d+$/.test(y);
    if (xn && yn) return Number(x) < Number(y) ? -1 : 1;
    if (xn !== yn) return xn ? -1 : 1;
    return x < y ? -1 : 1;
  }
  return 0;
}

function isNextRollback(version: string, previous: string): boolean {
  const parts = parseVersion(previous), next = parseVersion(version);
  const prefix = parts.slice(0, 3).join('.');
  if (parts.length === 3) {
    return next.slice(0, 3).join('.') === `${parts[0]}.${parts[1]}.${Number(parts[2]) + 1}` &&
      next[3] === 'rollback' && Number(next[4]) > 0;
  }
  if (next.slice(0, 3).join('.') !== prefix) return false;
  if (parts[3] === 'unstable') {
    return next[3] === 'unstable' && next[4] === parts[4] && next[5] === 'rollback' &&
      Number(next[6]) > (parts.length === 5 ? 0 : Number(parts[6]));
  }
  return next[3] === 'rollback' && Number(next[4]) > Number(parts[4]);
}

// The publisher emits immutable versioned assets. Validate the complete shared
// contract before exposing a native updater handle to the installation controls.
export function validateWindowsUpdate(metadata: Record<string, unknown>, version: string,
  current: { version: string; versionCode?: number }): DistributionDetails {
  parseVersion(version);
  if (metadata.schema !== 2 || metadata.version !== version || !Number.isInteger(metadata.versionCode) ||
      Number(metadata.versionCode) < 1 || Number(metadata.versionCode) > 2100000000 ||
      !Number.isInteger(current.versionCode) || Number(current.versionCode) < 1 || Number(metadata.versionCode) <= Number(current.versionCode) ||
      compareUpdateVersions(version, current.version) <= 0) return invalid();
  if (metadata.kind !== 'normal' && metadata.kind !== 'rollback') return invalid();
  if (typeof metadata.sourceCommit !== 'string' || !/^[a-f0-9]{40}$/.test(metadata.sourceCommit)) return invalid();
  parseVersion(metadata.sourceVersion, true);
  if (typeof metadata.publishedAt !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(metadata.publishedAt) || Number.isNaN(Date.parse(metadata.publishedAt)) ||
      metadata.pub_date !== metadata.publishedAt) return invalid();
  const compatibility = record(metadata.compatibility);
  if (compatibility.api !== 1 || compatibility.nodeData !== 1) return invalid();
  const recovery = /\.rollback\.|-rollback\./.test(version);
  if ((metadata.kind === 'rollback') !== recovery) return invalid();
  if (metadata.kind === 'rollback') {
    parseVersion(metadata.rollbackOf);
    parseVersion(metadata.restoresVersion);
    if (!isNextRollback(version, String(metadata.rollbackOf)) ||
        compareUpdateVersions(String(metadata.restoresVersion), String(metadata.rollbackOf)) >= 0) return invalid();
  } else if (metadata.rollbackOf !== undefined || metadata.restoresVersion !== undefined) return invalid();
  const platforms = record(metadata.platforms);
  const windows = record(platforms['windows-x86_64']);
  const android = record(record(metadata.android).aarch64);
  const base = `https://github.com/phcastello/Aegis/releases/download/node-v${version}/`;
  if (windows.url !== `${base}Aegis-Windows-x86_64-Setup.exe` ||
      android.url !== `${base}Aegis-Android-arm64.apk` || android.versionCode !== metadata.versionCode ||
      typeof windows.sha256 !== 'string' || !hash.test(windows.sha256) ||
      typeof android.sha256 !== 'string' || !hash.test(android.sha256) ||
      typeof windows.signature !== 'string' || !windows.signature.trim()) throw new Error('update_asset_mismatch');
  return {
    version, versionCode: Number(metadata.versionCode), distributionKind: metadata.kind,
    sourceCommit: metadata.sourceCommit, sourceVersion: String(metadata.sourceVersion), publishedAt: metadata.publishedAt,
    ...(metadata.kind === 'rollback' ? { rollbackOf: String(metadata.rollbackOf), restoresVersion: String(metadata.restoresVersion) } : {})
  };
}
