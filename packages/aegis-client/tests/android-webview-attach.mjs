// The installed WebView identity assertions must never be retried. Only
// discovery of the real Playwright webview on a still-live process may repeat.
export function isWebViewAttachTimeout(error) {
  return error?.name === 'TimeoutError' &&
    /androidDevice\.webView: Timeout \d+ms exceeded while waiting for event "webview"/.test(error.message ?? '');
}

export async function attachAndroidReleaseWebView({
  launch, openDevice, processId, socketCount = () => null, warn = console.warn
}) {
  for (let attempt = 0; attempt < 2; attempt++) {
    await launch();
    const pid = await processId();
    if (!Number.isSafeInteger(pid) || pid <= 0) throw Error('Android app process was not alive after Activity launch.');
    const device = await openDevice();
    let webview;
    try {
      webview = await device.webView({ pkg: 'com.aegis.node' }, { timeout: 45000 });
    } catch (error) {
      const timeout = isWebViewAttachTimeout(error);
      const stillAlive = timeout && (await processId()) === pid;
      if (timeout) {
        // Log only the presence of the same process and count of its own
        // DevTools sockets; never log PID, raw adb output, UI, or credentials.
        const count = stillAlive ? await socketCount(pid) : null;
        warn(`Android WebView attach timeout attempt=${attempt+1} sameProcessAlive=${stillAlive} devtoolsSockets=${Number.isSafeInteger(count) && count >= 0 ? count : 'unavailable'}`);
      }
      try {
        await device.close();
      } catch {
        // A failed disconnect cannot justify another attempt. Keep the
        // original discovery failure rather than masking it with cleanup.
        throw error;
      }
      // A crash/restart or a non-attach failure is not transient. A second
      // timeout is terminal. No page (and no acceptance assertion) ran yet.
      if (!timeout || !stillAlive || attempt === 1) throw error;
      continue;
    }
    if ((await processId()) !== pid) {
      await device.close().catch(() => {});
      throw Error('Android app process changed before WebView identity assertions.');
    }
    return { device, webview };
  }
  throw Error('Android WebView attach attempts exhausted.');
}
