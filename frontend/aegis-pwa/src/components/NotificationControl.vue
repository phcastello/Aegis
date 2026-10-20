<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { createNotificationControl, readyForPush, readPushRegistration, notificationFailureMessage, NotificationSetupError } from '../services/pushNotifications';
import { getPushConfiguration, registerPushSubscription, disablePushSubscription, getPushSubscriptionStatus } from '../services/pushApi';

const active = ref(false);
const busy = ref(true);
const message = ref('');
const needsPermission = ref(false);
const retryAvailable = ref(false);
let feedbackTimer: ReturnType<typeof setTimeout> | undefined;
const controller = createNotificationControl({
  supported: window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window,
  permission: () => 'Notification' in window ? Notification.permission : undefined,
  requestPermission: () => Notification.requestPermission(),
  ready: () => readyForPush(navigator.serviceWorker.ready)
}, {
  configuration: getPushConfiguration,
  register: async subscription => {
    const json = subscription.toJSON();
    if (!json.endpoint || !json.keys?.p256dh || !json.keys.auth) throw new NotificationSetupError('push_subscription_failed');
    const deviceId = localStorage.getItem('aegis.pushDeviceId') ?? crypto.randomUUID();
    localStorage.setItem('aegis.pushDeviceId', deviceId);
    return registerPushSubscription({ deviceId, endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth, userAgent: navigator.userAgent.slice(0, 500) });
  },
  status: (saved, endpoint) => getPushSubscriptionStatus(saved.subscriptionId, saved.token, endpoint),
  disable: saved => disablePushSubscription(saved.subscriptionId, saved.token)
}, {
  read: () => readPushRegistration(localStorage),
  save: saved => localStorage.setItem('aegis.pushRegistration', JSON.stringify(saved)),
  clear: () => localStorage.removeItem('aegis.pushRegistration')
});
function reportFailure(error: unknown): void {
  active.value = false;
  // Stage only: never log browser errors, endpoints, keys or management credentials.
  console.warn('Aegis notifications:', error instanceof NotificationSetupError ? error.code : 'backend_registration_failed');
  feedback(notificationFailureMessage(error, 'brave' in navigator), 12000);
  retryAvailable.value = !(error instanceof NotificationSetupError) || ![
    'permission_denied', 'push_not_supported', 'backend_push_not_configured'
  ].includes(error.code);
}
function dismiss(): void {
  clearTimeout(feedbackTimer);
  message.value = '';
  needsPermission.value = false;
  retryAvailable.value = false;
}
function feedback(text: string, timeout: number): void {
  dismiss();
  message.value = text;
  feedbackTimer = setTimeout(dismiss, timeout);
}
async function activate(): Promise<void> {
  if (busy.value) return;
  busy.value = true;
  dismiss();
  try {
    active.value = await controller.activate();
    feedback('Notificações ativadas. Você já pode pedir um lembrete pelo chat.', 5000);
  } catch (error) { reportFailure(error); }
  finally { busy.value = false; }
}
function offerSetup(): void {
  if (active.value || busy.value) return;
  dismiss();
  if ('Notification' in window && Notification.permission === 'granted') { void activate(); return; }
  if ('Notification' in window && Notification.permission === 'denied') {
    reportFailure(new NotificationSetupError('permission_denied'));
    return;
  }
  needsPermission.value = true;
}
function keydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') dismiss();
}
onMounted(async () => {
  document.addEventListener('keydown', keydown);
  try {
    active.value = await controller.initialize();
    needsPermission.value = !active.value && 'Notification' in window && Notification.permission === 'default';
  }
  catch (error) { reportFailure(error); }
  finally { busy.value = false; }
});
onBeforeUnmount(() => {
  clearTimeout(feedbackTimer);
  document.removeEventListener('keydown', keydown);
});
defineExpose({ offerSetup });
</script>
<template>
  <div v-if="needsPermission || message" class="notification-notice">
    <p role="status">{{ message || 'Permita notificações para receber lembretes com a Aegis fechada.' }}</p>
    <button v-if="needsPermission || retryAvailable" type="button" class="notification-notice__action" :disabled="busy" @click="activate">{{ needsPermission ? 'Permitir' : 'Tentar novamente' }}</button>
    <button type="button" class="notification-notice__close" aria-label="Fechar aviso de notificações" @click="dismiss"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M18 6 6 18" /></svg></button>
  </div>
</template>
