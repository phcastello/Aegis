export interface ReminderPushPayload {
  type: 'reminder';
  reminderId: string;
  text: string;
  acknowledgeToken: string;
  openToken: string;
}
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function parseReminderPush(value: unknown): ReminderPushPayload | null {
  if (!value || typeof value !== 'object') return null;
  const p = value as Partial<ReminderPushPayload>;
  return p.type === 'reminder' && typeof p.reminderId === 'string' && uuid.test(p.reminderId) &&
    typeof p.text === 'string' && p.text.length > 0 && p.text.length <= 600 &&
    typeof p.acknowledgeToken === 'string' && p.acknowledgeToken.length <= 4096 &&
    typeof p.openToken === 'string' && p.openToken.length <= 4096 ? p as ReminderPushPayload : null;
}
export function reminderNotificationOptions(payload: ReminderPushPayload) {
  return {
    body: payload.text, badge: '/icons/notification-badge.png',
    tag: `aegis-reminder-${payload.reminderId}`, renotify: false,
    actions: [{ action: 'acknowledge', title: 'OK' }], data: payload
  };
}
export async function handleReminderClick(
  payload: ReminderPushPayload,
  action: string,
  post: (id: string, action: 'acknowledge' | 'open', token: string) => Promise<void>,
  openApp: () => Promise<void>
): Promise<void> {
  if (action === 'acknowledge') {
    await post(payload.reminderId, 'acknowledge', payload.acknowledgeToken);
    return;
  }
  if (action !== '') return;
  // Body click should focus the app even if recording the interaction fails.
  await Promise.allSettled([post(payload.reminderId, 'open', payload.openToken), openApp()]);
}
