<script setup lang="ts">
import type { useUpdates } from '../composables/useUpdates';
defineProps<{ updates: ReturnType<typeof useUpdates>; workActive?: boolean }>();
</script>
<template>
  <aside v-if="updates.visible.value" class="update-notice" aria-label="Atualização da Aegis" role="status">
    <div><strong v-if="updates.update.value">Aegis {{ updates.update.value.version }} disponível</strong>
      <span v-if="updates.state.value === 'installing'">{{ updates.update.value?.kind === 'apk' ? 'Preparando instalador…' : 'Baixando atualização…' }} {{ updates.progress.value !== null ? `${updates.progress.value}%` : '' }}</span>
      <span v-else-if="updates.state.value === 'ready'">Download concluído. Instale quando terminar seu trabalho; a Aegis será reiniciada.</span>
      <span v-else-if="updates.state.value === 'permissionRequired'">Autorize a instalação pela Aegis nas configurações do Android e toque em Continuar instalação.</span>
      <span v-else-if="updates.state.value === 'opened'">Confirme a instalação na janela do sistema.</span>
      <span v-else>{{ updates.error.value ?? (workActive ? 'Finalize a gravação ou a resposta antes de atualizar.' : updates.update.value?.kind === 'installer' ? 'Baixe agora e confirme a instalação quando estiver pronto.' : 'A instalação exige sua confirmação no Android.') }}</span>
      <small v-if="updates.diagnostic.value">Código: {{ updates.diagnostic.value.code }}</small>
    </div>
    <button v-if="updates.state.value === 'available' || updates.state.value === 'error' || updates.state.value === 'permissionRequired' || updates.state.value === 'ready'" :disabled="workActive" @click="updates.install">{{ updates.state.value === 'permissionRequired' ? 'Continuar instalação' : updates.update.value?.kind === 'apk' ? 'Baixar e instalar' : updates.readyToInstall.value ? 'Instalar e reiniciar' : 'Baixar atualização' }}</button>
    <button v-if="updates.state.value !== 'installing'" class="update-dismiss" @click="updates.dismiss">Agora não</button>
  </aside>
</template>
