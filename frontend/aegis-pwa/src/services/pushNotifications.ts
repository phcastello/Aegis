export class NotificationSetupError extends Error {}
export function notificationFailureMessage(error: unknown): string {
  if (error instanceof NotificationSetupError) return error.message;
  if (error instanceof Error && error.name === 'NotAllowedError')
    return 'Permissão negada. Libere notificações nas configurações do navegador e tente novamente.';
  return 'Não foi possível atualizar notificações. Tente novamente.';
}
export interface PushConfiguration { enabled: boolean; publicKey: string | null }
export interface PushRegistration { subscriptionId: string; token: string }
export function applicationServerKey(key: string): Uint8Array<ArrayBuffer> {
  const raw = atob(key.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(key.length / 4) * 4, '='));
  return Uint8Array.from(raw, c => c.charCodeAt(0));
}
export async function enrollNotifications(
  config: PushConfiguration,
  permission: () => Promise<NotificationPermission>,
  manager: Pick<PushManager, 'getSubscription' | 'subscribe'>,
  register: (subscription: PushSubscription) => Promise<void>
): Promise<void> {
  if (!config.enabled || !config.publicKey) throw new NotificationSetupError('As notificações ainda não estão configuradas na Aegis.');
  if (await permission() !== 'granted') throw new NotificationSetupError('Permissão negada. Libere notificações nas configurações do navegador e tente novamente.');
  let subscription = await manager.getSubscription();
  const key = applicationServerKey(config.publicKey);
  if (subscription?.options.applicationServerKey && !new Uint8Array(subscription.options.applicationServerKey).every((b, i) => b === key[i])) {
    await subscription.unsubscribe();
    subscription = null;
  }
  subscription ??= await manager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
  await register(subscription);
}
