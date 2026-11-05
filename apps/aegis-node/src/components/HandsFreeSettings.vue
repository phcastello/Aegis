<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { useHandsFreeSettings } from '../composables/useHandsFreeSettings';

const { state, busy, stale, canAct, error, diagnosticCode, refresh, run, configureCollection } = useHandsFreeSettings();
const deleteArmed = ref(false);
let collectionRefresh: ReturnType<typeof setInterval> | undefined;
function foreground() { if (document.visibilityState === 'visible') void refresh(); }
onMounted(() => { void refresh(); document.addEventListener('visibilitychange', foreground); window.addEventListener('focus', foreground); collectionRefresh = setInterval(() => { if (document.visibilityState === 'visible' && state.value?.collection?.enabled) void refresh(); }, 5000); });
onBeforeUnmount(() => { clearInterval(collectionRefresh); document.removeEventListener('visibilitychange', foreground); window.removeEventListener('focus', foreground); });
</script>
<template>
  <section class="hands-free-settings" aria-labelledby="hands-free-title">
    <h3 id="hands-free-title">Hands-free</h3>
    <p>Diga “Aegis” seguido do pedido, inclusive com a tela bloqueada. A detecção acontece no celular. Somente o comando ativado é enviado para transcrição.</p>
    <template v-if="state">
      <label class="audio-preference"><input type="checkbox" :checked="state.enabled" :disabled="!canAct" @change="run('configure', ($event.target as HTMLInputElement).checked)" /> Escutar “Aegis”</label>
      <p role="status">{{ stale ? 'Último estado conhecido: ' : '' }}{{ state.message }}</p>
      <p v-if="stale" class="settings-muted">A escuta e o recebimento de áudio não estão confirmados neste momento.</p>
      <p v-if="state.modelReady" class="settings-muted">{{ stale ? 'Último modelo informado:' : 'Modelo instalado:' }} <strong>{{ state.modelName || 'Identificação indisponível' }}</strong></p>
      <div class="settings-actions">
        <button v-if="!state.permissionGranted" type="button" :disabled="!canAct" @click="run('permission')">Permitir microfone</button>
        <button v-if="!state.assistantSelected" type="button" :disabled="!canAct" @click="run('assistant')">Selecionar assistente no Android</button>
        <button type="button" :disabled="!canAct || state.enabled" @click="run('import')">{{ state.modelReady ? 'Importar modelo novamente' : 'Importar modelo privado' }}</button>
      </div>
      <p class="settings-muted">A seleção de assistente é feita na tela oficial do Android. O modelo pessoal deve ser importado uma vez, com a escuta desligada.</p>
      <label class="audio-preference"><input type="checkbox" :checked="state.diagnostic" :disabled="!canAct" @change="run('configure', undefined, ($event.target as HTMLInputElement).checked)" /> Diagnóstico: detectar chamadas sem enviar comandos</label>
      <div class="settings-actions"><button type="button" :disabled="!canAct" @click="run('cancel')">Cancelar interação</button><button type="button" :disabled="!canAct" @click="run('export')">Exportar métricas e ativações (ZIP)</button></div>
      <fieldset v-if="state.collection" class="collection-settings">
        <legend>Coleta local de ativações</legend>
        <p>Com sua autorização, cada ativação guarda o contexto do disparo e o áudio posterior somente neste celular. Um falso disparo pode gravar conversas próximas. O contexto anterior nunca é enviado à transcrição.</p>
        <label class="audio-preference"><input type="checkbox" :checked="state.collection.enabled" :disabled="!canAct" @change="configureCollection(($event.target as HTMLInputElement).checked, state.collection.limitMiB)" /> Guardar ativações localmente</label>
        <label>Limite de armazenamento <select :value="state.collection.limitMiB" :disabled="!canAct" @change="configureCollection(state.collection.enabled, Number(($event.target as HTMLSelectElement).value))"><option :value="25">25 MiB</option><option :value="100">100 MiB</option><option :value="250">250 MiB</option><option :value="500">500 MiB</option></select></label>
        <p role="status">{{ (state.collection.bytes / 1048576).toFixed(1) }} MiB usados · {{ state.collection.status === 'ready' ? 'Pronta' : state.collection.status === 'storage_limit' ? 'Limite atingido: exporte e exclua os dados, ou aumente o limite.' : 'Armazenamento indisponível ou em preparação.' }}</p>
        <p v-if="state.collection.skippedEvents">{{ state.collection.skippedEvents }} ativações não puderam ser armazenadas. Nenhum evento anterior foi apagado automaticamente.</p>
        <button v-if="!deleteArmed" type="button" :disabled="!canAct" @click="deleteArmed = true">Excluir dados locais…</button>
        <template v-else><p>Excluir todos os áudios e eventos locais? Exporte antes se quiser preservá-los.</p><button type="button" :disabled="!canAct || ['capturing', 'processing', 'speaking', 'continuation'].includes(state.status)" @click="run('collectionDelete'); deleteArmed = false">Confirmar exclusão local</button><button type="button" @click="deleteArmed = false">Manter dados</button></template>
      </fieldset>
    </template>
    <p v-if="busy" role="status">{{ state ? 'Atualizando estado…' : 'Consultando escuta…' }}</p>
    <p v-else-if="!state && !error">O estado da escuta ainda não foi recebido.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="diagnosticCode" class="settings-muted">Código: {{ diagnosticCode }}</p>
    <div class="settings-actions"><button type="button" :disabled="busy" @click="refresh">{{ error && !state ? 'Tentar novamente' : 'Atualizar estado' }}</button></div>
  </section>
</template>
<style scoped>.hands-free-settings { margin-top: 24px; border-top: 1px solid var(--border-color, #444); padding-top: 18px; }.hands-free-settings p { margin: 12px 0; }.collection-settings { margin-top:20px; border:1px solid var(--border-color, #444); border-radius:12px; padding:16px; }.collection-settings select { margin-left:12px; }</style>
