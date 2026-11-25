/// <reference lib="webworker" />
import { Queue } from 'workbox-background-sync';
import { handleReminderClick, parseReminderPush } from './services/reminderNotification';

declare const self: ServiceWorkerGlobalScope;
self.addEventListener('install', event => event.waitUntil(self.skipWaiting()));
self.addEventListener('activate', event => {
  // Retire only the previous Aegis shell. Do not clear user data or other caches.
  event.waitUntil((async () => {
    await caches.delete(`workbox-precache-v2-${self.registration.scope}`);
    await self.clients.claim();
  })());
});

// Only explicit human interactions are queued. No reminder generation or snooze.
const interactionQueue = new Queue('aegis-reminder-interactions-v1', {
  maxRetentionTime: 30 * 24 * 60,
  onSync: async ({ queue }) => {
    let entry;
    while ((entry = await queue.shiftRequest())) {
      try {
        const response = await fetch(entry.request.clone(), { signal: AbortSignal.timeout(10000) });
        if (response.status >= 500 || response.status === 429) throw new Error('retry');
        // Invalid/expired tokens (4xx) are terminal, never replayed indefinitely.
      } catch {
        await queue.unshiftRequest(entry);
        throw new Error('interaction_pending');
      }
    }
  }
});
async function recordInteraction(id: string, action: 'acknowledge' | 'open', token: string): Promise<void> {
  const request = new Request(`${self.location.origin}/api/notifications/reminders/${id}/${action}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token })
  });
  try {
    const response = await fetch(request.clone(), { signal: AbortSignal.timeout(10000) });
    if (response.status >= 500 || response.status === 429) throw new Error('retry');
  } catch {
    await interactionQueue.pushRequest({ request });
  }
}
async function openApp(): Promise<void> {
  const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
  const existing = windows.find(client => new URL(client.url).origin === self.location.origin && !new URL(client.url).pathname.startsWith('/api/'));
  if (existing) { await existing.focus(); return; }
  await self.clients.openWindow('/');
}
// Existing displayed notifications and this named queue outlive a worker update.
// New Web Push delivery is retired; preserve only explicit legacy click completion.
self.addEventListener('notificationclick', event => {
  event.notification.close();
  const payload = parseReminderPush(event.notification.data);
  if (payload) event.waitUntil(handleReminderClick(payload, event.action, recordInteraction, openApp));
});
