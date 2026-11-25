const repository = 'https://github.com/phcastello/Aegis';
export const feed = 'https://raw.githubusercontent.com/phcastello/Aegis/aegis-distribution/latest.json';
const versionPattern = /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-unstable\.[1-9]\d*(?:\.rollback\.[1-9]\d*)?|-rollback\.[1-9]\d*)?$/;
const sha256 = /^[a-f0-9]{64}$/;

// Only link to the exact official artifacts. A failed/malformed feed never
// replaces the visible GitHub release-list fallbacks.
export function recommendedDownloads(value) {
  if (value?.schema !== 2 || typeof value.version !== 'string' || !versionPattern.test(value.version)) return null;
  const base = `${repository}/releases/download/node-v${value.version}/`;
  const windows = value.platforms?.['windows-x86_64'];
  const android = value.android?.aarch64;
  if (windows?.url !== base + 'Aegis-Windows-x86_64-Setup.exe' || !sha256.test(windows.sha256 ?? '') ||
      android?.url !== base + 'Aegis-Android-arm64.apk' || !sha256.test(android.sha256 ?? '')) return null;
  return { version: value.version, windows: windows.url, android: android.url };
}
export function oauthMessage(search) {
  switch (new URLSearchParams(search).get('email')) {
    case 'connected': return 'Conta Google conectada. Você já pode voltar à Aegis no aplicativo.';
    case 'connect_failed': return 'Não foi possível conectar a conta Google. Volte à Aegis no aplicativo e tente novamente.';
    default: return null;
  }
}
if (typeof document !== 'undefined') {
  const message = oauthMessage(location.search);
  if (message) {
    const notice = document.getElementById('oauth');
    notice.textContent = message;
    notice.hidden = false;
  }
  // Never register a new worker. Ask only an existing legacy registration
  // to refresh; an old cached shell will retire with the browser's update.
  navigator.serviceWorker?.getRegistration().then(registration => registration?.update()).catch(() => {});
  fetch(feed, { cache: 'no-store', credentials: 'omit', referrerPolicy: 'no-referrer', signal: AbortSignal.timeout(8000) })
    .then(response => response.ok ? response.json() : null)
    .then(recommendedDownloads)
    .then(downloads => {
      if (!downloads) return;
      for (const platform of ['windows', 'android']) {
        const link = document.getElementById(`${platform}-download`);
        link.href = downloads[platform];
        link.querySelector('span').textContent = platform === 'windows' ? 'Baixar para Windows' : 'Baixar para Android';
      }
      document.getElementById('release-label').textContent = `Última versão · v${downloads.version}`;
    }).catch(() => {});
}
