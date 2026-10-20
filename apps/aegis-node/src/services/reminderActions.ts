import type { NotificationSettings } from './nodes.js';

export function actionDiagnosticsUnavailable(settings: NotificationSettings | null | undefined): boolean {
  return settings?.actionDiagnosticsAvailable === false || settings?.actionSyncState === 'unavailable';
}
// Failed reads preserve only the last observed action data, never a fallback zero.
// Older Nodes without the availability field keep their existing semantics.
export function mergeActionSettings(previous: NotificationSettings | null | undefined, incoming?: NotificationSettings): NotificationSettings {
  if (incoming && !actionDiagnosticsUnavailable(incoming)) return incoming;
  return {
    ...(incoming ?? previous ?? { granted: false, pushConfigured: false, autostart: false }),
    pendingActions: previous?.pendingActions,
    failedActions: previous?.failedActions,
    actionDiagnostics: previous?.actionDiagnostics,
    actionDiagnosticsAvailable: false,
    actionSyncState: 'unavailable',
  };
}

export function pendingActionLabel(count: number): string {
  return `${count} ${count === 1 ? 'ação pendente' : 'ações pendentes'} de reconhecimento ou adiamento`;
}
const results: Readonly<Record<string, string>> = {
  invalid_response: 'Resposta do servidor inválida; confirmação pendente',
  queued: 'Aguardando primeira tentativa', sending: 'Envio em andamento', awaiting_occurrence_sync: 'Aguardando sincronização da ocorrência local',
  temporary_http: 'Servidor temporariamente indisponível', credential_unavailable: 'Autenticação temporariamente indisponível',
  rejected_invalid: 'Rejeitada: ação inválida', rejected_forbidden: 'Rejeitada: dispositivo sem autorização', rejected_missing: 'Rejeitada: ocorrência não encontrada',
  rejected_superseded: 'A regra geográfica foi substituída; ação não aplicada',
  rejected_conflict: 'A ocorrência mudou; ação não aplicada', rejected_expired: 'Rejeitada: ação expirada',
  accepted: 'Aceita pelo servidor', applied: 'Aplicada pelo servidor', duplicate: 'Já confirmada pelo servidor',
  conflict: 'A ocorrência mudou; ação não aplicada', permanently_rejected: 'Rejeitada definitivamente', rejected: 'Rejeitada definitivamente',
  http_temporary: 'Servidor temporariamente indisponível', temporary_error: 'Falha temporária',
  network_unavailable: 'Sem conexão utilizável', network_error: 'Conexão indisponível', response_lost: 'Resposta não confirmada',
  unauthorized: 'Autenticação indisponível', not_attempted: 'Ainda não tentada', pending: 'Aguardando tentativa',
  retry_scheduled: 'Nova tentativa agendada', worker_deferred: 'Trabalho adiado pelo Android',
  http_4xx: 'Rejeição definitiva do servidor', http_5xx: 'Servidor temporariamente indisponível',
  invalid_action: 'Ação inválida preservada para diagnóstico', invalid_payload: 'Ação inválida preservada para diagnóstico',
};
// Never forward arbitrary native prose, URLs, IDs or server responses to the UI.
export function actionDiagnosticRows(settings: NotificationSettings | null | undefined) {
  if (!Array.isArray(settings?.actionDiagnostics)) return [];
  return settings.actionDiagnostics.slice(0, 30).flatMap(item => {
    if (!item || !['acknowledge', 'snooze'].includes(item.type)) return [];
    const age = Number.isFinite(item.ageSeconds) && item.ageSeconds >= 0 ? Math.floor(item.ageSeconds / 60) : null;
    const attempted = typeof item.lastAttemptAt === 'number' ? new Date(item.lastAttemptAt) : typeof item.lastAttemptAt === 'string' ? new Date(item.lastAttemptAt) : null;
    return [{
      type: item.type === 'snooze' ? 'Adiamento' : 'Reconhecimento',
      age: age === null ? 'Idade indisponível' : age < 60 ? `${age} min` : `${Math.floor(age / 60)} h`,
      attempts: Number.isSafeInteger(item.attempts) && item.attempts >= 0 ? item.attempts : 0,
      lastAttempt: attempted && Number.isFinite(attempted.getTime()) ? attempted.toLocaleString('pt-BR') : 'Ainda não tentada',
      lastResult: results[item.lastResult ?? ''] ?? 'Resultado não confirmado',
      state: ['permanent_failure', 'rejected', 'failed'].includes(item.state) ? 'Falha permanente' : 'Pendente',
      retryScheduled: item.retryScheduled === true,
    }];
  });
}
