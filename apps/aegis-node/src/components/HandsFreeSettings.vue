<script setup lang="ts">
import { listen, type UnlistenFn } from '@tauri-apps/api/event';
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { batteryRateDifference, batteryRunsForComparison, latestBatteryRuns, type HandsFreeBatteryMode, type HandsFreeMediaReferenceStatus } from '../services/handsFree';
import { useHandsFreeSettings } from '../composables/useHandsFreeSettings';

const props = withDefaults(defineProps<{ platform?: 'android' | 'windows' }>(), { platform: 'android' });
const { state, busy, stale, canAct, canConfigure, batteryLocked, error, diagnosticCode, refresh, run, configureCollection, automationSupported, automationLive, canStartBattery, startBattery, finishBattery, canStartAuto, startAuto, cancelAuto, exportBattery } = useHandsFreeSettings();
const mediaReferenceMessage: Record<HandsFreeMediaReferenceStatus,string> = {
  unsupported: 'Disponível a partir do Android 10.',
  off: 'Desativada.', requesting: 'Aguardando autorização do Android.', starting: 'Preparando referência de áudio.',
  waiting: 'Autorizada; aguardando reprodução de mídia.', warming: 'Preparando áudio de referência.',
  ready: 'Referência de mídia disponível.', unavailable: 'Áudio de referência indisponível; a escuta continua pelo microfone.',
  denied: 'Autorização não concedida.', blocked: 'Escuta hands-free indisponível.',
  stopped: 'Sessão encerrada pelo Android. Autorize novamente para usar.'
};
const canStartMediaReference = computed(() => Boolean(canConfigure.value && state.value?.enabled && !state.value?.diagnostic && state.value?.modelReady && state.value?.assistantSelected && state.value?.permissionGranted));
const battery = computed(() => state.value?.batteryDiagnostics);
const automation = computed(() => battery.value?.automation);
const comparisonVisible = computed(() => !automation.value || (automation.value.status === 'completed' && !automation.value.validation));
const automationCleanup = computed(() => automationLive.value || Boolean(automation.value?.status === 'interrupted' && battery.value?.requiresResume));
const eligibleRuns = computed(() => latestBatteryRuns(batteryRunsForComparison(battery.value?.runs ?? [], automation.value)));
const batteryRows = computed(() => (['off', 'capture_only', 'detection'] as const).map(mode => ({ mode, run: eligibleRuns.value[mode] })));
const captureDifference = computed(() => batteryRateDifference(eligibleRuns.value.capture_only, eligibleRuns.value.off));
const detectionDifference = computed(() => batteryRateDifference(eligibleRuns.value.detection, eligibleRuns.value.capture_only));
const batteryLabels: Record<HandsFreeBatteryMode, string> = { off: 'A · Escuta desligada', capture_only: 'B · Somente microfone', detection: 'C · Detecção local' };
function measured(value: number | null | undefined, unit: string, signed = false): string {
  if (value === null || value === undefined) return 'Indisponível';
  return `${signed && value > 0 ? '+' : ''}${value.toLocaleString('pt-BR', { maximumFractionDigits: 2 })} ${unit}`;
}
function minutes(value: number): string { return (value / 60000).toLocaleString('pt-BR', { maximumFractionDigits: 1 }); }
function seconds(value: number): string { return (value / 1000).toLocaleString('pt-BR', { maximumFractionDigits: 1 }); }
const automationMessage = computed(() => {
  const plan = automation.value;
  if (!plan) return '';
  const kind = plan.validation ? 'Verificação' : 'Comparação';
  if (plan.status === 'waiting_screen_off') return `${kind} preparada. Apague a tela para começar a etapa A.`;
  if (plan.status === 'transitioning') return plan.mode ? `Preparando ${batteryLabels[plan.mode]}.` : 'Preparando a próxima etapa.';
  if (plan.status === 'running') return plan.mode ? `${batteryLabels[plan.mode]} em execução.` : `${kind} em execução.`;
  if (plan.status === 'completed') return `${kind} concluída. A Aegis voltou às configurações anteriores.`;
  if (plan.status === 'cancelled') return `${kind} cancelada. A Aegis voltou às configurações anteriores.`;
  return `${kind} interrompida. ${battery.value?.requiresResume ? 'Cancele para voltar às configurações anteriores.' : 'Confira as condições no relatório antes de tentar novamente.'}`;
});
function isValidationRun(id: string): boolean { return Boolean(automation.value?.validation && automation.value.runIds.includes(id)); }
const deleteArmed = ref(false);
let stopVoiceEvents: UnlistenFn | undefined;
let mounted = false;
onMounted(async () => {
  mounted = true;
  if (props.platform !== 'windows') return;
  try {
    const stop = await listen('aegis-windows-voice-state', () => { if (document.visibilityState === 'visible') void refresh(); });
    if (mounted) { stopVoiceEvents = stop; void refresh(); } else stop();
  } catch { /* Focus and the refresh control also read native state. */ }
});
let collectionRefresh: ReturnType<typeof setInterval> | undefined;
function foreground() { if (document.visibilityState === 'visible') void refresh(); }
onMounted(() => { void refresh(); document.addEventListener('visibilitychange', foreground); window.addEventListener('focus', foreground); collectionRefresh = setInterval(() => { if (document.visibilityState === 'visible' && (state.value?.collection?.enabled || state.value?.mediaReference?.session || state.value?.batteryDiagnostics?.active || automationLive.value)) void refresh(); }, 5000); });
onBeforeUnmount(() => { mounted = false; stopVoiceEvents?.(); clearInterval(collectionRefresh); document.removeEventListener('visibilitychange', foreground); window.removeEventListener('focus', foreground); });
</script>
<template>
  <section class="hands-free-settings" aria-labelledby="hands-free-title">
    <h3 id="hands-free-title">Hands-free</h3>
    <p v-if="props.platform === 'windows'">Diga “Aegis” e faça seu pedido ao ouvir o sinal. A escuta continua com a Aegis na bandeja. Importe o pacote privado Micro v2 + Open v2 para ativar a escuta.</p>
    <p v-else>Diga “Aegis” seguido do pedido, inclusive com a tela bloqueada. A detecção acontece no celular. Somente o comando ativado é enviado para transcrição.</p>
    <template v-if="state">
      <label class="audio-preference"><input type="checkbox" :checked="state.enabled" :disabled="!canConfigure || (props.platform === 'windows' && !state.enabled && !state.modelReady)" @change="run('configure', ($event.target as HTMLInputElement).checked)" /> Escutar “Aegis”</label>
      <p role="status">{{ stale ? 'Último estado conhecido: ' : '' }}{{ state.message }}</p>
      <p v-if="stale" class="settings-muted">A escuta e o recebimento de áudio não estão confirmados neste momento.</p>
      <p v-if="state.modelReady" class="settings-muted">{{ stale ? 'Último modelo informado:' : 'Modelo instalado:' }} <strong>{{ state.modelName || 'Identificação indisponível' }}</strong></p>
      <div class="settings-actions">
        <button v-if="!state.permissionGranted" type="button" :disabled="!canConfigure" @click="run('permission')">Permitir microfone</button>
        <button v-if="props.platform === 'android' && !state.assistantSelected" type="button" :disabled="!canConfigure" @click="run('assistant')">Selecionar assistente no Android</button>
        <button type="button" :disabled="!canConfigure || state.enabled" @click="run('import')">{{ state.modelReady ? 'Importar modelo novamente' : 'Importar modelo privado' }}</button>
      </div>
      <p v-if="props.platform === 'android'" class="settings-muted">A seleção de assistente é feita na tela oficial do Android. O modelo pessoal deve ser importado uma vez, com a escuta desligada.</p>
      <label class="audio-preference"><input type="checkbox" :checked="state.diagnostic" :disabled="!canConfigure" @change="run('configure', undefined, ($event.target as HTMLInputElement).checked)" /> Diagnóstico: detectar chamadas sem enviar comandos</label>
      <div class="settings-actions"><button type="button" :disabled="!canConfigure" @click="run('cancel')">Cancelar interação</button><button v-if="props.platform === 'android'" type="button" :disabled="!canConfigure" @click="run('export')">Exportar métricas e ativações (ZIP)</button></div>
      <fieldset v-if="state.mediaReference" class="media-reference-settings">
        <legend>Cancelamento de áudio de mídia (experimental)</legend>
        <p>Autorize o áudio de músicas, vídeos e jogos para melhorar a escuta enquanto outras mídias tocam. A referência é processada localmente em memória e descartada; a Aegis não grava a tela.</p>
        <p class="settings-muted">A autorização é temporária e o Android pode encerrá-la ao bloquear a tela. Aplicativos podem impedir a captura de seu áudio.</p>
        <p role="status">{{ mediaReferenceMessage[state.mediaReference.status] }}</p>
        <div v-if="state.mediaReference.supported" class="settings-actions">
          <button v-if="state.mediaReference.session" type="button" :disabled="!canAct" @click="run('mediaReferenceStop')">Desativar referência de mídia</button>
          <button v-else type="button" :disabled="!canStartMediaReference" @click="run('mediaReferenceStart')">Autorizar áudio de mídia</button>
        </div>
      </fieldset>
      <fieldset v-if="battery" class="battery-settings">
        <legend>Investigar consumo de bateria</legend>
        <template v-if="automation">
          <p role="status">{{ automationMessage }}</p>
          <p v-if="automation.status === 'transitioning'">A preparação fica fora da medição. A janela de {{ minutes(automation.stageDurationMs) }} min ainda não começou.</p>
          <p v-if="automation.status === 'transitioning' && automation.preparationElapsedMs !== undefined && automation.preparationElapsedMs !== null" class="settings-muted">Preparação decorrida: {{ seconds(automation.preparationElapsedMs) }} s.</p>
          <p v-if="automationLive && automation.status !== 'transitioning' && automation.remainingEstimateMs !== null">Previsão: {{ minutes(automation.remainingEstimateMs) }} min de medição, mais preparação entre etapas.</p>
          <p v-if="automation.validation" class="settings-muted">Verificação funcional da automação. Estes resultados não medem consumo de bateria.</p>
          <p v-if="automation.alarmPrecision === 'inexact'" class="settings-muted">O Android pode atrasar as trocas de etapa. O relatório registra os tempos efetivos.</p>
          <p v-if="automation.delayMs !== undefined && automation.delayMs >= 5000" class="settings-muted">Houve atraso na troca de etapa. Considere a duração real registrada no relatório.</p>
          <button v-if="automationCleanup" type="button" :disabled="!canAct" @click="cancelAuto">{{ automation.validation ? 'Cancelar verificação' : 'Cancelar comparação' }}</button>
          <p v-if="automationCleanup" class="settings-muted">Cancelar encerra toda a sequência e volta às configurações anteriores.</p>
        </template>
        <template v-if="batteryLocked && !automationCleanup">
          <p role="status" v-if="battery.active">Em teste: {{ batteryLabels[battery.active.mode] }} · {{ minutes(battery.active.elapsedMs) }} min decorridos.</p>
          <p role="status" v-else>O teste foi interrompido. Encerre para voltar às configurações anteriores.</p>
          <p v-if="battery.active && battery.active.elapsedMs >= 1800000">Já decorreram 30 minutos. Você pode encerrar o teste.</p>
          <p>Este é um teste manual. Volte aqui para encerrar manualmente após cerca de 30 minutos.</p>
          <button type="button" :disabled="!canAct" @click="finishBattery">Encerrar teste e voltar às configurações anteriores</button>
        </template>
        <p v-if="automationSupported">Inicie uma vez e apague a tela. A Aegis compara A → B → C automaticamente, por 30 minutos de medição em cada etapa: cerca de 1h30, além da preparação entre etapas. Apague a tela para começar. As trocas podem atrasar no Android.</p>
        <p>Comece com carga entre 50% e 80%, aparelho frio e fora do carregador. Mantenha a tela apagada e o mesmo ambiente. Durante os testes, a Aegis não responde a chamadas e nenhum áudio é salvo ou enviado.</p>
        <p v-if="batteryLocked" class="settings-muted">Os controles normais ficam suspensos até o encerramento.</p>
        <p v-if="props.platform === 'android' && !state.assistantSelected" class="settings-muted">Selecione a Aegis como assistente no Android antes de iniciar os testes.</p>
        <p v-else-if="!state.permissionGranted" class="settings-muted">Para a sequência automática, permita o microfone nos controles acima.</p>
        <p v-else-if="!state.modelReady" class="settings-muted">Para a sequência automática, importe e confira o modelo de detecção nos controles acima.</p>
        <div v-if="automationSupported && !batteryLocked" class="settings-actions"><button type="button" :disabled="!canStartAuto()" @click="startAuto()">Iniciar comparação automática</button></div>
        <details v-if="!batteryLocked" class="battery-advanced" :open="!automationSupported">
          <summary>Opções avançadas</summary>
          <template v-if="automationSupported">
            <p>Verifique apenas a passagem entre as três etapas, com cerca de 1 minuto em cada uma. São cerca de 3 minutos de verificação, além da preparação entre etapas, após apagar a tela. As trocas podem atrasar no Android. Esta verificação não mede consumo.</p>
            <button type="button" :disabled="!canStartAuto()" @click="startAuto(true)">Verificar automação (sem medir consumo)</button>
          </template>
          <p>Testes manuais: cada botão inicia uma etapa de 30 minutos. Apague a tela e volte aqui para encerrar manualmente; os botões abaixo não executam a sequência automática.</p>
          <div class="settings-actions battery-start-actions">
            <button type="button" :disabled="!canStartBattery('off')" @click="startBattery('off')">A · Escuta desligada</button>
            <button type="button" :disabled="!canStartBattery('capture_only')" @click="startBattery('capture_only')">B · Somente microfone</button>
            <button type="button" :disabled="!canStartBattery('detection')" @click="startBattery('detection')">C · Detecção local (sem comandos)</button>
          </div>
        </details>
        <p class="settings-muted">A mede o aparelho com a escuta desligada. B mantém apenas a captura do microfone. C acrescenta a detecção local, sem transcrição, resposta, sinal sonoro ou coleta de ativações.</p>
        <div class="settings-actions"><button type="button" :disabled="!canAct || Boolean(battery.active) || automationLive" @click="exportBattery">Exportar relatório de bateria (sem áudio)</button></div>
        <template v-if="battery.runs.length">
          <h4>Resultados de bateria</h4>
          <p v-if="!comparisonVisible" class="settings-muted">{{ automation?.validation ? 'A verificação funcional fica somente no histórico, sem comparação de consumo.' : 'A comparação desta sequência aparece após a conclusão das três etapas. Os dados parciais ficam no histórico.' }}</p>
          <template v-if="comparisonVisible">
          <div class="battery-table-scroll"><table class="battery-table">
            <caption>Último resultado de cada cenário no grupo comparável mais recente. Os valores de descarga são do aparelho inteiro.</caption>
            <thead><tr><th scope="col">Cenário</th><th scope="col">Duração</th><th scope="col">Descarga do aparelho</th><th scope="col">Taxa do aparelho</th></tr></thead>
            <tbody><tr v-for="row in batteryRows" :key="row.mode"><th scope="row">{{ batteryLabels[row.mode] }}</th><template v-if="row.run"><td>{{ minutes(row.run.durationMs) }} min</td><td>{{ measured(row.run.summary.phoneChargeMah, 'mAh') }}</td><td>{{ measured(row.run.summary.phoneDischargeMahPerHour, 'mAh/h') }}</td></template><td v-else colspan="3">Sem teste elegível</td></tr></tbody>
          </table></div>
          <p>Estimativa do custo adicional de captura (B − A): <strong>{{ measured(captureDifference, 'mAh/h', true) }}</strong>. Estimativa adicional da detecção (C − B): <strong>{{ measured(detectionDifference, 'mAh/h', true) }}</strong>.</p>
          <p class="settings-muted">Somente testes do mesmo aparelho, versão, modelo e configuração são comparados. As diferenças estimam mudanças entre testes do telefone inteiro; não medem energia exclusiva da Aegis. Valores negativos são preservados e podem refletir variação das condições. Sensores indisponíveis aparecem como “Indisponível”.</p>
          </template>
          <details class="battery-history"><summary>Histórico dos últimos {{ battery.runs.length }} testes e CPU</summary>
            <article v-for="result in [...battery.runs].reverse()" :key="result.id" class="battery-result">
              <h4>{{ batteryLabels[result.mode] }} · {{ minutes(result.durationMs) }} min</h4>
              <p v-if="result.preparationMs !== undefined && result.preparationMs !== null" class="settings-muted">Preparação: {{ seconds(result.preparationMs) }} s, fora da janela de medição.</p>
              <p v-if="result.measurementStartBasis === 'first_positive_pcm'" class="settings-muted">A medição começou após a confirmação de áudio do microfone.</p>
              <p v-else-if="result.measurementStartBasis === 'capture_disabled'" class="settings-muted">A medição começou após confirmar a captura desligada.</p>
              <p v-else class="settings-muted">Este registro não informa o marco de início da medição.</p>
              <p v-if="isValidationRun(result.id)">Verificação funcional: este resultado não mede consumo.</p>
              <p v-else>{{ result.eligible ? 'Condições aceitas para comparação.' : 'Não elegível para comparação: duração, condições ou disponibilidade precisam ser verificadas na exportação.' }}</p>
              <p v-if="result.issues.length">{{ result.issues.length }} observação(ões) registrada(s). A exportação conserva os motivos do resultado.</p>
              <dl class="battery-cpu"><dt>Energia do aparelho</dt><dd>{{ measured(result.summary.phoneEnergyMwh, 'mWh') }}</dd><dt>Variação da bateria</dt><dd>{{ measured(result.summary.batteryPercentPoints, 'p.p.') }}</dd><dt>Tempo do aparelho acordado</dt><dd>{{ measured(result.summary.awakeMs, 'ms') }}</dd><dt>Tela ligada</dt><dd>{{ measured(result.summary.screenOnMs, 'ms') }}</dd></dl>
              <dl class="battery-cpu"><dt>CPU do processo</dt><dd>{{ measured(result.summary.processCpuMs, 'ms') }}</dd><dt>CPU da captura</dt><dd>{{ measured(result.summary.captureThreadCpuMs, 'ms') }}</dd><dt>CPU do detector</dt><dd>{{ measured(result.summary.detectorCpuMs, 'ms') }}</dd><dt>CPU do verificador</dt><dd>{{ measured(result.summary.verifierCpuMs, 'ms') }}</dd></dl>
              <p class="settings-muted">CPU é tempo de trabalho medido, em milissegundos; não é consumo em mAh. Os tempos podem se sobrepor; não some as linhas.</p>
            </article>
          </details>
        </template>
      </fieldset>
      <fieldset v-if="state.collection" class="collection-settings">
        <legend>Coleta local de ativações</legend>
        <p>Com sua autorização, cada ativação guarda o contexto do disparo e o áudio posterior somente neste celular. Um falso disparo pode gravar conversas próximas. O contexto anterior nunca é enviado à transcrição.</p>
        <label class="audio-preference"><input type="checkbox" :checked="state.collection.enabled" :disabled="!canConfigure" @change="configureCollection(($event.target as HTMLInputElement).checked, state.collection.limitMiB)" /> Guardar ativações localmente</label>
        <label>Limite de armazenamento <select :value="state.collection.limitMiB" :disabled="!canConfigure" @change="configureCollection(state.collection.enabled, Number(($event.target as HTMLSelectElement).value))"><option :value="25">25 MiB</option><option :value="100">100 MiB</option><option :value="250">250 MiB</option><option :value="500">500 MiB</option></select></label>
        <p role="status">{{ (state.collection.bytes / 1048576).toFixed(1) }} MiB usados · {{ state.collection.status === 'ready' ? 'Pronta' : state.collection.status === 'storage_limit' ? 'Limite atingido: exporte e exclua os dados, ou aumente o limite.' : 'Armazenamento indisponível ou em preparação.' }}</p>
        <p v-if="state.collection.skippedEvents">{{ state.collection.skippedEvents }} ativações não puderam ser armazenadas. Nenhum evento anterior foi apagado automaticamente.</p>
        <button v-if="!deleteArmed" type="button" :disabled="!canConfigure" @click="deleteArmed = true">Excluir dados locais…</button>
        <template v-else><p>Excluir todos os áudios e eventos locais? Exporte antes se quiser preservá-los.</p><button type="button" :disabled="!canConfigure || ['capturing', 'processing', 'speaking', 'continuation'].includes(state.status)" @click="run('collectionDelete'); deleteArmed = false">Confirmar exclusão local</button><button type="button" @click="deleteArmed = false">Manter dados</button></template>
      </fieldset>
    </template>
    <p v-if="busy" role="status">{{ state ? 'Atualizando estado…' : 'Consultando escuta…' }}</p>
    <p v-else-if="!state && !error">O estado da escuta ainda não foi recebido.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="diagnosticCode" class="settings-muted">Código: {{ diagnosticCode }}</p>
    <div class="settings-actions"><button type="button" :disabled="busy" @click="refresh">{{ error && !state ? 'Tentar novamente' : 'Atualizar estado' }}</button></div>
  </section>
</template>
<style scoped>.hands-free-settings { margin-top: 24px; border-top: 1px solid var(--border-color, #444); padding-top: 18px; }.hands-free-settings p { margin: 12px 0; }.collection-settings { margin-top:20px; border:1px solid var(--border-color, #444); border-radius:12px; padding:16px; }.collection-settings select { margin-left:12px; }.battery-settings, .media-reference-settings { margin-top:20px; padding:16px; border:1px solid var(--border-color, #444); border-radius:12px; min-width:0; }.battery-start-actions { flex-wrap:wrap; }.battery-start-actions button { flex:1 1 150px; }.battery-table-scroll { overflow-x:auto; }.battery-table { width:100%; border-collapse:collapse; font-size:.9rem; }.battery-table th,.battery-table td { padding:10px 8px; text-align:left; border-bottom:1px solid var(--border-color, #444); }.battery-table caption { text-align:left; padding-bottom:10px; }.battery-result { border-top:1px solid var(--border-color, #444); padding-top:8px; margin-top:12px; }.battery-cpu { display:grid; grid-template-columns:minmax(0,1fr) auto; gap:8px; }.battery-cpu dd { margin:0; }.battery-advanced { margin:16px 0; }.battery-advanced summary { cursor:pointer; padding:10px 0; }.battery-history summary { cursor:pointer; padding:10px 0; }</style>
