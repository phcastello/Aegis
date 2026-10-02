<script setup lang="ts">
import { ref } from 'vue';
import ChatView from '@aegis/client/views/ChatView.vue';
import NotificationControl from './components/NotificationControl.vue';
const notifications = ref<InstanceType<typeof NotificationControl> | null>(null);
</script>

<template>
  <ChatView @notification-required="notifications?.offerSetup()">
    <template #notifications><NotificationControl ref="notifications" /></template>
  </ChatView>
</template>
