<script setup lang="ts">
import { computed } from 'vue';
import type { NotificationSettings } from '../services/nodes';
import { actionDiagnosticRows, pendingActionLabel } from '../services/reminderActions';
const props = defineProps<{ settings: NotificationSettings | null }>();
const rows = computed(() => actionDiagnosticRows(props.settings));
</script>
<template>
  <div v-if="settings?.pendingActions || settings?.failedActions" class="reminder-action-sync">
    <p v-if="settings.pendingActions" class="settings-status" role="status">{{ pendingActionLabel(settings.pendingActions) }}. {{ settings.actionSyncState === 'syncing' ? 'Enviando ao servidor…' : settings.actionSyncState === 'temporarily_unavailable' ? 'Sincronização temporariamente indisponível; uma nova tentativa será feita quando possível.' : 'A confirmação do servidor está pendente.' }}</p>
    <p v-if="settings.failedActions" class="settings-error" role="status">{{ settings.failedActions }} {{ settings.failedActions === 1 ? 'ação com falha permanente' : 'ações com falha permanente' }}. Confira os detalhes.</p>
    <details v-if="rows.length"><summary>Detalhes das ações locais</summary><ul><li v-for="(item,index) in rows" :key="index"><strong>{{ item.type }} · {{ item.state }}</strong><small>Idade: {{ item.age }} · {{ item.attempts }} {{ item.attempts === 1 ? 'tentativa' : 'tentativas' }}</small><small>Última tentativa: {{ item.lastAttempt }}</small><small>{{ item.lastResult }} · {{ item.retryScheduled ? 'Retry agendado' : 'Sem retry agendado' }}</small></li></ul></details>
  </div>
</template>
<style scoped>.reminder-action-sync li { margin-block: 10px; }.reminder-action-sync ul { padding-inline-start: 20px; }</style>
