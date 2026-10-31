<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { handsFree, type HandsFreeOperation, type HandsFreeState } from '../services/handsFree';

const state = ref<HandsFreeState | null>(null);
const busy = ref(false);
const error = ref('');
let disposed = false;
async function refresh() {
  try { const next = await handsFree('state'); if (!disposed) state.value = next; }
  catch { if (!disposed) error.value = 'Não foi possível consultar a escuta. Tente atualizar o estado.'; }
}
async function run(operation: HandsFreeOperation, enabled?: boolean, diagnostic?: boolean) {
  if (busy.value) return;
  busy.value = true; error.value = '';
  try {
    const next = await handsFree(operation, operation === 'configure' ? {
      enabled: enabled ?? state.value?.enabled ?? false,
      diagnostic: diagnostic ?? state.value?.diagnostic ?? false
    } : undefined);
    if (!disposed) state.value = next;
  } catch (reason) { if (!disposed) error.value = reason instanceof Error ? reason.message : 'Não foi possível aplicar a configuração.'; }
  finally { if (!disposed) busy.value = false; }
}
function foreground() { if (document.visibilityState === 'visible') void refresh(); }
onMounted(() => { void refresh(); document.addEventListener('visibilitychange', foreground); window.addEventListener('focus', foreground); });
onBeforeUnmount(() => { disposed = true; document.removeEventListener('visibilitychange', foreground); window.removeEventListener('focus', foreground); });
</script>
<template>
  <section class="hands-free-settings" aria-labelledby="hands-free-title">
    <h3 id="hands-free-title">Hands-free</h3>
    <p>Diga “Aegis” seguido do pedido, inclusive com a tela bloqueada. A detecção acontece no celular. Somente o comando ativado é enviado para transcrição.</p>
    <template v-if="state">
      <label class="audio-preference"><input type="checkbox" :checked="state.enabled" :disabled="busy" @change="run('configure', ($event.target as HTMLInputElement).checked)" /> Escutar “Aegis”</label>
      <p role="status">{{ state.message }}</p>
      <div class="settings-actions">
        <button v-if="!state.permissionGranted" type="button" :disabled="busy" @click="run('permission')">Permitir microfone</button>
        <button v-if="!state.assistantSelected" type="button" :disabled="busy" @click="run('assistant')">Selecionar assistente no Android</button>
        <button type="button" :disabled="busy || state.enabled" @click="run('import')">{{ state.modelReady ? 'Importar modelo novamente' : 'Importar modelo privado' }}</button>
      </div>
      <p class="settings-muted">A seleção de assistente é feita na tela oficial do Android. O modelo pessoal deve ser importado uma vez, com a escuta desligada.</p>
      <label class="audio-preference"><input type="checkbox" :checked="state.diagnostic" :disabled="busy" @change="run('configure', undefined, ($event.target as HTMLInputElement).checked)" /> Diagnóstico: detectar chamadas sem enviar comandos</label>
      <div class="settings-actions"><button type="button" :disabled="busy" @click="run('cancel')">Cancelar interação</button><button type="button" :disabled="busy" @click="run('export')">Exportar métricas</button><button type="button" :disabled="busy" @click="refresh">Atualizar estado</button></div>
    </template>
    <p v-else>Consultando escuta…</p>
    <p v-if="error" role="alert">{{ error }}</p>
  </section>
</template>
<style scoped>.hands-free-settings { margin-top: 24px; border-top: 1px solid var(--border-color, #444); padding-top: 18px; }.hands-free-settings p { margin: 12px 0; }</style>
