import { clientEnvironment, onAuthorizationOpened } from './clientEnvironment';

export function observeGoogleConnection(callbacks: { started?(): void; connected(): void }): () => void {
  if (clientEnvironment().externalLinks === 'system') {
    let awaitingReturn = false;
    const stop = onAuthorizationOpened(() => { awaitingReturn = true; callbacks.started?.(); });
    const returned = () => {
      if (awaitingReturn && document.visibilityState === 'visible') {
        awaitingReturn = false;
        callbacks.connected();
      }
    };
    document.addEventListener('visibilitychange', returned);
    window.addEventListener('focus', returned);
    return () => {
      stop(); document.removeEventListener('visibilitychange', returned); window.removeEventListener('focus', returned);
    };
  }
  // The system browser now displays the static gateway. Only the native
  // application confirms the account using its existing authenticated API.
  return () => {};
}
