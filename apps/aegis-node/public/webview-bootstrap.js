// Keep this script ES5-compatible; Android system WebView can predate the
// production bundle's Vite 7 Chromium 107 target by years.
(function () {
  var userAgent = navigator.userAgent;
  var chromium = /(?:Chrome|Chromium)\/([0-9]+)/.exec(userAgent);
  if (!/Android/i.test(userAgent) || !chromium || Number(chromium[1]) >= 107) return;
  var root = document.getElementById('app');
  var notice = document.getElementById('aegis-webview-unsupported');
  if (root && notice) {
    root.setAttribute('data-aegis-webview-unsupported', 'true');
    notice.removeAttribute('hidden');
  }
}());
