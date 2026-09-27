/// <reference lib="webworker" />
import { clientsClaim } from 'workbox-core';
import { cleanupOutdatedCaches, createHandlerBoundToURL, precacheAndRoute } from 'workbox-precaching';
import { NavigationRoute, registerRoute } from 'workbox-routing';
import { Queue } from 'workbox-background-sync';
import { handleReminderClick, parseReminderPush, reminderNotificationOptions } from './services/reminderNotification';

declare const self: ServiceWorkerGlobalScope & { __WB_MANIFEST: Array<string | { url: string; revision: string | null }> };
self.skipWaiting();
clientsClaim();
cleanupOutdatedCaches();
precacheAndRoute(self.__WB_MANIFEST);
registerRoute(new NavigationRoute(createHandlerBoundToURL('/index.html'), { denylist: [/^\/api(?:\/|$)/] }));

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
const apiBase = (import.meta.env.VITE_AEGIS_API_BASE_URL ?? '').trim().replace(/\/+$/, '');
async function recordInteraction(id: string, action: 'acknowledge' | 'open', token: string): Promise<void> {
  const request = new Request(`${apiBase || self.location.origin}/api/notifications/reminders/${id}/${action}`, {
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
self.addEventListener('push', event => {
  let payload;
  try { payload = parseReminderPush(event.data?.json()); } catch { return; }
  if (payload) event.waitUntil(self.registration.showNotification('Aegis', reminderNotificationOptions(payload)));
});
self.addEventListener('notificationclick', event => {
  event.notification.close();
  const payload = parseReminderPush(event.notification.data);
  if (payload) event.waitUntil(handleReminderClick(payload, event.action, recordInteraction, openApp));
});
