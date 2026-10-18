import { dateLabel, type Geofence, type GeofenceDiagnosticEvent, type GeofencePresenceDiagnostic } from './domains.js';

const observationReasons = new Set(['confirmed', 'awaiting_confirmation', 'accuracy_insufficient', 'near_boundary', 'outside_initial', 'inside_initial']);

/** Age the displayed observation without changing the persisted baseline or arming. */
export function currentGeofenceDiagnostic(item: Geofence, rules: GeofencePresenceDiagnostic[] | undefined, now: number): GeofencePresenceDiagnostic | undefined {
  const state = rules?.find(rule => rule.id === item.id && rule.revision === item.revision);
  if (state?.lastObservationAt && now - state.lastObservationAt > 120_000 && state.installationStatus === 'installed' && observationReasons.has(state.reasonCode)) {
    return { ...state, confirmedPresence: state.confirmedPresence ?? state.presence, presence: 'unknown', reasonCode: 'stale_location' };
  }
  return state;
}

export function geofenceMonitoringStatus(item: Geofence, state: GeofencePresenceDiagnostic | undefined, now: number): string {
  if (item.syncStatus === 'pending_sync' && item.status === 'cancelled') return 'Cancelamento pendente no celular';
  if (item.syncStatus === 'pending_sync' && item.status === 'paused') return 'Pausa pendente no celular';
  if (item.status === 'paused') return 'Pausado';
  if (item.status === 'completed' || state?.monitoringState === 'completed') return 'Concluído';
  if (item.status === 'cancelled') return 'Cancelado';
  if (item.leaseUntil && new Date(item.leaseUntil).getTime() <= now) return 'Sincronização necessária';
  if (state) {
    if (state.installationStatus === 'permission_required' || state.reasonCode === 'permission_required') return 'Precisa de permissão';
    if (state.installationStatus === 'failed') return 'Falha na instalação';
    if (state.installationStatus !== 'installed') return 'Sincronização necessária';
    if (state.monitoringState === 'verifying') return item.transition === 'enter' ? 'Verificando possível chegada' : 'Verificando possível saída';
    if (state.armed) return item.transition === 'enter' ? 'Ativo — aguardando sua chegada' : 'Ativo — aguardando sua saída';
    if (state.monitoringState === 'waiting_departure') return 'Aguardando você sair da área';
    if (state.monitoringState === 'waiting_arrival') return 'Aguardando você entrar na área';
    const baseline = state.confirmedPresence ?? state.presence;
    if (item.transition === 'enter' && baseline === 'inside') return 'Aguardando você sair da área';
    if (item.transition === 'exit' && baseline === 'outside') return 'Aguardando você entrar na área';
    if (state.monitoringState === 'uncertain' || ['accuracy_insufficient', 'near_boundary', 'stale_location', 'location_unavailable'].includes(state.reasonCode)) return 'Localização incerta';
    return 'Aguardando condição inicial';
  }
  if (['pending', 'pending_sync', 'pendingSync', 'removed'].includes(item.syncStatus)) return 'Sincronização necessária';
  if (['waiting_initial', 'awaitingInitial', 'installed'].includes(item.syncStatus)) return 'Aguardando condição inicial';
  if (['recoverable_error', 'recoverableError', 'error'].includes(item.syncStatus)) return 'Precisa de atenção';
  return 'Aguardando estado do celular';
}

const presenceReasons: Readonly<Record<string, string>> = {
  awaiting_confirmation: 'Ainda falta evidência suficiente para confirmar a presença.',
  accuracy_insufficient: 'A precisão atual não permite confirmar a presença nesta área.',
  near_boundary: 'A posição está próxima da borda; ainda não há evidência suficiente de entrada ou saída.',
  stale_location: 'A última leitura está desatualizada. A presença anterior e o armamento continuam registrados.',
  location_unavailable: 'O Android não forneceu uma localização nova dentro do prazo.',
  permission_required: 'Permita a localização neste celular para acompanhar esta área.',
  location_disabled: 'Ative a localização nas configurações do Android.',
  provider_unavailable: 'O provedor de localização não está disponível neste Android.',
  installation_failed: 'O Android não conseguiu instalar esta geofence. Tente sincronizar novamente.',
  not_observed: 'Ainda não há evidência suficiente para estabelecer a condição inicial.',
  outside_initial: 'A regra de saída aguarda uma entrada confirmada antes de monitorar a saída.',
  inside_initial: 'A regra de chegada aguarda uma saída confirmada antes de monitorar a chegada.',
  pending_sync: 'Sincronize a regra neste celular para instalar o monitoramento.',
  transition_needs_confirmation: 'Um evento foi recebido e precisa de confirmação complementar limitada.',
  transition_contradictory_location: 'O evento recebido conflita com a localização recente. A presença anterior foi preservada.',
};

export function geofencePresenceReason(state: GeofencePresenceDiagnostic | undefined): string {
  if (!state) return 'Confira o estado no celular observador.';
  if (state.monitoringState === 'verifying') return 'Um evento foi recebido. O celular está buscando evidência complementar por tempo limitado.';
  return Object.prototype.hasOwnProperty.call(presenceReasons, state.reasonCode) ? presenceReasons[state.reasonCode]! : state.reasonCode === 'confirmed' ? '' : 'Confira os detalhes do monitoramento para acompanhar a última decisão do celular.';
}

// Display only known codes and typed fields, never arbitrary native payloads or errors.
const eventLabels: Readonly<Record<string, string>> = {
  native_enter_accepted: 'Entrada nativa aceita', native_exit_accepted: 'Saída nativa aceita',
  native_enter_unarmed: 'Entrada recebida com regra desarmada', native_exit_unarmed: 'Saída recebida com regra desarmada',
  native_event_initial: 'Evento usado como condição inicial', native_event_stale: 'Evento descartado por atraso',
  native_event_out_of_order: 'Evento anterior à presença confirmada mais recente',
  native_region_unknown: 'Região fora da configuração atual', native_configuration_stale: 'Configuração substituída',
  native_event_duplicate: 'Evento repetido descartado', native_event_error: 'Erro informado pelo geofencing Android',
  native_event_invalid_transition: 'Tipo de transição não reconhecido',
  native_service_unavailable: 'Serviço de geofencing indisponível', native_region_limit: 'Limite de regiões atingido no Android',
  native_registration_limit: 'Limite de registros de geofencing atingido', native_rule_inactive: 'Regra inativa no momento do evento',
  native_event_future: 'Evento com instante incompatível', native_baseline_unreliable: 'Presença anterior insuficiente para aceitar o evento',
  native_union_ambiguous: 'Transição do lugar composto ainda incerta', native_union_unchanged: 'Movimento entre círculos do mesmo lugar',
  native_union_still_inside: 'Saída de um círculo sem sair do lugar', native_no_place_transition: 'Presença do lugar não mudou',
  native_transition_unknown: 'Transição nativa desconhecida', native_event_replaced: 'Evento de instalação anterior',
  transition_confirmation_old: 'Confirmação anterior ao evento', transition_confirmation_insufficient: 'Confirmação com localização insuficiente',
  transition_confirmation_contradicted: 'Confirmação contradiz a transição recebida',
  confirmation_unavailable: 'Confirmação pontual indisponível', occurrence_outbox_full: 'Ocorrência bloqueada por eventos pendentes',
  transition_needs_confirmation: 'Transição precisa de confirmação', transition_contradictory_location: 'Conflito com a localização recente',
  transition_accepted: 'Transição confirmada', transition_expired: 'Prazo de confirmação esgotado',
  occurrence_created: 'Ocorrência criada', notification_posted: 'Notificação apresentada', notification_failed: 'Falha ao apresentar notificação',
};

function timestamp(value: unknown): string | null {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 && value <= 8_640_000_000_000_000 ? dateLabel(new Date(value).toISOString()) : null;
}

export function geofenceDiagnosticHistory(history: GeofenceDiagnosticEvent[] | undefined): { code: string; label: string; received: string; details: string }[] {
  if (!Array.isArray(history)) return [];
  return history.slice(-12).reverse().filter(event => event && typeof event === 'object').map(event => {
    const details: string[] = [];
    if (event.source === 'android_event') details.push('evento Android');
    if (event.source === 'one_shot') details.push('aquisição pontual');
    if (event.source === 'local_notification') details.push('apresentação da notificação');
    if (event.source === 'local_recovery') details.push('recuperação local');
    if (event.transition === 'enter') details.push('entrada');
    if (event.transition === 'exit') details.push('saída');
    if (typeof event.regionCurrent === 'boolean') details.push(event.regionCurrent ? 'região atual' : 'região rejeitada');
    if (typeof event.armed === 'boolean') details.push(event.armed ? 'regra armada' : 'regra desarmada');
    if (event.classification === 'inside') details.push('classificação: dentro');
    if (event.classification === 'outside') details.push('classificação: fora');
    if (event.classification === 'unknown') details.push('classificação: incerta');
    if (typeof event.occurrenceCreated === 'boolean') details.push(event.occurrenceCreated ? 'ocorrência criada' : 'sem nova ocorrência');
    const evidenceAt = timestamp(event.evidenceAt);
    if (evidenceAt) details.push(`evidência: ${evidenceAt}`);
    if (typeof event.ageMillis === 'number' && Number.isFinite(event.ageMillis) && event.ageMillis >= 0) details.push(`idade no recebimento: ${Math.floor(event.ageMillis / 1000)} s`);
    const known = Object.prototype.hasOwnProperty.call(eventLabels, event.code);
    return { code: known ? event.code : 'unknown', label: known ? eventLabels[event.code]! : 'Diagnóstico não reconhecido', received: timestamp(event.receivedAt) ?? 'Instante indisponível', details: details.join(' · ') };
  });
}
