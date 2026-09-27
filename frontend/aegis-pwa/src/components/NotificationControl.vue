<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { createNotificationControl, readyForPush, readPushRegistration, notificationFailureMessage, NotificationSetupError } from '../services/pushNotifications';
import { getPushConfiguration, registerPushSubscription, disablePushSubscription, getPushSubscriptionStatus } from '../services/aegisApi';

const active = ref(false);
const busy = ref(true);
const message = ref('');
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
  message.value = notificationFailureMessage(error);
}
async function toggle(): Promise<void> {
  if (busy.value) return;
  busy.value = true;
  message.value = '';
  try {
    if (active.value) {
      await controller.deactivate();
      active.value = false;
      message.value = 'Notificações desativadas neste dispositivo.';
    } else {
      active.value = await controller.activate();
      message.value = 'Notificações ativadas. Você já pode pedir um lembrete pelo chat.';
    }
  } catch (error) { reportFailure(error); }
  finally { busy.value = false; }
}
onMounted(async () => {
  try { active.value = await controller.reconcile(); }
  catch (error) { reportFailure(error); }
  finally { busy.value = false; }
});
</script>
<template>
  <div class="notification-control">
    <button type="button" class="notification-toggle" :disabled="busy" :aria-pressed="active" :aria-label="active ? 'Desativar notificações neste dispositivo' : 'Ativar notificações'" :title="active ? 'Notificações ativadas' : 'Ativar notificações'" @click="toggle">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4" /></svg>
      <span>{{ busy ? 'Aguarde…' : active ? 'Notificações ativadas' : 'Ativar notificações' }}</span>
    </button>
    <p v-if="message" role="status">{{ message }}</p>
  </div>
</template>
