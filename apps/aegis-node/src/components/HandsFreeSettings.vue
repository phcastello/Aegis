<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue';
import { useHandsFreeSettings } from '../composables/useHandsFreeSettings';

const { state, busy, stale, canAct, error, diagnosticCode, refresh, run } = useHandsFreeSettings();
function foreground() { if (document.visibilityState === 'visible') void refresh(); }
onMounted(() => { void refresh(); document.addEventListener('visibilitychange', foreground); window.addEventListener('focus', foreground); });
onBeforeUnmount(() => { document.removeEventListener('visibilitychange', foreground); window.removeEventListener('focus', foreground); });
</script>
<template>
  <section class="hands-free-settings" aria-labelledby="hands-free-title">
    <h3 id="hands-free-title">Hands-free</h3>
    <p>Diga “Aegis” seguido do pedido, inclusive com a tela bloqueada. A detecção acontece no celular. Somente o comando ativado é enviado para transcrição.</p>
    <template v-if="state">
      <label class="audio-preference"><input type="checkbox" :checked="state.enabled" :disabled="!canAct" @change="run('configure', ($event.target as HTMLInputElement).checked)" /> Escutar “Aegis”</label>
      <p role="status">{{ stale ? 'Último estado conhecido: ' : '' }}{{ state.message }}</p>
      <p v-if="stale" class="settings-muted">A escuta e o recebimento de áudio não estão confirmados neste momento.</p>
      <div class="settings-actions">
        <button v-if="!state.permissionGranted" type="button" :disabled="!canAct" @click="run('permission')">Permitir microfone</button>
        <button v-if="!state.assistantSelected" type="button" :disabled="!canAct" @click="run('assistant')">Selecionar assistente no Android</button>
        <button type="button" :disabled="!canAct || state.enabled" @click="run('import')">{{ state.modelReady ? 'Importar modelo novamente' : 'Importar modelo privado' }}</button>
      </div>
      <p class="settings-muted">A seleção de assistente é feita na tela oficial do Android. O modelo pessoal deve ser importado uma vez, com a escuta desligada.</p>
      <label class="audio-preference"><input type="checkbox" :checked="state.diagnostic" :disabled="!canAct" @change="run('configure', undefined, ($event.target as HTMLInputElement).checked)" /> Diagnóstico: detectar chamadas sem enviar comandos</label>
      <div class="settings-actions"><button type="button" :disabled="!canAct" @click="run('cancel')">Cancelar interação</button><button type="button" :disabled="!canAct" @click="run('export')">Exportar métricas</button></div>
    </template>
    <p v-if="busy" role="status">{{ state ? 'Atualizando estado…' : 'Consultando escuta…' }}</p>
    <p v-else-if="!state && !error">O estado da escuta ainda não foi recebido.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="diagnosticCode" class="settings-muted">Código: {{ diagnosticCode }}</p>
    <div class="settings-actions"><button type="button" :disabled="busy" @click="refresh">{{ error && !state ? 'Tentar novamente' : 'Atualizar estado' }}</button></div>
  </section>
</template>
<style scoped>.hands-free-settings { margin-top: 24px; border-top: 1px solid var(--border-color, #444); padding-top: 18px; }.hands-free-settings p { margin: 12px 0; }</style>
