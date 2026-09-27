export type NotificationFailureCode = 'permission_denied' | 'push_not_supported' | 'service_worker_unavailable' |
  'push_subscription_failed' | 'backend_push_not_configured' | 'backend_registration_failed' | 'subscription_inactive';
const failureMessages: Record<NotificationFailureCode, string> = {
  permission_denied: 'Permissão negada. Libere notificações nas configurações do navegador e tente novamente.',
  push_not_supported: 'Use Chrome com uma conexão HTTPS para ativar notificações.',
  service_worker_unavailable: 'Não foi possível preparar notificações. Reabra a Aegis e tente novamente.',
  push_subscription_failed: 'Não foi possível concluir a ativação das notificações. Tente novamente.',
  backend_push_not_configured: 'O envio de notificações ainda não está configurado no servidor da Aegis.',
  backend_registration_failed: 'Não foi possível concluir a ativação das notificações. Tente novamente.',
  subscription_inactive: 'A ativação das notificações não foi confirmada. Tente novamente.'
};
export class NotificationSetupError extends Error {
  constructor(public readonly code: NotificationFailureCode) { super(failureMessages[code]); }
}
export function notificationFailureMessage(error: unknown): string {
  return error instanceof NotificationSetupError ? error.message : 'Não foi possível concluir a ativação das notificações. Tente novamente.';
}
export interface PushConfiguration { enabled: boolean; publicKey: string | null }
export interface PushRegistration { subscriptionId: string; token: string }
export function applicationServerKey(key: string): Uint8Array<ArrayBuffer> {
  const raw = atob(key.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(key.length / 4) * 4, '='));
  return Uint8Array.from(raw, c => c.charCodeAt(0));
}
export function readPushRegistration(storage: Pick<Storage, 'getItem'>): PushRegistration | null {
  try {
    const saved = JSON.parse(storage.getItem('aegis.pushRegistration') ?? 'null');
    return saved && typeof saved.subscriptionId === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(saved.subscriptionId) &&
      typeof saved.token === 'string' && saved.token.length > 0 && saved.token.length <= 4096 ? saved : null;
  } catch { return null; }
}
// serviceWorker.ready never rejects and can wait indefinitely if registration failed.
export async function readyForPush(ready: Promise<ServiceWorkerRegistration>, timeoutMs = 10000): Promise<ServiceWorkerRegistration> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    const registration = await Promise.race([ready, new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(new NotificationSetupError('service_worker_unavailable')), timeoutMs);
    })]);
    if (!registration.active || !registration.pushManager) throw new NotificationSetupError('service_worker_unavailable');
    return registration;
  } catch { throw new NotificationSetupError('service_worker_unavailable'); }
  finally { clearTimeout(timer); }
}
interface NotificationBrowser {
  supported: boolean;
  permission(): NotificationPermission | undefined;
  requestPermission(): Promise<NotificationPermission>;
  ready(): Promise<Pick<ServiceWorkerRegistration, 'pushManager'>>;
}
interface NotificationBackend {
  configuration(): Promise<PushConfiguration>;
  register(subscription: PushSubscription): Promise<PushRegistration>;
  status(registration: PushRegistration, endpoint: string): Promise<{ active: boolean }>;
  disable(registration: PushRegistration): Promise<void>;
}
interface RegistrationStorage {
  read(): PushRegistration | null;
  save(registration: PushRegistration): void;
  clear(): void;
}
function configured(config: PushConfiguration): void {
  if (!config.enabled || !config.publicKey) throw new NotificationSetupError('backend_push_not_configured');
}
function matchesKey(subscription: PushSubscription, config: PushConfiguration): boolean {
  const existing = subscription.options.applicationServerKey;
  if (!existing || !config.publicKey) return false;
  const actual = new Uint8Array(existing), expected = applicationServerKey(config.publicKey);
  return actual.length === expected.length && actual.every((value, i) => value === expected[i]);
}
async function stage<T>(code: NotificationFailureCode, operation: () => Promise<T>): Promise<T> {
  try { return await operation(); }
  catch { throw new NotificationSetupError(code); }
}
// These operations are the UI's source of truth. Storage retains only management
// credentials; browser permission/subscription and backend status prove activation.
export function createNotificationControl(browser: NotificationBrowser, backend: NotificationBackend, storage: RegistrationStorage) {
  let config: PushConfiguration | undefined;
  let subscriptionDisabled = false;
  async function configuration(): Promise<PushConfiguration> {
    config = await stage('backend_registration_failed', () => backend.configuration());
    return config;
  }
  async function disableSaved(): Promise<void> {
    const saved = storage.read();
    if (saved) {
      await stage('backend_registration_failed', () => backend.disable(saved));
      storage.clear(); // Keep credentials when offline so the next opening can retry.
    }
  }
  async function reconcile(): Promise<boolean> {
    if (browser.permission() !== 'granted') {
      await disableSaved();
      await configuration(); // Preload config; no native permission prompt on opening.
      return false;
    }
    if (!browser.supported) throw new NotificationSetupError('push_not_supported');
    const registration = await stage('service_worker_unavailable', () => browser.ready());
    const subscription = await stage('push_subscription_failed', () => registration.pushManager.getSubscription());
    if (!subscription) {
      await disableSaved();
      await configuration();
      return false;
    }
    const current = await configuration();
    configured(current);
    if (!matchesKey(subscription, current) || subscription.expirationTime !== null && subscription.expirationTime <= Date.now()) {
      await disableSaved();
      return false;
    }
    const saved = storage.read();
    if (!saved) return false;
    const status = await stage('backend_registration_failed', () => backend.status(saved, subscription.endpoint));
    if (!status.active) {
      subscriptionDisabled = true;
      // Keep management credentials until explicit re-enrollment so a reopened PWA
      // can still identify a disabled (possibly permanently expired) endpoint.
    }
    return status.active;
  }
  async function activate(): Promise<boolean> {
    if (!browser.supported) throw new NotificationSetupError('push_not_supported');
    // Usually preloaded on opening: native permission remains in the explicit click.
    const current = config?.enabled && config.publicKey ? config : await configuration();
    configured(current);
    const permission = await stage('push_subscription_failed', () => browser.requestPermission());
    if (permission !== 'granted') {
      await disableSaved();
      throw new NotificationSetupError('permission_denied');
    }
    const registration = await stage('service_worker_unavailable', () => browser.ready());
    const subscription = await stage('push_subscription_failed', async () => {
      let existing = await registration.pushManager.getSubscription();
      if (existing && (subscriptionDisabled || !matchesKey(existing, current) || existing.expirationTime !== null && existing.expirationTime <= Date.now())) {
        if (!await existing.unsubscribe()) throw new Error('unsubscribe_failed');
        existing = null;
      }
      return existing ?? await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: applicationServerKey(current.publicKey!) });
    });
    const saved = await stage('backend_registration_failed', () => backend.register(subscription));
    // Save before status check: a failed check can be reconciled on the next opening.
    await stage('backend_registration_failed', async () => storage.save(saved));
    const status = await stage('backend_registration_failed', () => backend.status(saved, subscription.endpoint));
    const actual = await stage('push_subscription_failed', () => registration.pushManager.getSubscription());
    if (!status.active || browser.permission() !== 'granted' || !actual || actual.endpoint !== subscription.endpoint) {
      subscriptionDisabled = true;
      await stage('backend_registration_failed', () => backend.disable(saved));
      throw new NotificationSetupError('subscription_inactive');
    }
    subscriptionDisabled = false;
    return true;
  }
  async function deactivate(): Promise<void> {
    await disableSaved();
    if (browser.supported) {
      const registration = await stage('service_worker_unavailable', () => browser.ready());
      await stage('push_subscription_failed', async () => {
        const subscription = await registration.pushManager.getSubscription();
        if (subscription && !await subscription.unsubscribe()) throw new Error('unsubscribe_failed');
      });
    }
  }
  return { reconcile, activate, deactivate };
}
