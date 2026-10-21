<script setup lang="ts">
import { ref } from 'vue';
import ChatView from '@aegis/client/views/ChatView.vue';
import SettingsShell, { type SettingsSection } from '@aegis/client/components/SettingsShell.vue';
import SettingsTrigger from '@aegis/client/components/SettingsTrigger.vue';
import { getReleaseIdentity } from '@aegis/client/services/releaseIdentity';
const section = ref<SettingsSection | null>(null);
const version = __AEGIS_WEB_VERSION__;
const releaseIdentity = getReleaseIdentity(version);
</script>
<template>
  <ChatView :app-version="version">
    <template #global-navigation><SettingsTrigger @click="section = 'about'" /></template>
  </ChatView>
  <SettingsShell :section="section" @navigate="section = $event" @close="section = null">
    <section v-if="section === 'about'"><h2>Sobre a Aegis</h2><p class="settings-muted">Cliente web · {{ releaseIdentity.versionLabel }}</p><p class="about-release-codename">{{ releaseIdentity.codename }}</p><p v-if="releaseIdentity.tagline" class="about-release-tagline settings-muted">{{ releaseIdentity.tagline }}</p><p>Sua assistente pessoal, com modelos acessados por API.</p><p>Chat, histórico e integrações continuam disponíveis aqui. Recursos de identidade do dispositivo, ações nativas e localização em segundo plano usam o aplicativo Android ou Windows.</p></section>
    <section v-else-if="section === 'updates'"><h2>Atualizações do cliente web</h2><p>O cliente web verifica sua versão ao abrir. Reabra a página para carregar uma atualização disponível.</p><p class="settings-muted">Versão atual: {{ version }}</p></section>
    <section v-else><h2>{{ section === 'devices' ? 'Dispositivos' : section === 'places' ? 'Lugares' : 'Lembretes' }}</h2><p>Gerencie este recurso no aplicativo Android ou Windows pareado. A identidade nativa do dispositivo permanece no cofre do aplicativo.</p><p class="settings-muted">Você pode continuar usando o chat neste navegador.</p></section>
  </SettingsShell>
</template>
