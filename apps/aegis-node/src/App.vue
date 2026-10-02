<script setup lang="ts">
import { ref } from 'vue';
import ChatView from '@aegis/client/views/ChatView.vue';
import UpdateNotice from './components/UpdateNotice.vue';
import DiagnosticsPanel from './components/DiagnosticsPanel.vue';
import type { RuntimeInfo } from './services/runtime';
defineProps<{ runtime: RuntimeInfo }>();
const aboutOpen = ref(false);
</script>

<template>
  <ChatView>
    <template #client-info><button class="native-about-button" aria-label="Sobre a Aegis" @click="aboutOpen = !aboutOpen">ⓘ</button></template>
    <template #notice><UpdateNotice :platform="runtime.platform" /></template>
  </ChatView>
  <aside v-if="aboutOpen" class="native-about" aria-label="Sobre a Aegis">
    <button @click="aboutOpen = false">Fechar</button>
    <p>Aegis {{ runtime.version }}</p>
    <DiagnosticsPanel />
  </aside>
</template>
