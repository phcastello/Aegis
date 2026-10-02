import { clientEnvironment, onAuthorizationOpened } from './clientEnvironment';

export function observeGoogleConnection(callbacks: { connected(): void; failed(code: string | null): void }): () => void {
  if (clientEnvironment().externalLinks === 'system') {
    let awaitingReturn = false;
    const stop = onAuthorizationOpened(() => { awaitingReturn = true; callbacks.connected(); });
    const returned = () => {
      if (awaitingReturn && document.visibilityState === 'visible') {
        awaitingReturn = false;
        callbacks.connected();
      }
    };
    document.addEventListener('visibilitychange', returned);
    return () => { stop(); document.removeEventListener('visibilitychange', returned); };
  }
  const channel = 'BroadcastChannel' in window ? new BroadcastChannel('aegis.email.connection') : null;
  if (channel) channel.onmessage = (event: MessageEvent) => {
    if (event.data?.status === 'connected') callbacks.connected();
    else if (event.data?.status === 'failed') callbacks.failed(event.data.code);
  };
  const url = new URL(window.location.href);
  const status = url.searchParams.get('email');
  const code = url.searchParams.get('email_error_code');
  if (status === 'connected') {
    channel?.postMessage({ status: 'connected' });
    callbacks.connected();
  } else if (code) {
    channel?.postMessage({ status: 'failed', code });
    callbacks.failed(code);
  }
  if (status || code) {
    for (const key of ['email', 'email_error_code', 'email_error_message']) url.searchParams.delete(key);
    window.history.replaceState({}, document.title, `${url.pathname}${url.search}${url.hash}`);
  }
  return () => channel?.close();
}
