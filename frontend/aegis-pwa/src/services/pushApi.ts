import { requestJson } from '@aegis/client/services/aegisApi';

export function getPushConfiguration(): Promise<import('./pushNotifications').PushConfiguration> {
  return requestJson('/api/notifications/configuration', { cache: 'no-store', signal: AbortSignal.timeout(10000) });
}
export function registerPushSubscription(request: { deviceId: string; endpoint: string; p256dh: string; auth: string; userAgent: string }): Promise<import('./pushNotifications').PushRegistration> {
  return requestJson('/api/notifications/subscriptions', { method: 'POST', signal: AbortSignal.timeout(10000), body: JSON.stringify(request) });
}
export function disablePushSubscription(id: string, token: string): Promise<void> {
  return requestJson(`/api/notifications/subscriptions/${encodeURIComponent(id)}/disable`, { method: 'POST', signal: AbortSignal.timeout(10000), body: JSON.stringify({ token }) });
}
export function getPushSubscriptionStatus(id: string, token: string, endpoint: string): Promise<{ active: boolean }> {
  return requestJson(`/api/notifications/subscriptions/${encodeURIComponent(id)}/status`, { method: 'POST', signal: AbortSignal.timeout(10000), cache: 'no-store', body: JSON.stringify({ token, endpoint }) });
}
