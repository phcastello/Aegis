<script setup lang="ts">
import { onMounted } from 'vue';
import { useUpdates } from '../composables/useUpdates';
import { checkUpdate } from '../services/updates';
import type { Platform } from '../services/runtime';
const props = defineProps<{ platform: Platform }>();
const updates = useUpdates(() => checkUpdate(props.platform));
onMounted(() => void updates.start());
</script>

<template>
  <aside v-if="updates.visible.value" class="update-notice" role="status">
    <span v-if="updates.state.value === 'installing'">{{ updates.update.value?.kind === 'apk' ? 'Abrindo download do APK…' : 'Baixando atualização…' }} {{ updates.progress.value !== null ? `${updates.progress.value}%` : '' }}</span>
    <span v-else-if="updates.state.value === 'opened'">Confirme o download e a instalação do APK no Android.</span>
    <span v-else>{{ updates.error.value ?? `Nova versão disponível: ${updates.update.value?.version}` }}</span>
    <button v-if="updates.state.value === 'available' || updates.state.value === 'error'" @click="updates.install">{{ updates.update.value?.kind === 'apk' ? 'Baixar APK' : 'Atualizar' }}</button>
    <button v-if="updates.state.value !== 'installing'" @click="updates.dismiss">Agora não</button>
  </aside>
</template>
