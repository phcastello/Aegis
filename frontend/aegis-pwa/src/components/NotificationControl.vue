<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { enrollNotifications, notificationFailureMessage, NotificationSetupError } from '../services/pushNotifications';
import { getPushConfiguration, registerPushSubscription, disablePushSubscription, getPushSubscriptionStatus } from '../services/aegisApi';

const supported = window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
const active = ref(false);
const busy = ref(false);
let subscriptionDisabled = false;
const message = ref('');
const storageKey = 'aegis.pushRegistration';
const deviceKey = 'aegis.pushDeviceId';
function deviceId(): string {
  const id = localStorage.getItem(deviceKey) ?? crypto.randomUUID();
  localStorage.setItem(deviceKey, id);
  return id;
}
async function register(subscription: PushSubscription): Promise<void> {
  const json = subscription.toJSON();
  if (!json.endpoint || !json.keys?.p256dh || !json.keys.auth) throw new NotificationSetupError('Não foi possível registrar notificações neste dispositivo.');
  const result = await registerPushSubscription({ deviceId: deviceId(), endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth, userAgent: navigator.userAgent.slice(0, 500) });
  localStorage.setItem(storageKey, JSON.stringify(result));
}
async function toggle(): Promise<void> {
  if (busy.value) return;
  if (!supported) { message.value = 'Use uma conexão HTTPS para ativar notificações.'; return; }
  busy.value = true;
  message.value = '';
  try {
    if (active.value) {
      const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null');
      if (saved) await disablePushSubscription(saved.subscriptionId, saved.token);
      const registration = await navigator.serviceWorker.ready;
      await (await registration.pushManager.getSubscription())?.unsubscribe();
      localStorage.removeItem(storageKey);
      active.value = false;
      message.value = 'Notificações desativadas neste dispositivo.';
    } else {
      // Native permission is requested during this explicit click, before network awaits.
      const permission = Notification.requestPermission();
      const config = await getPushConfiguration();
      const registration = await navigator.serviceWorker.ready;
      if (subscriptionDisabled) await (await registration.pushManager.getSubscription())?.unsubscribe();
      await enrollNotifications(config, () => permission, registration.pushManager, register);
      subscriptionDisabled = false;
      active.value = true;
      message.value = 'Notificações ativadas. Você já pode pedir um lembrete pelo chat.';
    }
  } catch (e) {
    message.value = notificationFailureMessage(e);
  } finally { busy.value = false; }
}
onMounted(async () => {
  // Inspection only: loading the application never requests notification permission.
  if (!supported || Notification.permission !== 'granted') return;
  try {
    const registration = await navigator.serviceWorker.ready;
    if (!await registration.pushManager.getSubscription()) return;
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null');
    if (saved) {
      active.value = (await getPushSubscriptionStatus(saved.subscriptionId, saved.token)).active;
      subscriptionDisabled = !active.value;
    }
  } catch { active.value = false; }
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
